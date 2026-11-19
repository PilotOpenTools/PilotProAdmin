#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path $PSScriptRoot
$scratch = [IO.Directory]::CreateTempSubdirectory('PilotProAdmin-portable-')
$bundle = Join-Path $scratch.FullName 'portable'
$path = Join-Path $OutputDirectory "PilotProAdmin-v$Version-win-x64-portable.zip"

function Test-PortableApp([string]$Directory, [string]$Name) {
    if (-not (Test-Path (Join-Path $Directory 'coreclr.dll') -PathType Leaf)) {
        throw "$Name не содержит среду выполнения .NET."
    }
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $listener.Start(); $url = "http://127.0.0.1:$($listener.LocalEndpoint.Port)" }
    finally { $listener.Stop() }
    $log = Join-Path $Directory 'startup.log'
    $errors = Join-Path $Directory 'errors.log'
    $process = Start-Process -FilePath (Join-Path $Directory "$Name.exe") -ArgumentList @('--urls', $url) `
        -WorkingDirectory $Directory -RedirectStandardOutput $log -RedirectStandardError $errors -PassThru -WindowStyle Hidden
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
            try {
                if ((Invoke-RestMethod "$url/health" -NoProxy -TimeoutSec 2) -eq 'ok') { return }
            } catch [System.Net.Http.HttpRequestException], [System.OperationCanceledException] {}
            Start-Sleep -Milliseconds 300
        }
        throw "$Name не прошёл проверку /health: $(Get-Content -LiteralPath $log, $errors -Raw)"
    } finally {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id }
        $process.Dispose()
    }
}

try {
    dotnet publish (Join-Path $root 'proadmin/src/ProAdmin/PilotProAdmin.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $bundle 'PilotProAdmin')
    dotnet publish (Join-Path $root 'proadmin/migration/src/PilotMigration/PilotMigration.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $bundle 'PilotMigration')
    Copy-Item (Join-Path $root 'docs/windows-portable.md') (Join-Path $bundle 'QUICKSTART.md')
    Copy-Item (Join-Path $root 'docs/configuration.md') $bundle
    $zip = Join-Path $scratch.FullName 'portable.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($bundle, $zip)
    $extracted = Join-Path $scratch.FullName 'check'
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $extracted)
    foreach ($name in 'PilotProAdmin', 'PilotMigration') {
        Test-PortableApp (Join-Path $extracted $name) $name
    }
    Move-Item -LiteralPath $zip -Destination $path -Force
} finally {
    [IO.Directory]::Delete($scratch.FullName, $true)
}
