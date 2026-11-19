#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$ImageArchive,
    [string]$Backup,
    [string]$PilotImage,
    [string]$InstallDirectory = (Join-Path $PSScriptRoot 'PilotProAdmin-install')
)

$ErrorActionPreference = 'Stop'
$version = '__RELEASE_VERSION__'
$repository = '__RELEASE_REPOSITORY__'
$payload = '__RELEASE_PAYLOAD__'
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Запустите install.ps1 из релизного архива lite.' }

function Invoke-Docker {
    & docker @args
    if ($LASTEXITCODE -ne 0) { throw "Docker: команда $($args[0]) завершилась с ошибкой. См. вывод Docker выше." }
}
function Test-Image($image) {
    $ErrorActionPreference = 'Continue'
    & docker image inspect $image 2>$null | Out-Null
    return $LASTEXITCODE -eq 0
}
function Select-Option([string]$Prompt, [string[]]$Options) {
    do { $answer = (Read-Host $Prompt).Trim() } until ($answer -in $Options)
    $answer
}
function Read-ServiceUrl([string]$Prompt, [switch]$ServerOnly) {
    while ($true) {
        $value = (Read-Host $Prompt).Trim().TrimEnd('/')
        $uri = $null
        if ($value -notmatch '[\s$#]' -and [uri]::TryCreate($value, [UriKind]::Absolute, [ref]$uri) -and
            $uri.Scheme -in 'http', 'https' -and -not ($uri.UserInfo -or $uri.Query -or $uri.Fragment) -and
            (-not $ServerOnly -or $uri.AbsolutePath -eq '/')) { return $value }
        Write-Warning 'Введите полный HTTP(S)-адрес без пароля, параметров или фрагмента.'
    }
}

function Get-ExpectedPostgresDatabases([string]$SettingsPath, [string[]]$RecordedDatabases) {
    try {
        $xml = [xml][IO.File]::ReadAllText($SettingsPath)
    } catch {
        throw "Не удалось прочитать настройки PostgreSQL из $SettingsPath."
    }
    $databases = @($RecordedDatabases | Where-Object { $_ })
    foreach ($node in $xml.SelectNodes("//*[local-name()='PgConnectionString']")) {
        try {
            $connection = [System.Data.Common.DbConnectionStringBuilder]::new()
            # PowerShell adapts the builder as a dictionary. Assign the actual property.
            $connection.PSBase.ConnectionString = $node.GetAttribute('ConnectionString')
            $database = if ($connection.ContainsKey('Database')) { $connection.get_Item('Database') }
                        elseif ($connection.ContainsKey('Initial Catalog')) { $connection.get_Item('Initial Catalog') }
        } catch {
            throw "Не удалось разобрать строку подключения PostgreSQL в $SettingsPath."
        }
        if (-not $database) { throw "В $SettingsPath не указано имя базы PostgreSQL." }
        $databases += $database
    }
    # Fall back to the standard Pilot database when the backup identifies only metrics.
    if (-not ($databases | Where-Object { $_ -cne 'pilot_metrics' })) {
        $databases += 'pilot-ice_ru'
    }
    $unique = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($database in $databases) {
        if ($database -match '[\\/:*?"<>|\x00-\x1f]') { throw "Некорректное имя файла дампа в конфигурации $SettingsPath." }
        if ($unique.Add($database)) { $database }
    }
}

function Assert-CompleteBackup([string]$Directory, $Manifest) {
    if ($Manifest.ok -ne $true) { throw 'Копия снята с ошибками. Проверьте backup.log.' }
    if ($Manifest.parts.postgres -and $Manifest.parts.postgres -ne 'ok') { throw 'В копии не завершено резервное копирование PostgreSQL.' }
    $required = @('gitlab/config/gitlab-secrets.json')
    $contours = 'test', 'stage', 'prod'
    $rustfsVolumes = $Manifest.volumes.rustfs
    $rustfsContours = @($contours | Where-Object { $rustfsVolumes -and $rustfsVolumes.PSObject.Properties[$_] })
    foreach ($contour in $contours) {
        $required += "postgres/$contour/globals.sql", "pilot/$contour/pilot-server/settings/settings.xml"
    }
    # Older manifests may not map RustFS volumes. A successful RustFS part without a map
    # cannot establish which contours were backed up, so require all three for a full restore.
    if ($Manifest.parts.rustfs -eq 'ok' -and $rustfsContours.Count -eq 0) {
        $rustfsContours = $contours
    }
    $required += @($rustfsContours | ForEach-Object { "rustfs/rustfs_$_.tgz" })
    foreach ($part in 'grafana', 'loki', 'prometheus') {
        if (($Manifest.volumes -and $Manifest.volumes.PSObject.Properties[$part]) -or $Manifest.parts.$part -eq 'ok') {
            $required += "$part/grafana_${part}_data.tgz"
        }
    }
    foreach ($file in $required) {
        $artifact = Get-Item -LiteralPath (Join-Path $Directory $file) -ErrorAction SilentlyContinue
        if (-not $artifact -or $artifact.PSIsContainer -or $artifact.Length -eq 0) {
            throw "Копия неполная: отсутствует или пуст $file."
        }
    }
    foreach ($contour in $contours) {
        $settingsPath = Join-Path $Directory "pilot/$contour/pilot-server/settings/settings.xml"
        $databases = Get-ExpectedPostgresDatabases $settingsPath $Manifest.postgresDatabases.$contour
        $dumps = [Collections.Generic.Dictionary[string,IO.FileInfo]]::new([StringComparer]::Ordinal)
        foreach ($dump in Get-ChildItem -LiteralPath (Join-Path $Directory "postgres/$contour") -Filter '*.dump' -File) {
            $dumps[$dump.Name] = $dump
        }
        foreach ($database in $databases) {
            $file = "postgres/$contour/$database.dump"
            $dump = $null
            if (-not $dumps.TryGetValue("$database.dump", [ref]$dump) -or $dump.Length -eq 0) {
                throw "Копия неполная: отсутствует или пуст $file."
            }
        }
    }
}

$endpoint = Invoke-Docker context inspect --format '{{.Endpoints.docker.Host}}'
if (($env:DOCKER_HOST -and $env:DOCKER_HOST -notlike 'npipe:*') -or $endpoint -notlike 'npipe:*') {
    throw 'Выберите локальный контекст Docker Desktop для Windows.'
}
$platform = Invoke-Docker version --format '{{.Server.Os}}/{{.Server.Arch}}'
if ($platform -ne 'linux/amd64') { throw 'Запустите Docker Desktop с Linux-контейнерами на Windows x64.' }
Invoke-Docker compose version | Out-Host

$admin = "${repository}:$version"
$migration = "${repository}-migration:$version"
if (-not $ImageArchive) {
    foreach ($name in "PilotProAdmin-v$version-linux-amd64-images.tar.gz", "PilotProAdmin-v$version-linux-amd64-images.tar") {
        $candidate = Join-Path $PSScriptRoot $name
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $ImageArchive = $candidate; break }
    }
}
if ($ImageArchive) {
    $ImageArchive = (Resolve-Path -LiteralPath $ImageArchive).Path
    Write-Host 'Загрузка образов PilotProAdmin и PilotMigration из архива.'
    Invoke-Docker load --input $ImageArchive
    if (-not (Test-Image $admin) -or -not (Test-Image $migration)) {
        throw "В Docker после загрузки должны быть оба образа версии $version из релиза."
    }
} else {
    foreach ($image in $admin, $migration) {
        if (-not (Test-Image $image)) { Invoke-Docker pull $image }
    }
}

$kit = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($InstallDirectory))
if ($kit -match '[$#]') { throw 'Выберите папку установки без символов $ и #.' }
$envFile = Join-Path $kit '.env'
$reinstall = Test-Path -LiteralPath $envFile
$saved = @{}
if ($reinstall) {
    foreach ($line in [IO.File]::ReadAllLines($envFile)) {
        if ($line -match '^([A-Z0-9_]+)=(.*)$') { $saved[$Matches[1]] = $Matches[2] }
    }
    if ($saved.RELEASE_KIT -ne 'true') { throw 'Эта папка принадлежит другой установке. Укажите новый -InstallDirectory.' }
}
if (-not $Backup -and $saved.RELEASE_BACKUP -and -not (Test-Path -LiteralPath (Join-Path $saved.DATA_ROOT '.installed'))) {
    $Backup = $saved.RELEASE_BACKUP
}

$roles = @('admin')
$gitLabUrl = ''
$grafanaUrl = ''
$gitLabHost = ''
$pilotUrls = @{}
if (-not $Backup -and -not $saved.ROLES -and (Select-Option 'Восстановить полный стенд из резервной копии? 1 — да, 2 — нет' @('1', '2')) -eq '1') {
    $Backup = (Read-Host 'Путь к каталогу резервной копии с manifest.json').Trim().Trim('"')
    if (-not $Backup) { throw 'Укажите каталог резервной копии.' }
}
if ($Backup) {
    $Backup = (Resolve-Path -LiteralPath $Backup).Path
    if ($Backup -match '[$#]') { throw 'Путь к резервной копии должен быть без символов $ и #.' }
    if (-not (Test-Path -LiteralPath (Join-Path $Backup 'manifest.json') -PathType Leaf)) { throw 'В резервной копии нет manifest.json.' }
    $manifest = [IO.File]::ReadAllText((Join-Path $Backup 'manifest.json')) | ConvertFrom-Json
    Assert-CompleteBackup $Backup $manifest
    $gitLabBackups = @(Get-ChildItem -LiteralPath (Join-Path $Backup 'gitlab') -Filter '*_gitlab_backup.tar' -File -ErrorAction SilentlyContinue)
    if ($gitLabBackups.Count -ne 1 -or $gitLabBackups[0].Length -eq 0) {
        throw 'В каталоге gitlab должен быть один непустой архив *_gitlab_backup.tar.'
    }
    if ($gitLabBackups[0].Name -notmatch '_(\d+\.\d+\.\d+)(?:-(ce|ee))?_gitlab_backup\.tar$') {
        $metadata = & tar.exe -xOf $gitLabBackups[0].FullName 'backup_information.yml'
        if ($LASTEXITCODE -or ($metadata -join "`n") -notmatch '(?m)^:gitlab_version:\s*[''"]?(\d+\.\d+\.\d+)(?:-(ce|ee))?[''"]?\s*$') {
            throw 'Не удалось определить версию GitLab по имени архива или backup_information.yml.'
        }
    }
    $gitLabVersion = $Matches[1]
    $gitLabEdition = if ($Matches[2]) { $Matches[2] } else { 'ce' }
    $backupGitLabImage = "gitlab/gitlab-${gitLabEdition}:${gitLabVersion}-${gitLabEdition}.0"
    $roles += 'gitlab', 'metrics', 'test', 'stage', 'prod'
} elseif ($saved.ROLES) {
    $roles = $saved.ROLES -split ','
    foreach ($contour in 'test', 'stage', 'prod') { $pilotUrls[$contour] = $saved["PILOT_$($contour.ToUpperInvariant())_URL"] }
    $grafanaUrl = $saved.GRAFANA_URL
} else {
    if ((Select-Option 'GitLab. 1 — подключить существующий, 2 — установить здесь' @('1', '2')) -eq '1') {
        $gitLabUrl = Read-ServiceUrl 'Адрес сервера GitLab' -ServerOnly
    } else { $roles += 'gitlab' }

    $statistics = Select-Option 'Статистика. 1 — подключить Grafana, 2 — установить здесь, 3 — позже' @('1', '2', '3')
    if ($statistics -eq '1') { $grafanaUrl = Read-ServiceUrl 'Адрес Grafana' }
    if ($statistics -eq '2') { $roles += 'metrics' }
    foreach ($contour in 'prod', 'test', 'stage') {
        $selection = Select-Option "Pilot ${contour}. 1 — подключить существующий, 2 — установить здесь, 3 — позже" @('1', '2', '3')
        if ($selection -eq '1') { $pilotUrls[$contour] = Read-ServiceUrl "Адрес Pilot $contour (можно с именем базы)" }
        if ($selection -eq '2') { $roles += $contour }
    }
    if ($statistics -eq '2' -and 'prod' -notin $roles) {
        switch (Select-Option 'Локальная статистика использует PostgreSQL прода. 1 — установить прод здесь, 2 — подключить Grafana, 3 — настроить статистику позже' @('1', '2', '3')) {
            '1' { $roles += 'prod' }
            '2' { $roles = @($roles | Where-Object { $_ -ne 'metrics' }); $grafanaUrl = Read-ServiceUrl 'Адрес Grafana' }
            '3' { $roles = @($roles | Where-Object { $_ -ne 'metrics' }) }
        }
    }
}
if ('gitlab' -in $roles -and (-not $saved.ROLES -or $Backup)) {
    $defaultGitLabHost = if ($saved.GITLAB_HOST_IP) { $saved.GITLAB_HOST_IP } else { 'localhost' }
    $gitLabHost = (Read-Host "Имя или IPv4 этого компьютера для GitLab [$defaultGitLabHost]").Trim()
    if ($gitLabHost -and $gitLabHost -notmatch '^[a-zA-Z0-9][a-zA-Z0-9.-]*$') { throw 'Укажите имя хоста или IPv4 без протокола и порта.' }
    if (-not $gitLabHost) { $gitLabHost = $defaultGitLabHost }
}
if ($PilotImage -and $PilotImage -notmatch '^[a-zA-Z0-9][a-zA-Z0-9_./:@-]+$') {
    throw 'Укажите имя Docker-образа Pilot без пробелов, например registry.example/pilot-server:tag.'
}

# The package embeds the Compose files, monitoring config and the existing restore implementation.
Add-Type -AssemblyName System.IO.Compression
$mem = [IO.MemoryStream]::new([Convert]::FromBase64String($payload))
$zip = [IO.Compression.ZipArchive]::new($mem, [IO.Compression.ZipArchiveMode]::Read)
try {
    foreach ($entry in $zip.Entries) {
        if (-not $entry.Name) { continue }
        $target = Join-Path $kit $entry.FullName
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        $source = $entry.Open()
        try {
            $dest = [IO.File]::Create($target)
            try { $source.CopyTo($dest) } finally { $dest.Dispose() }
        } finally { $source.Dispose() }
    }
} finally { $zip.Dispose(); $mem.Dispose() }

if (-not $reinstall) {
    $data = (Join-Path $kit 'data').Replace('\', '/')
    $template = [IO.File]::ReadAllText((Join-Path $kit '.env.example'))
    $template = $template -replace '(?m)^DATA_ROOT=.*$', ('DATA_ROOT=' + $data)
    $project = 'pilotproadmin-' + [guid]::NewGuid().ToString('N').Substring(0, 12)
    [IO.File]::WriteAllText($envFile, $template + "`nRELEASE_KIT=true`nADMIN_BIND=127.0.0.1`nCOMPOSE_PROJECT_NAME=$project`n")
}
if ($PilotImage) {
    $lines = [IO.File]::ReadAllLines($envFile) | Where-Object { $_ -notmatch '^PILOT_SERVER_IMAGE=' }
    [IO.File]::WriteAllLines($envFile, [string[]](@($lines) + "PILOT_SERVER_IMAGE=$PilotImage"))
}

# Persist the immutable tags so that docker compose and future restarts use the same release.
$reportImage = "pilot-report-metrics:$version"
$automationImage = "pilot-automation-metrics:$version"
$lines = [IO.File]::ReadAllLines($envFile) | Where-Object { $_ -notmatch '^(RELEASE_ADMIN_IMAGE|RELEASE_MIGRATION_IMAGE|RELEASE_REPORT_METRICS_IMAGE|RELEASE_AUTOMATION_METRICS_IMAGE|AUTOMATION_TEST_PROFILE|AUTOMATION_STAGE_PROFILE)=' }
if ('metrics' -notin $roles) { $lines = @($lines) + 'AUTOMATION_TEST_PROFILE=inactive-test', 'AUTOMATION_STAGE_PROFILE=inactive-stage' }
if ($Backup) {
    $lines = @($lines | Where-Object { $_ -notmatch '^(RELEASE_BACKUP|RELEASE_RESTORE_GITLAB_IMAGE)=' })
    $lines += "RELEASE_BACKUP=$Backup", "RELEASE_RESTORE_GITLAB_IMAGE=$backupGitLabImage"
}
[IO.File]::WriteAllLines($envFile, [string[]](@($lines) + "RELEASE_ADMIN_IMAGE=$admin" + "RELEASE_MIGRATION_IMAGE=$migration" +
    "RELEASE_REPORT_METRICS_IMAGE=$reportImage" + "RELEASE_AUTOMATION_METRICS_IMAGE=$automationImage"))

if ('metrics' -in $roles) {
    foreach ($service in @(@($reportImage, 'report-collector'), @($automationImage, 'automation-collector'))) {
        if (-not (Test-Image $service[0])) { Invoke-Docker build --tag $service[0] (Join-Path $kit "collectors/$($service[1])") }
    }
}

$installArgs = @{
    Roles = $roles; SkipLoad = $true; ReleaseInstall = $true
    ReleaseGitLabUrl = $gitLabUrl; ReleaseGrafanaUrl = $grafanaUrl; ReleasePilotUrls = $pilotUrls
}
if ($gitLabHost) { $installArgs.GitLabHost = $gitLabHost }
if ($Backup) { $installArgs.FromBackup = $Backup } else { $installArgs.NoData = $true }
Push-Location $kit
try { & (Join-Path $kit 'install-stack.ps1') @installArgs } finally { Pop-Location }
