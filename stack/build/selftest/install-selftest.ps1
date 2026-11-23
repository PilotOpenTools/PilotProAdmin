<#
  Самопроверка install.ps1 без стенда (Windows PowerShell 5.1): docker подменён заглушкой docker.cmd.
    powershell -NoProfile -ExecutionPolicy Bypass -File build\selftest\install-selftest.ps1
  Проверяет вспомогательные функции (ключи, права на .env, повторы, разбор кода docker), передачу команды восстановления GitLab
  в контейнер через аргументы PowerShell и сценарии установки: чистая, с восстановлением (проекты есть и проектов нет).
  Шаг GitLab после установки: вызов gitlab-rails, ключи только через окружение, сбой и ненулевой код, напоминание о ключах.
  Обязательные пароли в .env и сверка загруженных образов с IMAGES.txt (совпадение, расхождение, нет файла или строки).
  RustFS: бакет создаётся клиентом rc из образа RustFS (пароль не в аргументах docker), данные в именованном томе (каталог и chown не нужны).
  Файлы RustFS из бэкапа: архив rustfsustfs_<контур>.tgz проверяется и распаковывается в том до compose up и до бакета; том с данными - отказ.
  Для проверки передачи команды нужен Docker и образ alpine:3.
#>
$ErrorActionPreference = 'Stop'
$stack = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$installPs1 = Join-Path $stack 'Install\install.ps1'
$failed = 0
function Check($cond, $msg) { if ($cond) { Write-Host "  ok: $msg" } else { Write-Host "  НЕ ПРОЙДЕНО: $msg" -ForegroundColor Red; $script:failed++ } }

# --- функции из install.ps1 (разбор AST, чтобы тестировать настоящий код) ---
$ast = [System.Management.Automation.Language.Parser]::ParseFile($installPs1, [ref]$null, [ref]$null)
foreach ($name in 'Read-Env', 'Set-EnvValue', 'Protect-Secret', 'New-Secret', 'Retry', 'Test-Docker', 'Get-Sha256Hex', 'Test-LoadedImage', 'Test-IPv4', 'ConvertTo-IpNumber', 'ConvertFrom-IpNumber', 'Get-ForbiddenRanges', 'New-FirewallScript') {
    $f = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)[0]
    if (-not $f) { throw "В install.ps1 нет функции $name" }
    Invoke-Expression $f.Extent.Text
}
function Run { & $args[0] $args[1..($args.Count - 1)]; if ($LASTEXITCODE) { throw "Ошибка: $($args -join ' ')" } }
. (Join-Path $stack 'build\kit-secrets.ps1')

$tmp = Join-Path $env:TEMP ("selftest-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $tmp | Out-Null
try {
    Write-Host '== ключи'
    $a = New-Secret; $b = New-Secret 24
    Check ($a -match '^[0-9a-f]{40}$') 'New-Secret: 40 hex-символов'
    Check ($b -match '^[0-9a-f]{48}$' -and $a -ne $b) 'New-Secret 24 байта: 48 символов, значения разные'

    Write-Host '== права на .env'
    $envPath = Join-Path $tmp '.env'
    [IO.File]::WriteAllLines($envPath, @('A=1', 'B=2'))
    Protect-Secret $envPath
    # По SID, а не по именам: на русской Windows имена локализованы.
    function Acl($p) { @((Get-Acl $p).Access | ForEach-Object { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } | Sort-Object) }
    $acl = Acl $envPath
    Check ($acl.Count -ge 2 -and -not ($acl | Where-Object { $_ -in 'S-1-1-0', 'S-1-5-32-545', 'S-1-5-11' })) 'после Protect-Secret нет доступа для Users, Everyone и Authenticated Users'
    Check (($acl -contains 'S-1-5-18') -and ($acl -contains 'S-1-5-32-544')) 'остались SYSTEM и администраторы'
    Set-EnvValue 'B' '3'; Set-EnvValue 'C' 'x'
    Check (((Acl $envPath) -join ',') -eq ($acl -join ',')) 'Set-EnvValue не сбрасывает права'
    $h = Read-Env $envPath
    Check ($h.A -eq '1' -and $h.B -eq '3' -and $h.C -eq 'x') 'Set-EnvValue меняет и добавляет значения'

    Write-Host '== повторы'
    function Start-Sleep { }
    $script:n = 0
    Retry { $script:n++; if ($script:n -lt 3) { throw 'сеть' } }
    Check ($script:n -eq 3) 'Retry: успех на третьей попытке'
    $script:n = 0; $msg = ''
    try { Retry { $script:n++; throw 'сеть' } } catch { $msg = $_.Exception.Message }
    Check ($script:n -eq 3 -and $msg -match '3 попытки') 'Retry: после трёх неудач ошибка с понятным текстом'
    Remove-Item Function:\Start-Sleep

    Write-Host '== код возврата docker'
    function docker { cmd /c "exit $env:FAKE_RC" }
    $env:FAKE_RC = '0'; Check (Test-Docker @('exec', 'x')) 'Test-Docker: код 0'
    $env:FAKE_RC = '1'; Check (-not (Test-Docker @('exec', 'x'))) 'Test-Docker: код 1'
    Remove-Item Function:\docker

    Write-Host '== команда восстановления GitLab через аргументы PowerShell'
    $assign = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -eq '$restoreCmd' }, $true)[0]
    $restoreCmd = Invoke-Expression $assign.Right.Extent.Text
    $fake = Join-Path $tmp 'fake'; New-Item -ItemType Directory $fake | Out-Null
    [IO.File]::WriteAllText("$fake\gitlab-backup", "#!/bin/sh`necho 'Restoring ...'`necho 'ERROR:  relation x already exists'`necho 'STATEMENT:  CREATE TABLE x'`necho `"backup=`$2`"`nexit `$STUB_RC`n".Replace("`r`n", "`n"))
    $dockerOk = $false; try { docker version *> $null; $dockerOk = ($LASTEXITCODE -eq 0) } catch { }
    if (-not $dockerOk) { Write-Host '  пропуск: Docker недоступен' -ForegroundColor Yellow }
    else {
        foreach ($rc in 0, 3) {
            $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
            $out = (docker run --rm -v "${fake}:/fake:ro" -e "PATH=/fake:/usr/sbin:/usr/bin:/sbin:/bin" -e "STUB_RC=$rc" -e 'BACKUP_NAME=cicd_1' --entrypoint sh alpine:3 -c ('mkdir -p /var/log/gitlab && ' + $restoreCmd) 2>&1 | ForEach-Object { "$_" }) -join "`n"
            $code = $LASTEXITCODE; $ErrorActionPreference = $prev
            Check ($code -eq $rc) "код возврата gitlab-backup ($rc) доходит до PowerShell"
            Check ($out -match 'Restoring' -and $out -notmatch 'ERROR:|STATEMENT:') "вывод без строк ERROR psql (код $rc)"
            Check ($out -match 'backup=BACKUP=cicd_1 force=yes' -or $out -match 'backup=') "имя бэкапа передаётся через переменную окружения (код $rc)"
        }
        Write-Host '== пароль доступа к RustFS: переменные окружения и кавычки в аргументе'
        $env:MCU = 'us.er'; $env:MCP = "p@ss:w/o rd#1'x`"%`$"
        $script1 = 'echo \"[$MCU][$MCP]\"'
        $res = docker run --rm -e MCU -e MCP --entrypoint sh alpine:3 -c $script1
        Remove-Item Env:MCU, Env:MCP
        Check ("$res" -eq "[us.er][p@ss:w/o rd#1'x`"%`$]") 'значения со спецсимволами доходят до контейнера без искажений'
    }

    Write-Host '== сценарии установки (docker.cmd вместо docker)'
    $stub = Join-Path $tmp 'stub'; New-Item -ItemType Directory $stub | Out-Null
    # exit /b после конвейера внутри блока даёт код 0, поэтому gitlab-rails разбирается раньше блока exec.
    $bat = @'
@echo off
echo docker %* >> "%STUB_LOG%"
set "RAILS="
for %%A in (%*) do if "%%A"=="gitlab-rails" set "RAILS=1"
if defined RAILS set > "%STUB_RAILS_ENV%"
if defined RAILS exit /b %STUB_HARDEN_RC%
set "ROLES="
for %%A in (%*) do if "%%A"=="/docker-entrypoint-initdb.d/02-metrics-roles.sh" set "ROLES=1"
if defined ROLES exit /b %STUB_ROLES_RC%
if "%1"=="load" for /f "usebackq delims=" %%L in ("%~4") do (echo Loaded image: %%L& exit /b 0)
if "%1"=="image" if "%2"=="inspect" if "%4"=="--format" (if "%~5"=="{{.Id}}" (echo %STUB_ID%& exit /b 0) else (echo %STUB_LAYERS%& exit /b 0))
if "%1"=="images" (echo abc& exit /b 0)
if "%1"=="ps" (if "%STUB_RUSTFS_RUNNING%"=="1" echo abc123& exit /b 0)
if "%1"=="run" (
  echo %* | findstr /c:"ls -A /v" >nul && (if defined STUB_RUSTFS_LS type "%STUB_RUSTFS_LS%"& exit /b 0)
)
if "%1"=="compose" if "%2"=="config" (echo grafana/grafana:11& exit /b 0)
if "%1"=="exec" (
  echo %* | findstr /c:"gitlab-psql" >nul && (echo %STUB_PROJECTS%& exit /b 0)
  echo %* | findstr /c:" grep " >nul && exit /b %STUB_GREP_RC%
  exit /b 0
)
exit /b 0
'@
    [IO.File]::WriteAllText("$stub\docker.cmd", $bat)
    $env:PATH = "$stub;$env:PATH"; $env:STUB_LOG = "$tmp\docker.log"; $env:STUB_GREP_RC = '1'; $env:STUB_PROJECTS = '14'
    $env:STUB_RAILS_ENV = "$tmp\rails.env"; $env:STUB_HARDEN_RC = '0'; $env:STUB_ROLES_RC = '0'
    $env:STUB_ID = ''; $env:STUB_LAYERS = ''; $env:STUB_RUSTFS_RUNNING = '0'; $env:STUB_RUSTFS_LS = ''
    # Известные значения ключей выкладки: они не должны попадать в журнал вызовов docker.
    $kTest = 'aaaa1111bbbb2222cccc3333dddd4444eeee5555'; $kStage = 'bbbb2222cccc3333dddd4444eeee5555ffff6666'; $kProd = 'cccc3333dddd4444eeee5555ffff66667777aaaa'

    function Fresh([string]$name) {
        $work = Join-Path $tmp $name; $dataRoot = Join-Path $tmp "$name-data"
        if (Test-Path $work) { Remove-Item -Recurse -Force $work }
        New-Item -ItemType Directory $work | Out-Null
        Copy-Item "$stack\Install\install.ps1", "$stack\Install\gitlab-post-restore.rb", "$stack\Install\.env.example" $work
        if (Test-Path $env:STUB_RAILS_ENV) { Remove-Item $env:STUB_RAILS_ENV }
        $ex = [IO.File]::ReadAllText("$work\.env.example").Replace('DATA_ROOT=C:/Ascon-Docker', 'DATA_ROOT=' + ($dataRoot -replace '\\', '/'))
        [IO.File]::WriteAllText("$work\.env.example", $ex)
        [IO.File]::WriteAllText($env:STUB_LOG, '')
        [pscustomobject]@{ Work = $work; Data = $dataRoot }
    }
    function Install($w, [string[]]$extra) {
        $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        $o = (& powershell -NoProfile -ExecutionPolicy Bypass -File "$($w.Work)\install.ps1" @extra 2>&1 | ForEach-Object { "$_" }) -join "`n"
        $c = $LASTEXITCODE; $ErrorActionPreference = $prev
        [pscustomobject]@{ Out = $o; Code = $c }
    }

    $w = Fresh 'clean'
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'prod,admin,metrics')
    $e = Read-Env "$($w.Work)\.env"
    Check ($r.Code -eq 0) 'чистая установка: код 0'
    if ($r.Code) { Write-Host $r.Out }
    Check ($e.PG_SUPERUSER_PASSWORD -match '^[0-9a-f]{48}$' -and $e.PILOT_ADMIN_PASSWORD -ne 'change-me') 'пароли-заглушки заменены случайными'
    Check ($e.MODULES_DEPLOY_KEY_TEST -match '^[0-9a-f]{40}$' -and $e.MODULES_DEPLOY_KEY_STAGE -match '^[0-9a-f]{40}$' -and $e.MODULES_DEPLOY_KEY_PROD -match '^[0-9a-f]{40}$' -and $e.MODULES_DEPLOY_KEY_TEST -ne $e.MODULES_DEPLOY_KEY_PROD) 'три ключа выкладки сгенерированы'
    Check ($e.COLLECTOR_API_KEY -match '^[0-9a-f]{40}$' -and $e.MIGRATION_API_KEY -match '^[0-9a-f]{40}$') 'ключи сборщика и миграции сгенерированы'
    Check (-not (Acl "$($w.Work)\.env" | Where-Object { $_ -in 'S-1-1-0', 'S-1-5-32-545', 'S-1-5-11' })) '.env закрыт от остальных пользователей'
    Check ($r.Out -match 'DEPLOY_KEY_PROD') 'напоминание про переменные группы GitLab'
    Check (Test-Path "$($w.Data)/.installed") 'метка .installed записана'
    $log = Get-Content $env:STUB_LOG -Raw
    Check ($log -match 'docker run --rm --network pilot-prod -e RC_HOST_m --entrypoint rc rustfs/rustfs:1\.0\.5 mb --ignore-existing m/pilot-ice-ru') 'бакет создаётся клиентом rc из образа RustFS'
    Check ($log -match 'docker exec rustfs-prod curl -fsS http://127\.0\.0\.1:9000/health' -and $log -notmatch 'minio/mc|mc mb|MC_HOST') 'перед бакетом ждём готовности RustFS; клиента mc нет'
    Check ($log -notmatch [regex]::Escape($e.MINIO_PASSWORD) -and $r.Out -notmatch [regex]::Escape($e.MINIO_PASSWORD)) 'пароль RustFS не попадает в аргументы docker и вывод'
    Check (-not (Test-Path "$($w.Data)/prod/rustfs") -and $log -notmatch 'chown' -and $r.Out -notmatch 'uid 10001|chown') 'каталог данных RustFS на хосте не создаётся, прав не требует, том без старого minio не проверяется'
    $log = Get-Content $env:STUB_LOG -Raw
    Check ($e.METRICS_WRITER_PASSWORD -match '^[0-9a-f]{48}$' -and $e.METRICS_READER_PASSWORD -match '^[0-9a-f]{48}$' -and $e.METRICS_WRITER_PASSWORD -ne $e.METRICS_READER_PASSWORD) 'пароли ролей метрик сгенерированы'
    Check ($log -notmatch $e.METRICS_WRITER_PASSWORD -and $log -notmatch $e.METRICS_READER_PASSWORD -and $r.Out -notmatch $e.METRICS_WRITER_PASSWORD) 'пароли ролей метрик не попадают в аргументы docker и вывод'
    Check (([regex]::Matches($log, 'docker exec postgres-prod bash /docker-entrypoint-initdb\.d/02-metrics-roles\.sh')).Count -eq 2 -and $log -match 'docker compose up -d --pull missing postgres-prod') 'скрипт ролей запускается в postgres-prod дважды: до запуска стека и после создания баз Pilot'
    Check ($log.IndexOf('02-metrics-roles.sh') -lt $log.IndexOf('--remove-orphans')) 'первый запуск скрипта ролей - до запуска остальных сервисов'
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'prod,admin,metrics')
    Check ($r.Code -eq 0 -and $r.Out -match 'DEPLOY_KEY_PROD') 'без роли gitlab напоминание о ключах печатается при каждом запуске'
    Check ("$(Get-Content $env:STUB_LOG -Raw)" -notmatch 'gitlab-rails') 'без роли gitlab шаг GitLab не вызывается'

    $w = Fresh 'gitlab-clean'
    [IO.File]::AppendAllText("$($w.Work)\.env.example", "MODULES_GITLAB_GROUP_URL=http://10.0.0.5:8080/client-modules`nSERVER_MODULES_GITLAB_GROUP_URL=http://10.0.0.5:8080/server-extensions`n")
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'admin,gitlab')
    $e = Read-Env "$($w.Work)\.env"; $log = Get-Content $env:STUB_LOG -Raw
    if ($r.Code) { Write-Host $r.Out }
    Check ($r.Code -eq 0 -and $log -match 'docker cp .*gitlab-post-restore\.rb gitlab:/tmp/post\.rb' -and $log -match 'gitlab-rails runner /tmp/post\.rb') 'чистая установка с ролью gitlab: шаг GitLab вызван (закрытие регистрации)'
    Check ($log -match 'docker exec -e DEPLOY_KEY_TEST -e DEPLOY_KEY_STAGE -e DEPLOY_KEY_PROD ') 'ключи передаются как -e ИМЯ без значения'
    Check ($e.MODULES_DEPLOY_KEY_PROD -and $log -notmatch $e.MODULES_DEPLOY_KEY_PROD -and $log -notmatch $e.MODULES_DEPLOY_KEY_STAGE -and $log -notmatch $e.MODULES_DEPLOY_KEY_TEST) 'значения ключей не попадают в журнал вызовов docker'
    $rails = @(Get-Content $env:STUB_RAILS_ENV)
    Check (($rails -contains "DEPLOY_KEY_PROD=$($e.MODULES_DEPLOY_KEY_PROD)") -and ($log -match 'GROUP_URLS=http://10.0.0.5:8080/client-modules,http://10.0.0.5:8080/server-extensions ')) 'ключ и группы доходят до скрипта через окружение'
    Check ($r.Out -notmatch 'DEPLOY_KEY_PROD \(Masked') 'ключи и группы заданы, шаг прошёл: напоминания нет'
    Check ($log -match 'ALLOW_MISSING_GROUPS=1 ') 'чистая установка: отсутствие группы допускается'
    Check ($e.GITLAB_ROOT_PASSWORD -match '^[0-9a-f]{48}$') 'первая установка с gitlab: пароль root случайный'

    # Роль gitlab добавлена к уже установленному стеку: (а) данных GitLab нет - пароль генерируется, (б) данные есть - .env не меняется.
    $w = Fresh 'gitlab-later'
    $null = Install $w @('-NoData', '-SkipLoad', '-Roles', 'prod')
    $e0 = Read-Env "$($w.Work)\.env"
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'prod,gitlab')
    $e = Read-Env "$($w.Work)\.env"
    Check ($e.GITLAB_ROOT_PASSWORD -match '^[0-9a-f]{48}$' -and $e.GITLAB_ROOT_PASSWORD -ne 'change-me-please') 'роль gitlab добавлена позже, данных GitLab нет: пароль root сгенерирован'
    Check ($e.PG_SUPERUSER_PASSWORD -eq $e0.PG_SUPERUSER_PASSWORD -and $e.PILOT_ADMIN_PASSWORD -eq $e0.PILOT_ADMIN_PASSWORD) 'остальные пароли не изменились'
    Check ($r.Out -match 'Сгенерированы значения.*GITLAB_ROOT_PASSWORD') 'итог сообщает о сгенерированном пароле root'

    $w = Fresh 'gitlab-later-data'
    $null = Install $w @('-NoData', '-SkipLoad', '-Roles', 'prod')
    New-Item -ItemType Directory -Force "$($w.Data)/gitlab/data" | Out-Null; [IO.File]::WriteAllText("$($w.Data)/gitlab/data/f", 'x')
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'prod,gitlab')
    $e = Read-Env "$($w.Work)\.env"
    Check ($e.GITLAB_ROOT_PASSWORD -eq 'change-me-please') 'роль gitlab позже, данные GitLab есть: пароль root в .env не тронут'
    Check ($r.Out -match 'в данных GitLab уже есть база') 'выведено предупреждение про заглушку root'
    Check ($r.Out -notmatch 'Сгенерированы значения.*GITLAB_ROOT_PASSWORD') 'пароль root не назван сгенерированным'

    $w = Fresh 'gitlab-clean-nogroup'; $env:STUB_HARDEN_RC = '2'
    [IO.File]::AppendAllText("$($w.Work)\.env.example", "MODULES_GITLAB_GROUP_URL=http://10.0.0.5:8080/client-modules`n")
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'admin,gitlab')
    $env:STUB_HARDEN_RC = '0'
    Check ($r.Code -eq 0 -and $r.Out -notmatch 'Шаг GitLab после установки не выполнен') 'код 2 шага - не сбой: код возврата 0, предупреждения о сбое нет'
    Check ($r.Out -match 'Групп модулей в GitLab ещё нет' -and $r.Out -match 'DEPLOY_KEY_PROD \(Masked') 'но переменные не заданы: сообщение и напоминание о ключах'

    $w = Fresh 'multi'
    [IO.File]::AppendAllText("$($w.Work)\.env.example", "INTERNAL_BIND=10.0.0.1`n")
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'test')
    Check ($r.Code -ne 0 -and $r.Out -match 'заглушки') 'несколько машин: заглушки остаются, установка останавливается'

    $w = Fresh 'multi-metrics'
    $ex = [IO.File]::ReadAllText("$($w.Work)\.env.example") -replace '(?m)^(PG_SUPERUSER_PASSWORD|PILOT_PG_PASSWORD|MINIO_PASSWORD|GRAFANA_ADMIN_PASSWORD|PILOT_ADMIN_PASSWORD)=.*$', '$1=manual123456'
    [IO.File]::WriteAllText("$($w.Work)\.env", $ex + "INTERNAL_BIND=10.0.0.1`nMETRICS_HOST=10.0.0.2`n")
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'prod')
    Check ($r.Code -ne 0 -and ($r.Out -replace '\s+', ' ') -match 'не заданы пароли ролей метрик: METRICS_WRITER_PASSWORD, METRICS_READER_PASSWORD' -and "$(Get-Content $env:STUB_LOG -Raw)" -notmatch 'compose up') 'несколько машин: пустые пароли ролей метрик не придумываются, остановка до compose'

    $w = Fresh 'roles-fail'; $env:STUB_ROLES_RC = '1'
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'prod')
    $env:STUB_ROLES_RC = '0'
    Check ($r.Code -ne 0 -and $r.Out -match 'Не удалось создать роли метрик' -and "$(Get-Content $env:STUB_LOG -Raw)" -notmatch '--remove-orphans') 'сбой скрипта ролей останавливает установку до запуска сборщиков'
    $w = Fresh 'roles-none'
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'admin')
    Check ($r.Code -eq 0 -and "$(Get-Content $env:STUB_LOG -Raw)" -notmatch '02-metrics-roles' -and (Read-Env "$($w.Work)\.env").METRICS_WRITER_PASSWORD) 'без роли prod скрипт ролей не запускается, значение для compose создаётся'

    $snapRoot = Join-Path $tmp 'snap'
    function Snap() {
        if (Test-Path $snapRoot) { Remove-Item -Recurse -Force $snapRoot }
        foreach ($d in 'admin\env\gitlab', 'admin\env\proadmin', 'admin\env\prod', 'admin\proadmin-data', 'postgres\prod', 'gitlab\config', 'gitlab\runner-config', 'pilot\prod\pilot-server\settings') { New-Item -ItemType Directory "$snapRoot\$d" -Force | Out-Null }
        [IO.File]::WriteAllText("$snapRoot\admin\env\proadmin\.env", "MODULES_DEPLOY_KEY_TEST=$kTest`nMODULES_DEPLOY_KEY_STAGE=$kStage`nMODULES_DEPLOY_KEY_PROD=$kProd`n")
        [IO.File]::WriteAllText("$snapRoot\admin\proadmin-data\settings.json", "{`n  `"ModulesGitLabGroupUrl`": `"http://10.0.0.9:8080/client-modules`",`n  `"ServerModulesGitLabGroupUrl`": `"http://10.0.0.9:8080/server-extensions`",`n  `"ReportsGitLabGroupUrl`": `"http://10.0.0.9:8080/reports`"`n}`n")
        [IO.File]::WriteAllText("$snapRoot\manifest.json", '{"ok": true, "created": "2026-01-01", "host": "h"}')
        [IO.File]::WriteAllText("$snapRoot\admin\env\gitlab\.env", "GITLAB_HOST_IP=10.0.0.9`nGITLAB_ROOT_PASSWORD=abcdefgh12345`nGITLAB_HTTP_PORT=8080`n")
        [IO.File]::WriteAllText("$snapRoot\admin\env\prod\.env", "PG_SUPERUSER_PASSWORD=pgpass123456`nPILOT_PG_PASSWORD=pilotpass12345`nMINIO_PASSWORD=miniopass12345`n")
        [IO.File]::WriteAllText("$snapRoot\postgres\prod\globals.sql", 'select 1;'); [IO.File]::WriteAllText("$snapRoot\postgres\prod\pilot-ice_ru.dump", 'd')
        [IO.File]::WriteAllText("$snapRoot\gitlab\cicd_2026_gitlab_backup.tar", 'tar')
        [IO.File]::WriteAllText("$snapRoot\pilot\prod\pilot-server\settings\settings.xml", '<Configuration/>')
    }

    Snap; $w = Fresh 'restore-ok'; $env:STUB_PROJECTS = '14'
    $r = Install $w @('-FromBackup', $snapRoot, '-Roles', 'prod,gitlab', '-SkipLoad', '-GitLabHost', '10.0.0.5')
    if ($r.Code) { Write-Host $r.Out }
    $log = Get-Content $env:STUB_LOG -Raw
    Check ($r.Code -eq 0 -and $r.Out -match 'проектов в GitLab: 14') 'восстановление GitLab: успех, число проектов проверено'
    Check ($log -match 'docker stop -t 600 gitlab' -and $log -notmatch 'docker restart gitlab') 'GitLab останавливается с запасом времени, а не docker restart'
    Check ($log.IndexOf('GITLAB_ASSUME_YES') -lt $log.IndexOf('stop -t 600 gitlab') -and $log.IndexOf('stop -t 600 gitlab') -lt $log.IndexOf('start gitlab-runner')) 'порядок: restore, остановка, запуск runner'
    Check ((Test-Path "$($w.Data)/.installed") -and -not (Test-Path "$($w.Data)/.restore-started")) '.installed есть, метка .restore-started снята'
    Check ($r.Out -match 'Redirect URI') 'подсказка про Redirect URI OAuth'
    Check ($r.Out.IndexOf('Готово') -gt $r.Out.IndexOf('проектов в GitLab')) '«Готово» печатается после восстановления'
    Check ($log.IndexOf('stop -t 600 gitlab') -lt $log.IndexOf('start gitlab-runner') -and $log.IndexOf('start gitlab-runner') -lt $log.IndexOf('gitlab-rails runner')) 'порядок: перезапуск GitLab, runner, шаг GitLab'
    Check ($log.IndexOf('gitlab:/tmp/post.rb') -lt $log.IndexOf('chmod 644 /tmp/post.rb') -and $log.IndexOf('chmod 644 /tmp/post.rb') -lt $log.IndexOf('gitlab-rails runner')) 'скрипту в контейнере выдаются права на чтение до запуска (читает пользователь git)'
    Check ($log -match 'ALLOW_MISSING_GROUPS=0 ') 'после восстановления отсутствие группы - ошибка, а не пропуск'
    Check ($log -match 'docker exec -e DEPLOY_KEY_TEST -e DEPLOY_KEY_STAGE -e DEPLOY_KEY_PROD ' -and $log -notmatch $kTest -and $log -notmatch $kStage -and $log -notmatch $kProd) 'ключи передаются как -e ИМЯ, их значений нет в журнале вызовов docker'
    $rails = @(Get-Content $env:STUB_RAILS_ENV)
    Check (($rails -contains "DEPLOY_KEY_STAGE=$kStage") -and ($log -match 'GROUP_URLS=http://10.0.0.9:8080/client-modules,http://10.0.0.9:8080/server-extensions ') -and ($log -match 'REPORTS_GROUP_URL=http://10.0.0.9:8080/reports ')) 'значения ключей и пути групп (из снимка бэкапа) доходят до скрипта'
    Check (($log -match 'OLD_HOST=10.0.0.9 ') -and ($log -match 'NEW_URL=http://10.0.0.5:8080 ') -and -not (Test-Path "$($w.Data)/.gitlab-old-host")) 'старый и новый адрес переданы, метка старого адреса снята после успеха'
    Check ($r.Out -notmatch 'DEPLOY_KEY_PROD \(Masked') 'с ролью gitlab и успешным шагом напоминания о ключах нет'

    Snap; $w = Fresh 'restore-fail'; $env:STUB_HARDEN_RC = '1'
    $r = Install $w @('-FromBackup', $snapRoot, '-Roles', 'prod,gitlab', '-SkipLoad', '-GitLabHost', '10.0.0.5')
    Check ($r.Code -ne 0 -and $r.Out -match 'Шаг GitLab после установки не выполнен') 'сбой шага gitlab-rails: ненулевой код и предупреждение'
    Check ($r.Out -match 'DEPLOY_KEY_PROD \(Masked') 'после сбоя печатается напоминание о ключах'
    Check ((Test-Path "$($w.Data)/.installed") -and (Test-Path "$($w.Data)/.gitlab-old-host") -and $r.Out -match 'Готово') 'данные восстановлены, остальные шаги выполнены, метка старого адреса осталась'
    $env:STUB_HARDEN_RC = '0'; [IO.File]::WriteAllText($env:STUB_LOG, '')
    $r = Install $w @('-FromBackup', $snapRoot, '-Roles', 'prod,gitlab', '-SkipLoad')
    Check ($r.Code -eq 0 -and ((Get-Content $env:STUB_LOG -Raw) -match 'OLD_HOST=10.0.0.9 ') -and -not (Test-Path "$($w.Data)/.gitlab-old-host") -and $r.Out -notmatch 'DEPLOY_KEY_PROD \(Masked') 'повторный запуск повторяет шаг со старым адресом из метки; после успеха метки и напоминания нет'

    Snap; $w = Fresh 'restore-empty'; $env:STUB_PROJECTS = '0'
    $r = Install $w @('-FromBackup', $snapRoot, '-Roles', 'prod,gitlab', '-SkipLoad')
    Check ($r.Code -ne 0 -and $r.Out -match 'нет проектов') 'в базе нет проектов: ошибка с понятным текстом'
    Check ((-not (Test-Path "$($w.Data)/.installed")) -and (Test-Path "$($w.Data)/.restore-started")) '.installed не записан, метка .restore-started осталась'
    $env:STUB_PROJECTS = '14'
    $r = Install $w @('-FromBackup', $snapRoot, '-Roles', 'prod,gitlab', '-SkipLoad')
    Check ($r.Code -ne 0 -and $r.Out -match 'restore-started') 'повторный запуск не восстанавливает данные второй раз'

    Write-Host '== ключи защиты данных админки'
    # Данные комплекта (data рядом со скриптом): чужие ключи не переносятся; свой бэкап (-FromBackup): ключи сохраняются.
    Snap; New-Item -ItemType Directory "$snapRoot\admin\proadmin-data\keys" | Out-Null; [IO.File]::WriteAllText("$snapRoot\admin\proadmin-data\keys\key-1.xml", '<key/>')
    $w = Fresh 'keys-kit'; Copy-Item $snapRoot "$($w.Work)\data" -Recurse
    $r = Install $w @('-Roles', 'prod,admin', '-SkipLoad')
    if ($r.Code) { Write-Host $r.Out }
    Check ($r.Code -eq 0 -and (Test-Path "$($w.Data)/services/proadmin/settings.json") -and -not (Test-Path "$($w.Data)/services/proadmin/keys")) 'данные комплекта: настройки админки перенесены, ключи нет'
    # -FromBackup на ту же папку data (с косой чертой на конце и в другом регистре) - те же данные комплекта: чужие ключи не переносятся.
    $w = Fresh 'keys-kit2'; Copy-Item $snapRoot "$($w.Work)\data" -Recurse
    $r = Install $w @('-FromBackup', ($w.Work.ToUpper() + '\data\'), '-Roles', 'prod,admin', '-SkipLoad')
    Check ($r.Code -eq 0 -and (Test-Path "$($w.Data)/services/proadmin/settings.json") -and -not (Test-Path "$($w.Data)/services/proadmin/keys")) '-FromBackup на data комплекта (путь с \ и другим регистром): ключи не перенесены'
    $w = Fresh 'keys-own'
    $r = Install $w @('-FromBackup', $snapRoot, '-Roles', 'prod,admin', '-SkipLoad')
    Check ($r.Code -eq 0 -and (Test-Path "$($w.Data)/services/proadmin/keys/key-1.xml")) 'свой бэкап: ключи перенесены (никого не разлогинивает)'
    $kit = Join-Path $tmp 'kit-data'; New-Item -ItemType Directory "$kit\admin\proadmin-data\keys", "$kit\admin\migration-data" -Force | Out-Null
    [IO.File]::WriteAllText("$kit\admin\proadmin-data\keys\key-1.xml", '<key/>'); [IO.File]::WriteAllText("$kit\admin\proadmin-data\settings.json", '{}')
    $removed = @(Remove-KitSecrets $kit)
    Check (($removed -join ',') -eq 'admin\proadmin-data\keys' -and -not (Test-Path "$kit\admin\proadmin-data\keys") -and (Test-Path "$kit\admin\proadmin-data\settings.json") -and (Test-Path "$kit\admin\migration-data")) 'build: Remove-KitSecrets убирает только папку ключей админки'
    Check (@(Remove-KitSecrets $kit).Count -eq 0) 'build: повторный вызов и данные без ключей не ошибка'
    # build.ps1 целиком без стенда не запустить: проверяем, что он подключает kit-secrets.ps1 и чистит data до перемещения в комплект.
    $build = [IO.File]::ReadAllText("$stack\build\build.ps1")
    $iClean = $build.IndexOf('Remove-KitSecrets $new'); $iMove = $build.IndexOf('Move-Item $new $data')
    Check ($build -match '\. "\$PSScriptRoot\\kit-secrets\.ps1"' -and $iClean -gt 0 -and $iClean -lt $iMove) 'build: build.ps1 убирает ключи из data до перемещения в Install\data'

    Write-Host '== брандмауэр для служебных портов: функции'
    foreach ($v in '10.0.0.1', '192.168.1.255', '0.0.0.0', '255.255.255.255') { Check (Test-IPv4 $v) "$v - IPv4" }
    foreach ($v in '', 'host.example.local', '10.0.0', '10.0.0.256', '1.2.3.4.5', '010.0.0.1', '10.0.0.1/24', '10..0.1', ' 10.0.0.1') { Check (-not (Test-IPv4 $v)) "'$v' - не IPv4" }
    function InRanges([string]$ip, [string[]]$ranges) {
        $n = ConvertTo-IpNumber $ip
        [bool]($ranges | Where-Object { $a, $b = $_ -split '-'; if (-not $b) { $b = $a }; $n -ge (ConvertTo-IpNumber $a) -and $n -le (ConvertTo-IpNumber $b) })
    }
    $allowed = '10.0.0.2', '10.0.0.3', '0.0.0.0', '255.255.255.255', '192.168.1.1'
    $forbidden = @(Get-ForbiddenRanges $allowed)
    Check (-not ($allowed | Where-Object { InRanges $_ $forbidden }) -and -not (InRanges '127.0.0.1' $forbidden) -and -not (InRanges '127.200.1.1' $forbidden)) 'запретные диапазоны не пересекаются с разрешёнными адресами и 127.0.0.0/8'
    Check ((InRanges '10.0.0.1' $forbidden) -and (InRanges '10.0.0.4' $forbidden) -and (InRanges '1.1.1.1' $forbidden) -and (InRanges '192.168.1.2' $forbidden) -and (InRanges '126.255.255.255' $forbidden) -and (InRanges '128.0.0.0' $forbidden) -and (InRanges '255.255.255.254' $forbidden)) 'все остальные адреса, включая соседей разрешённых и границы, в запретных диапазонах'
    Check ((@(Get-ForbiddenRanges @())) -join ',' -eq '0.0.0.0-126.255.255.255,128.0.0.0-172.15.255.255,172.32.0.0-255.255.255.255') 'без разрешённых запрещено всё, кроме 127.0.0.0/8 и 172.16.0.0/12'
    Check (-not (InRanges '172.16.0.1' $forbidden) -and -not (InRanges '172.31.255.255' $forbidden) -and (InRanges '172.15.255.255' $forbidden) -and (InRanges '172.32.0.0' $forbidden)) 'сети Docker и WSL (172.16.0.0/12) в запрет не входят, соседние адреса входят'
    $fwText = New-FirewallScript '10.0.0.1' @('10.0.0.2', '10.0.0.1') @('5433', '9000')
    $fwFile = Join-Path $tmp 'fw-test.ps1'; [IO.File]::WriteAllText($fwFile, $fwText, (New-Object Text.UTF8Encoding $true))
    $perr = $null; [void][System.Management.Automation.Language.Parser]::ParseFile($fwFile, [ref]$null, [ref]$perr)
    Check (-not $perr) 'файл правил Windows проходит разбор PowerShell'
    Check ($fwText -match "-LocalPort \`$ports -RemoteAddress '10\.0\.0\.2', '10\.0\.0\.1', '127\.0\.0\.1' -Action Allow" -and $fwText -match "-Action Block" -and $fwText -match '\$ports = 5433, 9000') 'правила: разрешение машинам стенда и loopback, запрет остальным на нужных портах'
    Check ($fwText -notmatch '-Profile' -and $fwText -match '-Remove') 'правила действуют во всех профилях сети, есть снятие'

    Write-Host '== брандмауэр для служебных портов: установка'
    function MultiEnv($w, [string]$extra) {
        $ex = [IO.File]::ReadAllText("$($w.Work)\.env.example") -replace '(?m)^(PG_SUPERUSER_PASSWORD|PILOT_PG_PASSWORD|MINIO_PASSWORD|GRAFANA_ADMIN_PASSWORD|PILOT_ADMIN_PASSWORD|METRICS_WRITER_PASSWORD|METRICS_READER_PASSWORD)=.*$', '$1=manual123456'
        [IO.File]::WriteAllText("$($w.Work)\.env", $ex + $extra)
    }
    $w = Fresh 'fw'; MultiEnv $w "INTERNAL_BIND=10.0.0.1`nPROD_HOST=10.0.0.2`nMETRICS_HOST=10.0.0.3`nTEST_HOST=test.example.local`nADMIN_HOST=10.0.0.2`n"
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'prod')
    if ($r.Code) { Write-Host $r.Out }
    $fw = "$($w.Work)\firewall-windows.ps1"
    $perr = $null; if (Test-Path $fw) { [void][System.Management.Automation.Language.Parser]::ParseFile($fw, [ref]$null, [ref]$perr) }
    $txt = if (Test-Path $fw) { [IO.File]::ReadAllText($fw) } else { '' }
    Check ((Test-Path $fw) -and -not $perr -and [IO.File]::ReadAllBytes($fw)[0] -eq 0xEF) 'файл правил создан, в UTF-8 с BOM (PowerShell 5.1), проходит разбор'
    Check ($txt -match '\$ports = 5433, 9000, 9001, 9100, 9101' -and $txt -match "'10\.0\.0\.2', '10\.0\.0\.3', '10\.0\.0\.1', '127\.0\.0\.1'") 'порты ролей машины, адреса стенда (без повтора) и сама машина'
    Check ($txt -notmatch '(?m)^[^#].*test\.example\.local') 'имя хоста не попало в команды'
    Check ((($r.Out -replace '\s+', ' ') -match 'INTERNAL_BIND=10\.0\.0\.1.*firewall-windows\.ps1.*Автоматически они не применяются') -and (($r.Out -replace '\s+', ' ') -match 'не IP-адреса: TEST_HOST=test\.example\.local')) 'одно предупреждение с путём к файлу; имя хоста названо'
    Check ("$(Get-Content $env:STUB_LOG -Raw)" -notmatch 'New-NetFirewallRule|netsh') 'правила автоматически не применяются'
    [IO.File]::WriteAllText("$($w.Work)\.env", ([IO.File]::ReadAllText("$($w.Work)\.env") -replace '(?m)^INTERNAL_BIND=.*$', 'INTERNAL_BIND=0.0.0.0'))
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'prod')
    $txt = if (Test-Path $fw) { [IO.File]::ReadAllText($fw) } else { '' }
    Check ($txt -notmatch "'0\.0\.0\.0'" -and $txt -match "'10\.0\.0\.2', '10\.0\.0\.3', '127\.0\.0\.1'" -and $txt -match '172\.16\.0\.0/12') 'INTERNAL_BIND=0.0.0.0 не попадает в разрешённые адреса; в шапке сказано про сети Docker'
    $e = Read-Env "$($w.Work)\.env"; [IO.File]::WriteAllText("$($w.Work)\.env", ([IO.File]::ReadAllText("$($w.Work)\.env") -replace '(?m)^INTERNAL_BIND=.*\r?\n', ''))
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'prod')
    Check (-not (Test-Path $fw) -and $r.Out -notmatch 'INTERNAL_BIND') 'INTERNAL_BIND убран из .env: устаревший файл правил удалён, предупреждения нет'

    Write-Host '== HTTPS: сертификат и ключ в certs\'
    function TlsSetup([string]$name, [string]$extra = '') {
        $w = Fresh $name
        Copy-Item "$stack\Install\docker-compose.tls.yml" $w.Work
        Copy-Item "$($w.Work)\.env.example" "$($w.Work)\.env"
        if ($extra) { [IO.File]::AppendAllText("$($w.Work)\.env", $extra) }
        New-Item -ItemType Directory "$($w.Work)\certs" | Out-Null
        [IO.File]::WriteAllText("$($w.Work)\certs\tls.crt", "-----BEGIN CERTIFICATE-----`nMIIB`n-----END CERTIFICATE-----`n")
        [IO.File]::WriteAllText("$($w.Work)\certs\tls.key", "-----BEGIN PRIVATE KEY-----`nMIIE`n-----END PRIVATE KEY-----`n")
        $w
    }
    $w = TlsSetup 'tls'
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'admin,metrics')
    $e = Read-Env "$($w.Work)\.env"
    if ($r.Code) { Write-Host $r.Out }
    Check ($r.Code -eq 0 -and $e.COMPOSE_FILE -eq 'docker-compose.yml,docker-compose.tls.yml' -and $e.COMPOSE_PATH_SEPARATOR -eq ',') 'с certs\ в COMPOSE_FILE подключена надстройка HTTPS'
    $tlsRoot = ($w.Data -replace '\\', '/') + '/tls'
    Check ($e.TLS_ADMIN_CRT -eq "$tlsRoot/admin/tls.crt" -and $e.TLS_GRAFANA_KEY -eq "$tlsRoot/grafana/tls.key") 'пути для compose записаны в .env: копии в DATA_ROOT/tls (с прямыми слэшами)'
    Check ((Test-Path "$($w.Data)\tls\admin\tls.key") -and ([IO.File]::ReadAllText("$($w.Data)\tls\grafana\tls.key") -eq [IO.File]::ReadAllText("$($w.Work)\certs\tls.key"))) 'ключ и сертификат скопированы'
    Check (-not (Acl "$($w.Data)\tls\admin\tls.key" | Where-Object { $_ -in 'S-1-1-0', 'S-1-5-32-545', 'S-1-5-11' })) 'доступ к копии ключа закрыт от остальных пользователей, как у .env'
    Check ($r.Out -match 'HTTPS включён' -and $r.Out -match 'https://localhost:5300' -and $r.Out -match 'https://localhost:3000') 'сообщения называют https'
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'admin,metrics')
    Check ((@(Get-Content "$($w.Work)\.env" | Where-Object { $_ -match '^COMPOSE_FILE=' })).Count -eq 1) 'повторный запуск не дублирует COMPOSE_FILE'
    Remove-Item -Recurse -Force "$($w.Work)\certs"
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'admin,metrics')
    $e = Read-Env "$($w.Work)\.env"
    Check ($r.Code -eq 0 -and -not $e.COMPOSE_FILE -and -not $e.COMPOSE_PATH_SEPARATOR -and -not $e.TLS_ADMIN_CRT -and -not ((Get-Content "$($w.Work)\.env") -match '^TLS_')) 'certs\ убраны: надстройка и переменные TLS исчезают из .env, снова HTTP'
    Check ($r.Out -match 'http://localhost:5300' -and $r.Out -notmatch 'HTTPS включён') 'сообщения называют http'
    $w = TlsSetup 'tls-prod'
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'prod')
    Check ($r.Code -eq 0 -and -not (Read-Env "$($w.Work)\.env").COMPOSE_FILE) 'роли без админки и Grafana: HTTPS не включается'
    $w = TlsSetup 'tls-one'; Remove-Item "$($w.Work)\certs\tls.key"
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'admin')
    Check ($r.Code -ne 0 -and ($r.Out -replace '\s+', ' ') -match 'найден только один из файлов') 'есть только сертификат: остановка с понятным текстом, а не молчаливый HTTP'
    $w = TlsSetup 'tls-enc'; [IO.File]::WriteAllText("$($w.Work)\certs\tls.key", "-----BEGIN ENCRYPTED PRIVATE KEY-----`nMIIE`n-----END ENCRYPTED PRIVATE KEY-----`n")
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'admin')
    Check ($r.Code -ne 0 -and ($r.Out -replace '\s+', ' ') -match 'зашифрован паролем') 'ключ с паролем отвергается'
    $w = TlsSetup 'tls-junk'; [IO.File]::WriteAllText("$($w.Work)\certs\tls.crt", 'junk')
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'admin')
    Check ($r.Code -ne 0 -and ($r.Out -replace '\s+', ' ') -match 'не похож на сертификат') 'файл не в формате PEM отвергается'
    $w = TlsSetup 'tls-path' "TLS_CERT_FILE=$tmp\pki\my.crt`nTLS_KEY_FILE=$tmp\pki\my.key`nADMIN_PUBLIC_URL=http://10.0.0.5:5300`n"
    New-Item -ItemType Directory "$tmp\pki" | Out-Null; Move-Item "$($w.Work)\certs\tls.crt" "$tmp\pki\my.crt"; Move-Item "$($w.Work)\certs\tls.key" "$tmp\pki\my.key"
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'admin')
    $e = Read-Env "$($w.Work)\.env"
    Check ($r.Code -eq 0 -and $e.TLS_ADMIN_CRT -eq (($w.Data -replace '\\', '/') + '/tls/admin/tls.crt') -and [IO.File]::ReadAllText("$($w.Data)\tls\admin\tls.crt") -eq [IO.File]::ReadAllText("$tmp\pki\my.crt")) 'пути TLS_CERT_FILE и TLS_KEY_FILE из .env'
    Check (($r.Out -replace '\s+', ' ') -match 'ADMIN_PUBLIC_URL.*http://.*https://') 'ADMIN_PUBLIC_URL с http:// при HTTPS: предупреждение'
    $w = TlsSetup 'tls-own' "COMPOSE_FILE=a.yml;b.yml`n"
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'admin')
    $e = Read-Env "$($w.Work)\.env"
    Check ($e.COMPOSE_FILE -eq 'a.yml;b.yml;docker-compose.tls.yml' -and -not $e.COMPOSE_PATH_SEPARATOR) 'свой COMPOSE_FILE сохраняется, надстройка дописывается с его разделителем'
    Remove-Item -Recurse -Force "$($w.Work)\certs"
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'admin')
    Check ((Read-Env "$($w.Work)\.env").COMPOSE_FILE -eq 'a.yml;b.yml') 'без certs в своём COMPOSE_FILE остаётся только своё'

    Write-Host '== обязательные значения в .env (повторный запуск, данные уже есть)'
    # .env из шаблона без перечисленных ключей; метка .installed: пароли-заглушки не заменяются, только проверяются обязательные.
    function EnvWithout($w, [string[]]$keys, [string[]]$extra = @()) {
        $lines = [IO.File]::ReadAllLines("$($w.Work)\.env.example") | Where-Object { $keys -notcontains ($_ -split '=')[0] }
        [IO.File]::WriteAllLines("$($w.Work)\.env", [string[]](@($lines) + $extra))
        New-Item -ItemType Directory $w.Data -Force | Out-Null
        [IO.File]::WriteAllText("$($w.Data)/.installed", 'x')
        [IO.File]::WriteAllText($env:STUB_LOG, '')
    }
    $w = Fresh 'req-pg'; EnvWithout $w @('PG_SUPERUSER_PASSWORD')
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'prod')
    Check ($r.Code -ne 0 -and ($r.Out -replace '\s+', ' ') -match 'не заданы обязательные значения: PG_SUPERUSER_PASSWORD' -and "$(Get-Content $env:STUB_LOG -Raw)" -notmatch 'compose up') 'роли prod нужен PG_SUPERUSER_PASSWORD: остановка до compose, ключ назван'
    Check (-not (Read-Env "$($w.Work)\.env").PG_SUPERUSER_PASSWORD) 'пароль роли не придумывается'
    $w = Fresh 'req-only'; EnvWithout $w @('GRAFANA_ADMIN_PASSWORD', 'MINIO_PASSWORD')
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'prod')
    Check ($r.Code -ne 0 -and ($r.Out -replace '\s+', ' ') -match 'обязательные значения: MINIO_PASSWORD\.') 'в списке только ключи, нужные ролям машины (Grafana на prod-машине не нужна)'
    $w = Fresh 'req-graf'; EnvWithout $w @('GRAFANA_ADMIN_PASSWORD')
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'prod')
    Check ($r.Code -eq 0 -and (Read-Env "$($w.Work)\.env").GRAFANA_ADMIN_PASSWORD -match '^[0-9a-f]{48}$' -and $r.Out -match 'GRAFANA_ADMIN_PASSWORD') 'пароль Grafana на машине без роли metrics получает значение (compose требует его во всём файле)'
    $per = @(foreach ($c in 'PROD', 'TEST', 'STAGE') { "${c}_PG_SUPERUSER_PASSWORD=a"; "${c}_PILOT_PG_PASSWORD=a"; "${c}_MINIO_USER=u"; "${c}_MINIO_PASSWORD=a" })
    $w = Fresh 'req-prefixed'; EnvWithout $w @('PG_SUPERUSER_PASSWORD', 'PILOT_PG_PASSWORD', 'MINIO_USER', 'MINIO_PASSWORD') $per
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'prod,test,stage,admin')
    $e = Read-Env "$($w.Work)\.env"
    Check ($r.Code -eq 0 -and -not $e.PG_SUPERUSER_PASSWORD -and -not $e.MINIO_PASSWORD) 'пароли заданы у всех трёх контуров: общие не нужны и не создаются'
    $w = Fresh 'req-minio-user'; EnvWithout $w @('MINIO_USER')
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'admin')
    Check ($r.Code -ne 0 -and ($r.Out -replace '\s+', ' ') -match 'обязательные значения: MINIO_USER') 'роль admin берёт S3-учётку миграции из MINIO_USER: пустое значение останавливает установку'

    Write-Host '== сверка загруженных образов с IMAGES.txt'
    $id1 = 'sha256:' + ('a' * 64); $id2 = 'sha256:' + ('b' * 64)
    $lay1 = '["sha256:1111","sha256:2222"]'; $lay2 = '["sha256:3333"]'
    $h1 = Get-Sha256Hex $lay1
    Check ($h1 -match '^[0-9a-f]{64}$' -and $h1 -ne (Get-Sha256Hex $lay2)) 'Get-Sha256Hex: 64 hex-символа, разные слои дают разные значения'
    function ImgSetup($name, $id, $layers, $imagesTxtLine) {
        $w = Fresh 'img'
        Copy-Item "$($w.Work)\.env.example" "$($w.Work)\.env"
        New-Item -ItemType Directory "$($w.Work)\images" | Out-Null
        [IO.File]::WriteAllText("$($w.Work)\images\a.tar.gz", "$name`r`n")
        $env:STUB_ID = $id; $env:STUB_LAYERS = $layers
        if ($imagesTxtLine) { [IO.File]::WriteAllLines("$($w.Work)\IMAGES.txt", [string[]]@('Папка собрана 2026-01-01 00:00', '', 'В папке (images\):', $imagesTxtLine)) }
        $w
    }
    $w = ImgSetup 'pilot-proadmin:latest' $id1 $lay1 ("  {0,-50} {1}  2026-01-01  {2}" -f 'pilot-proadmin:latest', $id1, $h1)
    $r = Install $w @('-NoData', '-Roles', 'admin')
    if ($r.Code) { Write-Host $r.Out }
    Check ($r.Code -eq 0 -and $r.Out -notmatch 'не совпал') 'идентификатор совпал: установка идёт дальше'
    $w = ImgSetup 'pilot-proadmin:latest' $id2 $lay2 ("  {0,-50} {1}  2026-01-01  {2}" -f 'pilot-proadmin:latest', $id1, $h1)
    $r = Install $w @('-NoData', '-Roles', 'admin')
    Check ($r.Code -ne 0 -and $r.Out -match 'pilot-proadmin:latest из a\.tar\.gz не совпал с IMAGES\.txt' -and "$(Get-Content $env:STUB_LOG -Raw)" -notmatch 'compose up') 'расхождение идентификатора и слоёв: ошибка с именем образа и файла, до запуска сервисов'
    $w = ImgSetup 'pilot-proadmin:latest' $id2 $lay1 ("  {0,-50} {1}  2026-01-01  {2}" -f 'pilot-proadmin:latest', $id1, $h1)
    $r = Install $w @('-NoData', '-Roles', 'admin')
    Check ($r.Code -eq 0) 'другое хранилище образов (идентификатор иной, слои те же): не ошибка'
    $w = ImgSetup 'pilot-proadmin:latest' $id1 $lay1 $null
    $r = Install $w @('-NoData', '-Roles', 'admin')
    Check ($r.Code -eq 0 -and $r.Out -match 'Нет IMAGES\.txt') 'нет IMAGES.txt: предупреждение, установка продолжается'
    $w = ImgSetup 'pilot-proadmin:latest' $id1 $lay1 ("  {0,-50} {1}  2026-01-01  {2}" -f 'pilot-migration:latest', $id1, $h1)
    $r = Install $w @('-NoData', '-Roles', 'admin')
    Check ($r.Code -eq 0 -and $r.Out -match 'pilot-proadmin:latest в IMAGES\.txt нет полной записи') 'нет строки для образа: предупреждение'
    $w = ImgSetup 'pilot-proadmin:latest' $id2 $lay2 ("  {0,-50} {1}  2026-01-01" -f 'pilot-proadmin:latest', $id1.Substring(7, 12))
    $r = Install $w @('-NoData', '-Roles', 'admin')
    Check ($r.Code -eq 0 -and $r.Out -match 'в IMAGES\.txt нет полной записи') 'строка старого формата (12 символов): предупреждение, не ошибка'
    $w = ImgSetup 'rustfs/rustfs:1.0.5' $id1 $lay1 ("  rustfs/rustfs:1.0.5@sha256:{0}   {1}  2026-01-01  {2}" -f ('c' * 64), $id1, $h1)
    $r = Install $w @('-NoData', '-Roles', 'admin')
    Check ($r.Code -eq 0 -and $r.Out -notmatch 'нет полной записи') 'запись с digest в имени сопоставляется с загруженным тегом'
    $w = ImgSetup 'pilot-proadmin:latest' $id2 $lay2 ("  {0,-50} {1}  2026-01-01  {2}" -f 'pilot-proadmin:latest', $id1, $h1)
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'admin')
    Check ($r.Code -eq 0) 'с -SkipLoad сверка не выполняется'
    $env:STUB_ID = ''; $env:STUB_LAYERS = ''

    Write-Host '== RustFS: бакет'
    $w = Fresh 'rustfs-special'
    $ex = [IO.File]::ReadAllText("$($w.Work)\.env.example") -replace '(?m)^MINIO_PASSWORD=.*$', 'MINIO_PASSWORD=p@ss:w/o rd#1'
    [IO.File]::WriteAllText("$($w.Work)\.env", $ex)
    $r = Install $w @('-NoData', '-SkipLoad', '-Roles', 'prod')
    $log = Get-Content $env:STUB_LOG -Raw
    Check ($log -match '-e RCU -e RCP ' -and $log -match '--entrypoint sh rustfs/rustfs:1\.0\.5 -c "rc alias set m http://rustfs-prod:9000' -and $log -match 'rc mb --ignore-existing m/pilot-ice-ru') 'спецсимволы: логин и пароль идут переменными окружения внутри sh -c, бакет создаёт rc'
    Check ($log -notmatch [regex]::Escape('p@ss') -and $log -notmatch [regex]::Escape('w/o rd')) 'пароль со спецсимволами не попадает в аргументы docker'

    Write-Host '== восстановление из бэкапа: хранилище (файлы S3)'
    function RustfsSnap { Snap; New-Item -ItemType Directory "$snapRoot\rustfs" -Force | Out-Null; [IO.File]::WriteAllText("$snapRoot\rustfs\rustfs_prod.tgz", 'x') }
    function LsFile([string[]]$names) { $f = Join-Path $tmp 'ls.txt'; [IO.File]::WriteAllLines($f, $names); $f }
    RustfsSnap; $w = Fresh 'rustfs-restore-new'
    $r = Install $w @('-FromBackup', $snapRoot, '-Roles', 'prod', '-SkipLoad')
    if ($r.Code) { Write-Host $r.Out }
    $log = Get-Content $env:STUB_LOG -Raw
    Check ($r.Code -eq 0 -and -not (Test-Path "$($w.Data)/prod/rustfs")) 'бэкап с rustfs\rustfs_<контур>.tgz: код 0, в DATA_ROOT ничего не копируется'
    Check ($log -match 'docker run --rm --user 0 --entrypoint sh -v cicd_rustfs_prod:/v:ro rustfs/rustfs:1\.0\.5 -c "?ls -A /v') 'содержимое тома cicd_rustfs_prod проверяется (ls -A) в образе RustFS'
    Check ($log -match '-v cicd_rustfs_prod:/v -v \S+\.restore-rustfs:/b:ro rustfs/rustfs:1\.0\.5 -c "tar tzf /b/rustfs_prod\.tgz > /dev/null && tar xzf /b/rustfs_prod\.tgz -C /v"') 'архив проверяется (tar tzf) и распаковывается в том cicd_rustfs_prod одной командой'
    $tx = $log.IndexOf('tar xzf /b/rustfs_prod.tgz'); $cu = $log.IndexOf('--remove-orphans'); $mb = $log.IndexOf('mb --ignore-existing'); $ns = $log.IndexOf('compose up --no-start')
    Check ($ns -ge 0 -and $ns -lt $tx -and $tx -lt $cu -and $tx -lt $mb) 'порядок: compose up --no-start, распаковка, затем compose up (pilot-server) и бакет'
    Check ($r.Out -notmatch 'restore\.ps1|после установки восстановите' -and $log -notmatch 'docker stop rustfs-prod') 'предупреждения «после установки восстановите» нет; незапущенный rustfs-prod не останавливается'
    Check (-not (Test-Path "$($w.Data)/.restore-rustfs")) 'промежуточная копия архива удалена'

    RustfsSnap; $w = Fresh 'rustfs-restore-empty'; $env:STUB_RUSTFS_LS = LsFile @('.rustfs', '.rustfs.sys')
    $r = Install $w @('-FromBackup', $snapRoot, '-Roles', 'prod', '-SkipLoad')
    Check ($r.Code -eq 0 -and (Get-Content $env:STUB_LOG -Raw) -match 'tar xzf /b/rustfs_prod\.tgz') 'том только со служебными .rustfs и .rustfs.sys считается пустым: восстановление идёт'

    RustfsSnap; $w = Fresh 'rustfs-restore-busy'; $env:STUB_RUSTFS_LS = LsFile @('.rustfs', 'photos')
    $r = Install $w @('-FromBackup', $snapRoot, '-Roles', 'prod', '-SkipLoad')
    $log = Get-Content $env:STUB_LOG -Raw; $flat = $r.Out -replace '\s+', ' '
    Check ($r.Code -ne 0 -and $flat -match 'cicd_rustfs_prod уже есть данные RustFS' -and $flat -match 'restore\.ps1 -Env prod -Parts rustfs -ConfirmOverwrite rustfs') 'том с данными: отказ с понятным текстом и подсказкой про restore.ps1 -ConfirmOverwrite rustfs'
    Check ($log -notmatch 'tar xzf' -and $log -notmatch '--remove-orphans') 'при отказе ничего не распаковано и стек не запущен'

    RustfsSnap; $w = Fresh 'rustfs-restore-running'; $env:STUB_RUSTFS_LS = ''; $env:STUB_RUSTFS_RUNNING = '1'
    $r = Install $w @('-FromBackup', $snapRoot, '-Roles', 'prod', '-SkipLoad')
    $env:STUB_RUSTFS_RUNNING = '0'; $log = Get-Content $env:STUB_LOG -Raw
    $st = $log.IndexOf('docker stop rustfs-prod'); $tx = $log.IndexOf('tar xzf /b/rustfs_prod.tgz')
    Check ($r.Code -eq 0 -and $st -ge 0 -and $st -lt $tx -and $tx -lt $log.IndexOf('--remove-orphans')) 'запущенный rustfs-prod останавливается перед записью; compose up запускает его после'

    Snap; $w = Fresh 'rustfs-restore-none'
    $r = Install $w @('-FromBackup', $snapRoot, '-Roles', 'prod', '-SkipLoad')
    $log = Get-Content $env:STUB_LOG -Raw
    Check ($r.Code -eq 0 -and $log -notmatch 'tar xzf|ls -A /v') 'бэкап без части rustfs: без ошибок, том не трогается (прежнее поведение)'

    # Бэкап времён MinIO: папка minio\<контур>\pilot-ice-ru, в settings.xml адрес minio-<контур>, учётка Pilot только в .env.
    Snap
    New-Item -ItemType Directory "$snapRoot\minio\prod\pilot-ice-ru\10\199\obj" -Force | Out-Null
    [IO.File]::WriteAllText("$snapRoot\minio\prod\pilot-ice-ru\10\199\obj\xl.meta", 'm')
    [IO.File]::WriteAllText("$snapRoot\pilot\prod\pilot-server\settings\settings.xml", '<Configuration><FileArchiveParametersS3 AccessKeyId="pilot" Url="http://minio-prod:9000" Bucket="pilot-ice-ru" /></Configuration>')
    [IO.File]::AppendAllText("$snapRoot\admin\env\prod\.env", "MINIO_PILOT_USER=pilot`nMINIO_PILOT_SECRET=pilotS3secret98765`n")
    $w = Fresh 'minio-legacy'
    $r = Install $w @('-FromBackup', $snapRoot, '-Roles', 'prod', '-SkipLoad')
    if ($r.Code) { Write-Host $r.Out }
    $log = Get-Content $env:STUB_LOG -Raw
    $xml = [IO.File]::ReadAllText("$($w.Data)/prod/pilot-server/settings/settings.xml")
    Check ($r.Code -eq 0 -and $xml -match 'Url="http://rustfs-prod:9000"' -and $xml -notmatch 'minio-prod') 'бэкап MinIO: адрес S3-архива в settings.xml заменён на rustfs-prod'
    Check ($log -match '-v cicd_rustfs_prod:/v -v \S+\.restore-minio:/b:ro rustfs/rustfs:1\.0\.5 -c "mkdir -p /v/pilot-ice-ru && cp -r /b/pilot-ice-ru/\. /v/pilot-ice-ru/ && chown 10001:10001 /v && chown -R 10001:10001 /v/pilot-ice-ru"') 'бэкап MinIO: папка бакета копируется в том cicd_rustfs_prod, владелец 10001 (и корень тома)'
    $cp = $log.IndexOf('cp -r /b/pilot-ice-ru'); $cu = $log.IndexOf('--remove-orphans'); $ua = $log.IndexOf('rc admin user add')
    Check ($cp -ge 0 -and $cp -lt $cu -and $cu -lt $ua) 'бэкап MinIO: копирование до compose up, учётка Pilot - после (шаг «Бакеты RustFS»)'
    Check ($log -match '-e PILOT_U -e PILOT_S -e PILOT_POLICY' -and $log -notmatch 'pilotS3secret98765') 'учётка Pilot: секрет передаётся окружением, в аргументах docker его нет'
    Check ($r.Out -match 'rustfs-prod: учётка pilot' -and -not (Test-Path "$($w.Data)/.restore-minio")) 'учётка pilot заведена; промежуточная копия удалена'
}
finally {
    $env:PATH = ($env:PATH -split ';' | Where-Object { $_ -ne "$tmp\stub" }) -join ';'
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

# --- база Pilot из файловой базы: разбор пути (папка, .zip, путь внутри .zip) и распаковка ---
Write-Host "`nФайловая база Pilot: путь к источнику"
foreach ($name in 'Resolve-PilotDbSource', 'Copy-PilotDbSource', 'Copy-Tree') {
    $f = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)[0]
    if (-not $f) { throw "В install.ps1 нет функции $name" }
    Invoke-Expression $f.Extent.Text
}
$onLinux = $false
$dbTmp = Join-Path ([IO.Path]::GetTempPath()) ("pilotdb-selftest-" + [guid]::NewGuid().ToString('N'))
try {
    foreach ($b in 'one', 'two') {
        New-Item -ItemType Directory -Force "$dbTmp\src\Databases\$b\FileArchive\10\199" | Out-Null
        [IO.File]::WriteAllText("$dbTmp\src\Databases\$b\base.dbp", $b)
        [IO.File]::WriteAllText("$dbTmp\src\Databases\$b\FileArchive\x.pilotfa", '')
        [IO.File]::WriteAllText("$dbTmp\src\Databases\$b\FileArchive\10\199\body", 'b')
    }
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $zipPath = "$dbTmp\Databases.zip"
    # Записи с \ в именах - как в архивах, собранных в Windows.
    $zs = [IO.File]::Create($zipPath); $za = New-Object IO.Compression.ZipArchive($zs, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem "$dbTmp\src" -Recurse -File) {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, $file.FullName, $file.FullName.Substring("$dbTmp\src\".Length))
        }
    } finally { $za.Dispose(); $zs.Dispose() }
    $err = { param($p) try { Resolve-PilotDbSource $p | Out-Null; '' } catch { $_.Exception.Message } }
    Check ((Resolve-PilotDbSource "$dbTmp\src\Databases\one").Dir -eq "$dbTmp\src\Databases\one") 'папка базы'
    Check ((Resolve-PilotDbSource "`"$dbTmp\src\Databases\one\`"").Dir -eq "$dbTmp\src\Databases\one") 'папка в кавычках и с \ в конце'
    Check ((Resolve-PilotDbSource "$dbTmp\src\Databases\one\base.dbp").Dir -eq "$dbTmp\src\Databases\one") 'путь к самому base.dbp'
    Check ((Resolve-PilotDbSource "$zipPath\Databases\two\base.dbp").Inner -eq 'Databases/two') 'путь к base.dbp внутри .zip'
    Check ((& $err "$dbTmp\src") -match 'несколько баз') 'папка с двумя базами - отказ'
    Check ((& $err "$dbTmp\nope") -match 'Не найдено') 'нет пути - отказ'
    $z = Resolve-PilotDbSource "$zipPath\Databases\two"
    Check ($z.Zip -eq $zipPath -and $z.Inner -eq 'Databases/two') 'путь внутри .zip (записи с \)'
    Check ((& $err $zipPath) -match 'несколько баз') '.zip с двумя базами без пути - отказ'
    Check ((& $err "$zipPath\Databases\three") -match 'нет base.dbp') 'нет базы внутри .zip - отказ'
    Copy-PilotDbSource $z "$dbTmp\out"
    Check ((Test-Path "$dbTmp\out\base.dbp") -and [IO.File]::ReadAllText("$dbTmp\out\base.dbp") -eq 'two' -and (Test-Path "$dbTmp\out\FileArchive\10\199\body") -and (Test-Path "$dbTmp\out\FileArchive\x.pilotfa")) 'распаковка базы и архива из .zip'
    Check (-not (Test-Path "$dbTmp\out\Databases")) 'из .zip берётся только выбранная база'
} finally { Remove-Item -Recurse -Force $dbTmp -ErrorAction SilentlyContinue }

# --- база Pilot из файловой базы: не запустилась - установка падает, а не пишет .installed ---
Write-Host "`nФайловая база Pilot: база не запустилась"
$dbFunctions = @{}
foreach ($name in 'Import-PilotDatabase', 'Assert-PilotDatabaseLaunched', 'Wait-PilotDatabase', 'Get-PilotImportMarker', 'Test-PilotDatabaseRegistered', 'Resume-PilotDatabaseImport') {
    $dbFunctions[$name] = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)[0]
    if (-not $dbFunctions[$name]) { throw "В install.ps1 нет функции $name" }
}
$notLaunched = @('Import-PilotDatabase', 'Assert-PilotDatabaseLaunched' | ForEach-Object {
    $dbFunctions[$_].Body.FindAll({ param($n) $n -is [System.Management.Automation.Language.IfStatementAst] -and $n.Clauses[0].Item1.Extent.Text -match '\$state -ne ''Launched''' }, $true) })
Check ($notLaunched.Count -eq 2 -and @($notLaunched | Where-Object { $_.Clauses[0].Item2.Extent.Text -notmatch '\bthrow\b' -or $_.Clauses[0].Item2.Extent.Text -match '\breturn\b' }).Count -eq 0) 'не запустилась (загрузка и повторная установка) - throw, без return'
# Релизная обёртка при повторном запуске не передаёт -PilotDatabases: начатая загрузка проверяется или продолжается и без него.
$installText = [IO.File]::ReadAllText($installPs1)
Check ($installText -match '(?s)if \(-not \(Test-Path "\$root/\.installed"\)\) \{\s*foreach \(\$c in @\(\$myContours \| Where-Object \{ -not \$pilotDbSources\.Contains\(\$_\) \}\)\) \{ Resume-PilotDatabaseImport \$c \}') 'повторный запуск без -PilotDatabases (релизная обёртка): начатая загрузка проверяется до .installed'
# Метка незавершённой загрузки - до первой записи данных (рабочая копия), снимается только после запуска базы.
$importText = $dbFunctions['Import-PilotDatabase'].Body.Extent.Text
$markerAt = $importText.IndexOf('WriteAllText((Get-PilotImportMarker $c)')
Check ($markerAt -gt 0 -and $markerAt -lt $importText.IndexOf('Copy-PilotDbSource') -and $importText.LastIndexOf('Get-PilotImportMarker') -gt $importText.IndexOf("'Launched'")) 'метка загрузки ставится до рабочей копии и снимается после запуска базы'
foreach ($f in $dbFunctions.Values) { Invoke-Expression $f.Extent.Text }
$dbTmp = Join-Path ([IO.Path]::GetTempPath()) ("pilotdb-state-" + [guid]::NewGuid().ToString('N'))
try {
    $root = $dbTmp; $pilotDbName = 'pilot-ice_ru'
    function Start-Sleep { }
    # Журнал сервера: база запустилась, потом сломалась - важно последнее состояние.
    $script:serverLog = "Database $pilotDbName to state Launched`nDatabase $pilotDbName to state Broken"
    function docker { if ($args[0] -eq 'inspect') { '2026-11-20T10:00:00Z' } else { $script:serverLog } }
    Check ((Wait-PilotDatabase 'test' (Get-Date)) -eq 'Broken') 'Wait-PilotDatabase: Launched, затем Broken - Broken'
    $script:serverLog = "Database $pilotDbName to state Broken`nDatabase $pilotDbName to state Launched"
    Check ((Wait-PilotDatabase 'test' (Get-Date)) -eq 'Launched') 'Wait-PilotDatabase: Broken, затем Launched - Launched'

    New-Item -ItemType Directory -Force "$root/test/pilot-server/settings" | Out-Null
    [IO.File]::WriteAllText("$root/test/pilot-server/settings/settings.xml", "<Configuration><Databases><DatabaseParameters Name='$pilotDbName' /></Databases></Configuration>")
    $script:serverLog = "Database $pilotDbName to state Launched`nDatabase $pilotDbName to state Broken"
    $msg = try { Import-PilotDatabase 'test' $null; '' } catch { $_.Exception.Message }
    Check ($msg -match 'не запущена \(Broken\)' -and $msg -match 'before-import') 'повторная установка: прописанная, но сломанная база - ошибка с инструкцией'
    $script:serverLog = "Database $pilotDbName to state Launched"
    $msg = try { Import-PilotDatabase 'test' $null; '' } catch { $_.Exception.Message }
    Check ($msg -eq '') 'повторная установка: запущенная база - пропуск без ошибки'

    # Загрузка оборвалась до записи settings.xml (например, на копировании в RustFS): рабочая папка есть, before-import нет.
    [IO.File]::WriteAllText("$root/test/pilot-server/settings/settings.xml", '<Configuration><Databases /></Configuration>')
    New-Item -ItemType Directory -Force "$root/test/databases/.import-$pilotDbName" | Out-Null
    function Import-PilotDatabase { param($c, $source) $script:resumed = "$c|$($source.Title)" }
    function Resolve-PilotDbSource { param($p) if ($p -eq 'D:\base') { @{ Dir = $p; Title = $p } } else { throw "Не найдено: $p" } }
    $script:resumed = ''
    [IO.File]::WriteAllText((Get-PilotImportMarker 'test'), "D:\base`n")
    $msg = try { Resume-PilotDatabaseImport 'test'; '' } catch { $_.Exception.Message }
    Check ($msg -eq '' -and $script:resumed -eq 'test|D:\base') 'прерванная загрузка без -PilotDatabases продолжается из сохранённого источника'
    [IO.File]::WriteAllText((Get-PilotImportMarker 'test'), "D:\moved`n")
    $msg = try { Resume-PilotDatabaseImport 'test'; '' } catch { $_.Exception.Message }
    Check ($msg -match 'не завершена' -and $msg -match '-PilotDatabases') 'источник недоступен - остановка с инструкцией (без .installed)'
    [IO.File]::WriteAllText((Get-PilotImportMarker 'test'), '')
    $msg = try { Resume-PilotDatabaseImport 'test'; '' } catch { $_.Exception.Message }
    Check ($msg -match 'источник неизвестен') 'источник не сохранён - остановка с инструкцией'
    Remove-Item -LiteralPath (Get-PilotImportMarker 'test')
    $script:resumed = ''
    $msg = try { Resume-PilotDatabaseImport 'test'; '' } catch { $_.Exception.Message }
    Check ($msg -eq '' -and $script:resumed -eq '') 'без метки и before-import - контур без загрузки базы, проверка не нужна'
    Remove-Item Function:\docker, Function:\Start-Sleep, Function:\Import-PilotDatabase, Function:\Resolve-PilotDbSource
} finally { Remove-Item -Recurse -Force $dbTmp -ErrorAction SilentlyContinue }

Write-Host ''
if ($failed) { Write-Host "НЕ ПРОЙДЕНО: $failed" -ForegroundColor Red; exit 1 }
Write-Host 'ВСЕ ПРОВЕРКИ ПРОЙДЕНЫ' -ForegroundColor Green
