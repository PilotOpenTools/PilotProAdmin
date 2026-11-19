<#
  Установка (и обновление) стека из этой папки: наши образы - из images\, остальные docker
  скачивает из интернета; папки данных, сети, запуск, администраторы Pilot, бакеты RustFS.
  Повторный запуск безопасен: так же ставится новая версия комплекта поверх старой - данные
  в DATA_ROOT и .env сохраняются, пересоздаются контейнеры с изменившимися образами или настройками
  (при переходе в другую папку установки ещё и Pilot-Server с PostgreSQL: у них меняются пути монтирования).

  С данными: если в папке есть data\ или указан бэкап, всё поднимается из него: базы Pilot, файлы, RustFS,
  GitLab со всеми репозиториями, админка, миграция, Grafana. Пароли берутся из бэкапа.
  Данные восстанавливаются один раз и только в пустой DATA_ROOT (метка DATA_ROOT\.installed).

  .\install.ps1 -GitLabHost 10.0.0.5   # с данными из data\; GitLabHost - IP этого сервера вместо IP исходного
  .\install.ps1 -NoData         # чистая установка, data\ не используется
  .\install.ps1 -FromBackup D:\Backup\2026-11-03_0300 -GitLabHost 10.0.0.5   # данные из другого бэкапа
  .\install.ps1 -WithGitLab     # + свой GitLab CE и runner (если у заказчика нет GitLab)
  .\install.ps1 -WithMyAdmin    # + Pilot-myAdmin Web для test и stage
  .\install.ps1 -SkipLoad       # образы из images\ уже загружены
  .\install.ps1 -Roles prod     # только эти сервисы: prod, test, stage, admin, metrics, gitlab; адреса других машин - в .env
#>
param([string]$FromBackup, [string]$GitLabHost, [string[]]$Roles, [switch]$NoData, [switch]$WithGitLab, [switch]$WithMyAdmin, [switch]$SkipLoad,
    [switch]$ReleaseInstall, [string]$ReleaseGitLabUrl, [string]$ReleaseGrafanaUrl, [hashtable]$ReleasePilotUrls)
$ErrorActionPreference = 'Stop'
# До перехода в папку комплекта: путь к бэкапу может быть относительным.
if ($FromBackup) { $FromBackup = (Resolve-Path $FromBackup).Path.TrimEnd([char]92, [char]47) }
Set-Location $PSScriptRoot

function Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Run { & $args[0] $args[1..($args.Count - 1)]; if ($LASTEXITCODE) { throw "Ошибка: $($args -join ' ')" } }
$contours = 'prod', 'test', 'stage'
$envPath = "$PSScriptRoot\.env"

# Не Get-Content: PS 5.1 читает UTF-8 без BOM как ANSI и портит кириллицу в комментариях .env.
function Read-Env($path) {
    $h = @{}
    if (Test-Path $path) { foreach ($l in [IO.File]::ReadAllLines($path)) { if ($l -match '^\s*([A-Z0-9_]+)=(.*)$') { $h[$Matches[1]] = $Matches[2].Trim() } } }
    $h
}
function Set-EnvValue($key, $value) {
    $found = $false
    $lines = foreach ($l in [IO.File]::ReadAllLines($envPath)) {
        if ($l -match "^$key=") { "$key=$value"; $found = $true } else { $l }
    }
    if (-not $found) { $lines = @($lines) + "$key=$value" }
    [IO.File]::WriteAllLines($envPath, [string[]]$lines)
}
# robocopy: коды 0-7 - успех.
function Copy-Tree($from, $to) {
    if (-not (Test-Path $from)) { Write-Host "  в бэкапе нет $from - пропуск"; return }
    & robocopy $from $to /E /COPY:DAT /R:2 /W:2 /NP /NFL /NDL /NJH /NJS /XJ | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy $from -> код $LASTEXITCODE" }
}
function Replace-InFile($path, $old, $new) {
    if (Test-Path $path) { [IO.File]::WriteAllText($path, [IO.File]::ReadAllText($path).Replace($old, $new)) }
}
# В .env все пароли стека: доступ только у текущего пользователя, SYSTEM и администраторов.
function Protect-Secret($path) {
    if (-not (Test-Path $path) -or -not (Get-Command icacls -ErrorAction SilentlyContinue)) { return }
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    & icacls $path /inheritance:r /grant:r "*${me}:F" '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Null
    if ($LASTEXITCODE) { Write-Warning "Не удалось ограничить доступ к ${path}: сделайте это вручную." }
}
# Криптостойкий ключ (hex), не Get-Random.
function New-Secret([int]$bytes = 20) {
    $b = New-Object byte[] $bytes
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($b) } finally { $rng.Dispose() }
    -join ($b | ForEach-Object { '{0:x2}' -f $_ })
}
# Сеть бывает нестабильной: три попытки с паузой.
function Retry([scriptblock]$action) {
    for ($i = 1; ; $i++) {
        try { & $action; return }
        catch {
            if ($i -ge 3) { throw "Не удалось за 3 попытки (сеть, прокси, доступ к реестрам): $($_.Exception.Message)" }
            Write-Warning "Попытка $i из 3 не удалась: $($_.Exception.Message). Повтор через 15 с."
            Start-Sleep 15
        }
    }
}
# Нативная команда без вывода; true при коде 0 (stderr при Stop в PS 5.1 иначе бросал бы исключение).
function Test-Docker([string[]]$dockerArgs) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { & docker @dockerArgs *> $null; $LASTEXITCODE -eq 0 } finally { $ErrorActionPreference = $prev }
}

# Контейнер RustFS отвечает (curl есть в образе); до этого rc mb получил бы отказ в соединении.
function Wait-RustFs([string]$contour) {
    for ($n = 0; $n -le 60; $n++) {
        if (Test-Docker @('exec', "rustfs-$contour", 'curl', '-fsS', 'http://127.0.0.1:9000/health')) { return $true }
        Start-Sleep 2
    }
    $false
}
# Бакет RustFS клиентом rc из образа RustFS. Учётные данные не должны попадать в аргументы docker run (их видно в списке процессов):
# RC_HOST_<алиас> читается из окружения, поэтому так передаются только простые значения (в URL они кодируются); остальные идут
# через переменные окружения внутри sh -c. Флаг --ignore-existing у rc mb описан в документации клиента, на живом RustFS не проверен.
function New-RustFsBucket([string]$contour, [string]$user, [string]$pass) {
    if ("$user$pass" -match '^[A-Za-z0-9._~+=-]+$') {
        $env:RC_HOST_m = "http://${user}:${pass}@rustfs-${contour}:9000"
        try { docker run --rm --network "pilot-$contour" -e RC_HOST_m --entrypoint rc $rustfsImage mb --ignore-existing m/pilot-ice-ru | Out-Null }
        finally { Remove-Item Env:RC_HOST_m }
    } else {
        $env:RCU = $user; $env:RCP = $pass
        # \" - кавычки для разбора аргументов Windows: PowerShell 5.1 сам их внутри аргумента не экранирует.
        $script = 'rc alias set m http://rustfs-{0}:9000 \"$RCU\" \"$RCP\" >/dev/null && rc mb --ignore-existing m/pilot-ice-ru' -f $contour
        try { docker run --rm --network "pilot-$contour" -e RCU -e RCP -e HOME=/tmp --entrypoint sh $rustfsImage -c $script | Out-Null }
        finally { Remove-Item Env:RCU, Env:RCP }
    }
}

# --- брандмауэр для служебных портов (INTERNAL_BIND) ---
# IPv4 в виде a.b.c.d (имена хостов не резолвим: ответ DNS на другой машине может отличаться).
function Test-IPv4([string]$s) {
    $ip = $null
    # Без ведущих нулей: «010» разные программы читают как 8 или как 10.
    $s -match '^(0|[1-9]\d{0,2})(\.(0|[1-9]\d{0,2})){3}$' -and [Net.IPAddress]::TryParse($s, [ref]$ip) -and $ip.AddressFamily -eq 'InterNetwork'
}
function ConvertTo-IpNumber([string]$ip) { $b = [Net.IPAddress]::Parse($ip).GetAddressBytes(); [long]$b[0] * 16777216 + [long]$b[1] * 65536 + [long]$b[2] * 256 + [long]$b[3] }
function ConvertFrom-IpNumber([long]$n) { '{0}.{1}.{2}.{3}' -f (($n -shr 24) -band 255), (($n -shr 16) -band 255), (($n -shr 8) -band 255), ($n -band 255) }
# Windows Firewall не умеет «все, кроме»: запрещающее правило строится из адресных диапазонов, которые остаются после вычета разрешённых,
# 127.0.0.0/8 и 172.16.0.0/12 (сети Docker и WSL: контейнеры этой машины приходят оттуда, если обращаются к порту через её адрес в сети).
function Get-ForbiddenRanges([string[]]$allowed) {
    $skip = @([pscustomobject]@{ S = [long]2130706432; E = [long]2147483647 }, [pscustomobject]@{ S = [long]2886729728; E = [long]2887778303 }) + @($allowed | ForEach-Object { $n = ConvertTo-IpNumber $_; [pscustomobject]@{ S = $n; E = $n } })
    $cur = [long]0
    foreach ($r in ($skip | Sort-Object S)) {
        if ($r.S -gt $cur) { if ($r.S - 1 -eq $cur) { ConvertFrom-IpNumber $cur } else { '{0}-{1}' -f (ConvertFrom-IpNumber $cur), (ConvertFrom-IpNumber ($r.S - 1)) } }
        $cur = [Math]::Max($cur, $r.E + 1)
    }
    if ($cur -le 4294967295) { if ($cur -eq 4294967295) { ConvertFrom-IpNumber $cur } else { '{0}-{1}' -f (ConvertFrom-IpNumber $cur), '255.255.255.255' } }
}
# Файл правил Windows Firewall для служебных портов: разрешение машинам стенда и запрет остальным (запрет сильнее разрешения, поэтому он не пересекается с разрешённым).
function New-FirewallScript([string]$bind, [string[]]$peers, [string[]]$ports) {
    $allow = @($peers) + '127.0.0.1'
    $lines = @(
        "# Правила Windows Firewall для служебных портов этой машины (INTERNAL_BIND=$bind). Создан install.ps1 из .env, автоматически не применяется.",
        "# Порты (PostgreSQL, RustFS, миграция, Prometheus, Loki, агенты): $($ports -join ' ')",
        "# Разрешены адреса: $(if ($peers) { $peers -join ' ' } else { 'нет' }) (машины стенда из .env и сама эта машина). Остальным эти порты закрыты, кроме 127.0.0.0/8 и 172.16.0.0/12 (сети Docker и WSL: оттуда приходят контейнеры этой машины).",
        '# Порты админки, Grafana, Pilot и GitLab правила не затрагивают. Адрес каждой машины стенда должен быть в .env',
        '# (PROD_HOST, TEST_HOST, STAGE_HOST, METRICS_HOST, ADMIN_HOST, GITLAB_HOST_IP), иначе она потеряет доступ к этим портам.',
        '#   проверить синтаксис без применения:  [scriptblock]::Create((Get-Content -Raw .\firewall-windows.ps1)) | Out-Null',
        '#   применить: PowerShell от имени администратора, затем  .\firewall-windows.ps1  (повтор безопасен)',
        '#   снять:  .\firewall-windows.ps1 -Remove',
        'param([switch]$Remove)',
        '$ErrorActionPreference = ''Stop''',
        '$allowName = ''Internal ports: allow stack machines''',
        '$blockName = ''Internal ports: block others''',
        'Get-NetFirewallRule -DisplayName $allowName, $blockName -ErrorAction SilentlyContinue | Remove-NetFirewallRule',
        'if ($Remove) { return }',
        "`$ports = $($ports -join ', ')",
        "New-NetFirewallRule -DisplayName `$allowName -Direction Inbound -Protocol TCP -LocalPort `$ports -RemoteAddress $(($allow | ForEach-Object { "'$_'" }) -join ', ') -Action Allow | Out-Null",
        "New-NetFirewallRule -DisplayName `$blockName -Direction Inbound -Protocol TCP -LocalPort `$ports -RemoteAddress $(((Get-ForbiddenRanges $peers) | ForEach-Object { "'$_'" }) -join ', ') -Action Block | Out-Null"
    )
    ($lines -join "`r`n") + "`r`n"
}

# --- HTTPS (необязательно): сертификат и ключ в certs\ включают его для админки и Grafana на тех же портах ---
$tlsOverlay = 'docker-compose.tls.yml'
function Remove-EnvValue([string]$key) {
    $lines = [IO.File]::ReadAllLines($envPath)
    $kept = @($lines | Where-Object { $_ -notmatch "^$key=" })
    if ($kept.Count -ne $lines.Count) { [IO.File]::WriteAllLines($envPath, [string[]]$kept) }
}
function Get-TlsPath([string]$value, [string]$default) {
    $p = if ($value) { $value } else { $default }
    if (-not [IO.Path]::IsPathRooted($p)) { $p = Join-Path $PSScriptRoot $p }
    $p
}
# Включает HTTPS, если есть оба файла; без них убирает надстройку из COMPOSE_FILE. Возвращает true, если HTTPS включён.
function Initialize-Tls {
    $c = Read-Env $envPath
    $crt = Get-TlsPath $c.TLS_CERT_FILE 'certs\tls.crt'; $key = Get-TlsPath $c.TLS_KEY_FILE 'certs\tls.key'
    $haveCrt = Test-Path -LiteralPath $crt -PathType Leaf; $haveKey = Test-Path -LiteralPath $key -PathType Leaf
    # Разделитель в COMPOSE_FILE: свой, если задан; у чужого списка файлов - стандартный для Windows; у нашего - запятая (одинаково на любой ОС).
    $sep = if ($c.COMPOSE_PATH_SEPARATOR) { $c.COMPOSE_PATH_SEPARATOR } elseif ($c.COMPOSE_FILE) { ';' } else { ',' }
    # В нашем списке основной файл подразумевается: свои файлы пользователя - это всё остальное.
    $others = @(@($c.COMPOSE_FILE -split [regex]::Escape($sep)) | Where-Object { $_ -and $_ -ne $tlsOverlay -and -not ($sep -eq ',' -and $_ -eq 'docker-compose.yml') })
    if (-not (($haveCrt -or $haveKey) -and ($isAdmin -or $isMetrics))) {
        foreach ($k in 'TLS_ADMIN_CRT', 'TLS_ADMIN_KEY', 'TLS_GRAFANA_CRT', 'TLS_GRAFANA_KEY') { Remove-EnvValue $k }
        if ($others) { Set-EnvValue COMPOSE_FILE ($others -join $sep) }
        else { Remove-EnvValue COMPOSE_FILE; if ($c.COMPOSE_PATH_SEPARATOR -eq ',') { Remove-EnvValue COMPOSE_PATH_SEPARATOR } }
        return $false
    }
    if ($haveCrt -ne $haveKey) { throw "HTTPS: найден только один из файлов ($crt, $key). Положите оба или уберите оставшийся: иначе админка и Grafana остались бы без HTTPS незаметно для вас." }
    if (-not (Test-Path "$PSScriptRoot\$tlsOverlay")) { throw "HTTPS: нет файла $tlsOverlay рядом с install.ps1 (комплект неполный)." }
    if ([IO.File]::ReadAllText($crt) -notmatch '-----BEGIN CERTIFICATE-----') { throw "HTTPS: $crt не похож на сертификат в формате PEM (нет строки BEGIN CERTIFICATE)." }
    $keyText = [IO.File]::ReadAllText($key)
    if ($keyText -match '-----BEGIN ENCRYPTED PRIVATE KEY-----') { throw "HTTPS: ключ $key зашифрован паролем, а админка и Grafana читают только ключ без пароля. Снимите пароль: openssl pkey -in <ключ> -out tls.key" }
    if ($keyText -notmatch '-----BEGIN (RSA |EC )?PRIVATE KEY-----') { throw "HTTPS: $key не похож на закрытый ключ в формате PEM." }
    # Копии в DATA_ROOT\tls: Docker Desktop показывает контейнеру права исходного файла (ключ, созданный в Linux-контейнере, имел бы 600 от root,
    # и пользователь Grafana или админки его не прочёл бы); у файла, созданного Windows, права обычные. Доступ к ключу на хосте ограничен, как у .env.
    foreach ($s in 'admin', 'grafana') {
        $d = "$root/tls/$s"
        New-Item -ItemType Directory -Force $d | Out-Null
        # Новый файл из байтов, а не Copy-Item: тот переносит и расширенные атрибуты (режим 600 от Linux), и контейнер ключ не прочтёт.
        foreach ($pair in @(@($crt, 'tls.crt'), @($key, 'tls.key'))) {
            $target = "$d/$($pair[1])"
            if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
            [IO.File]::WriteAllBytes($target, [IO.File]::ReadAllBytes($pair[0]))
        }
        Protect-Secret "$d/tls.key"
        Set-EnvValue "TLS_$($s.ToUpper())_CRT" ((Resolve-Path -LiteralPath "$d/tls.crt").Path -replace '\\', '/')
        Set-EnvValue "TLS_$($s.ToUpper())_KEY" ((Resolve-Path -LiteralPath "$d/tls.key").Path -replace '\\', '/')
    }
    $files = @(if ($others) { $others } else { 'docker-compose.yml' }) + $tlsOverlay
    Set-EnvValue COMPOSE_FILE ($files -join $sep)
    if ($sep -eq ',') { Set-EnvValue COMPOSE_PATH_SEPARATOR ',' }
    $true
}

# --- откуда данные ---
$snap = $null
if ($FromBackup) { $snap = $FromBackup }
elseif (-not $NoData -and (Test-Path "$PSScriptRoot\data\manifest.json")) { $snap = "$PSScriptRoot\data" }
if ($snap) {
    if (-not (Test-Path "$snap\manifest.json")) { throw "В $snap нет manifest.json - это не каталог запуска бэкапа" }
    $manifest = [IO.File]::ReadAllText("$snap\manifest.json") | ConvertFrom-Json
    if (-not $manifest.ok) { throw "Бэкап $snap снят с ошибками, восстанавливать его нельзя. Подробности - в его backup.log" }
}

# --- .env ---
if (-not (Test-Path .env)) {
    # Стек уже стоит, а .env потерян: новый .env из шаблона вернул бы заглушки вместо рабочих паролей.
    if (Test-Path 'C:/Ascon-Docker/.installed') { throw "Найден C:/Ascon-Docker/.installed, но .env рядом со скриптом нет. Верните .env прежней установки: пароли в нём должны совпасть с рабочими." }
    Copy-Item .env.example .env
    if (-not $snap) { Write-Host "Создан .env из .env.example: пароли-заглушки установка заменит сама (одна машина) или остановится (несколько машин)." -ForegroundColor Yellow }
}
Protect-Secret $envPath
$root = (Read-Env $envPath).DATA_ROOT
if (-not $root) { $root = 'C:/Ascon-Docker' }

# --- роли этой машины: ключ, затем запись в .env, иначе всё, кроме GitLab ---
$allRoles = 'prod', 'test', 'stage', 'admin', 'metrics', 'gitlab'
$rolesSet = (Read-Env $envPath).ROLES
$roleList = @(@(if ($Roles) { $Roles -split ',' } elseif ($rolesSet) { $rolesSet -split ',' } else { 'prod', 'test', 'stage', 'admin', 'metrics' }) | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Unique)
$unknown = $roleList | Where-Object { $allRoles -notcontains $_ }
if ($unknown) { throw "Неизвестная роль: $($unknown -join ', '). Допустимо: $($allRoles -join ', ')" }
$rolesChosen = [bool]($Roles -or $rolesSet)
if ($WithGitLab) { $roleList = @(@($roleList) + 'gitlab' | Select-Object -Unique) }
$myContours = @($contours | Where-Object { $roleList -contains $_ })
$isAdmin   = $roleList -contains 'admin'
$isMetrics = $roleList -contains 'metrics'
$restore = $snap -and -not (Test-Path "$root/.installed")
if ($ReleaseInstall -and $snap -and -not $restore) {
    throw "В $root уже есть установленный стенд. Для восстановления выберите новый каталог установки."
}
if ($snap -and -not $restore) { Write-Host "Стек в $root уже установлен (метка .installed): данные из $snap не восстанавливаю." -ForegroundColor Yellow }
if ($restore) {
    if (Test-Path "$root/.restore-started") { throw "Прошлое восстановление GitLab в $root не завершилось (метка .restore-started): данные могли измениться, повторное восстановление их затрёт. Журнал: $root/gitlab/logs/restore.log. Очистите $root и запустите установку снова." }
    $busy = $myContours | Where-Object { (Test-Path "$root/$_/postgres") -and (Get-ChildItem "$root/$_/postgres" -Force | Select-Object -First 1) }
    if ($busy) { throw "В $root уже есть данные ($($busy -join ', ')). Укажите пустой DATA_ROOT в .env. Если прошлое восстановление оборвалось, очистите эту папку и запустите снова." }
    Write-Host "Восстановление из бэкапа $snap (снят $($manifest.created) на $($manifest.host))" -ForegroundColor Yellow

    # Пароли должны совпасть с бэкапом: они записаны в settings.xml, в ролях PostgreSQL и в служебных данных RustFS.
    # DATA_ROOT и пути исходной машины не переносим.
    $skip = '^(DATA_ROOT|[A-Z]+_ROOT|PG_HOST|PG_PORT|PG_DB|PG_USER|PG_PASSWORD|MIGRATION_URL|COMPOSE_PROJECT_NAME|COMPOSE_FILE|COMPOSE_PROFILES|RELEASE_.*)$'
    foreach ($f in 'gitlab', 'proadmin', 'metrics') {
        # Бэкапы до 2026-11-05 хранят настройки админки и GitLab на уровень глубже.
        $file = "$snap\admin\env\$f\.env"
        if (-not (Test-Path $file)) { $file = "$snap\admin\env\admin\$f\.env" }
        $b = Read-Env $file
        foreach ($k in $b.Keys) { if ($k -notmatch $skip) { Set-EnvValue $k $b[$k] } }
    }
    foreach ($c in $contours) {
        $b = Read-Env "$snap\admin\env\$c\.env"
        foreach ($k in $b.Keys) { if ($k -notmatch $skip) { Set-EnvValue "$($c.ToUpper())_$k" $b[$k] } }
    }
    $cfg = Read-Env $envPath
    if ($ReleaseInstall -and $cfg.RELEASE_RESTORE_GITLAB_IMAGE) {
        Set-EnvValue GITLAB_IMAGE $cfg.RELEASE_RESTORE_GITLAB_IMAGE
    }
    if ($cfg.PROD_PILOT_ADMIN) { Set-EnvValue PILOT_ADMIN_LOGIN $cfg.PROD_PILOT_ADMIN; Set-EnvValue PILOT_ADMIN_PASSWORD $cfg.PROD_PILOT_PASSWORD }
    $oldHost = $cfg.GITLAB_HOST_IP
    if ($GitLabHost) {
        if ($oldHost -and $oldHost -ne 'localhost') { Replace-InFile $envPath $oldHost $GitLabHost }
        Set-EnvValue GITLAB_HOST_IP $GitLabHost
    }
    # Роли выбраны явно: GitLab только там, где он указан.
    if (-not $rolesChosen -and (Get-ChildItem "$snap\gitlab\*_gitlab_backup.tar" -ErrorAction SilentlyContinue)) { $roleList = @(@($roleList) + 'gitlab' | Select-Object -Unique) }
}

# --- профили запоминаем в .env: так их видят и повторный install, и ручной docker compose ---
$cfg = Read-Env $envPath
$previous = @(if ($cfg.COMPOSE_PROFILES) { $cfg.COMPOSE_PROFILES -split ',' })
if (-not $rolesChosen -and $previous -contains 'gitlab') { $roleList = @(@($roleList) + 'gitlab' | Select-Object -Unique) }
$WithGitLab = $roleList -contains 'gitlab'
$localPg = @($myContours | Where-Object { -not $cfg["$($_.ToUpper())_EXTERNAL_PG_HOST"] })
$localS3 = @($myContours | Where-Object { -not $cfg["$($_.ToUpper())_EXTERNAL_S3_URL"] })
if ($restore -and ($localPg.Count -ne $myContours.Count -or $localS3.Count -ne $myContours.Count)) {
    throw 'Для подключения внешних PostgreSQL или S3 используйте чистую установку (-NoData). Данные внешних сервисов восстанавливает их владелец.'
}
if ($WithGitLab -and $cfg.GITHUB_TYPES_REPOSITORY) { throw 'Для GitHub выберите роли без gitlab и уберите -WithGitLab.' }
$wantMyAdmin = $WithMyAdmin -or ($previous | Where-Object { $_ -like 'myadmin*' })
$active = @($roleList)
if ($wantMyAdmin) { foreach ($c in 'test', 'stage') { if ($myContours -contains $c) { $active += "myadmin-$c" } } }

# Мосты: контур или мониторинг на другой машине нужен тем, кто к нему обращается.
$needs = @{ prod = ($isAdmin -or $isMetrics -or $myContours -contains 'test' -or $myContours -contains 'stage'); test = $isAdmin; stage = $isAdmin }
foreach ($c in $contours) {
    if ($myContours -contains $c -or -not $needs[$c]) { continue }
    if ($cfg["$($c.ToUpper())_HOST"]) { $active += "link-$c" } else { Write-Warning "Контура $c на этой машине нет, а $($c.ToUpper())_HOST в .env не задан: сервисы до него не достучатся." }
}
if ($isMetrics) { $active += 'agents' }
elseif ($cfg.METRICS_HOST) { $active += 'agents', 'link-metrics' }
elseif ($myContours) { Write-Warning "METRICS_HOST в .env не задан: нагрузка и логи этой машины в Grafana не попадут." }

Set-EnvValue ROLES ($roleList -join ',')
$active = @($active | Sort-Object -Unique)
Set-EnvValue COMPOSE_PROFILES ($active -join ',')
Write-Host "Роли машины: $($roleList -join ', ')" -ForegroundColor Yellow

# --- секреты: пустые ключи генерируем, пароли-заглушки при первой установке заменяем ---
$cfg = Read-Env $envPath
$generated = @()
$example = Read-Env "$PSScriptRoot\.env.example"
$multi = [bool]('INTERNAL_BIND', 'PROD_HOST', 'TEST_HOST', 'STAGE_HOST', 'METRICS_HOST' | Where-Object { $cfg[$_] })
$first = (-not $restore) -and -not (Test-Path "$root/.installed")
# Postgres уже инициализированы (оборванная установка): новые пароли в .env не совпали бы с теми, что внутри данных.
if ($first) {
    foreach ($c in $myContours) { foreach ($d in 'postgres') {
        if ((Test-Path "$root/$c/$d") -and (Get-ChildItem "$root/$c/$d" -Force | Select-Object -First 1)) {
            $first = $false
            Write-Warning "В $root/$c/$d уже есть данные без метки .installed: пароли-заглушки в .env не заменяю, чтобы они совпали с паролями внутри данных. Смените их после установки через shared/rotate-contour-secrets.ps1."
        }
    } }
}
if ($first) {
    $bad = @()
    $names = @('PILOT_ADMIN_PASSWORD', 'PG_SUPERUSER_PASSWORD', 'PILOT_PG_PASSWORD', 'MINIO_PASSWORD', 'GRAFANA_ADMIN_PASSWORD'); if ($roleList -contains 'gitlab') { $names += 'GITLAB_ROOT_PASSWORD' }
    foreach ($k in $names) {
        if (-not $cfg[$k] -or $cfg[$k] -eq $example[$k]) {
            # Несколько машин делят один .env: пароль, придуманный на одной из них, не совпал бы с остальными.
            if ($multi) { $bad += $k } else { Set-EnvValue $k (New-Secret 24); $generated += $k }
        }
    }
    if ($bad) { throw "В .env остались пароли-заглушки: $($bad -join ', '). Задайте их (одинаково на всех машинах) и запустите установку снова." }
    $cfg = Read-Env $envPath
    if (-not $multi -and -not $cfg.COLLECTOR_API_KEY) { Set-EnvValue COLLECTOR_API_KEY (New-Secret); $generated += 'COLLECTOR_API_KEY' }
}
# Роль gitlab добавлена к уже установленному стеку: заглушка root осталась бы известным паролем.
$cfg = Read-Env $envPath
if (-not $restore -and ($roleList -contains 'gitlab') -and (-not $cfg.GITLAB_ROOT_PASSWORD -or $cfg.GITLAB_ROOT_PASSWORD -eq $example.GITLAB_ROOT_PASSWORD)) {
    if ((Test-Path "$root/gitlab/data") -and (Get-ChildItem "$root/gitlab/data" -Force | Select-Object -First 1)) {
        Write-Warning "Пароль root GitLab - заглушка из .env.example (в данных GitLab уже есть база): смените его в GitLab (Preferences / Password) и запишите новый в .env."
    } elseif ($multi) {
        throw "В .env осталась заглушка GITLAB_ROOT_PASSWORD. Задайте пароль (одинаково на всех машинах) и запустите установку снова."
    } else {
        Set-EnvValue GITLAB_ROOT_PASSWORD (New-Secret 24); $generated += 'GITLAB_ROOT_PASSWORD'
    }
}
$cfg = Read-Env $envPath
if (-not $cfg.COLLECTOR_API_KEY) { Write-Warning "COLLECTOR_API_KEY пуст: сборщик статистики принимает данные без ключа. Задайте ключ в .env и пропишите тот же в клиентских модулях." }
if (-not $cfg.MIGRATION_API_KEY) { Set-EnvValue MIGRATION_API_KEY (New-Secret); $generated += 'MIGRATION_API_KEY' }
# Ключи выкладки из CI: отдельные на контур. Старый общий ключ становится ключом test, для stage и prod создаются новые.
if ($isAdmin) {
    $cfg = Read-Env $envPath
    if ($ReleaseInstall -and -not $cfg.ADMIN_SETUP_KEY) { Set-EnvValue ADMIN_SETUP_KEY (New-Secret); $generated += 'ADMIN_SETUP_KEY' }
    if ($cfg.MODULES_DEPLOY_KEY -and -not $cfg.MODULES_DEPLOY_KEY_TEST) { Set-EnvValue MODULES_DEPLOY_KEY_TEST $cfg.MODULES_DEPLOY_KEY; Set-EnvValue MODULES_DEPLOY_KEY '' }
    foreach ($s in 'TEST', 'STAGE', 'PROD') {
        if (-not (Read-Env $envPath)["MODULES_DEPLOY_KEY_$s"]) { Set-EnvValue "MODULES_DEPLOY_KEY_$s" (New-Secret); $generated += "MODULES_DEPLOY_KEY_$s" }
    }
}
# compose проверяет обязательные переменные во всём файле, в том числе у сервисов ролей, которых на этой машине нет.
# Значение для роли этой машины придумывать нельзя (оно должно совпасть с рабочим), для остальных нужно любое.
$missing = @()
function Require-Env([string]$key, [string[]]$forRoles, [bool]$perContour = $false) {
    $c = Read-Env $envPath
    if ($c[$key]) { return }
    if ($perContour -and -not ($contours | Where-Object { -not $c["$($_.ToUpper())_$key"] })) { return }
    if ($forRoles | Where-Object { $roleList -contains $_ }) { $script:missing += $key; return }
    Set-EnvValue $key (New-Secret 24); $script:generated += $key
}
Require-Env 'PG_SUPERUSER_PASSWORD' $localPg $true
Require-Env 'PILOT_PG_PASSWORD' @('prod', 'test', 'stage', 'admin', 'metrics') $true
$s3Roles = @($localS3)
if ($isAdmin -and ($contours | Where-Object { -not $cfg["$($_.ToUpper())_EXTERNAL_S3_URL"] })) { $s3Roles += 'admin' }
Require-Env 'MINIO_USER' $s3Roles $true
Require-Env 'MINIO_PASSWORD' $s3Roles $true
Require-Env 'GRAFANA_ADMIN_PASSWORD' @('metrics')
# Пароли ролей метрик в данных не записаны: установка задаёт их ролям PostgreSQL при каждом запуске, поэтому пустые можно придумать.
# Несколько машин делят один набор: пароль, придуманный на одной из них, не совпал бы с остальными.
$metricsBad = @()
foreach ($k in 'METRICS_WRITER_PASSWORD', 'METRICS_READER_PASSWORD') {
    if ((Read-Env $envPath)[$k]) { continue }
    if ($multi -and ($roleList | Where-Object { 'prod', 'test', 'stage', 'metrics' -contains $_ })) { $metricsBad += $k } else { Set-EnvValue $k (New-Secret 24); $generated += $k }
}
if ($metricsBad) { throw "В .env не заданы пароли ролей метрик: $($metricsBad -join ', '). Задайте их (одинаково на всех машинах) и запустите установку снова." }
if ($missing) { throw "В .env не заданы обязательные значения: $($missing -join ', '). Эти пароли записаны в данных контуров, поэтому установка их не придумывает: верните прежние значения или задайте новые (одинаково на всех машинах)." }
Protect-Secret $envPath
$cfg = Read-Env $envPath
# Образ RustFS даёт клиент rc для создания бакета.
$rustfsImage = if ($cfg.RUSTFS_IMAGE) { $cfg.RUSTFS_IMAGE } else { 'rustfs/rustfs:1.0.5' }
$project = if ($cfg.COMPOSE_PROJECT_NAME) { $cfg.COMPOSE_PROJECT_NAME } else { 'cicd' }
$tlsOn = Initialize-Tls
$cfg = Read-Env $envPath
if ($tlsOn) {
    Write-Host "HTTPS включён для админки и Grafana (сертификат $(Get-TlsPath $cfg.TLS_CERT_FILE 'certs\tls.crt'))." -ForegroundColor Yellow
    if ($cfg.ADMIN_PUBLIC_URL -match '^http://') { Write-Warning 'ADMIN_PUBLIC_URL в .env начинается с http://: укажите https://, а в Redirect URI OAuth-приложения GitLab и в переменной группы ADMIN_URL (CI модулей) - адрес админки по https.' }
}
$scheme = if ($tlsOn) { 'https' } else { 'http' }

if ($ReleaseInstall) {
    . "$PSScriptRoot/release-ports.ps1"
    Set-EnvValue ADMIN_FIRST_RUN true
    $oldGitLabPort = if ($cfg.GITLAB_HTTP_PORT) { $cfg.GITLAB_HTTP_PORT } else { '8080' }
    $oldPorts = @{ prod = 5546; test = 5547; stage = 5549 }
    foreach ($c in $contours) {
        $value = $cfg["$($c.ToUpperInvariant())_PILOT_PORT"]
        if ($value) { $oldPorts[$c] = $value }
    }
    $ports = Select-ReleasePorts
    foreach ($key in $ports.Keys) { Set-EnvValue $key $ports[$key] }

    if ($WithGitLab) {
        $publicHost = if ($GitLabHost) { $GitLabHost } elseif ($cfg.GITLAB_HOST_IP) { $cfg.GITLAB_HOST_IP } else { 'localhost' }
        Set-EnvValue GITLAB_HOST_IP $publicHost
        $gitLabBase = "http://${publicHost}:$($ports.GITLAB_HTTP_PORT)"
        if (-not $restore) { Set-EnvValue GITLAB_GROUP_URL "$gitLabBase/automation" }
    } elseif ($ReleaseGitLabUrl) {
        $gitLabBase = $ReleaseGitLabUrl
        Set-EnvValue GITLAB_GROUP_URL "$gitLabBase/automation"
    }
    if ($isMetrics) { Set-EnvValue GRAFANA_URL "${scheme}://localhost:$($ports.GRAFANA_PORT)" }
    elseif (-not $restore) { Set-EnvValue GRAFANA_URL $ReleaseGrafanaUrl }

    foreach ($c in $contours) {
        $prefix = $c.ToUpperInvariant()
        if ($c -in $myContours) {
            $newPort = $ports["${prefix}_PILOT_PORT"]
            $url = "http://localhost:$newPort"
            if ($restore) {
                $previous = $cfg["PILOT_${prefix}_URL"]
                $uri = $null
                if ([uri]::TryCreate($previous, [UriKind]::Absolute, [ref]$uri)) {
                    $url += $uri.AbsolutePath.TrimEnd('/')
                }
            }
        } else { $url = $ReleasePilotUrls[$c] }
        Set-EnvValue "PILOT_${prefix}_URL" $url
    }
    Set-EnvValue ENV_MAP ":$($ports.STAGE_PILOT_PORT)=stage;:$($ports.TEST_PILOT_PORT)=test;stage=stage;test=test;*=prod"
    $cfg = Read-Env $envPath
}

# --- образы из комплекта ---
# Сверка загруженного образа с IMAGES.txt (строка: имя, полный идентификатор, дата, отпечаток слоёв). Идентификатор зависит от
# хранилища образов (classic или containerd), поэтому при расхождении сверяются ещё и слои: они от хранилища не зависят.
function Get-Sha256Hex([string]$text) {
    $h = [Security.Cryptography.SHA256]::Create()
    try { -join ($h.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)) | ForEach-Object { '{0:x2}' -f $_ }) } finally { $h.Dispose() }
}
function Test-LoadedImage([string]$name, [string]$file, [string]$imagesTxt) {
    $aid = "$(& docker image inspect $name --format '{{.Id}}')"
    if ($LASTEXITCODE) { throw "Не удалось прочитать образ $name после загрузки из $file." }
    $alay = Get-Sha256Hex "$(& docker image inspect $name --format '{{json .RootFS.Layers}}')"
    $found = $false
    foreach ($l in [IO.File]::ReadAllLines($imagesTxt)) {
        $p = @($l.Trim() -split '\s+')
        if ($p.Count -lt 4 -or ($p[0] -ne $name -and ($p[0] -replace '@.*$', '') -ne $name)) { continue }
        if ($p[1] -notmatch '^sha256:[0-9a-f]{64}$' -or $p[3] -notmatch '^[0-9a-f]{64}$') { continue }
        $found = $true
        if ($p[1] -eq $aid -or $p[3] -eq $alay) { return }
    }
    if (-not $found) { Write-Warning "Для образа $name в IMAGES.txt нет полной записи (комплект старого формата?): целостность не проверена."; return }
    throw "Образ $name из $file не совпал с IMAGES.txt (ожидался другой идентификатор, загружен $aid). Файл повреждён при копировании или не от этого комплекта: скопируйте папку комплекта заново."
}
if (-not $SkipLoad) {
    Step "Загрузка образов из комплекта"
    $imagesTxt = "$PSScriptRoot\IMAGES.txt"
    $checkImages = Test-Path $imagesTxt
    if (-not $checkImages) { Write-Warning "Нет IMAGES.txt: целостность образов не проверяется (комплект старого формата?)." }
    foreach ($f in Get-ChildItem images\*.tar.gz) {
        Write-Host "  $($f.Name)"
        $loaded = & docker load -q -i $f.FullName
        if ($LASTEXITCODE) { throw "Ошибка: docker load -q -i $($f.FullName)" }
        if (-not $checkImages) { continue }
        foreach ($line in @($loaded)) {
            if ("$line" -match '^Loaded image: (.+)$') { Test-LoadedImage $Matches[1].Trim() $f.Name $imagesTxt }
            elseif ("$line" -match '^Loaded image ID: ') { Write-Warning "Образ из $($f.Name) загружен без имени: целостность не проверена." }
        }
    }
}

# --- файлы из бэкапа (до первого старта контейнеров) ---
if ($restore) {
    Step "Файлы из бэкапа / $root"
    foreach ($c in $myContours) {
        foreach ($sub in 'pilot-server', 'server-settings', 'license', 'databases') { Copy-Tree "$snap\pilot\$c\$sub" "$root/$c/$sub" }
        # Администратор Pilot уже есть в восстановленной базе.
        New-Item -ItemType Directory -Force "$root/$c/pilot-server/settings" | Out-Null
        New-Item -ItemType File -Force "$root/$c/pilot-server/settings/.admin-created" | Out-Null
    }
    if ($isAdmin) {
        Copy-Tree "$snap\admin\proadmin-data" "$root/services/proadmin"
        Copy-Tree "$snap\admin\migration-data" "$root/services/migration"
        # Данные комплекта, а не бэкап этой же машины: ключи защиты данных админки чужие, админка создаст свои. Свой бэкап (-FromBackup) ключи сохраняет.
        if ($snap -eq "$PSScriptRoot\data" -and (Test-Path "$root/services/proadmin/keys")) { Remove-Item -Recurse -Force "$root/services/proadmin/keys" }
    }
    if ($WithGitLab) {
        # gitlab-secrets.json должен лежать на месте до первого старта - им зашифрованы данные в бэкапе.
        Copy-Tree "$snap\gitlab\config" "$root/gitlab/config"
        Copy-Tree "$snap\gitlab\runner-config" "$root/gitlab/runner-config"
        New-Item -ItemType Directory -Force "$root/gitlab/data/backups" | Out-Null
        Copy-Item "$snap\gitlab\*_gitlab_backup.tar" "$root/gitlab/data/backups/"
    }
    if ($GitLabHost -and $oldHost -and $oldHost -ne 'localhost') {
        Replace-InFile "$root/services/proadmin/settings.json" $oldHost $GitLabHost
        Replace-InFile "$root/gitlab/runner-config/config.toml" $oldHost $GitLabHost
        # Адрес в nuget.config правит шаг после установки GitLab; $oldHost живёт только в этом запуске, поэтому его запоминаем.
        if ($WithGitLab -and $oldHost -ne $GitLabHost) { [IO.File]::WriteAllText("$root/.gitlab-old-host", "$oldHost`n") }
    }
    if ($ReleaseInstall) {
        $settingsPath = "$root/services/proadmin/settings.json"
        if ($gitLabBase -and $WithGitLab) {
            foreach ($address in @($oldHost, $publicHost, 'localhost', '127.0.0.1', 'host.docker.internal') | Where-Object { $_ } | Select-Object -Unique) {
                Replace-InFile $settingsPath "http://${address}:$oldGitLabPort/" "$gitLabBase/"
                Replace-InFile "$root/gitlab/runner-config/config.toml" "http://${address}:$oldGitLabPort" $gitLabBase
            }
            foreach ($key in 'GITLAB_GROUP_URL', 'REPORTS_GITLAB_GROUP_URL', 'MODULES_GITLAB_GROUP_URL', 'SERVER_MODULES_GITLAB_GROUP_URL', 'TYPES_GITLAB_PROJECT_URL') {
                $uri = $null
                if ([uri]::TryCreate($cfg[$key], [UriKind]::Absolute, [ref]$uri)) {
                    Set-EnvValue $key "$gitLabBase$($uri.AbsolutePath)"
                }
            }
        }
        if (Test-Path -LiteralPath $settingsPath) {
            $settings = [IO.File]::ReadAllText($settingsPath) | ConvertFrom-Json
            Update-RestoredPilotPorts $settings $oldPorts $ports $myContours
            $settings | Add-Member -NotePropertyName firstRunCompleted -NotePropertyValue $false -Force
            if ($isMetrics) { $settings | Add-Member -NotePropertyName grafanaUrl -NotePropertyValue $cfg.GRAFANA_URL -Force }
            [IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 50))
        }
    }
}

# --- папки и settings.xml ---
Step "Папки данных в $root"
foreach ($c in $myContours) {
    $settings = "$root/$c/pilot-server/settings"
    New-Item -ItemType Directory -Force -Path $settings, "$root/$c/license", "$root/$c/databases" | Out-Null
    if (-not (Test-Path "$settings/settings.xml")) {
        [IO.File]::WriteAllText("$settings/settings.xml", '<Configuration></Configuration>')
    }
    # Настройки сервера из админки («Серверы»); по умолчанию логирование автоматизаций - везде, кроме прода.
    $serverSettings = "$root/$c/server-settings"
    New-Item -ItemType Directory -Force -Path $serverSettings | Out-Null
    if (-not (Test-Path "$serverSettings/server-settings.conf")) {
        $logging = if ($c -eq 'prod') { 'false' } else { 'true' }
        [IO.File]::WriteAllText("$serverSettings/server-settings.conf",
            "# Настройки сервера Pilot (контур $c). Правятся в админке / «Серверы»; применяются при перезапуске pilot-server-$c.`nIsAutomationLoggingEnabled=$logging`n")
    }
}
if ($isAdmin) { New-Item -ItemType Directory -Force -Path "$root/services/proadmin", "$root/services/migration", "$root/import", "$root/export" | Out-Null }

# --- админка: адреса контуров на других машинах (при восстановлении - и в её настройках из бэкапа) ---
if ($isAdmin) {
    $pilotPorts = @{ prod = 5546; test = 5547; stage = 5549 }
    foreach ($c in $contours) {
        $remote = $cfg["$($c.ToUpper())_HOST"]
        if ($myContours -contains $c -or -not $remote) { continue }
        Set-EnvValue "PILOT_$($c.ToUpper())_URL" "http://${remote}:$($pilotPorts[$c])/pilot-ice_ru"
        Replace-InFile "$root/services/proadmin/settings.json" "localhost:$($pilotPorts[$c])" "${remote}:$($pilotPorts[$c])"
    }
    $cfg = Read-Env $envPath
}

# --- сети ---
Step "Docker-сети контуров"
$nets = docker network ls --format '{{.Name}}'
foreach ($n in 'pilot-prod', 'pilot-test', 'pilot-stage') {
    if ($nets -notcontains $n) { Run docker network create $n | Out-Null; Write-Host "  создана $n" }
}

foreach ($c in $myContours) {
    $prefix = $c.ToUpper()
    if ($c -notin $localPg) {
        $pgHost = $cfg["${prefix}_EXTERNAL_PG_HOST"]
        $pgPort = $cfg["${prefix}_EXTERNAL_PG_PORT"]; if (-not $pgPort) { $pgPort = '5432' }
        $pgDb = $cfg["${prefix}_EXTERNAL_PG_DATABASE"]; if (-not $pgDb) { $pgDb = 'pilot-ice_ru' }
        if ($pgHost -notmatch '^[A-Za-z0-9.-]+$' -or $pgPort -notmatch '^[0-9]{1,5}$' -or [int]$pgPort -lt 1 -or [int]$pgPort -gt 65535 -or $pgDb -notmatch '^[A-Za-z0-9_.-]+$') { throw "Некорректное подключение внешнего PostgreSQL для $c." }
        $env:PGPASSWORD = $cfg["${prefix}_PILOT_PG_PASSWORD"]; if (-not $env:PGPASSWORD) { $env:PGPASSWORD = $cfg.PILOT_PG_PASSWORD }
        try {
            if (-not (Test-Docker @('run', '--rm', '--network', "pilot-$c", '-e', 'PGPASSWORD', '-e', 'PGCONNECT_TIMEOUT=10', '--entrypoint', 'psql', 'postgres:16.16@sha256:30551eca978d79ccf49d037bc297b5670baea61a04ceca42ff7fd3100163d3ce', '-X', '-h', $pgHost, '-p', $pgPort, '-U', 'pilotuser', '-d', $pgDb, '-v', 'ON_ERROR_STOP=1', '-c', 'SELECT 1'))) { throw "PostgreSQL контура ${c}: проверьте доступ pilotuser к базе $pgDb по адресу ${pgHost}:$pgPort." }
        } finally { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }
        Write-Host "  PostgreSQL $c доступен"
    }
    if ($c -notin $localS3) {
        $endpoint = $cfg["${prefix}_EXTERNAL_S3_URL"]; $bucket = $cfg["${prefix}_EXTERNAL_S3_BUCKET"]
        $uri = $null
        if (-not [uri]::TryCreate($endpoint, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -notin @('http', 'https') -or -not $uri.Host -or $uri.UserInfo -or $uri.AbsolutePath -ne '/' -or $uri.Query -or $uri.Fragment -or $bucket -notmatch '^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$') { throw "Некорректные адрес или бакет внешнего S3 для $c." }
        $s3User = $cfg["${prefix}_S3_ACCESS_KEY"]; $s3Secret = $cfg["${prefix}_S3_SECRET_KEY"]
        if (-not $s3User -or -not $s3Secret) { throw "Укажите ${prefix}_S3_ACCESS_KEY и ${prefix}_S3_SECRET_KEY для внешнего S3." }
        $env:RCU = $s3User; $env:RCP = $s3Secret
        $env:RC_ENDPOINT = $endpoint; $env:RC_BUCKET = $bucket
        try {
            $probe = 'rc alias set m "$RC_ENDPOINT" "$RCU" "$RCP" >/dev/null && rc ls "m/$RC_BUCKET" >/dev/null'
            if ($PSVersionTable.PSVersion.Major -lt 7) { $probe = $probe.Replace('"', '\"') }
            if (-not (Test-Docker @('run', '--rm', '--network', "pilot-$c", '-e', 'RCU', '-e', 'RCP', '-e', 'RC_ENDPOINT', '-e', 'RC_BUCKET', '-e', 'HOME=/tmp', '--entrypoint', 'sh', $rustfsImage, '-c', $probe))) { throw "S3 контура ${c}: проверьте доступ к бакету $bucket." }
        } finally { Remove-Item Env:RCU, Env:RCP, Env:RC_ENDPOINT, Env:RC_BUCKET -ErrorAction SilentlyContinue }
        Write-Host "  S3 $c доступен"
    }
}

# The release images are already loaded. Start the two applications after their backup files are in place.
if ($ReleaseInstall -and $isAdmin) {
    Step 'PilotProAdmin'
    Run docker compose up -d --pull never --wait --wait-timeout 90 proadmin
    Step 'PilotMigration'
    Run docker compose up -d --pull never --wait --wait-timeout 90 migration
}

# Не «compose pull --policy missing»: он всё равно лезет в реестр за нашими образами и падает.
if ($restore) {
    # Контейнеры и тома создаём, не запуская: том надо наполнить до старта Grafana, а базы - до старта Pilot-Server.
    Step "Скачивание недостающих образов (Pilot-Server, PostgreSQL, Grafana…)"
    Retry { Run docker compose up --no-start --pull missing }

    if ($isMetrics) {
        $tarImage = Run docker compose config --images | Where-Object { $_ -match 'grafana/grafana' } | Select-Object -First 1
        foreach ($part in 'grafana', 'loki', 'prometheus') {
            $file = "grafana_${part}_data.tgz"
            $archive = "$snap/$part/$file"
            if (-not (Test-Path -LiteralPath $archive)) { continue }
            Step "Мониторинг из бэкапа: $part"
            $volume = "${project}_${part}_data"
            $existing = Run docker run --rm --user 0 --entrypoint sh -v "${volume}:/v:ro" $tarImage -c 'ls -A /v'
            if ($existing) { throw "В томе $volume уже есть данные. Для восстановления нужен новый стенд с пустыми томами." }
            # Docker Desktop может не видеть диск, с которого читается резервная копия.
            $staging = "$root/.restore-$part"
            New-Item -ItemType Directory -Force $staging | Out-Null
            $copy = Join-Path $staging $file
            try {
                Copy-Item -LiteralPath $archive -Destination $copy
                Run docker run --rm --user 0 --entrypoint sh -v "${volume}:/v" -v "${staging}:/b:ro" $tarImage -c "tar tzf /b/$file > /dev/null && tar xzf /b/$file -C /v"
            } finally { Remove-Item -LiteralPath $copy -Force -ErrorAction SilentlyContinue }
        }
    }

    # Файлы S3: архив тома rustfs\rustfs_<контур>.tgz (тот же формат у бэкапа и у data\ комплекта). До старта pilot-server и до создания бакета.
    foreach ($c in $myContours) {
        $rfile = "rustfs_$c.tgz"
        if (-not (Test-Path "$snap\rustfs\$rfile")) { continue }
        Step "Файлы RustFS ($c) из бэкапа"
        $rvol = "${project}_rustfs_$c"
        # Образ RustFS уже скачан предыдущим шагом; в нём есть tar (busybox). Пустым считается том только со служебными .rustfs и .rustfs.sys (как в restore.ps1).
        $rnames = @(docker run --rm --user 0 --entrypoint sh -v "${rvol}:/v:ro" $rustfsImage -c 'ls -A /v')
        if ($LASTEXITCODE) { throw "Не удалось прочитать том $rvol" }
        if (@($rnames | Where-Object { $_ -and @('.rustfs', '.rustfs.sys') -notcontains "$_" }).Count) {
            throw "В томе $rvol уже есть данные RustFS, восстановление поверх них установщик не выполняет. Если они не нужны, удалите том (docker compose down; docker volume rm $rvol) и запустите установку снова; либо восстановите вручную: backup\restore.ps1 -Env $c -Parts rustfs -ConfirmOverwrite rustfs"
        }
        # Промежуточная копия: Docker Desktop не монтирует съёмные диски, откуда может идти восстановление.
        $staging = "$root/.restore-rustfs"
        New-Item -ItemType Directory -Force $staging | Out-Null
        try {
            Copy-Item "$snap\rustfs\$rfile" $staging
            if (docker ps -q --filter "name=^rustfs-$c`$") { Run docker stop "rustfs-$c" | Out-Null }   # compose up запустит снова
            # Архив проверяется до распаковки; владелец файлов (10001) берётся из архива.
            Run docker run --rm --user 0 --entrypoint sh -v "${rvol}:/v" -v "${staging}:/b:ro" $rustfsImage -c "tar tzf /b/$rfile > /dev/null && tar xzf /b/$rfile -C /v"
        }
        finally { Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue }
    }

    Step "Базы PostgreSQL из бэкапа"
    foreach ($c in $myContours) { Run docker compose up -d "postgres-$c" }
    foreach ($c in $myContours) {
        $pg = "postgres-$c"
        # По TCP: во время initdb сервер слушает только сокет, pg_isready по сокету соврёт.
        $n = 0; while ($true) { docker exec $pg pg_isready -h 127.0.0.1 -U postgres | Out-Null; if ($LASTEXITCODE -eq 0) { break }; if (++$n -gt 90) { throw "$pg не поднялся" }; Start-Sleep 2 }
        Run docker exec $pg rm -rf /tmp/restore
        Run docker cp "$snap\postgres\$c\." "${pg}:/tmp/restore"
        # Роли: «already exists» ожидаемо (pilotuser заводит init-скрипт), ALTER ROLE вернёт исходные пароли.
        Run docker exec $pg sh -c "psql -U postgres -q -f /tmp/restore/globals.sql 2>&1 | grep -v 'already exists' || true"
        foreach ($dump in Get-ChildItem "$snap\postgres\$c" -Filter '*.dump') {
            Run docker exec $pg sh -c "pg_restore -U postgres --clean --if-exists --create --exit-on-error -d postgres '/tmp/restore/$($dump.Name)' 2>&1"
            Write-Host "  $($c): $($dump.BaseName)"
        }
        docker exec $pg rm -rf /tmp/restore | Out-Null
    }
}

# --- роли PostgreSQL для метрик ---
# При первом старте роли metrics_writer и metrics_reader создаёт init-скрипт; на существующих данных (база создана по-старому, под pilotuser)
# и после восстановления из бэкапа скрипт приводит владельца и права. Идемпотентно. Пароли берутся из окружения контейнера
# (их задаёт compose из .env), в аргументы docker они не попадают.
function Initialize-MetricsRoles {
    if ($localPg -notcontains 'prod') { return }
    Retry { Run docker compose up -d --pull missing postgres-prod }
    # По TCP: во время initdb сервер слушает только сокет, pg_isready по сокету соврёт.
    $n = 0; while ($true) { docker exec postgres-prod pg_isready -h 127.0.0.1 -U postgres | Out-Null; if ($LASTEXITCODE -eq 0) { break }; if (++$n -gt 90) { throw "postgres-prod не поднялся за 3 минуты." }; Start-Sleep 2 }
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'   # psql пишет предупреждения в stderr
    try { $out = & docker exec postgres-prod bash /docker-entrypoint-initdb.d/02-metrics-roles.sh 2>&1 | ForEach-Object { "$_" }; $rc = $LASTEXITCODE } finally { $ErrorActionPreference = $prev }
    if ($rc) { throw "Не удалось создать роли метрик в postgres-prod: $($out -join ' ') Сборщики и Grafana без них работать не будут." }
}
Step "Роли PostgreSQL для метрик"
Initialize-MetricsRoles

# --- запуск: недостающие сторонние образы docker скачает из интернета ---
Step "Скачивание недостающих образов (Pilot-Server, PostgreSQL, Grafana…) и запуск"
Retry { Run docker compose up -d --pull missing --remove-orphans }
if ($localS3 -and -not (docker images -q $rustfsImage)) { Retry { Run docker pull -q $rustfsImage | Out-Null } }

# GitLab готов, когда запущен puma и отвечает /-/readiness; healthy у образа этого не гарантирует.
function Test-GitLabReady { Test-Docker @('exec', 'gitlab', 'sh', '-c', 'gitlab-ctl status puma 2>/dev/null | grep -q ''^run: puma'' && curl -fs http://localhost/-/readiness >/dev/null') }
# На медленной машине «gitlab-ctl stop» не дожидается остановки (timeout: run: puma … want down): повторяем, затем kill.
function Stop-GitLabService([string]$name) {
    for ($i = 0; $i -lt 3; $i++) {
        if (Test-Docker @('exec', 'gitlab', 'gitlab-ctl', 'stop', $name)) { return }
        if (Test-Docker @('exec', 'gitlab', 'sh', '-c', "gitlab-ctl status $name 2>/dev/null | grep -q '^down:'")) { return }
        Start-Sleep 10
    }
    Test-Docker @('exec', 'gitlab', 'gitlab-ctl', 'kill', $name) | Out-Null
    Start-Sleep 5
    if (-not (Test-Docker @('exec', 'gitlab', 'sh', '-c', "gitlab-ctl status $name 2>/dev/null | grep -q '^down:'"))) {
        throw "GitLab: сервис $name не останавливается (gitlab-ctl stop $name). Данные GitLab ещё не менялись, но базы Pilot уже восстановлены: смотрите docker logs gitlab, очистите DATA_ROOT и запустите установку снова."
    }
}
function Wait-GitLab {
    $n = 0
    while (-not (Test-GitLabReady)) {
        if (++$n -gt 180) { throw "GitLab не стал готов за 30 минут: смотрите docker logs gitlab." }
        Start-Sleep 10
    }
}
# Адрес группы: настройки админки на этой машине, затем снимок бэкапа, затем .env.
function Get-GroupUrl([string]$jsonKey, [string]$envKey) {
    foreach ($f in "$root/services/proadmin/settings.json", $(if ($snap) { "$snap/admin/proadmin-data/settings.json" })) {
        if ($f -and (Test-Path $f)) { $v = ([IO.File]::ReadAllText($f) | ConvertFrom-Json).$jsonKey; if ($v) { return "$v".Trim() } }
    }
    "$((Read-Env $envPath)[$envKey])"
}
# Адрес возврата OAuth админки: GITLAB_OAUTH_REDIRECT_URI, иначе ADMIN_PUBLIC_URL/GitLab/Callback, иначе пусто.
function Get-AdminCallback {
    $k = Read-Env $envPath
    if ($k.GITLAB_OAUTH_REDIRECT_URI) { return "$($k.GITLAB_OAUTH_REDIRECT_URI)".Trim() }
    if ($k.ADMIN_PUBLIC_URL) { return "$($k.ADMIN_PUBLIC_URL)".Trim().TrimEnd('/') + '/GitLab/Callback' }
    ''
}
# Шаг после восстановления или чистой установки GitLab: gitlab-post-restore.rb (регистрация, переменные групп, защита веток, nuget.config).
function Invoke-GitLabPost([string]$groups, [string]$reports, [string]$oldHost, [string]$newUrl, [string]$allowMissing) {
    Run docker cp "$PSScriptRoot\gitlab-post-restore.rb" gitlab:/tmp/post.rb | Out-Null
    # docker cp хранит права файла с хоста, а читает его пользователь git.
    Run docker exec gitlab chmod 644 /tmp/post.rb | Out-Null
    $k = Read-Env $envPath
    $clientId = "$($k.GITLAB_OAUTH_CLIENT_ID)"; $callback = Get-AdminCallback
    # Значения ключей идут только через окружение: в аргументы docker (и в список процессов) они не попадают.
    $env:DEPLOY_KEY_TEST = $k.MODULES_DEPLOY_KEY_TEST; $env:DEPLOY_KEY_STAGE = $k.MODULES_DEPLOY_KEY_STAGE; $env:DEPLOY_KEY_PROD = $k.MODULES_DEPLOY_KEY_PROD
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        & docker exec -e DEPLOY_KEY_TEST -e DEPLOY_KEY_STAGE -e DEPLOY_KEY_PROD -e "GROUP_URLS=$groups" -e "REPORTS_GROUP_URL=$reports" -e "OLD_HOST=$oldHost" -e "NEW_URL=$newUrl" -e "ALLOW_MISSING_GROUPS=$allowMissing" -e "OAUTH_CLIENT_ID=$clientId" -e "ADMIN_CALLBACK=$callback" gitlab gitlab-rails runner /tmp/post.rb 2>&1 | ForEach-Object { "$_" } | Write-Host
        $rc = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $prev
        $env:DEPLOY_KEY_TEST = $null; $env:DEPLOY_KEY_STAGE = $null; $env:DEPLOY_KEY_PROD = $null
        $null = Test-Docker @('exec', 'gitlab', 'rm', '-f', '/tmp/post.rb')
    }
    $rc
}
$gitlabWarn = $null
if ($restore) {
    $tar = Get-ChildItem "$root/gitlab/data/backups" -Filter '*_gitlab_backup.tar' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($tar) {
        Step "GitLab: ожидание готовности (до 30 минут)"
        Wait-GitLab
        Step "GitLab: восстановление бэкапа (5-15 минут)"
        $backup = $tar.Name -replace '_gitlab_backup\.tar$', ''
        Run docker stop gitlab-runner
        Run docker exec gitlab chown git:git "/var/opt/gitlab/backups/$($tar.Name)"
        Stop-GitLabService puma
        Stop-GitLabService sidekiq
        # Метка до «force=yes»: после прерванного восстановления повторное затёрло бы изменившиеся данные.
        # Ставится после остановки сервисов: сбой остановки данные GitLab ещё не трогает.
        New-Item -ItemType File -Force "$root/.restore-started" | Out-Null
        # Полный вывод - в журнал, на экран без тысяч строк ERROR от psql; код возврата берём из файла (в sh нет PIPESTATUS).
        $restoreCmd = '{ gitlab-backup restore BACKUP=$BACKUP_NAME force=yes; echo $? > /tmp/restore.rc; } 2>&1 | tee /var/log/gitlab/restore.log | grep -v -E ''^(ERROR|STATEMENT|DETAIL|HINT|CONTEXT):''; exit $(cat /tmp/restore.rc)'
        docker exec -e GITLAB_ASSUME_YES=1 -e "BACKUP_NAME=$backup" gitlab sh -c $restoreCmd
        if ($LASTEXITCODE) { throw "gitlab-backup restore завершился с кодом $LASTEXITCODE. Журнал: $root/gitlab/logs/restore.log. Очистите $root и запустите установку снова." }
        if (Test-Docker @('exec', 'gitlab', 'grep', '-q', 'There were errors in restoring the schema', '/var/log/gitlab/restore.log')) {
            $gitlabWarn = "GitLab сообщил об ошибках схемы при восстановлении (см. $root/gitlab/logs/restore.log): проверьте docker exec gitlab gitlab-rake gitlab:check SANITIZE=true."
        }
        $projects = ((docker exec gitlab gitlab-psql -d gitlabhq_production -tAc 'select count(*) from projects') -join '').Trim()
        if ($LASTEXITCODE -or $projects -notmatch '^\d+$') { throw "Не удалось прочитать число проектов GitLab после восстановления. Журнал: $root/gitlab/logs/restore.log. Очистите $root и запустите установку снова." }
        if ([int]$projects -le 0) { throw "После восстановления в GitLab нет проектов: данные из бэкапа не загрузились (GitLab ещё достраивал схему или бэкап пуст). Журнал: $root/gitlab/logs/restore.log. Очистите $root и запустите установку снова." }
        Write-Host "  проектов в GitLab: $projects"
        # После переноса репозитории могут оказаться у root, и Gitaly отклонит push («dubious ownership»).
        Run docker exec gitlab chown -R git:git /var/opt/gitlab/git-data/repositories
        # Нечистая остановка PostgreSQL посреди восстановления уводит GitLab в цикл падений: даём 10 минут.
        Step "GitLab: перезапуск и ожидание готовности (до 30 минут)"
        Run docker stop -t 600 gitlab
        Run docker start gitlab
        Wait-GitLab
        Run docker start gitlab-runner
        Remove-Item -Force $tar.FullName, "$root/.restore-started"
    }
    [IO.File]::WriteAllText("$root/.installed", "$snap`n$($manifest.created)`n")
}

# --- администраторы Pilot (один раз на контур) ---
Step "Администраторы Pilot-Server"
$login = if ($cfg.PILOT_ADMIN_LOGIN) { $cfg.PILOT_ADMIN_LOGIN } else { 'admin' }
foreach ($c in $myContours) {
    $marker = "$root/$c/pilot-server/settings/.admin-created"
    if (Test-Path $marker) { continue }
    docker exec "pilot-server-$c" ./Ascon.Pilot.Daemon --admin /usr/share/ascon/pilot-server/settings/settings.xml $login $cfg.PILOT_ADMIN_PASSWORD | Out-Null
    if ($LASTEXITCODE -eq 0) { New-Item -ItemType File $marker | Out-Null; docker restart "pilot-server-$c" | Out-Null; Write-Host "  $($c): $login" }
    else { Write-Warning "Не удалось создать администратора на pilot-server-$c" }
}

# Базы Pilot к этому моменту уже созданы: роли метрик закрываются и от них.
Initialize-MetricsRoles

# --- бакеты RustFS (при восстановлении уже есть - --ignore-existing) ---
Step "Бакеты RustFS"
foreach ($c in $localS3) {
    $u = $cfg["$($c.ToUpper())_MINIO_USER"];     if (-not $u) { $u = $cfg.MINIO_USER }
    $p = $cfg["$($c.ToUpper())_MINIO_PASSWORD"]; if (-not $p) { $p = $cfg.MINIO_PASSWORD }
    $LASTEXITCODE = 0
    if (Wait-RustFs $c) { New-RustFsBucket $c $u $p } else { $LASTEXITCODE = 1 }
    if ($LASTEXITCODE) { Write-Warning "Не удалось создать бакет в rustfs-$c (смотрите docker logs rustfs-${c})" }
}

New-Item -ItemType Directory -Force $root | Out-Null
if (-not (Test-Path "$root/.installed")) { [IO.File]::WriteAllText("$root/.installed", "чистая установка`n") }
Protect-Secret $envPath
Protect-Secret "$PSScriptRoot\PASSWORDS.md"

# --- ключи выкладки вручную: на машине admin без GitLab - всегда; с GitLab - только если шаг ниже пропущен или не удался ---
$postWarn = $null; $postFail = $false; $manualKeys = $false; $oauthDone = $false
if ($WithGitLab) {
    Step 'GitLab: регистрация, переменные групп, защита веток, адрес в nuget.config'
    $rc = 1
    try {
        $moduleGroups = (@(Get-GroupUrl 'ModulesGitLabGroupUrl' 'MODULES_GITLAB_GROUP_URL'; Get-GroupUrl 'ServerModulesGitLabGroupUrl' 'SERVER_MODULES_GITLAB_GROUP_URL') | Where-Object { $_ }) -join ','
        $reportsGroup = Get-GroupUrl 'ReportsGitLabGroupUrl' 'REPORTS_GITLAB_GROUP_URL'
        $oldLabel = if (Test-Path "$root/.gitlab-old-host") { ([IO.File]::ReadAllText("$root/.gitlab-old-host")).Trim() } else { '' }
        $k = Read-Env $envPath
        $glHost = if ($k.GITLAB_HOST_IP) { $k.GITLAB_HOST_IP } else { 'localhost' }; $glPort = if ($k.GITLAB_HTTP_PORT) { $k.GITLAB_HTTP_PORT } else { '8080' }
        if (-not $moduleGroups -or -not $k.MODULES_DEPLOY_KEY_TEST -or -not $k.MODULES_DEPLOY_KEY_STAGE -or -not $k.MODULES_DEPLOY_KEY_PROD) {
            $manualKeys = $true
            Write-Warning 'Нет ключей MODULES_DEPLOY_KEY_* в .env или адресов групп модулей в настройках админки: переменные групп и защита веток не заданы.'
        }
        Wait-GitLab
        # Чистая установка: группы из настроек админки в новом GitLab ещё могут не существовать.
        $allowMissing = if (-not $restore -and -not $oldLabel) { "1" } else { "0" }
        $rc = Invoke-GitLabPost $moduleGroups $reportsGroup $oldLabel "http://${glHost}:$glPort" $allowMissing
        # Адрес возврата OAuth шаг добавляет сам, если знает Application ID и адрес админки.
        $oauthDone = ($rc -ne 1) -and [bool]$k.GITLAB_OAUTH_CLIENT_ID -and [bool](Get-AdminCallback)
        if ($rc -eq 2) {
            # Код 2 - групп модулей ещё нет: не сбой, но переменные не заданы.
            $manualKeys = $true; Write-Warning "Групп модулей в GitLab ещё нет: переменные групп и защита веток не заданы (регистрация закрыта)."
        }
        if ($rc -eq 0 -and $oldLabel -and ($moduleGroups -or $reportsGroup)) { Remove-Item -Force "$root/.gitlab-old-host" }
    } catch { Write-Warning $_.Exception.Message }
    if ($rc -ne 0 -and $rc -ne 2) {
        $postFail = $true; $manualKeys = $true
        $postWarn = "Шаг GitLab после установки не выполнен или завершился с ошибкой (код $rc, подробности выше). Саморегистрация, переменные групп, защита веток и адрес в nuget.config могли не примениться; шаг идемпотентен, повторный запуск установки повторит его."
    }
} elseif ($isAdmin -and (Read-Env $envPath).MODULES_DEPLOY_KEY_PROD) { $manualKeys = $true }

Write-Host "`nГотово." -ForegroundColor Green
$pilotAt = @{ prod = 'localhost:5546 (myAdmin Web: http://localhost:5601), консоль RustFS http://localhost:9001'; test = 'localhost:5547, консоль RustFS http://localhost:9011'; stage = 'localhost:5549, консоль RustFS http://localhost:9021' }
if ($ReleaseInstall) {
    $cfg = Read-Env $envPath
    foreach ($c in $myContours) {
        $prefix = $c.ToUpperInvariant()
        $pilotAt[$c] = "http://localhost:$($cfg["${prefix}_PILOT_PORT"]), консоль RustFS http://localhost:$($cfg["${prefix}_S3_UI_PORT"])"
        if ($cfg["${prefix}_MYADMIN_PORT"]) { $pilotAt[$c] += ", myAdmin Web http://localhost:$($cfg["${prefix}_MYADMIN_PORT"])" }
    }
    Write-Host "Ключ первого входа: ADMIN_SETUP_KEY в $envPath."
}
foreach ($c in $myContours) { Write-Host "  Pilot $c    $($pilotAt[$c])" -ForegroundColor Green }
if ($isAdmin)   { Write-Host "  Админка     ${scheme}://localhost:$($cfg.ADMIN_PORT)" -ForegroundColor Green }
if ($isMetrics) { Write-Host "  Grafana     ${scheme}://localhost:$($cfg.GRAFANA_PORT)" -ForegroundColor Green }
if ($WithGitLab) { Write-Host "  GitLab                      http://$($cfg.GITLAB_HOST_IP):$($cfg.GITLAB_HTTP_PORT)" -ForegroundColor Green }
if ($generated) { Write-Host "Сгенерированы значения в ${envPath}: $($generated -join ', ')" -ForegroundColor Green }
if ($restore) {
    Write-Host "Данные восстановлены из $snap. Все пароли - в $envPath." -ForegroundColor Green
    if ($oldHost -and -not $GitLabHost) { Write-Warning "GitLab настроен на адрес исходной машины ($oldHost). Если у этого сервера другой IP, см. инструкцию по установке, раздел об установке с данными." }
    if ($GitLabHost -and -not $oauthDone) { Write-Warning "Redirect URI OAuth-приложения админки хранится в GitLab (Admin / Applications): укажите там адрес админки с путём /GitLab/Callback. Адрес админки для браузеров задаёт ADMIN_PUBLIC_URL в .env (если он задан на машине с GitLab вместе с GITLAB_OAUTH_CLIENT_ID, установка добавляет адрес сама)." }
} else {
    Write-Host "Дальше: лицензии в $root/<контур>/license/Pilot Server/License/, база Pilot и подключение GitLab - см. инструкцию по установке." -ForegroundColor Green
}
if ($gitlabWarn) { Write-Warning $gitlabWarn }
if ($postWarn) { Write-Warning $postWarn }
if ($manualKeys) { Write-Warning "Ключи выкладки из CI: задайте в переменных групп модулей GitLab DEPLOY_KEY_TEST (Masked), DEPLOY_KEY_STAGE и DEPLOY_KEY_PROD (Masked и Protected; ветки main и release/* защищены) значениями MODULES_DEPLOY_KEY_TEST, MODULES_DEPLOY_KEY_STAGE, MODULES_DEPLOY_KEY_PROD из .env. Старый общий ключ принимается только для test." }
if ($cfg.INTERNAL_BIND) {
    $ports = @()
    foreach ($c in $myContours) { $ports += @{ prod = '5433 9000 9001'; test = '5434 9010 9011'; stage = '5435 9020 9021' }[$c] -split ' ' }
    if ($isAdmin) { $ports += $(if ($cfg.MIGRATION_PORT) { $cfg.MIGRATION_PORT } else { '5320' }) }
    if ($isMetrics) { $ports += $(if ($cfg.PROMETHEUS_PORT) { $cfg.PROMETHEUS_PORT } else { '9090' }), '3100' }
    if ($active -contains 'agents') { $ports += '9100', '9101' }
    # Адреса других машин стенда (и этой): правила пускают на служебные порты только их.
    $peers = @(); $skipped = @()
    foreach ($k in 'PROD_HOST', 'TEST_HOST', 'STAGE_HOST', 'METRICS_HOST', 'ADMIN_HOST', 'GITLAB_HOST_IP') {
        $v = $cfg[$k]
        if (-not $v -or ($k -eq 'GITLAB_HOST_IP' -and $v -eq 'localhost')) { continue }
        if (Test-IPv4 $v) { $peers += $v } else { $skipped += "$k=$v" }
    }
    if ((Test-IPv4 $cfg.INTERNAL_BIND) -and $cfg.INTERNAL_BIND -ne '0.0.0.0') { $peers += $cfg.INTERNAL_BIND }
    $peers = @($peers | Select-Object -Unique)
    $fw = "$PSScriptRoot\firewall-windows.ps1"
    [IO.File]::WriteAllText($fw, (New-FirewallScript $cfg.INTERNAL_BIND $peers $ports), (New-Object Text.UTF8Encoding $true))
    Write-Warning "INTERNAL_BIND=$($cfg.INTERNAL_BIND): служебные порты слушают сеть без аутентификации: $($ports -join ' '). Правила брандмауэра, которые пускают на них только машины стенда, записаны в ${fw}: просмотрите и примените от имени администратора. Автоматически они не применяются."
    if ($skipped) { Write-Warning "В правила не вошли значения, которые не IP-адреса: $($skipped -join ' '). Имена хостов не резолвятся: задайте IP, иначе эти машины не получат доступа." }
} else {
    # Правила относятся к заданному INTERNAL_BIND: устаревший файл потом применили бы по ошибке.
    Remove-Item -Force "$PSScriptRoot\firewall-windows.ps1" -ErrorAction SilentlyContinue
}
if ($postFail) { exit 1 }
