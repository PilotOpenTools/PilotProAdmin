# Loaded by install.ps1 after the effective Compose profiles and backup settings are known.
function Update-RestoredPilotPorts($Settings, [hashtable]$OldPorts, [hashtable]$PublishedPorts, [string[]]$Contours) {
    function Update-Url([string]$Url, [string]$Contour) {
        $uri = $null
        $newPort = $PublishedPorts["$($Contour.ToUpperInvariant())_PILOT_PORT"]
        if ($newPort -and [uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$uri) -and
            ($uri.IsLoopback -or $uri.Host -eq 'host.docker.internal') -and
            $uri.Port -eq $OldPorts[$Contour] -and $uri.Port -ne $newPort) {
            $builder = [UriBuilder]::new($uri)
            $builder.Port = $newPort
            return $builder.Uri.AbsoluteUri
        }
        return $Url
    }

    foreach ($environment in $Settings.environments) {
        if ($environment.key -in $Contours -and $environment.PSObject.Properties['pilotUrl']) {
            $environment.pilotUrl = Update-Url $environment.pilotUrl $environment.key
        }
    }
    if ('stage' -in $Contours -and $Settings.PSObject.Properties['stagePilotUrl']) {
        $Settings.stagePilotUrl = Update-Url $Settings.stagePilotUrl 'stage'
    }
}

function Test-ReleasePort([int]$Port) {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::IPv6Any, $Port)
    try {
        $listener.Server.DualMode = $true
        $listener.ExclusiveAddressUse = $true
        $listener.Start()
        return $true
    } catch [Net.Sockets.SocketException] {
        return $false
    } finally {
        $listener.Stop()
    }
}

function Select-ReleasePorts {
    $keys = @{
        'proadmin:8080' = 'ADMIN_PORT'; 'migration:8080' = 'MIGRATION_PORT'
        'gitlab:80' = 'GITLAB_HTTP_PORT'; 'gitlab:22' = 'GITLAB_SSH_PORT'
        'grafana:3000' = 'GRAFANA_PORT'; 'report-metrics-collector:8080' = 'COLLECTOR_PORT'
        'prometheus:9090' = 'PROMETHEUS_PORT'; 'loki:3100' = 'LOKI_PORT'
        'node-exporter:9100' = 'NODE_EXPORTER_PORT'; 'cadvisor:8080' = 'CADVISOR_PORT'
    }
    foreach ($contour in 'test', 'stage', 'prod') {
        $prefix = $contour.ToUpperInvariant()
        $keys["pilot-server-${contour}:5545"] = "${prefix}_PILOT_PORT"
        $keys["pilot-web-myadmin-${contour}:5200"] = "${prefix}_MYADMIN_PORT"
        $keys["postgres-${contour}:5432"] = "${prefix}_PG_PORT"
        $keys["rustfs-${contour}:9000"] = "${prefix}_S3_PORT"
        $keys["rustfs-${contour}:9001"] = "${prefix}_S3_UI_PORT"
    }
    $compose = (Run docker compose config --format json | Out-String) | ConvertFrom-Json
    $reserved = [Collections.Generic.HashSet[int]]::new()
    $result = @{}
    $existingNames = @(Run docker ps -a --format '{{.Names}}')
    foreach ($service in $compose.services.PSObject.Properties) {
        $container = $service.Value.container_name
        $owned = $false
        if ($container -in $existingNames) {
            $owner = Run docker inspect --format '{{index .Config.Labels "com.docker.compose.project.working_dir"}}' $container
            $ownerProject = Run docker inspect --format '{{index .Config.Labels "com.docker.compose.project"}}' $container
            $owned = $owner -and $owner -ne '<no value>' -and $ownerProject -eq $compose.name -and
                [IO.Path]::GetFullPath($owner) -eq [IO.Path]::GetFullPath($PSScriptRoot)
            if (-not $owned) { throw "Контейнер $container уже принадлежит другой установке. Подключите существующий сервис либо выберите отдельную машину." }
        }
        foreach ($binding in $service.Value.ports) {
            if (-not $binding.published) { continue }
            $port = [int]$binding.published
            $key = $keys["$($service.Name):$($binding.target)"]
            if (-not $key) { throw "Неизвестный опубликованный порт $port ($($service.Name))." }
            $ownPort = $false
            if ($owned) {
                $inspect = (Run docker inspect --format '{{json .NetworkSettings.Ports}}' $container | Out-String) | ConvertFrom-Json
                $ownPort = $port -in @($inspect."$($binding.target)/tcp" | ForEach-Object HostPort)
            }
            if ($reserved.Contains($port) -or (-not $ownPort -and -not (Test-ReleasePort $port))) {
                $suggestion = $port + 1
                while ($suggestion -le 65535 -and ($reserved.Contains($suggestion) -or -not (Test-ReleasePort $suggestion))) {
                    $suggestion++
                }
                do {
                    $prompt = if ($suggestion -le 65535) { "Порт $port занят ($($service.Name)). Новый порт [$suggestion]" }
                              else { "Порт $port занят ($($service.Name)). Укажите свободный порт" }
                    $answer = (Read-Host $prompt).Trim()
                    if (-not $answer -and $suggestion -le 65535) { $answer = "$suggestion" }
                    $valid = $answer -match '^\d{1,5}$' -and [int]$answer -ge 1 -and [int]$answer -le 65535 -and
                        -not $reserved.Contains([int]$answer) -and (Test-ReleasePort ([int]$answer))
                    if (-not $valid) { Write-Warning 'Введите свободный TCP-порт от 1 до 65535.' }
                } until ($valid)
                $port = [int]$answer
            }
            $null = $reserved.Add($port)
            $result[$key] = $port
        }
    }
    $result
}
