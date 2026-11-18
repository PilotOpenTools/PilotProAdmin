<#
  Проверка и восстановление бэкапа, сделанного backup.ps1.

  Проверка (ничего живого не трогает):
    .\backup\restore.ps1 -Check                        последний бэкап
    .\backup\restore.ps1 -Check -From D:\Backups\2026-11-02_0300
      Дампы Postgres восстанавливаются во временный контейнер postgres:16.16 (без портов и сетей): расхождение числа
      таблиц с живой базой - предупреждение (схема могла смениться), пустая база - проблема, число строк только печатается; архив GitLab
      и его секреты - на месте и читаются, бандлы репозиториев есть; файлы RustFS - архивы rustfs\rustfs_<контур>.tgz читаются и не пусты.
      Без -From берётся последняя копия с ok=true: неудачные копии пропускаются.

  Восстановление контура (останавливает его pilot-server/rustfs, заменяет данные):
    .\backup\restore.ps1 -Env test -Parts postgres,pilot,rustfs [-From ...]
      Часть rustfs - архив в том Docker RustFS контура (<проект>_rustfs_<контур>, проект как у backup.ps1/-Project); том должен
      существовать (создаёт стек). В томе только служебные .rustfs и .rustfs.sys - восстановление идёт сразу; есть данные бакетов -
      только при -ConfirmOverwrite rustfs (содержимое удаляется перед распаковкой). rustfs-<контур> на время записи остановлен.
      Права не задаются: tar от root сохраняет владельца файлов из архива (10001); проверено на RustFS 1.0.5 (Docker Desktop).
    .\backup\restore.ps1 -Env prod -Parts postgres -ConfirmProd prod
  Тома мониторинга (останавливает сервис, пока том заполняется; -Env не нужен):
    .\backup\restore.ps1 -Parts grafana,loki,prometheus [-From ...]
      Том должен существовать (создаёт стек) и быть пустым. Непустой том перезаписывается только при -ConfirmOverwrite <части>:
      его содержимое удаляется перед распаковкой. Имя тома - <проект>_grafana_data и т. д., проект - как у backup.ps1 (-Project).
  GitLab восстанавливается вручную - см. README.md.
#>
param(
    [switch]$Check,
    [string]$From,
    [string]$Root = 'D:\Backups',
    [ValidateSet('prod', 'test', 'stage')][string]$Env,
    [ValidateSet('postgres', 'pilot', 'rustfs', 'grafana', 'loki', 'prometheus')][string[]]$Parts = @('postgres', 'pilot', 'rustfs'),
    [string]$ConfirmProd,
    [ValidateSet('rustfs', 'grafana', 'loki', 'prometheus')][string[]]$ConfirmOverwrite = @(),
    [string]$DataRoot = 'C:\Ascon-Docker',
    [string]$Project,
    [string]$TarImage
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'monitoring.ps1')
if (-not $TarImage) { $TarImage = $tarImageDefault }
$composeProject = Get-ComposeProject $Project @((Join-Path (Split-Path $PSScriptRoot -Parent) '.env'), (Join-Path (Split-Path $PSScriptRoot -Parent) 'Install\.env'))

function Exec([string]$exe, [string[]]$argList) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        $out = & $exe @argList 2>&1 | ForEach-Object { "$_" }
        if ($LASTEXITCODE -ne 0) { $out | Write-Host; throw "$exe $($argList -join ' ') -> код $LASTEXITCODE" }
        return $out
    }
    finally { $ErrorActionPreference = $prev }
}

# Нативная команда, нужен её stdout (текст); stderr (например, «Unable to find image») в результат не попадает.
function ExecOut([string]$exe, [string[]]$argList) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        $out = & $exe @argList 2>$null
        if ($LASTEXITCODE -ne 0) { throw "$exe $($argList -join ' ') -> код $LASTEXITCODE" }
        return $out
    }
    finally { $ErrorActionPreference = $prev }
}

function CopyMirror([string]$from, [string]$to) {
    if (-not (Test-Path $from)) { Write-Host "  в бэкапе нет $from - пропуск"; return }
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        & robocopy $from $to /MIR /COPY:DAT /R:2 /W:2 /NP /NFL /NDL /XJ | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy $from -> код $LASTEXITCODE" }
    }
    finally { $ErrorActionPreference = $prev }
}

# Том RustFS контура: <проект>_rustfs_<контур> (compose установщика); имя проекта по умолчанию - ещё и раскладка по папкам
# (name: pilot-<контур>, ключ тома rustfs_data). Нет тома - ошибка. (Та же логика в backup.ps1.)
function Resolve-RustfsVolume([string]$contour, $project) {
    $names = @("$($project.Name)_rustfs_$contour")
    if (-not $project.Explicit) { $names += "pilot-${contour}_rustfs_data", "${contour}_rustfs_data" }
    foreach ($n in $names) { if (Test-DockerVolume $n) { return $n } }
    throw "нет тома $($names[0]) (проект compose '$($project.Name)'): задайте -Project или COMPOSE_PROJECT_NAME в .env; том создаёт стек (docker compose up)"
}

# Таблицы и строки базы (n_live_tup после ANALYZE) - одинаковый запрос для живой базы и восстановленной.
$statsSql = "analyze; select count(*) || ' таблиц, ' || coalesce(sum(n_live_tup),0) || ' строк' from pg_stat_user_tables"
# Образ закреплён так же, как в compose контуров.
$pgImage = 'postgres:16.16@sha256:30551eca978d79ccf49d037bc297b5670baea61a04ceca42ff7fd3100163d3ce'
function TableCount([string]$stats) { if ($stats -match '^(\d+) таблиц') { [int]$Matches[1] } else { -1 } }

if (-not $From) {
    # Только удачные копии: manifest.ok = true (неудачная копия может быть неполной).
    $From = Get-ChildItem $Root -Directory | Where-Object {
        $m = Join-Path $_.FullName 'manifest.json'
        (Test-Path $m) -and ((Get-Content $m -Raw -Encoding UTF8 | ConvertFrom-Json).ok -eq $true)
    } | Sort-Object Name | Select-Object -Last 1 -ExpandProperty FullName
    if (-not $From) { throw "В $Root нет удачных бэкапов (manifest.ok = true)" }
}
$manifest = Get-Content (Join-Path $From 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
Write-Host "Бэкап $From (создан $($manifest.created), ok=$($manifest.ok))" -ForegroundColor Cyan
if (-not $Check -and $manifest.ok -ne $true) { throw "Бэкап снят с ошибками (ok=false), восстанавливать его нельзя. Подробности - в backup.log рядом с manifest.json." }

if ($Check) {
    $problems = 0
    if ($manifest.ok -ne $true) { $problems++; Write-Host '  ОШИБКА: в manifest.json ok = false, копия снята с ошибками' -ForegroundColor Red }
    $tmp = 'restore-check'
    Exec docker @('rm', '-f', $tmp) | Out-Null
    Exec docker @('run', '-d', '--name', $tmp, '--network', 'none', '-e', 'POSTGRES_PASSWORD=check', $pgImage) | Out-Null
    try {
        for ($i = 0; $i -lt 30; $i++) {
            & docker exec $tmp pg_isready -U postgres 2>$null | Out-Null
            if ($LASTEXITCODE -eq 0) { break }; Start-Sleep 1
        }
        foreach ($dir in Get-ChildItem (Join-Path $From 'postgres') -Directory) {
            $e = $dir.Name
            Exec docker @('cp', (Join-Path $dir.FullName 'globals.sql'), "${tmp}:/tmp/globals.sql") | Out-Null
            Exec docker @('exec', $tmp, 'psql', '-q', '-U', 'postgres', '-f', '/tmp/globals.sql') | Out-Null
            foreach ($dump in Get-ChildItem $dir.FullName -Filter '*.dump') {
                $db = $dump.BaseName
                $as = "chk_${e}_$($db -replace '[^A-Za-z0-9_]', '_')"
                Exec docker @('cp', $dump.FullName, "${tmp}:/tmp/x.dump") | Out-Null
                Exec docker @('exec', $tmp, 'createdb', '-U', 'postgres', $as) | Out-Null
                Exec docker @('exec', $tmp, 'pg_restore', '-U', 'postgres', '--no-owner', '--exit-on-error', '-d', $as, '/tmp/x.dump') | Out-Null
                $restored = (Exec docker @('exec', $tmp, 'psql', '-U', 'postgres', '-d', $as, '-Atc', $statsSql)) | Select-Object -Last 1
                $live = '(контейнер не запущен)'
                try { $live = (Exec docker @('exec', "postgres-$e", 'psql', '-U', 'postgres', '-d', $db, '-Atc', $statsSql)) | Select-Object -Last 1 } catch { }
                Write-Host ("  postgres {0}/{1}: бэкап {2}; живая сейчас {3}" -f $e, $db, $restored, $live)
                $nb = TableCount $restored; $nl = TableCount $live
                if ($nb -le 0) { $problems++; Write-Host "    ОШИБКА: в восстановленной базе $db нет таблиц" -ForegroundColor Red }
                elseif ($nl -ge 0 -and $nb -ne $nl) { Write-Host "    ВНИМАНИЕ: число таблиц в $db расходится ($nb в бэкапе, $nl в живой базе): возможно, схема менялась после снятия копии (обновление Pilot)" -ForegroundColor Yellow }
            }
        }
    }
    catch { $problems++; Write-Host "  ОШИБКА postgres: $($_.Exception.Message)" -ForegroundColor Red }
    finally { Exec docker @('rm', '-f', $tmp) | Out-Null }

    $gl = Get-ChildItem (Join-Path $From 'gitlab') -Filter '*_gitlab_backup.tar' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $gl) { $problems++; Write-Host '  ОШИБКА gitlab: нет архива' -ForegroundColor Red }
    else {
        $list = & tar -tf $gl.FullName
        $secrets = Test-Path (Join-Path $From 'gitlab\config\gitlab-secrets.json')
        $hasInfo = [bool]($list | Where-Object { $_ -match 'backup_information.yml' })
        $hasDb = [bool]($list | Where-Object { $_ -match '^(\./)?db/' })
        $repos = @($list | Where-Object { $_ -match '\.bundle$' }).Count
        Write-Host ("  gitlab: {0:n1} МБ, backup_information={1}, db={2}, бандлов репозиториев={3}, gitlab-secrets.json={4}" -f ($gl.Length / 1MB), $hasInfo, $hasDb, $repos, $secrets)
        if (-not ($hasInfo -and $hasDb -and $secrets -and $repos -gt 0)) { $problems++; Write-Host '    ОШИБКА: в архиве GitLab не хватает backup_information.yml, db/, бандлов репозиториев или нет gitlab-secrets.json' -ForegroundColor Red }
    }

    # Тома мониторинга: архив читается и не пуст. Части, которых в копии нет (старые копии, -Skip), не проверяются.
    foreach ($part in $monitoring.Keys) {
        $m = $monitoring[$part]; $dir = Join-Path $From $part
        $inManifest = $manifest.parts -and ($manifest.parts.PSObject.Properties.Name -contains $part) -and $manifest.parts.$part -eq 'ok'
        if (-not (Test-Path (Join-Path $dir $m.File))) {
            if ($inManifest) { $problems++; Write-Host "  ОШИБКА ${part}: в манифесте ok, но нет $($m.File)" -ForegroundColor Red }
            continue
        }
        try {
            $entries = @(ExecOut docker @('run', '--rm', '-v', "${dir}:/b:ro", $TarImage, 'tar', 'tzf', "/b/$($m.File)") | Where-Object { "$_" }).Count
            Write-Host ("  {0}: архив читается, записей {1}" -f $part, $entries)
            if ($entries -eq 0) { $problems++; Write-Host "    ОШИБКА: архив $($m.File) пуст" -ForegroundColor Red }
        }
        catch { $problems++; Write-Host "  ОШИБКА ${part}: архив $($m.File) не читается ($($_.Exception.Message))" -ForegroundColor Red }
    }

    # Файлы хранилища: архивы томов rustfs\rustfs_<контур>.tgz читаются и не пусты; частей, которых в копии нет, не требуем.
    $rdir = Join-Path $From 'rustfs'
    $rvols = if ($manifest.PSObject.Properties.Name -contains 'volumes' -and $manifest.volumes -and ($manifest.volumes.PSObject.Properties.Name -contains 'rustfs')) { $manifest.volumes.rustfs } else { $null }
    foreach ($c in 'prod', 'test', 'stage') {
        $f = "rustfs_$c.tgz"
        $inManifest = $rvols -and ($rvols.PSObject.Properties.Name -contains $c)
        if (-not (Test-Path (Join-Path $rdir $f))) {
            if ($inManifest) { $problems++; Write-Host "  ОШИБКА rustfs ${c}: в манифесте том $($rvols.$c), но нет $f" -ForegroundColor Red }
            continue
        }
        try {
            $entries = @(ExecOut docker @('run', '--rm', '-v', "${rdir}:/b:ro", $TarImage, 'tar', 'tzf', "/b/$f") | Where-Object { "$_" }).Count
            Write-Host ("  rustfs {0}: архив читается, записей {1}" -f $c, $entries)
            if ($entries -eq 0) { $problems++; Write-Host "    ОШИБКА: архив $f пуст" -ForegroundColor Red }
        }
        catch { $problems++; Write-Host "  ОШИБКА rustfs ${c}: архив $f не читается ($($_.Exception.Message))" -ForegroundColor Red }
    }
    if ($problems) { Write-Host "ПРОВЕРКА: проблем $problems" -ForegroundColor Red; exit 1 }
    Write-Host 'ПРОВЕРКА: ok' -ForegroundColor Green
    exit 0
}

$contourParts = @($Parts | Where-Object { $_ -in 'postgres', 'pilot', 'rustfs' })
$volumeParts = @($Parts | Where-Object { $monitoring.Contains($_) })
if ($contourParts.Count -and -not $Env) { throw 'Укажите -Check или -Env <prod|test|stage> (для частей postgres, pilot, rustfs)' }
if ($contourParts.Count -and $Env -eq 'prod' -and $ConfirmProd -ne 'prod') { throw 'Восстановление прода: добавьте -ConfirmProd prod' }

# Том мониторинга: сервис остановлен, пока том заполняется; непустой том - только по -ConfirmOverwrite, перед записью архив проверяется.
foreach ($part in $volumeParts) {
    $m = $monitoring[$part]
    $dir = Join-Path $From $part
    if (-not (Test-Path (Join-Path $dir $m.File))) { throw "В копии нет $(Join-Path $dir $m.File)" }
    $vol = Resolve-MonitoringVolume $m.Volume $composeProject
    $busy = @(ExecOut docker @('run', '--rm', '-v', "${vol}:/v:ro", $TarImage, 'ls', '-A', '/v') | Where-Object { "$_" }).Count -gt 0
    if ($busy -and $ConfirmOverwrite -notcontains $part) { throw "Том $vol не пуст. Его содержимое будет удалено: добавьте -ConfirmOverwrite $part (или удалите том и создайте стек заново)" }
    $wasRunning = [bool](ExecOut docker @('ps', '--filter', "name=^$($m.Container)$", '--format', '{{.Names}}'))
    Write-Host "  ${part}: том $vol" -ForegroundColor Yellow
    if ($wasRunning) { Exec docker @('stop', $m.Container) | Out-Null }
    try {
        $clear = if ($busy) { 'find /v -mindepth 1 -delete && ' } else { '' }
        Exec docker @('run', '--rm', '-v', "${vol}:/v", '-v', "${dir}:/b:ro", $TarImage, 'sh', '-c', "tar tzf /b/$($m.File) > /dev/null && ${clear}tar xzf /b/$($m.File) -C /v") | Out-Null
    }
    finally { if ($wasRunning) { Exec docker @('start', $m.Container) | Out-Null } }
}
if (-not $contourParts.Count) { Write-Host "Готово: тома $($volumeParts -join ', ') восстановлены из $From" -ForegroundColor Green; exit 0 }

$server = "pilot-server-$Env"; $pg = "postgres-$Env"; $rustfs = "rustfs-$Env"
# Том RustFS: проверки до остановки pilot-server. Только служебные .rustfs и .rustfs.sys (их RustFS создаёт сам при первом старте) - том считается пустым (очищается без подтверждения).
if ($Parts -contains 'rustfs') {
    $rfile = "rustfs_$Env.tgz"; $rdir = Join-Path $From 'rustfs'
    if (-not (Test-Path (Join-Path $rdir $rfile))) { throw "В копии нет $(Join-Path $rdir $rfile)" }
    $rvol = Resolve-RustfsVolume $Env $composeProject
    $rnames = @(ExecOut docker @('run', '--rm', '-v', "${rvol}:/v:ro", $TarImage, 'ls', '-A', '/v') | Where-Object { "$_" })
    $rdata = @($rnames | Where-Object { @('.rustfs', '.rustfs.sys') -notcontains "$_" }).Count -gt 0
    if ($rdata -and $ConfirmOverwrite -notcontains 'rustfs') { throw "Том $rvol не пуст (есть данные бакетов). Его содержимое будет удалено: добавьте -ConfirmOverwrite rustfs" }
}
Write-Host "Останавливаю $server" -ForegroundColor Yellow
Exec docker @('stop', $server) | Out-Null
try {
    if ($Parts -contains 'postgres') {
        $dir = Join-Path $From "postgres\$Env"
        foreach ($dump in Get-ChildItem $dir -Filter '*.dump') {
            $db = $dump.BaseName
            Write-Host "  postgres: $db"
            Exec docker @('cp', $dump.FullName, "${pg}:/tmp/restore.dump") | Out-Null
            # Сборщики метрик держат подключения к pilot_metrics - их надо отключить, иначе DROP не пройдёт.
            Exec docker @('exec', $pg, 'psql', '-U', 'postgres', '-Atc', "select pg_terminate_backend(pid) from pg_stat_activity where datname = '$db' and pid <> pg_backend_pid()") | Out-Null
            Exec docker @('exec', $pg, 'pg_restore', '-U', 'postgres', '--clean', '--if-exists', '--create', '--exit-on-error', '-d', 'postgres', '/tmp/restore.dump') | Out-Null
            Exec docker @('exec', $pg, 'rm', '-f', '/tmp/restore.dump') | Out-Null
        }
        # Дамп мог быть снят до появления ролей метрик (владелец pilotuser), а базы Pilot пересозданы с открытым CONNECT:
        # скрипт ролей запускается один раз после всех баз (идемпотентно); только там, где есть база метрик.
        if (@(Get-ChildItem $dir -Filter '*.dump' | Where-Object { $_.BaseName -eq 'pilot_metrics' }).Count) { Exec docker @('exec', $pg, 'bash', '/docker-entrypoint-initdb.d/02-metrics-roles.sh') | Out-Null }
    }
    if ($Parts -contains 'pilot') {
        foreach ($sub in 'pilot-server', 'server-settings', 'license', 'databases') {
            Write-Host "  pilot: $sub"
            CopyMirror (Join-Path $From "pilot\$Env\$sub") (Join-Path $DataRoot "$Env\$sub")
        }
    }
    if ($Parts -contains 'rustfs') {
        Write-Host "  rustfs: том $rvol" -ForegroundColor Yellow
        $rWasRunning = [bool](ExecOut docker @('ps', '--filter', "name=^$rustfs$", '--format', '{{.Names}}'))
        if ($rWasRunning) { Exec docker @('stop', $rustfs) | Out-Null }
        try {
            # Архив проверяется до очистки: нечитаемый не должен стереть том.
            $clear = if ($rnames.Count) { 'find /v -mindepth 1 -delete && ' } else { '' }
            Exec docker @('run', '--rm', '-v', "${rvol}:/v", '-v', "${rdir}:/b:ro", $TarImage, 'sh', '-c', "tar tzf /b/$rfile > /dev/null && ${clear}tar xzf /b/$rfile -C /v") | Out-Null
        }
        finally { if ($rWasRunning) { Exec docker @('start', $rustfs) | Out-Null } }
    }
}
finally {
    Write-Host "Запускаю $server" -ForegroundColor Yellow
    Exec docker @('start', $server) | Out-Null
}
Write-Host "Готово: $Env восстановлен из $From ($($Parts -join ', '))" -ForegroundColor Green
