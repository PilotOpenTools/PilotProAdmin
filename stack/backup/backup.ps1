# Preserve the repository entry point used by scheduled backups and build.ps1.
param(
    [string]$Root,
    [int]$KeepDays,
    [string[]]$Skip,
    [string]$DataRoot,
    [string]$Project,
    [string]$TarImage
)
$script = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'proadmin/transfer/backup.ps1'
$PSBoundParameters['StackDirectory'] = Split-Path $PSScriptRoot -Parent
& $script @PSBoundParameters
exit $LASTEXITCODE
