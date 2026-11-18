#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path $PSScriptRoot
$admin = Join-Path $root 'proadmin/src/ProAdmin/PilotProAdmin.csproj'
$migration = Join-Path $root 'proadmin/migration/src/PilotMigration/PilotMigration.csproj'
$project = [xml](Get-Content -LiteralPath $admin -Raw)
if ($project.Project.PropertyGroup.Version -ne $Version) {
    throw "Тег v$Version расходится с версией в PilotProAdmin.csproj."
}

function Test-ReleaseExecutable($Directory, $Name, $Port) {
    $url = "http://127.0.0.1:$Port"
    $log = Join-Path $Directory 'startup.log'
    $errors = Join-Path $Directory 'errors.log'
    $process = Start-Process -FilePath (Join-Path $Directory "$Name.exe") -ArgumentList @('--urls', $url) `
        -WorkingDirectory $Directory -RedirectStandardOutput $log -RedirectStandardError $errors -PassThru -WindowStyle Hidden
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            if ($process.HasExited) { break }
            if ((Get-Content -LiteralPath $log -Raw) -like "*Now listening on: $url*" -and
                (Invoke-RestMethod "$url/health" -TimeoutSec 2) -eq 'ok') { return }
            Start-Sleep -Milliseconds 300
        } while ([DateTime]::UtcNow -lt $deadline)
        throw "$Name не прошёл проверку /health: $(Get-Content -LiteralPath $log, $errors -Raw)"
    } finally {
        if (!$process.HasExited) { Stop-Process -Id $process.Id }
        $process.Dispose()
    }
}

$scratch = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) "PilotProAdmin-release-$([guid]::NewGuid())")
$checksums = @()
$assets = @()
try {
    foreach ($edition in 'lite', 'portable') {
        $bundle = Join-Path $scratch.FullName $edition
        $zip = Join-Path $scratch.FullName "PilotProAdmin-v$Version-win-x64-$edition.zip"
        $selfContained = ($edition -eq 'portable').ToString().ToLowerInvariant()
        dotnet publish $admin -c Release -r win-x64 --self-contained $selfContained -o (Join-Path $bundle 'PilotProAdmin')
        dotnet publish $migration -c Release -r win-x64 --self-contained $selfContained -o (Join-Path $bundle 'PilotMigration')
        Copy-Item (Join-Path $root 'docs/getting-started.md') (Join-Path $bundle 'QUICKSTART.md')
        Copy-Item (Join-Path $root 'docs/configuration.md') $bundle
        [IO.Compression.ZipFile]::CreateFromDirectory($bundle, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
        $extracted = "$bundle-check"
        [IO.Compression.ZipFile]::ExtractToDirectory($zip, $extracted)
        foreach ($name in 'PilotProAdmin', 'PilotMigration') {
            $app = Join-Path $extracted $name
            if ((Test-Path -LiteralPath (Join-Path $app 'coreclr.dll')) -ne ($edition -eq 'portable')) {
                throw "$edition/$name имеет неверный состав среды .NET."
            }
            $port = if ($name -eq 'PilotProAdmin') { 15300 } else { 15320 }
            Test-ReleaseExecutable $app $name $port
        }
        $assets += $zip
        $checksums += '{0}  {1}' -f (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $zip -Leaf)
    }
    $output = New-Item -ItemType Directory -Path (Join-Path $root 'artifacts') -Force
    Copy-Item -LiteralPath $assets -Destination $output.FullName -Force
    $checksums | Set-Content -LiteralPath (Join-Path $output.FullName 'SHA256SUMS.txt') -Encoding ascii
} finally {
    # The scratch path belongs to this build. Never recursively delete the output directory.
    [IO.Directory]::Delete($scratch.FullName, $true)
}
