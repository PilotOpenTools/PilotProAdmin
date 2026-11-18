<#
  Управление стеком по папкам: prod, test, stage, admin (gitlab, proadmin + migration), metrics.

    .\shared\stack.ps1 up                 поднять всё в нужном порядке
    .\shared\stack.ps1 up prod            поднять один контур / группу
    .\shared\stack.ps1 down metrics       остановить группу (данные и тома не трогаются)
    .\shared\stack.ps1 ps                 состояние контейнеров
    .\shared\stack.ps1 build admin        пересобрать и поднять (образы proadmin, migration, сборщики метрик)

  Порядок запуска: контуры Pilot / admin / metrics (сборщики автоматизаций читают лог из pilot-server-test/stage).
#>
param(
    [Parameter(Mandatory)][ValidateSet('up', 'down', 'ps', 'build')][string]$Action,
    [string]$Target = 'all'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$groups = [ordered]@{
    prod    = @('prod')
    test    = @('test')
    stage   = @('stage')
    admin   = @('..\gitlab', '..\proadmin')
    metrics = @('metrics')
}
$dirs = if ($Target -eq 'all') { $groups.Values | ForEach-Object { $_ } } else {
    if (-not $groups.Contains($Target)) { throw "Неизвестная цель '$Target'. Допустимо: all, $($groups.Keys -join ', ')" }
    $groups[$Target]
}
if ($Action -eq 'down') { $dirs = @($dirs); [array]::Reverse($dirs) }

# Сети контуров создаются один раз; compose помечает их как external.
foreach ($n in 'pilot-prod', 'pilot-test', 'pilot-stage') {
    if (-not (docker network ls --format '{{.Name}}' | Where-Object { $_ -eq $n })) { docker network create $n | Out-Null }
}

$commit = try { git -C $root rev-parse --short HEAD 2>$null } catch { $null }

foreach ($d in $dirs) {
    Write-Host "== $d : $Action" -ForegroundColor Cyan
    Push-Location (Join-Path $root $d)
    try {
        switch ($Action) {
            'up'    { docker compose up -d }
            'build' {
                docker compose up -d --build
                # Наши образы (pilot-*:latest) дополнительно получают тег коммита: к нему можно откатиться.
                if ($LASTEXITCODE -eq 0 -and $commit) {
                    docker compose config --images | Where-Object { $_ -match '^pilot-[^:/]+:latest$' } |
                        ForEach-Object { docker tag $_ ($_ -replace ':latest$', ":$commit") }
                }
            }
            'down'  { docker compose down }
            'ps'    { docker compose ps }
        }
        if ($LASTEXITCODE -ne 0) { throw "docker compose завершился с кодом $LASTEXITCODE в $d" }
    }
    finally { Pop-Location }
}
