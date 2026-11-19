$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
. (Join-Path $repo 'stack/Install/release-ports.ps1')

$settings = @'
{
  "environments": [
    { "key": "prod", "pilotUrl": "http://localhost:5546/prod%20db" },
    { "key": "test", "pilotUrl": "http://localhost:5547/test-db" }
  ],
  "stagePilotUrl": "http://host.docker.internal:5549/stage-db",
  "gitLabGroupUrl": "http://localhost:5546/automation"
}
'@ | ConvertFrom-Json
Update-RestoredPilotPorts $settings @{ prod = 5546; test = 5547; stage = 5549 } `
    @{ PROD_PILOT_PORT = 5547; TEST_PILOT_PORT = 5548; STAGE_PILOT_PORT = 5550 } @('prod', 'test', 'stage')
if ($settings.environments[0].pilotUrl -ne 'http://localhost:5547/prod%20db' -or
    $settings.environments[1].pilotUrl -ne 'http://localhost:5548/test-db' -or
    $settings.stagePilotUrl -ne 'http://host.docker.internal:5550/stage-db' -or
    $settings.gitLabGroupUrl -ne 'http://localhost:5546/automation') {
    throw 'Pilot ports must change independently, preserving database paths and unrelated settings.'
}

$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $repo 'scripts/install-release.ps1'), [ref]$null, [ref]$null)
$function = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-CompleteBackup'
}, $true)
Invoke-Expression $function.Extent.Text

function Assert-Rejects([string]$File) {
    try { Assert-CompleteBackup $backup.FullName $manifest } catch {
        if ($_.Exception.Message -like "*$File*") { return }
        throw
    }
    throw "Backup accepted without $File."
}

$backup = [IO.Directory]::CreateTempSubdirectory('PilotProAdmin-restore-test-')
try {
    $manifest = '{"ok":true,"parts":{"grafana":"ok"},"volumes":{"rustfs":{"test":"cicd_rustfs_test"}}}' | ConvertFrom-Json
    $required = @('gitlab/config/gitlab-secrets.json', 'grafana/grafana_grafana_data.tgz')
    foreach ($contour in 'test', 'stage', 'prod') {
        $required += "postgres/$contour/globals.sql", "pilot/$contour/pilot-server/settings/settings.xml"
    }
    foreach ($file in $required) {
        $artifact = Join-Path $backup.FullName $file
        [void][IO.Directory]::CreateDirectory((Split-Path $artifact -Parent))
        [IO.File]::WriteAllText($artifact, 'fixture')
    }
    Assert-Rejects 'rustfs/rustfs_test.tgz'
    $rustfs = Join-Path $backup.FullName 'rustfs/rustfs_test.tgz'
    [void][IO.Directory]::CreateDirectory((Split-Path $rustfs -Parent))
    [IO.File]::WriteAllText($rustfs, '')
    Assert-Rejects 'rustfs/rustfs_test.tgz'
    [IO.File]::WriteAllText($rustfs, 'fixture')
    Assert-CompleteBackup $backup.FullName $manifest
    [IO.File]::Delete((Join-Path $backup.FullName 'grafana/grafana_grafana_data.tgz'))
    Assert-Rejects 'grafana/grafana_grafana_data.tgz'
} finally {
    $backup.Delete($true)
}
Write-Host 'Release restore checks passed.'
