#!/usr/bin/env bash
# Проверка файла правил брандмауэра (firewall-linux.sh) на настоящем iptables в одноразовом контейнере; правила хоста не затрагиваются.
#   docker run --rm --privileged -v "<путь к stack>:/stack:ro" alpine:3 sh -c 'apk add -q --no-cache bash && bash /stack/build/selftest/firewall-live-selftest.sh'
# Нужен интернет (apk ставит iptables, iproute2, socat) и --privileged: внутри создаются сетевые пространства имён.
# 1. iptables-restore --test принимает правила, а испорченные отвергает.
# 2. Применение идемпотентно: цепочка пересоздаётся, переход из DOCKER-USER один.
# 3. Трафик через DNAT, как у опубликованного порта Docker: разрешённый адрес проходит, чужой нет, порты вне списка и локальные мосты br-* не затронуты,
#    после --remove всё снова проходит.
set -u
STACK=/stack
FAILED=0
ok() { if eval "$1"; then echo "  ok: $2"; else echo "  НЕ ПРОЙДЕНО: $2"; FAILED=$((FAILED + 1)); fi; }

apk add -q --no-cache iptables iproute2 socat >/dev/null || { echo "Не удалось установить iptables, iproute2 и socat (нужен интернет)"; exit 1; }
eval "$(sed -n '/^# firewall:begin/,/^# firewall:end/p' "$STACK/Install/install.sh")"
W=/tmp/fw; mkdir -p "$W"
fw_linux_script 10.99.0.1 "10.99.0.2" "5433 9000" > "$W/fw.sh"

echo "== разбор правил"
ok "sh -n $W/fw.sh" "файл проходит разбор sh"
# Без цепочки DOCKER-USER (Docker не управляет iptables) файл останавливается и ничего не создаёт.
ok "! sh $W/fw.sh 2>/dev/null && [ -z \"\$(iptables -S INTERNAL 2>/dev/null)\" ]" "без DOCKER-USER применение отказывает, цепочка INTERNAL не создана"
# Docker создаёт DOCKER-USER сам и вызывает её из FORWARD: воспроизводим это.
iptables -N DOCKER-USER && iptables -A DOCKER-USER -j RETURN && iptables -I FORWARD -j DOCKER-USER || { echo "iptables в контейнере недоступен (нужен --privileged)"; exit 1; }
ok "sh $W/fw.sh --test" "iptables-restore --test принимает правила"
sed 's/--ctorigdstport 5433/--ctorigdstport 5433 --нет-такого-ключа/' "$W/fw.sh" > "$W/bad.sh"
ok "! sh $W/bad.sh --test 2>/dev/null" "испорченное правило отвергается"
ok "[ -z \"\$(iptables -S INTERNAL 2>/dev/null)\" ]" "--test ничего не применил"

echo "== применение и повторное применение"
ok "sh $W/fw.sh" "применение проходит"
n1="$(iptables -S INTERNAL | wc -l)"
ok "[ \"\$(iptables -S DOCKER-USER | sed -n 2p)\" = '-A DOCKER-USER -j INTERNAL' ]" "переход в INTERNAL стоит первым в DOCKER-USER"
ok "sh $W/fw.sh" "повторное применение проходит"
n2="$(iptables -S INTERNAL | wc -l)"
ok "[ \"$n1\" = \"$n2\" ] && [ \"\$(iptables -S DOCKER-USER | grep -c 'INTERNAL')\" = 1 ]" "правил столько же ($n1), переход один"

echo "== трафик через DNAT (как опубликованный порт Docker)"
iptables -F INTERNAL; iptables -D DOCKER-USER -j INTERNAL   # проверяем сначала без правил
sysctl -qw net.ipv4.ip_forward=1
ip link add lan0 type bridge && ip addr add 10.99.0.1/24 dev lan0 && ip link set lan0 up
ip link add br-loc type bridge && ip addr add 10.97.0.1/24 dev br-loc && ip link set br-loc up
for spec in ext1:10.99.0.2:lan0 ext2:10.99.0.3:lan0 loc:10.97.0.2:br-loc; do
  ns="${spec%%:*}"; rest="${spec#*:}"; ip_="${rest%%:*}"; br="${rest#*:}"
  ip netns add "$ns"; ip link add "v-$ns" type veth peer name "p-$ns"
  ip link set "p-$ns" netns "$ns"; ip link set "v-$ns" master "$br"; ip link set "v-$ns" up
  ip netns exec "$ns" ip addr add "$ip_/24" dev "p-$ns"; ip netns exec "$ns" ip link set "p-$ns" up; ip netns exec "$ns" ip link set lo up
  ip netns exec "$ns" ip route add default via "${ip_%.*}.1"
done
ip netns add srv; ip link add v-srv type veth peer name p-srv; ip link set p-srv netns srv
ip addr add 10.98.0.1/24 dev v-srv; ip link set v-srv up
ip netns exec srv ip addr add 10.98.0.2/24 dev p-srv; ip netns exec srv ip link set p-srv up; ip netns exec srv ip link set lo up
ip netns exec srv ip route add default via 10.98.0.1
ip netns exec srv socat TCP-LISTEN:5432,fork,reuseaddr SYSTEM:'echo hello' &
SRV=$!; sleep 1
for p in 5433 9000 5555; do iptables -t nat -A PREROUTING -p tcp --dport "$p" -j DNAT --to-destination 10.98.0.2:5432; done
reach() { ip netns exec "$1" sh -c "socat -T 2 - TCP:$2:$3,connect-timeout=2 </dev/null 2>/dev/null" | grep -q hello; }   # $1 - пространство имён клиента, $2 - адрес, $3 - порт

ok "reach ext1 10.99.0.1 5433 && reach ext2 10.99.0.1 5433" "без правил: оба клиента достают до порта (DNAT работает)"
sh "$W/fw.sh"
ok "reach ext1 10.99.0.1 5433 && reach ext1 10.99.0.1 9000" "с правилами: разрешённый адрес проходит на оба закрытых порта"
ok "! reach ext2 10.99.0.1 5433 && ! reach ext2 10.99.0.1 9000" "с правилами: чужой адрес не проходит"
ok "reach ext2 10.99.0.1 5555" "порт вне списка (как админка, Grafana) остаётся открытым для чужого адреса"
ok "reach loc 10.97.0.1 5433" "клиент с локального моста br-* проходит (контейнеры этой же машины)"
ok "ip netns exec ext1 sh -c 'socat -T 2 - TCP:10.99.0.1:5433,connect-timeout=2 </dev/null' | grep -q hello" "ответы разрешённому клиенту возвращаются (ответные пакеты не режутся)"
sh "$W/fw.sh" --remove
ok "reach ext2 10.99.0.1 5433 && [ -z \"\$(iptables -S INTERNAL 2>/dev/null)\" ] && ! iptables -S DOCKER-USER | grep -q INTERNAL" "--remove: цепочка и переход убраны, чужой снова проходит"
kill "$SRV" 2>/dev/null

echo
if [ "$FAILED" = 0 ]; then echo "ВСЕ ПРОВЕРКИ ПРОЙДЕНЫ"; else echo "НЕ ПРОЙДЕНО: $FAILED"; exit 1; fi
