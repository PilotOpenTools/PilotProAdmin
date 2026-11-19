<#
  Общее для backup.ps1 и restore.ps1: тома мониторинга и образ для tar.
  Подключается точкой: . (Join-Path $PSScriptRoot 'monitoring.ps1')
#>

# Образ закреплён так же, как в шаблонах CI модулей (его же кладёт в комплект build.ps1 -Offline).
$tarImageDefault = 'alpine:3.20@sha256:d9e853e87e55526f6b2917df91a2115c36dd7c696a35be12163d44e6e2a4b6bc'

# Имя файла в копии не зависит от проекта compose: копию можно вернуть в стек с другим именем проекта.
# Совместимость: grafana_grafana_data.tgz читает установщик (install.sh, install.ps1).
$monitoring = [ordered]@{
    grafana    = @{ Volume = 'grafana_data';    Container = 'grafana';    File = 'grafana_grafana_data.tgz';       Exclude = './plugins' }
    loki       = @{ Volume = 'loki_data';       Container = 'loki';       File = 'grafana_loki_data.tgz';          Exclude = '' }
    prometheus = @{ Volume = 'prometheus_data'; Container = 'prometheus'; File = 'grafana_prometheus_data.tgz';   Exclude = '' }
}

# Проект compose, как в установщике: -Project, иначе COMPOSE_PROJECT_NAME из .env, иначе cicd.
# Explicit = false: имя взято по умолчанию, и для раскладки по папкам (metrics\docker-compose.yml, тома grafana_*) допустим запасной вариант.
function Get-ComposeProject([string]$project, [string[]]$envFiles) {
    if ($project) { return [pscustomobject]@{ Name = $project; Explicit = $true } }
    foreach ($f in $envFiles) {
        if (-not (Test-Path $f)) { continue }
        foreach ($line in [IO.File]::ReadAllLines($f)) {
            if ($line -match '^\s*COMPOSE_PROJECT_NAME\s*=\s*(.*?)\s*$' -and $Matches[1]) { return [pscustomobject]@{ Name = $Matches[1].Trim('"', "'"); Explicit = $true } }
        }
    }
    return [pscustomobject]@{ Name = 'cicd'; Explicit = $false }
}

function Test-DockerVolume([string]$name) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { & docker volume inspect $name *> $null; return ($LASTEXITCODE -eq 0) }
    finally { $ErrorActionPreference = $prev }
}

# Имя тома мониторинга; нет тома - ошибка с понятным текстом (том должен создать стек: docker compose up).
function Resolve-MonitoringVolume([string]$short, $project) {
    $name = "$($project.Name)_$short"
    if (Test-DockerVolume $name) { return $name }
    # Раскладка по папкам: metrics\docker-compose.yml закрепляет имена grafana_*, это проект «grafana».
    if (-not $project.Explicit -and (Test-DockerVolume "grafana_$short")) { return "grafana_$short" }
    throw "нет тома $name (проект compose '$($project.Name)'): задайте -Project или COMPOSE_PROJECT_NAME в .env; если мониторинга на этой машине нет, исключите эту часть"
}
