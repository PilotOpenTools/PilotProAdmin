#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [ValidatePattern('^ghcr\.io/[a-z0-9_.-]+/[a-z0-9_.-]+$')]
    [string]$Repository = 'ghcr.io/pilotopentools/pilotproadmin',
    [string]$OutputDirectory,
    [switch]$WithPortable
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path $PSScriptRoot
$project = [xml](Get-Content (Join-Path $root 'proadmin/src/ProAdmin/PilotProAdmin.csproj') -Raw)
if ($project.Project.PropertyGroup.Version -ne $Version) { throw "Версия $Version расходится с PilotProAdmin.csproj." }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts' }
$output = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName

function Add-Text($zip, $name, $content) {
    $writer = [IO.StreamWriter]::new($zip.CreateEntry($name).Open(), [Text.UTF8Encoding]::new($true))
    try { $writer.Write($content) } finally { $writer.Dispose() }
}

$memory = [IO.MemoryStream]::new()
try {
    $payloadZip = [IO.Compression.ZipArchive]::new($memory, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        $install = 'stack/Install'
        $metrics = 'stack/metrics'
        $files = [ordered]@{
            'install-stack.ps1' = "$install/install.ps1"
            'release-ports.ps1' = "$install/release-ports.ps1"
            '.env.example' = "$install/.env.example"
            'docker-compose.yml' = "$install/docker-compose.yml"
            'docker-compose.tls.yml' = "$install/docker-compose.tls.yml"
            'wait-for-postgres.sh' = "$install/wait-for-postgres.sh"
            'gitlab-post-restore.rb' = "$install/gitlab-post-restore.rb"
            'monitoring/grafana/provisioning/datasources/pilot-metrics.yml' = 'stack/build/pilot-metrics.yml'
            'monitoring/grafana/dashboards/pilot-automation.json' = "$metrics/automation-collector/grafana_dashboard_pilot_automation.json"
            'monitoring/grafana/dashboards/pilot-reports.json' = "$metrics/report-collector/grafana/dashboard-pilot-reports.json"
        }
        foreach ($file in (git -C $root ls-files -- "$install/init" "$metrics/config" "$metrics/report-collector" "$metrics/automation-collector")) {
            $entry = $file.Replace("$install/init/", 'init/').Replace("$metrics/config/", 'monitoring/')
            $entry = $entry.Replace("$metrics/report-collector/", 'collectors/report-collector/')
            $entry = $entry.Replace("$metrics/automation-collector/", 'collectors/automation-collector/')
            $files[$entry] = $file
        }
        foreach ($entry in $files.Keys) {
            $path = Join-Path $root $files[$entry]
            if ($entry -like '*.ps1' -or $entry -like '*.sh') {
                $content = [IO.File]::ReadAllText($path)
                if ($entry -like '*.ps1') { Add-Text $payloadZip $entry $content }
                else {
                    $writer = [IO.StreamWriter]::new($payloadZip.CreateEntry($entry).Open())
                    try { $writer.Write($content.Replace("`r`n", "`n")) } finally { $writer.Dispose() }
                }
            } else {
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($payloadZip, $path, $entry)
            }
        }
    } finally { $payloadZip.Dispose() }
    $payload = [Convert]::ToBase64String($memory.ToArray())
} finally { $memory.Dispose() }

$setup = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'install-release.ps1'))
$setup = $setup.Replace('__RELEASE_VERSION__', $Version).Replace('__RELEASE_REPOSITORY__', $Repository).Replace('__RELEASE_PAYLOAD__', $payload)
$name = "PilotProAdmin-v$Version-lite.zip"
$path = Join-Path $output $name
$stream = [IO.File]::Create($path)
try {
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        Add-Text $zip 'install.ps1' $setup
        $readme = [IO.File]::ReadAllText((Join-Path $root 'docs/getting-started.md'))
        Add-Text $zip 'README.md' ($readme.Replace('vX.Y.Z', "v$Version"))
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }
$assets = @($name)
if ($WithPortable) {
    & (Join-Path $PSScriptRoot 'package-windows-portable.ps1') -Version $Version -OutputDirectory $output
    $assets += "PilotProAdmin-v$Version-win-x64-portable.zip"
}
$checksums = foreach ($asset in $assets) {
    $file = Join-Path $output $asset
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host $file
    "$hash  $asset"
}
$checksums | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
