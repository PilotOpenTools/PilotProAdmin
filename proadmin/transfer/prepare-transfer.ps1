#Requires -Version 5.1
[CmdletBinding(DefaultParameterSetName = 'Detect')]
param(
    [string]$OutputDirectory = (Join-Path $env:LOCALAPPDATA 'PilotProAdmin-Transfer'),
    [Parameter(Mandatory, ParameterSetName = 'Installed')]
    [string]$InstallDirectory,
    [Parameter(Mandatory, ParameterSetName = 'Split')]
    [string]$StackDirectory,
    [string]$DataRoot,
    [string]$Project,
    [switch]$Archive
)

$ErrorActionPreference = 'Stop'
$dockerHost = & docker context inspect --format '{{.Endpoints.docker.Host}}'
if ($LASTEXITCODE -or $dockerHost -notlike 'npipe:*' -or ($env:DOCKER_HOST -and $env:DOCKER_HOST -notlike 'npipe:*')) {
    throw 'Выберите локальный контекст Docker Desktop на исходной машине.'
}
$engine = & docker info --format '{{.OSType}}'
if ($LASTEXITCODE -or $engine -ne 'linux') { throw 'Запустите Docker Desktop в режиме Linux containers на компьютере с исходным стендом.' }

# Папка установки - та, где лежат .env и docker-compose.yml (её создаёт install.ps1, например ...\PilotProAdmin-install).
# Указали папку выше (распакованный релиз) - берём единственную вложенную папку с .env и docker-compose.yml.
if ($InstallDirectory -and -not (Test-Path -LiteralPath (Join-Path $InstallDirectory '.env') -PathType Leaf)) {
    $nested = @(Get-ChildItem -LiteralPath $InstallDirectory -Directory -ErrorAction SilentlyContinue | Where-Object {
        (Test-Path -LiteralPath (Join-Path $_.FullName '.env') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $_.FullName 'docker-compose.yml') -PathType Leaf)
    })
    if ($nested.Count -ne 1) {
        throw "В $InstallDirectory нет .env. Укажите папку установки, где лежат .env и docker-compose.yml."
    }
    $InstallDirectory = $nested[0].FullName
    Write-Host "Папка установки: $InstallDirectory"
}

if (-not ($InstallDirectory -or $StackDirectory)) {
    # Метку читаем из JSON: Windows PowerShell 5.1 срезает кавычки внутри --format '{{index ... "label"}}',
    # и docker отвечает «function "com" not defined».
    $workDir = $null
    $inspect = & docker inspect postgres-prod 2>$null
    if (-not $LASTEXITCODE -and $inspect) {
        $labels = (($inspect | Out-String) | ConvertFrom-Json)[0].Config.Labels
        if ($labels) { $workDir = $labels.'com.docker.compose.project.working_dir' }
    }
    if (-not $workDir) {
        throw 'Не найдена папка Compose контейнера postgres-prod (контейнер не запущен или создан не через docker compose). Укажите -InstallDirectory - папку установки, где лежат .env и docker-compose.yml.'
    }
    Write-Host "Папка стенда (по контейнеру postgres-prod): $workDir"
    if (Test-Path -LiteralPath (Join-Path $workDir 'backup')) {
        $StackDirectory = $workDir
    } elseif ((Split-Path $workDir -Leaf) -eq 'prod' -and
        (Test-Path -LiteralPath (Join-Path (Split-Path $workDir -Parent) 'test/.env'))) {
        $StackDirectory = Split-Path $workDir -Parent
    } elseif (Test-Path -LiteralPath (Join-Path $workDir '.env') -PathType Leaf) {
        $InstallDirectory = $workDir
    } else {
        throw "Не найдена конфигурация стенда в $workDir. Укажите -InstallDirectory или -StackDirectory."
    }
}

$backup = @{ Root = $OutputDirectory; KeepDays = 0; Skip = @('images'); ForTransfer = $true; Archive = $Archive }
if ($InstallDirectory) { $backup.InstallDirectory = $InstallDirectory }
if ($StackDirectory) { $backup.StackDirectory = (Resolve-Path -LiteralPath $StackDirectory).Path }
if ($DataRoot) { $backup.DataRoot = $DataRoot }
if ($Project) { $backup.Project = $Project }
& (Join-Path $PSScriptRoot 'backup.ps1') @backup
exit $LASTEXITCODE
