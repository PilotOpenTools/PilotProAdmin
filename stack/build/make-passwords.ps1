<#
  Собирает Install\PASSWORDS.md: таблица логинов и паролей из datadmin\env (те же значения получит .env при установке).
  Запускается из build.ps1 и отдельно. Файл содержит пароли, поэтому в git не попадает.
#>
$ErrorActionPreference = 'Stop'
$install = "$(Split-Path $PSScriptRoot -Parent)\Install"
$env_ = "$install\data\admin\env"

function Read-Env($path) {
    $h = @{}
    if (Test-Path $path) { foreach ($l in [IO.File]::ReadAllLines($path)) { if ($l -match '^\s*([A-Z0-9_]+)=(.*)$') { $h[$Matches[1]] = $Matches[2].Trim() } } }
    $h
}
function Show($v) { if ($v) { '`' + $v + '`' } else { '-' } }

if (Test-Path "$install\data\manifest.json") {
    $manifest = [IO.File]::ReadAllText("$install\data\manifest.json") | ConvertFrom-Json
    # Бэкапы до 2026-11-05 хранят настройки админки и GitLab на уровень глубже.
    $deep  = if (Test-Path "$env_\proadmin\.env") { $env_ } else { "$env_\admin" }
    $admin = Read-Env "$deep\proadmin\.env"
    $gl    = Read-Env "$deep\gitlab\.env"
    $mt    = Read-Env "$env_\metrics\.env"
    $rows = New-Object Collections.ArrayList
    function Row($what, $where, $login, $pass) { [void]$rows.Add("| $what | $where | $(Show $login) | $(Show $pass) |") }

    $ports = @{ prod = 5546; test = 5547; stage = 5549 }
    foreach ($c in 'prod', 'test', 'stage') {
        $U = $c.ToUpper()
        $where = '`<сервер>:' + $ports[$c] + '`, база `pilot-ice_ru`'
        if ($c -eq 'prod') { $where += ', http://localhost:5601' }
        Row "Pilot-Server $c (Pilot-Desktop, myAdmin)" $where $admin["${U}_PILOT_ADMIN"] $admin["${U}_PILOT_PASSWORD"]
    }
    Row 'GitLab' ('`http://<IP сервера>:' + $gl.GITLAB_HTTP_PORT + '`, ssh `' + $gl.GITLAB_SSH_PORT + '`') 'root' $gl.GITLAB_ROOT_PASSWORD
    Row 'Grafana' "http://localhost:$($mt.GRAFANA_PORT)" $mt.GRAFANA_ADMIN_USER $mt.GRAFANA_ADMIN_PASSWORD
    $rustfs = @{ prod = 9001; test = 9011; stage = 9021 }
    $pg    = @{ prod = 5433; test = 5434; stage = 5435 }
    foreach ($c in 'prod', 'test', 'stage') {
        $e = Read-Env "$env_\$c\.env"
        Row "Консоль RustFS $c" "http://localhost:$($rustfs[$c])" $e.MINIO_USER $e.MINIO_PASSWORD
        Row "RustFS $c, учётка Pilot (S3)" "порт $($rustfs[$c] - 1)" $e.MINIO_PILOT_USER $e.MINIO_PILOT_SECRET
        Row "PostgreSQL $c, суперпользователь" "``localhost:$($pg[$c])``" 'postgres' $e.PG_SUPERUSER_PASSWORD
        Row "PostgreSQL $c, Pilot" "``localhost:$($pg[$c])``" 'pilotuser' $e.PILOT_PG_PASSWORD
        # Роли базы метрик есть только у бэкапов, снятых после их появления; на проде. В старых бэкапах пароли создаёт установка.
        if ($c -eq 'prod' -and $e.METRICS_WRITER_PASSWORD) { Row 'PostgreSQL prod, метрики: запись (сборщики)' "``localhost:$($pg[$c])``, база pilot_metrics" 'metrics_writer' $e.METRICS_WRITER_PASSWORD }
        if ($c -eq 'prod' -and $e.METRICS_READER_PASSWORD) { Row 'PostgreSQL prod, метрики: чтение (Grafana)' "``localhost:$($pg[$c])``, база pilot_metrics" 'metrics_reader' $e.METRICS_READER_PASSWORD }
    }
    foreach ($c in 'test', 'stage', 'prod') {
        $U = $c.ToUpper()
        if ($admin["MODULES_DEPLOY_${U}_LOGIN"]) { Row "Служебная учётка выкладки модулей, $c" 'Pilot-Server' $admin["MODULES_DEPLOY_${U}_LOGIN"] $admin["MODULES_DEPLOY_${U}_PASSWORD"] }
    }
    foreach ($c in 'test', 'stage', 'prod') {
        $U = $c.ToUpper()
        if ($admin["MODULES_DEPLOY_KEY_$U"]) { Row "Ключ выкладки модулей из CI, $c (DEPLOY_KEY_$U в GitLab)" '-' $null $admin["MODULES_DEPLOY_KEY_$U"] }
    }
    if ($admin.MODULES_DEPLOY_KEY) { Row 'Старый общий ключ выкладки (принимается только для test)' '-' $null $admin.MODULES_DEPLOY_KEY }
    if ($mt.COLLECTOR_API_KEY) { Row 'Ключ приёма статистики (сборщик)' "http://localhost:$($mt.COLLECTOR_PORT)" $null $mt.COLLECTOR_API_KEY }
    Row 'Ключ миграции (админка / сервис)' "http://localhost:$($admin.MIGRATION_PORT)" $null $admin.MIGRATION_API_KEY
    Row 'OAuth-приложение GitLab для админки' 'GitLab / Admin / Applications' $admin.GITLAB_OAUTH_CLIENT_ID $admin.GITLAB_OAUTH_CLIENT_SECRET

    $created = ([datetime]$manifest.created).ToString('yyyy-MM-dd HH:mm')
    $block = @"
Эти значения сняты с исходной машины $created и начинают работать сразу после установки варианта 1.
После установки те же значения лежат в ``.env`` установочной папки.

Вход в админку автоматизаций (http://localhost:$($admin.ADMIN_PORT)): логин и пароль Pilot нужного контура, затем
вход в GitLab. В GitLab также работают все пользователи исходного GitLab со своими паролями.

| Что | Где | Логин | Пароль / ключ |
|---|---|---|---|
$($rows -join "`n")
"@
} else {
    $block = 'В этой папке нет данных, поэтому готовых паролей нет: их задаёте вы в `.env`, см. инструкцию по установке.'
}

$text = "# Логины и пароли стека`n`nФайл только для администратора: в нём все пароли открытым текстом. Разработчикам его не передавайте.`n`n$($block.Replace("`r`n", "`n"))`n"
[IO.File]::WriteAllText("$install\PASSWORDS.md", $text)
# В файле все пароли: доступ только у текущего пользователя, SYSTEM и администраторов.
if (Get-Command icacls -ErrorAction SilentlyContinue) {
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    & icacls "$install\PASSWORDS.md" /inheritance:r /grant:r "*${me}:F" '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Null
    if ($LASTEXITCODE) { Write-Warning 'Не удалось ограничить доступ к PASSWORDS.md: сделайте это вручную.' }
}
