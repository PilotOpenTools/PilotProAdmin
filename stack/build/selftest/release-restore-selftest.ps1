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
foreach ($name in 'Get-ExpectedPostgresDatabases', 'Assert-CompleteBackup') {
    $function = $ast.Find({ param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    Invoke-Expression $function.Extent.Text
}

function Assert-Rejects([string]$File) {
    try { Assert-CompleteBackup $backup.FullName $manifest } catch {
        if ($_.Exception.Message -like "*$File*") { return }
        throw
    }
    throw "Backup accepted without $File."
}

$backup = [IO.Directory]::CreateTempSubdirectory('PilotProAdmin-restore-test-')
try {
    $manifest = '{"ok":true,"parts":{"grafana":"ok","postgres":"ok"},"volumes":{"rustfs":{"test":"cicd_rustfs_test"}}}' | ConvertFrom-Json
    $required = @('gitlab/config/gitlab-secrets.json', 'grafana/grafana_grafana_data.tgz')
    foreach ($contour in 'test', 'stage', 'prod') {
        $required += "postgres/$contour/globals.sql", "postgres/$contour/pilot-ice_ru.dump"
    }
    foreach ($file in $required) {
        $artifact = Join-Path $backup.FullName $file
        [void][IO.Directory]::CreateDirectory((Split-Path $artifact -Parent))
        [IO.File]::WriteAllText($artifact, 'fixture')
    }
    foreach ($contour in 'test', 'stage', 'prod') {
        $xml = Join-Path $backup.FullName "pilot/$contour/pilot-server/settings/settings.xml"
        [void][IO.Directory]::CreateDirectory((Split-Path $xml -Parent))
        [IO.File]::WriteAllText($xml, '<Configuration/>')
    }
    Assert-Rejects 'rustfs/rustfs_test.tgz'
    $rustfs = Join-Path $backup.FullName 'rustfs/rustfs_test.tgz'
    [void][IO.Directory]::CreateDirectory((Split-Path $rustfs -Parent))
    [IO.File]::WriteAllText($rustfs, '')
    Assert-Rejects 'rustfs/rustfs_test.tgz'
    [IO.File]::WriteAllText($rustfs, 'fixture')
    Assert-CompleteBackup $backup.FullName $manifest
    $testDump = Join-Path $backup.FullName 'postgres/test/pilot-ice_ru.dump'
    [IO.File]::Delete($testDump)
    Assert-Rejects 'postgres/test/pilot-ice_ru.dump'
    [IO.File]::WriteAllText($testDump, '')
    Assert-Rejects 'postgres/test/pilot-ice_ru.dump'
    [IO.File]::WriteAllText($testDump, 'fixture')

    $prodDump = Join-Path $backup.FullName 'postgres/prod/pilot-ice_ru.dump'
    [IO.File]::WriteAllText((Join-Path $backup.FullName 'postgres/prod/pilot_metrics.dump'), 'fixture')
    [IO.File]::Delete($prodDump)
    Assert-Rejects 'postgres/prod/pilot-ice_ru.dump'
    [IO.File]::WriteAllText($prodDump, 'fixture')

    $testXml = Join-Path $backup.FullName 'pilot/test/pilot-server/settings/settings.xml'
    [IO.File]::WriteAllText($testXml, '<Configuration><DatabaseParameters Name="Test Pilot"><PgConnectionString ConnectionString="Host=pg;Database=&quot;test;custom&quot;"/></DatabaseParameters></Configuration>')
    Assert-Rejects 'postgres/test/test;custom.dump'
    [IO.File]::WriteAllText((Join-Path $backup.FullName 'postgres/test/test;custom.dump'), 'fixture')
    Assert-CompleteBackup $backup.FullName $manifest

    $manifest | Add-Member -NotePropertyName postgresDatabases -NotePropertyValue (@'
{"test":["test;custom","project-archive"],"stage":["pilot-ice_ru"],"prod":["pilot-ice_ru","pilot_metrics"]}
'@ | ConvertFrom-Json)
    Assert-Rejects 'postgres/test/project-archive.dump'
    [IO.File]::WriteAllText((Join-Path $backup.FullName 'postgres/test/project-archive.dump'), 'fixture')
    [IO.File]::Delete((Join-Path $backup.FullName 'grafana/grafana_grafana_data.tgz'))
    Assert-Rejects 'grafana/grafana_grafana_data.tgz'
} finally {
    $backup.Delete($true)
}

function New-ScheduledTaskAction {
    throw 'The backup schedule must be rejected before any task is created.'
}

$installation = [IO.Directory]::CreateTempSubdirectory('PilotProAdmin-backup-schedule-test-')
$envFile = Join-Path $installation.FullName '.env'
try {
    [IO.File]::WriteAllLines($envFile, @(
        'ROLES=admin,gitlab,metrics,prod,test,stage'
        '  TEST_EXTERNAL_PG_HOST=postgres.invalid'
    ))
    $register = Join-Path $repo 'proadmin/transfer/register-task.ps1'
    try {
        & $register -InstallDirectory $installation.FullName
        throw 'The backup schedule accepted an external PostgreSQL host.'
    } catch {
        if ($_.Exception.Message -notlike '*TEST_EXTERNAL_PG_HOST*') { throw }
    }
} finally {
    [IO.File]::Delete($envFile)
    $installation.Delete()
}
Write-Host 'Release restore checks passed.'
