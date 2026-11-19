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

if (-not ($InstallDirectory -or $StackDirectory)) {
    $workDir = & docker inspect --format '{{index .Config.Labels "com.docker.compose.project.working_dir"}}' postgres-prod
    if ($LASTEXITCODE -or -not $workDir -or $workDir -eq '<no value>') {
        throw 'Не найдена папка Compose контейнера postgres-prod. Укажите -InstallDirectory или -StackDirectory.'
    }
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
