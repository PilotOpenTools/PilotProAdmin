<#
  Самопроверка частей grafana, loki, prometheus, rustfs в backup.ps1 и restore.ps1 без стенда (Windows PowerShell 5.1):
  docker подменён скриптом docker.ps1 в PATH, тома и архивы эмулируются каталогами.
    powershell -NoProfile -ExecutionPolicy Bypass -File build\selftest\backup-selftest.ps1
  Проверяет: имена томов (по умолчанию cicd, из COMPOSE_PROJECT_NAME, из -Project, запасной вариант grafana_*), монтирование тома
  только для чтения, исключение plugins у Grafana, манифест, сбой tar (нет «копии», код 1), -Skip; в части admin - ключи админки
  и .env контуров (контур есть в DATA_ROOT, а .env нет - ошибка; контуров нет - не ошибка); проверку копии (-Check) и
  восстановление: пустой том, непустой том (отказ и -ConfirmOverwrite), нечитаемый архив не стирает том, остановка и запуск сервиса.
  Часть rustfs (тома Docker): имена томов (cicd_rustfs_<контур>, -Project, раскладка pilot-<контур>_rustfs_data), ro-монтирование,
  манифест (volumes.rustfs), -Check архивов, восстановление (пустой том, -ConfirmOverwrite rustfs, остановка и запуск
  rustfs-<контур>).
  Docker для этой самопроверки не нужен.
#>
$ErrorActionPreference = 'Stop'
$stackSrc = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$failed = 0
function Check($cond, $msg) { if ($cond) { Write-Host "  ok: $msg" } else { Write-Host "  НЕ ПРОЙДЕНО: $msg" -ForegroundColor Red; $script:failed++ } }

$tmp = Join-Path $env:TEMP ('backup-selftest-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$stack = Join-Path $tmp 'stack'; $bin = Join-Path $tmp 'bin'
$vols = Join-Path $tmp 'vols'; $running = Join-Path $tmp 'running'
New-Item -ItemType Directory $stack, "$stack\backup", $bin, $vols, $running | Out-Null
Copy-Item "$stackSrc\backup\backup.ps1", "$stackSrc\backup\restore.ps1", "$stackSrc\backup\monitoring.ps1" "$stack\backup"

# --- заглушка docker: журнал вызовов; тома и запущенные контейнеры - каталоги и файлы во временной папке ---
$stub = @'
$a = @($args)
Add-Content -Path $env:STUB_LOG -Value ('docker ' + ($a -join ' ')) -Encoding UTF8
function HostPath([string]$kind) {   # путь на хосте для -v <хост>:/b или -v <том>:/v
    for ($i = 0; $i -lt $a.Count - 1; $i++) {
        if ($a[$i] -eq '-v' -and $a[$i + 1] -match '^(?<h>.+?):/(?<c>[bv])(:ro)?$' -and $Matches.c -eq $kind) { return $Matches.h }
    }
    return $null
}
switch ($a[0]) {
    'volume' { if (Test-Path (Join-Path $env:STUB_VOLS $a[2])) { exit 0 } else { exit 1 } }
    'ps' {
        $n = ($a | Where-Object { $_ -like 'name=*' }) -replace '^name=\^?', '' -replace '\$$', ''
        if ($n -and (Test-Path (Join-Path $env:STUB_RUN $n))) { $n }
        exit 0
    }
    'stop' { Remove-Item (Join-Path $env:STUB_RUN $a[1]) -ErrorAction SilentlyContinue; exit 0 }
    'start' { New-Item -ItemType File -Force (Join-Path $env:STUB_RUN $a[1]) | Out-Null; exit 0 }
    'run' {
        $b = HostPath 'b'; $v = HostPath 'v'
        if (-not $b -and -not $v) { exit 0 }   # временный postgres проверки копии
        $v = if ($v) { Join-Path $env:STUB_VOLS $v }
        $last = 0; for ($i = 0; $i -lt $a.Count; $i++) { if ($a[$i] -eq '-v') { $last = $i + 2 } }
        $rest = @($a[($last + 1)..($a.Count - 1)])   # после образа
        $cmd = if ($rest[0] -eq 'sh') { $rest[2] } else { $rest -join ' ' }
        foreach ($seg in ($cmd -split ' && ')) {
            $seg = $seg.Trim()
            if ($seg -match '^tar czf /b/(\S+) ') {
                $f = Join-Path $b $Matches[1]
                $n = @(Get-ChildItem $v).Count
                Set-Content $f "ENTRIES=$n"
                if ($env:STUB_TAR_FAIL) { exit 1 }
            }
            elseif ($seg -match '^tar tzf /b/(\S+)( > /dev/null)?$') {
                if ($env:STUB_TAR_BAD) { exit 1 }
                $n = [int](((Get-Content (Join-Path $b $Matches[1]) -TotalCount 1) -replace 'ENTRIES=', ''))
                if ($env:STUB_TAR_EMPTY) { $n = 0 }
                if (-not $Matches[2] -and $n -gt 0) { 1..$n | ForEach-Object { "./f$_" } }
            }
            elseif ($seg -eq 'find /v -mindepth 1 -delete') { Get-ChildItem $v | Remove-Item -Recurse -Force }
            elseif ($seg -match '^tar xzf /b/(\S+) -C /v$') { Set-Content (Join-Path $v "restored-$($Matches[1]).txt") (Get-Content (Join-Path $b $Matches[1])) }
            elseif ($seg -eq 'ls -A /v') { Get-ChildItem $v -Force | ForEach-Object Name }
            else { Write-Error "заглушка docker: неизвестная команда '$seg'"; exit 2 }
        }
        exit 0
    }
    default { exit 0 }   # info, rm, exec, cp
}
'@
[IO.File]::WriteAllText("$bin\docker.ps1", $stub, (New-Object Text.UTF8Encoding $true))
# git: backup.ps1 в конце спрашивает коммит; без заглушки $LASTEXITCODE после неудачи git (папка не репозиторий) попал бы в код выхода обёртки -Command.
[IO.File]::WriteAllText("$bin\git.ps1", "'abc1234'`nexit 0`n")
# tar.exe Windows раньше GNU tar из Git Bash: тот принимает «C:\...» за удалённый хост (restore.ps1 -Check читает им архив GitLab).
$env:PATH = "$bin;$env:SystemRoot\System32;$env:PATH"
$env:STUB_LOG = "$tmp\docker.log"; $env:STUB_VOLS = $vols; $env:STUB_RUN = $running

function NewVolume([string]$name, [string[]]$files = @('data.db')) {
    $d = Join-Path $vols $name; New-Item -ItemType Directory -Force $d | Out-Null
    foreach ($f in $files) { Set-Content (Join-Path $d $f) 'x' }
}
function ResetVolumes { Get-ChildItem $vols | Remove-Item -Recurse -Force; Get-ChildItem $running | Remove-Item -Force; Set-Content $env:STUB_LOG '' }
function Run([string]$script, [string[]]$extra) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    # -Command, а не -File: массивы (-Skip a,b) через -File приходят одной строкой. Пути в $extra заключает в кавычки вызывающий (Q).
    $cmd = "& '$stack\backup\$script' " + ($extra -join ' ') + '; exit $LASTEXITCODE'
    $o = (& powershell -NoProfile -ExecutionPolicy Bypass -Command $cmd 2>&1 | ForEach-Object { "$_" }) -join "`n"
    $c = $LASTEXITCODE; $ErrorActionPreference = $prev
    [pscustomobject]@{ Out = $o; Code = $c }
}
function Backup([string]$name, [string[]]$extra = @()) {
    $root = Join-Path $tmp $name
    $skip = if ($extra -contains '-Skip') { @() } else { @('-Skip', 'postgres,pilot,rustfs,gitlab,admin,images') }
    # Своя папка данных: по умолчанию backup.ps1 смотрит в C:\Ascon-Docker, а на машине разработчика там настоящие контуры.
    $data = if ($extra -contains '-DataRoot') { @() } else { @('-DataRoot', (Q (Join-Path $tmp 'data-none'))) }
    $r = Run 'backup.ps1' (@('-Root', (Q $root), '-TarImage', 'alpine:test') + $data + $skip + $extra)
    $dir = Get-ChildItem $root -Directory | Select-Object -First 1
    $r | Add-Member Dir $dir.FullName; $r | Add-Member Manifest (Get-Content (Join-Path $dir.FullName 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json)
    $r
}
function Q([string]$p) { "'$p'" }
function Log { Get-Content $env:STUB_LOG -Raw }
$allVols = 'grafana_data', 'loki_data', 'prometheus_data'

try {
    Write-Host '== копия: проект по умолчанию (cicd)'
    ResetVolumes
    foreach ($v in $allVols) { NewVolume "cicd_$v" }
    NewVolume 'cicd_grafana_data' 'grafana.db', 'plugins'
    $r = Backup 'b1'
    $m = $r.Manifest
    Check ($r.Code -eq 0 -and $m.ok -eq $true -and $m.parts.grafana -eq 'ok' -and $m.parts.loki -eq 'ok' -and $m.parts.prometheus -eq 'ok') 'код 0, три части ok в манифесте'
    if ($r.Code) { Write-Host $r.Out }
    Check ($m.volumes.grafana -eq 'cicd_grafana_data' -and $m.volumes.loki -eq 'cicd_loki_data' -and $m.volumes.prometheus -eq 'cicd_prometheus_data') 'манифест называет тома'
    Check ((Test-Path "$($r.Dir)\grafana\grafana_grafana_data.tgz") -and (Test-Path "$($r.Dir)\loki\grafana_loki_data.tgz") -and (Test-Path "$($r.Dir)\prometheus\grafana_prometheus_data.tgz")) 'архивы лежат под именами, которые читает установщик (grafana_grafana_data.tgz)'
    Check (-not (Get-ChildItem $r.Dir -Recurse -Filter '*.tmp')) 'временных файлов не осталось'
    $log = Log
    Check (([regex]::Matches($log, '-v (\w+):/v:ro ')).Count -eq 3) 'тома монтируются только для чтения'
    Check (([regex]::Matches($log, '--exclude=\./plugins')).Count -eq 1 -and $log -match 'cicd_grafana_data:/v:ro [^\r\n]*--exclude=\./plugins') 'plugins исключён только у Grafana'
    Check ($log -match 'alpine:test sh -c tar czf /b/grafana_grafana_data\.tgz\.tmp -C /v .* && tar tzf /b/grafana_grafana_data\.tgz\.tmp') 'архив перечитывается после упаковки, образ берётся из -TarImage'

    Write-Host '== копия: имя проекта'
    ResetVolumes
    foreach ($v in $allVols) { NewVolume "lab_$v"; NewVolume "other_$v" }
    Set-Content "$stack\.env" "TZ=x`nCOMPOSE_PROJECT_NAME=lab`n"
    $r = Backup 'b2'
    Check ($r.Code -eq 0 -and $r.Manifest.volumes.loki -eq 'lab_loki_data') 'COMPOSE_PROJECT_NAME из .env'
    $r = Backup 'b3' @('-Project', 'other')
    Check ($r.Code -eq 0 -and $r.Manifest.volumes.prometheus -eq 'other_prometheus_data') '-Project важнее .env'
    Set-Content "$stack\.env" "# COMPOSE_PROJECT_NAME=lab`nTZ=x`n"
    ResetVolumes; foreach ($v in $allVols) { NewVolume "grafana_$v" }
    $r = Backup 'b4'
    Check ($r.Code -eq 0 -and $r.Manifest.volumes.grafana -eq 'grafana_grafana_data') 'проект не задан, томов cicd_* нет: раскладка по папкам (тома grafana_*)'
    ResetVolumes; foreach ($v in $allVols) { NewVolume "grafana_$v" }
    $r = Backup 'b5' @('-Project', 'cicd')
    Check ($r.Code -eq 1 -and $r.Manifest.ok -eq $false -and $r.Manifest.parts.grafana -like 'error*cicd_grafana_data*') 'проект задан явно: запасное имя не подставляется, ошибка называет том'
    Remove-Item "$stack\.env"

    Write-Host '== копия: сбои и -Skip'
    ResetVolumes; NewVolume 'cicd_grafana_data'; NewVolume 'cicd_prometheus_data'
    $r = Backup 'b6'
    Check ($r.Code -eq 1 -and $r.Manifest.ok -eq $false -and $r.Manifest.parts.loki -like 'error*cicd_loki_data*' -and $r.Manifest.parts.grafana -eq 'ok' -and $r.Manifest.parts.prometheus -eq 'ok') 'нет одного тома: ошибка только этой части, код 1, остальные части сняты'
    ResetVolumes; foreach ($v in $allVols) { NewVolume "cicd_$v" }
    $env:STUB_TAR_FAIL = '1'
    $r = Backup 'b7'
    Remove-Item Env:STUB_TAR_FAIL
    Check ($r.Code -eq 1 -and $r.Manifest.parts.grafana -like 'error*' -and -not (Test-Path "$($r.Dir)\grafana\grafana_grafana_data.tgz") -and -not (Get-ChildItem $r.Dir -Recurse -Filter '*.tmp')) 'сбой tar: часть с ошибкой, готового архива и обрывка нет'
    Set-Content $env:STUB_LOG ''
    $r = Backup 'b8' @('-Skip', 'postgres,pilot,rustfs,gitlab,admin,images,grafana,loki,prometheus')
    Check ($r.Code -eq 0 -and $r.Manifest.parts.grafana -eq 'skipped' -and $r.Manifest.parts.loki -eq 'skipped' -and (Log) -notmatch 'docker run') '-Skip grafana,loki,prometheus: ничего не запускается'

    Write-Host '== копия: данные админки с ключами'
    # Бэкап своей машины хранит ключи защиты данных админки (иначе после восстановления всех разлогинило бы); из комплекта их убирает build.ps1.
    ResetVolumes
    New-Item -ItemType Directory "$tmp\proadmin\data\keys", "$tmp\proadmin\migration\data" -Force | Out-Null
    Set-Content "$tmp\proadmin\data\keys\key-1.xml" '<key/>'; Set-Content "$tmp\proadmin\data\settings.json" '{}'
    $r = Backup 'b9' @('-Skip', 'postgres,pilot,rustfs,gitlab,images,grafana,loki,prometheus')
    Check ($r.Code -eq 0 -and $r.Manifest.parts.admin -eq 'ok' -and (Test-Path "$($r.Dir)\admin\proadmin-data\keys\key-1.xml") -and (Test-Path "$($r.Dir)\admin\proadmin-data\settings.json")) 'часть admin сохраняет папку ключей вместе с настройками'

    Write-Host '== копия: часть rustfs (тома Docker)'
    ResetVolumes
    $only = 'postgres,pilot,gitlab,admin,images,grafana,loki,prometheus'   # все части, кроме rustfs
    NewVolume 'cicd_rustfs_prod' '.rustfs.sys', 'bucket'; NewVolume 'cicd_rustfs_test' '.rustfs.sys', 'bucket'   # stage на этой машине нет
    $r = Backup 'd1' @('-Skip', $only)
    if ($r.Code) { Write-Host $r.Out }
    $m = $r.Manifest
    Check ($r.Code -eq 0 -and $m.parts.rustfs -eq 'ok' -and $m.volumes.rustfs.prod -eq 'cicd_rustfs_prod' -and $m.volumes.rustfs.test -eq 'cicd_rustfs_test' -and -not ($m.volumes.rustfs.PSObject.Properties.Name -contains 'stage')) 'rustfs: ok, volumes.rustfs.<контур> называет тома; контур без контейнера (stage) пропущен'
    Check ((Test-Path "$($r.Dir)\rustfs\rustfs_prod.tgz") -and (Test-Path "$($r.Dir)\rustfs\rustfs_test.tgz") -and -not (Test-Path "$($r.Dir)\rustfs\rustfs_stage.tgz") -and -not (Get-ChildItem $r.Dir -Recurse -Filter '*.tmp')) 'архивы rustfs\rustfs_<контур>.tgz лежат в копии, временных файлов нет'
    $log = Log
    Check ($log -match 'run --rm -v cicd_rustfs_prod:/v:ro -v [^\r\n]*rustfs:/b alpine:test sh -c tar czf /b/rustfs_prod\.tgz\.tmp -C /v \. && tar tzf /b/rustfs_prod\.tgz\.tmp' -and $log -notmatch 'cicd_rustfs_\w+:/v ') 'том rustfs монтируется только для чтения, образ из -TarImage, архив перечитывается после упаковки'
    Check ($log -notmatch 'docker (stop|start) rustfs') 'при копии контейнер rustfs не останавливается (том читается на лету)'
    New-Item -ItemType File "$running\rustfs-prod" | Out-Null   # контейнер контура есть
    $r = Backup 'd1b' @('-Skip', $only, '-Project', 'lab')
    Check ($r.Code -eq 1 -and $r.Manifest.parts.rustfs -like 'error*lab_rustfs_prod*') '-Project задан явно: том ищется как lab_rustfs_<контур>, запасные имена не подставляются, ошибка называет том'
    NewVolume 'lab_rustfs_prod'
    $r = Backup 'd1c' @('-Skip', $only, '-Project', 'lab')
    Check ($r.Code -eq 0 -and $r.Manifest.volumes.rustfs.prod -eq 'lab_rustfs_prod') '-Project: том lab_rustfs_prod'
    ResetVolumes; NewVolume 'pilot-prod_rustfs_data' '.rustfs.sys', 'bucket'
    $r = Backup 'd1d' @('-Skip', $only)
    Check ($r.Code -eq 0 -and $r.Manifest.volumes.rustfs.prod -eq 'pilot-prod_rustfs_data') 'проект не задан, томов cicd_* нет: раскладка по папкам (pilot-<контур>_rustfs_data)'
    ResetVolumes; NewVolume 'test_rustfs_data' '.rustfs.sys', 'bucket'
    $r = Backup 'd1e' @('-Skip', $only)
    Check ($r.Code -eq 0 -and $r.Manifest.volumes.rustfs.test -eq 'test_rustfs_data') 'запасное имя <контур>_rustfs_data'
    ResetVolumes; NewVolume 'cicd_rustfs_prod' '.rustfs.sys', 'bucket'
    $env:STUB_TAR_FAIL = '1'
    $r = Backup 'd1f' @('-Skip', $only)
    Remove-Item Env:STUB_TAR_FAIL
    Check ($r.Code -eq 1 -and $r.Manifest.parts.rustfs -like 'error*' -and -not (Test-Path "$($r.Dir)\rustfs\rustfs_prod.tgz") -and -not (Get-ChildItem $r.Dir -Recurse -Filter '*.tmp')) 'сбой tar: часть rustfs с ошибкой, готового архива и обрывка нет'
    ResetVolumes; NewVolume 'cicd_rustfs_test' '.rustfs.sys', 'bucket'; New-Item -ItemType File "$running\rustfs-prod" | Out-Null
    $r = Backup 'd1g' @('-Skip', $only)
    Check ($r.Code -eq 1 -and $r.Manifest.parts.rustfs -like 'error*prod*' -and $r.Manifest.ok -eq $false) 'контейнер rustfs-prod есть, а тома нет: ошибка части (не тихий пропуск)'
    ResetVolumes
    $r = Backup 'd1h' @('-Skip', $only)
    Check ($r.Code -eq 1 -and $r.Manifest.parts.rustfs -like 'error*ни один том*') 'нет ни одного тома RustFS: ошибка, а не пустая копия'
    Write-Host '== копия: .env контуров'
    # Контур есть на машине (каталог в DATA_ROOT), а его .env рядом со стеком нет: без него копию не восстановить - ошибка части admin.
    $dr = Join-Path $tmp 'data-test'; New-Item -ItemType Directory "$dr\test" -Force | Out-Null
    $r = Backup 'b10' @('-Skip', 'postgres,pilot,rustfs,gitlab,images,grafana,loki,prometheus', '-DataRoot', (Q $dr))
    Check ($r.Code -eq 1 -and $r.Manifest.ok -eq $false -and $r.Manifest.parts.admin -like 'error*test*') 'каталог контура есть, .env нет: часть admin с ошибкой, код 1, ошибка называет контур'
    # Тот же контур с .env: ошибки нет, .env в копии.
    New-Item -ItemType Directory "$stack\test" -Force | Out-Null; Set-Content "$stack\test\.env" 'PG_PASSWORD=x'
    $r = Backup 'b11' @('-Skip', 'postgres,pilot,rustfs,gitlab,images,grafana,loki,prometheus', '-DataRoot', (Q $dr))
    Check ($r.Code -eq 0 -and $r.Manifest.parts.admin -eq 'ok' -and (Test-Path "$($r.Dir)\admin\env\test\.env")) 'каталог контура и .env есть: часть admin ok, .env в копии'
    Remove-Item -Recurse -Force "$stack\test"
    # Контуров на машине нет (раздельная раскладка, машина admin): отсутствие их .env - не ошибка.
    $r = Backup 'b12' @('-Skip', 'postgres,pilot,rustfs,gitlab,images,grafana,loki,prometheus')
    Check ($r.Code -eq 0 -and $r.Manifest.parts.admin -eq 'ok') 'контуров на машине нет: .env не требуются, часть admin ok'

    Write-Host '== проверка копии (-Check)'
    ResetVolumes; foreach ($v in $allVols) { NewVolume "cicd_$v" }
    $good = Backup 'c1'
    # Остальное, что проверяет -Check: каталог postgres, архив GitLab с обязательными файлами и gitlab-secrets.json.
    $src = Join-Path $tmp 'glsrc'; New-Item -ItemType Directory "$src\db" | Out-Null
    Set-Content "$src\backup_information.yml" 'x'; Set-Content "$src\db\a.sql" 'x'; Set-Content "$src\repo.bundle" 'x'
    New-Item -ItemType Directory "$($good.Dir)\postgres", "$($good.Dir)\gitlab\config" | Out-Null
    & tar -cf "$($good.Dir)\gitlab\cicd_1_gitlab_backup.tar" -C $src .
    Set-Content "$($good.Dir)\gitlab\config\gitlab-secrets.json" '{}'
    $r = Run 'restore.ps1' @('-Check', '-From', (Q $good.Dir), '-TarImage', 'alpine:test', '-DataRoot', (Q "$tmp\nodata"))
    if ($r.Code) { Write-Host $r.Out }
    Check ($r.Code -eq 0 -and $r.Out -match 'grafana: архив читается' -and $r.Out -match 'loki: архив читается' -and $r.Out -match 'prometheus: архив читается' -and $r.Out -match 'ПРОВЕРКА: ok') 'исправная копия: три архива читаются, проверка ok'
    $env:STUB_TAR_EMPTY = '1'
    $r = Run 'restore.ps1' @('-Check', '-From', (Q $good.Dir), '-TarImage', 'alpine:test', '-DataRoot', (Q "$tmp\nodata"))
    Remove-Item Env:STUB_TAR_EMPTY
    Check ($r.Code -eq 1 -and $r.Out -match 'архив grafana_grafana_data\.tgz пуст') 'пустой архив: проблема, код 1'
    $env:STUB_TAR_BAD = '1'
    $r = Run 'restore.ps1' @('-Check', '-From', (Q $good.Dir), '-TarImage', 'alpine:test', '-DataRoot', (Q "$tmp\nodata"))
    Remove-Item Env:STUB_TAR_BAD
    Check ($r.Code -eq 1 -and $r.Out -match 'ОШИБКА loki: архив grafana_loki_data\.tgz не читается') 'нечитаемый архив: проблема, код 1'
    Remove-Item "$($good.Dir)\loki\grafana_loki_data.tgz"
    $r = Run 'restore.ps1' @('-Check', '-From', (Q $good.Dir), '-TarImage', 'alpine:test', '-DataRoot', (Q "$tmp\nodata"))
    Check ($r.Code -eq 1 -and $r.Out -match 'ОШИБКА loki: в манифесте ok, но нет grafana_loki_data\.tgz') 'часть ok в манифесте, а архива нет: проблема'
    $mf = Get-Content "$($good.Dir)\manifest.json" -Raw | ConvertFrom-Json
    $mf.parts.PSObject.Properties.Remove('loki'); $mf.parts.PSObject.Properties.Remove('prometheus')
    [IO.File]::WriteAllText("$($good.Dir)\manifest.json", ($mf | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
    $r = Run 'restore.ps1' @('-Check', '-From', (Q $good.Dir), '-TarImage', 'alpine:test', '-DataRoot', (Q "$tmp\nodata"))
    Check ($r.Code -eq 0 -and $r.Out -match 'grafana: архив читается' -and $r.Out -notmatch 'loki') 'старая копия (в манифесте нет loki, prometheus): они не проверяются'

    Write-Host '== проверка копии: архивы rustfs'
    New-Item -ItemType Directory "$($good.Dir)\rustfs" | Out-Null; Set-Content "$($good.Dir)\rustfs\rustfs_prod.tgz" 'ENTRIES=2'
    $mf = Get-Content "$($good.Dir)\manifest.json" -Raw | ConvertFrom-Json
    $mf | Add-Member -Force volumes ([pscustomobject]@{ rustfs = [pscustomobject]@{ prod = 'cicd_rustfs_prod'; test = 'cicd_rustfs_test' } })
    [IO.File]::WriteAllText("$($good.Dir)\manifest.json", ($mf | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
    $r = Run 'restore.ps1' @('-Check', '-From', (Q $good.Dir), '-TarImage', 'alpine:test', '-DataRoot', (Q "$tmp\nodata"))
    Check ($r.Code -eq 1 -and $r.Out -match 'rustfs prod: архив читается, записей 2' -and $r.Out -match 'ОШИБКА rustfs test: в манифесте том cicd_rustfs_test, но нет rustfs_test\.tgz') 'новая копия: архив prod читается; том test в манифесте, а архива нет: проблема'
    Set-Content "$($good.Dir)\rustfs\rustfs_test.tgz" 'ENTRIES=3'
    $r = Run 'restore.ps1' @('-Check', '-From', (Q $good.Dir), '-TarImage', 'alpine:test', '-DataRoot', (Q "$tmp\nodata"))
    Check ($r.Code -eq 0 -and $r.Out -match 'rustfs test: архив читается, записей 3' -and $r.Out -notmatch 'rustfs stage' -and $r.Out -match 'ПРОВЕРКА: ok') 'оба архива rustfs читаются, stage без архива и без записи в манифесте не проверяется'
    $env:STUB_TAR_EMPTY = '1'
    $r = Run 'restore.ps1' @('-Check', '-From', (Q $good.Dir), '-TarImage', 'alpine:test', '-DataRoot', (Q "$tmp\nodata"))
    Remove-Item Env:STUB_TAR_EMPTY
    Check ($r.Code -eq 1 -and $r.Out -match 'архив rustfs_prod\.tgz пуст') 'пустой архив rustfs: проблема, код 1'
    Set-Content $env:STUB_LOG ''
    $env:STUB_TAR_BAD = '1'
    $r = Run 'restore.ps1' @('-Check', '-From', (Q $good.Dir), '-TarImage', 'alpine:test', '-DataRoot', (Q "$tmp\nodata"))
    Remove-Item Env:STUB_TAR_BAD
    Check ($r.Code -eq 1 -and $r.Out -match 'ОШИБКА rustfs prod: архив rustfs_prod\.tgz не читается') 'нечитаемый архив rustfs: проблема, код 1'
    Check ((Log) -match 'run --rm -v [^\r\n]*rustfs:/b:ro alpine:test tar tzf /b/rustfs_prod\.tgz') 'проверка читает архив с каталогом копии, смонтированным только для чтения'
    Remove-Item "$($good.Dir)\rustfs" -Recurse -Force
    $mf = Get-Content "$($good.Dir)\manifest.json" -Raw | ConvertFrom-Json
    $mf.PSObject.Properties.Remove('volumes')
    [IO.File]::WriteAllText("$($good.Dir)\manifest.json", ($mf | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))

    Write-Host '== восстановление томов'
    ResetVolumes
    NewVolume 'cicd_grafana_data' 'grafana.db', 'plugins'; NewVolume 'cicd_loki_data'; NewVolume 'cicd_prometheus_data' 'a', 'b', 'c'
    $src = Backup 'r1'
    # Пустые тома и один запущенный сервис.
    foreach ($v in $allVols) { Get-ChildItem "$vols\cicd_$v" | Remove-Item -Recurse -Force }
    New-Item -ItemType File "$running\grafana" | Out-Null
    Set-Content $env:STUB_LOG ''
    $r = Run 'restore.ps1' @('-Parts', 'grafana,loki', '-From', (Q $src.Dir), '-TarImage', 'alpine:test')
    if ($r.Code) { Write-Host $r.Out }
    $log = Log
    Check ($r.Code -eq 0 -and (Test-Path "$vols\cicd_grafana_data\restored-grafana_grafana_data.tgz.txt") -and (Test-Path "$vols\cicd_loki_data\restored-grafana_loki_data.tgz.txt")) 'пустые тома: архивы распакованы, -Env не нужен'
    Check (-not (Test-Path "$vols\cicd_prometheus_data\restored-grafana_prometheus_data.tgz.txt") -and $log -notmatch 'find /v') 'часть prometheus не запрошена: не тронута; в пустом томе ничего не удаляется'
    Check ($log.IndexOf('docker stop grafana') -ge 0 -and $log.IndexOf('docker stop grafana') -lt $log.IndexOf('tar xzf /b/grafana_grafana_data.tgz') -and $log.IndexOf('tar xzf /b/grafana_grafana_data.tgz') -lt $log.IndexOf('docker start grafana') -and $log -notmatch 'docker (stop|start) loki') 'запущенный grafana остановлен на время записи и запущен снова; остановленный loki не трогается'
    Check ($log -match '-v cicd_grafana_data:/v -v [^\r\n]*grafana:/b:ro ') 'каталог копии монтируется только для чтения'

    Write-Host '== восстановление: непустой том, нечитаемый архив, ошибки'
    # Том уже непустой (после предыдущего шага).
    Set-Content "$vols\cicd_grafana_data\keep.txt" 'живые данные'
    Set-Content $env:STUB_LOG ''
    $r = Run 'restore.ps1' @('-Parts', 'grafana', '-From', (Q $src.Dir), '-TarImage', 'alpine:test')
    Check ($r.Code -ne 0 -and $r.Out -match 'не пуст' -and $r.Out -match '-ConfirmOverwrite grafana' -and (Test-Path "$vols\cicd_grafana_data\keep.txt") -and (Log) -notmatch 'tar xzf') 'непустой том: отказ с подсказкой, данные целы, распаковки не было'
    $env:STUB_TAR_BAD = '1'
    $r = Run 'restore.ps1' @('-Parts', 'grafana', '-ConfirmOverwrite', 'grafana', '-From', (Q $src.Dir), '-TarImage', 'alpine:test')
    Remove-Item Env:STUB_TAR_BAD
    Check ($r.Code -ne 0 -and (Test-Path "$vols\cicd_grafana_data\keep.txt")) 'нечитаемый архив при -ConfirmOverwrite: том не очищается (tar tzf идёт раньше find -delete)'
    Set-Content $env:STUB_LOG ''
    $r = Run 'restore.ps1' @('-Parts', 'grafana', '-ConfirmOverwrite', 'grafana', '-From', (Q $src.Dir), '-TarImage', 'alpine:test')
    $log = Log
    Check ($r.Code -eq 0 -and -not (Test-Path "$vols\cicd_grafana_data\keep.txt") -and (Test-Path "$vols\cicd_grafana_data\restored-grafana_grafana_data.tgz.txt") -and $log -match 'tar tzf /b/grafana_grafana_data\.tgz > /dev/null && find /v -mindepth 1 -delete && tar xzf') '-ConfirmOverwrite: архив проверен, том очищен и заполнен из копии'
    $r = Run 'restore.ps1' @('-Parts', 'loki', '-ConfirmOverwrite', 'grafana', '-From', (Q $src.Dir), '-TarImage', 'alpine:test')
    Check ($r.Code -ne 0 -and $r.Out -match 'не пуст') '-ConfirmOverwrite действует только на перечисленные части'
    Remove-Item "$($src.Dir)\prometheus\grafana_prometheus_data.tgz"
    $r = Run 'restore.ps1' @('-Parts', 'prometheus', '-From', (Q $src.Dir), '-TarImage', 'alpine:test')
    Check ($r.Code -ne 0 -and $r.Out -match 'В копии нет') 'в копии нет архива: ошибка'
    Remove-Item "$vols\cicd_loki_data" -Recurse -Force
    $r = Run 'restore.ps1' @('-Parts', 'loki', '-From', (Q $src.Dir), '-TarImage', 'alpine:test')
    Check ($r.Code -ne 0 -and $r.Out -match 'нет тома cicd_loki_data') 'нет тома: ошибка с именем тома'
    $r = Run 'restore.ps1' @('-Parts', 'postgres', '-From', (Q $src.Dir))
    Check ($r.Code -ne 0 -and $r.Out -match 'Укажите -Check или -Env') 'части контура без -Env: отказ, как раньше'
    $r = Run 'restore.ps1' @('-From', (Q $src.Dir))
    Check ($r.Code -ne 0 -and $r.Out -match 'Укажите -Check или -Env') 'без частей и без -Env: отказ, как раньше'

    Write-Host '== восстановление: часть rustfs (том Docker)'
    ResetVolumes
    NewVolume 'cicd_rustfs_test' '.rustfs.sys', 'bucket', 'obj.bin'
    $src = Backup 'e1' @('-Skip', $only)
    # Том как после старта стека: только служебный .rustfs.sys; rustfs и pilot-server запущены.
    Get-ChildItem "$vols\cicd_rustfs_test" | Remove-Item -Recurse -Force; New-Item -ItemType Directory "$vols\cicd_rustfs_test\.rustfs.sys" | Out-Null
    New-Item -ItemType File "$running\rustfs-test", "$running\pilot-server-test" | Out-Null
    Set-Content $env:STUB_LOG ''
    $r = Run 'restore.ps1' @('-Env', 'test', '-Parts', 'rustfs', '-From', (Q $src.Dir), '-TarImage', 'alpine:test')
    if ($r.Code) { Write-Host $r.Out }
    $log = Log
    Check ($r.Code -eq 0 -and (Test-Path "$vols\cicd_rustfs_test\restored-rustfs_test.tgz.txt")) 'том только с .rustfs.sys: восстановление без -ConfirmOverwrite, архив распакован в том'
    $iStop = $log.IndexOf('docker stop rustfs-test'); $iTar = $log.IndexOf('tar xzf /b/rustfs_test.tgz'); $iStart = $log.IndexOf('docker start rustfs-test')
    Check ($iStop -ge 0 -and $iStop -lt $iTar -and $iTar -lt $iStart -and $log -match 'docker stop pilot-server-test' -and $log -match 'docker start pilot-server-test') 'rustfs-<контур> и pilot-server остановлены на время записи и запущены снова'
    Check ($log -match '-v cicd_rustfs_test:/v -v [^\r\n]*rustfs:/b:ro ' -and $log -match 'tar tzf /b/rustfs_test\.tgz > /dev/null && find /v -mindepth 1 -delete && tar xzf') 'каталог копии монтируется только для чтения; служебный .rustfs.sys перед распаковкой очищается, архив проверен раньше очистки'
    # Остановленный rustfs не запускается сам; пустой том ничего не очищает.
    Remove-Item "$running\rustfs-test"; Get-ChildItem "$vols\cicd_rustfs_test" | Remove-Item -Recurse -Force
    Set-Content $env:STUB_LOG ''
    $r = Run 'restore.ps1' @('-Env', 'test', '-Parts', 'rustfs', '-From', (Q $src.Dir), '-TarImage', 'alpine:test')
    $log = Log
    Check ($r.Code -eq 0 -and $log -notmatch 'docker (stop|start) rustfs-test' -and $log -notmatch 'find /v') 'пустой том и остановленный rustfs: ничего не удаляется, контейнер не запускается'
    # Непустой том (данные бакетов).
    New-Item -ItemType File "$running\rustfs-test" | Out-Null
    Set-Content "$vols\cicd_rustfs_test\keep.bin" 'живые данные'
    Set-Content $env:STUB_LOG ''
    $r = Run 'restore.ps1' @('-Env', 'test', '-Parts', 'rustfs', '-From', (Q $src.Dir), '-TarImage', 'alpine:test')
    Check ($r.Code -ne 0 -and $r.Out -match 'не пуст' -and $r.Out -match '-ConfirmOverwrite rustfs' -and (Test-Path "$vols\cicd_rustfs_test\keep.bin") -and (Log) -notmatch 'docker (stop|start)' -and (Log) -notmatch 'tar xzf') 'непустой том: отказ с подсказкой -ConfirmOverwrite rustfs до остановки контейнеров, данные целы'
    $r = Run 'restore.ps1' @('-Env', 'test', '-Parts', 'rustfs', '-ConfirmOverwrite', 'grafana', '-From', (Q $src.Dir), '-TarImage', 'alpine:test')
    Check ($r.Code -ne 0 -and $r.Out -match 'не пуст' -and (Test-Path "$vols\cicd_rustfs_test\keep.bin")) '-ConfirmOverwrite другой части не разрешает перезапись rustfs'
    $env:STUB_TAR_BAD = '1'
    $r = Run 'restore.ps1' @('-Env', 'test', '-Parts', 'rustfs', '-ConfirmOverwrite', 'rustfs', '-From', (Q $src.Dir), '-TarImage', 'alpine:test')
    Remove-Item Env:STUB_TAR_BAD
    Check ($r.Code -ne 0 -and (Test-Path "$vols\cicd_rustfs_test\keep.bin") -and (Test-Path "$running\rustfs-test")) 'нечитаемый архив при -ConfirmOverwrite rustfs: том не очищается, rustfs запущен снова'
    Set-Content $env:STUB_LOG ''
    $r = Run 'restore.ps1' @('-Env', 'test', '-Parts', 'rustfs', '-ConfirmOverwrite', 'rustfs', '-From', (Q $src.Dir), '-TarImage', 'alpine:test')
    Check ($r.Code -eq 0 -and -not (Test-Path "$vols\cicd_rustfs_test\keep.bin") -and (Test-Path "$vols\cicd_rustfs_test\restored-rustfs_test.tgz.txt") -and (Log) -match 'find /v -mindepth 1 -delete && tar xzf') '-ConfirmOverwrite rustfs: том очищен и заполнен из копии'
    Rename-Item "$vols\cicd_rustfs_test" 'moved'
    $r = Run 'restore.ps1' @('-Env', 'test', '-Parts', 'rustfs', '-From', (Q $src.Dir), '-TarImage', 'alpine:test')
    Check ($r.Code -ne 0 -and $r.Out -match 'нет тома cicd_rustfs_test') 'нет тома: ошибка с именем тома'
    Rename-Item "$vols\moved" 'cicd_rustfs_test'
    Remove-Item "$($src.Dir)\rustfs\rustfs_test.tgz"
    $r = Run 'restore.ps1' @('-Env', 'test', '-Parts', 'rustfs', '-From', (Q $src.Dir), '-TarImage', 'alpine:test')
    Check ($r.Code -ne 0 -and $r.Out -match 'В копии нет') 'в копии нет архива rustfs: ошибка'
    $r = Run 'restore.ps1' @('-Parts', 'rustfs', '-From', (Q $src.Dir))
    Check ($r.Code -ne 0 -and $r.Out -match 'Укажите -Check или -Env') 'rustfs без -Env: отказ, как для других частей контура'
}
finally {
    $env:PATH = ($env:PATH -split ';' | Where-Object { $_ -ne $bin -and $_ -ne "$env:SystemRoot\System32" }) -join ';'
    Remove-Item Env:STUB_LOG, Env:STUB_VOLS, Env:STUB_RUN -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
Write-Host ''
if ($failed) { Write-Host "НЕ ПРОЙДЕНО: $failed" -ForegroundColor Red; exit 1 }
Write-Host 'ВСЕ ПРОВЕРКИ ПРОЙДЕНЫ' -ForegroundColor Green
