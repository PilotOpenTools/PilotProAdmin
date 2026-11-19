<#
  Резервная копия стека Pilot CI/CD.

    .\backup\backup.ps1                          всё, в D:\Backups, хранить 14 дней
    .\backup\backup.ps1 -Root E:\Backups -KeepDays 30
    .\backup\backup.ps1 -Skip gitlab,grafana     пропустить части

  Части (каталог <Root>\<yyyy-MM-dd_HHmm>\):
    postgres  pg_dump -Fc каждой базы контуров prod/test/stage + роли (pg_dumpall --globals-only)
    pilot     settings.xml, server-settings, license, databases (там же .server-modules) контуров
    rustfs    файловые архивы Pilot (S3) контуров - том Docker RustFS каждого контура (<проект>_rustfs_<контур>; в раздельной
              раскладке pilot-<контур>_rustfs_data) одноразовым контейнером alpine (tar), файл rustfs\rustfs_<контур>.tgz;
              в манифесте volumes.rustfs.<контур>. Контуры без контейнера rustfs-<контур> пропускаются.
    gitlab    gitlab-backup create (репозитории, БД, артефакты, пакеты) + config/ (gitlab-secrets.json!) + runner-config
    admin     data/ админки и миграции, все .env
    grafana   том grafana_data (пользователи, источники, правки дашбордов; без plugins)
    loki      том loki_data (логи)
    prometheus том prometheus_data (метрики)
              Тома мониторинга архивируются одноразовым контейнером alpine (tar), имена - <проект>_grafana_data и т. д.:
              проект из -Project или COMPOSE_PROJECT_NAME в .env (по умолчанию cicd; для раскладки по папкам, где тома
              называются grafana_*, подходит и умолчание). Копия снимается при работающих сервисах.
    images   закреплённые образы, которых может больше не быть в реестрах (образы, закреплённые по digest, и у Pilot плавающий :release);
              лежат в <Root>\images отдельно от запусков, сохраняются один раз на digest и по сроку не удаляются

  Порядок: сначала Postgres, потом файлы - в копии файлов есть всё, на что ссылается дамп.
  Итог - manifest.json в каталоге запуска и last-backup.json в корне; код выхода 1, если какая-то часть упала.
  В бэкапе лежат секреты (.env, gitlab-secrets.json): каталог должен быть доступен только администратору.
  Восстановление и проверка - restore.ps1, описание - README.md рядом.
#>
param(
    [string]$Root = 'D:\Backups',
    [int]$KeepDays = 14,
    [ValidateSet('postgres', 'pilot', 'rustfs', 'gitlab', 'admin', 'grafana', 'loki', 'prometheus', 'images')][string[]]$Skip = @(),
    [string]$DataRoot = 'C:\Ascon-Docker',
    [string]$Project,
    [string]$TarImage,
    [string]$StackDirectory = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'stack'),
    [string]$InstallDirectory,
    [switch]$ForTransfer,
    [switch]$Archive
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'monitoring.ps1')
if (-not $TarImage) { $TarImage = $tarImageDefault }
$cicd = $StackDirectory                 # корень раздельного стека (prod, test, stage, metrics)
$ws = Split-Path $cicd -Parent           # C:\CICD: gitlab и proadmin лежат рядом со стеком
$envs = 'prod', 'test', 'stage'
$installEnv = $null
if ($InstallDirectory) {
    $installEnv = Join-Path (Resolve-Path -LiteralPath $InstallDirectory).Path '.env'
    $lines = [IO.File]::ReadAllLines($installEnv)
    $dataLine = $lines | Where-Object { $_ -match '^DATA_ROOT=' } | Select-Object -Last 1
    if (-not $PSBoundParameters.ContainsKey('DataRoot') -and $dataLine) { $DataRoot = $dataLine.Substring(10).Trim('"', "'") }
    $envFiles = @($installEnv)
    $gitLabRoot = Join-Path $DataRoot 'gitlab'
} else {
    $envFiles = @((Join-Path $cicd '.env'), (Join-Path $cicd 'Install\.env'))
    $gitLabRoot = Join-Path $ws 'gitlab'
}
$composeProject = Get-ComposeProject $Project $envFiles
$volumes = [ordered]@{}   # тома мониторинга, попавшие в копию
$postgresDatabases = [ordered]@{}

$stamp = Get-Date -Format 'yyyy-MM-dd_HHmm'
if ($ForTransfer) { $stamp += '_' + [guid]::NewGuid().ToString('N').Substring(0, 8) }
$run = Join-Path $Root $stamp
New-Item -ItemType Directory -Force $run | Out-Null
function Protect-TransferOutput([string]$path, [string]$inheritance) {
    $user = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    & icacls $path /inheritance:r /grant:r "*${user}:${inheritance}F" "*S-1-5-18:${inheritance}F" "*S-1-5-32-544:${inheritance}F" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Не удалось ограничить доступ к $path." }
}
if ($ForTransfer) { Protect-TransferOutput $run '(OI)(CI)' }
$log = Join-Path $run 'backup.log'
$results = [ordered]@{}

function Log([string]$msg) {
    $line = '{0:HH:mm:ss} {1}' -f (Get-Date), $msg
    Write-Host $line
    Add-Content -Path $log -Value $line -Encoding UTF8
}

# Нативная команда: вывод в лог, ошибка по коду выхода (в PS 5.1 stderr при Stop бросал бы исключение).
function Exec([string]$exe, [string[]]$argList) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        & $exe @argList 2>&1 | ForEach-Object { "$_" } | Out-File -FilePath $log -Append -Encoding UTF8
        if ($LASTEXITCODE -ne 0) { throw "$exe $($argList -join ' ') -> код $LASTEXITCODE" }
    }
    finally { $ErrorActionPreference = $prev }
}

# Нативная команда, нужен её stdout (текст).
function ExecOut([string]$exe, [string[]]$argList) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        $out = & $exe @argList 2>$null
        if ($LASTEXITCODE -ne 0) { throw "$exe $($argList -join ' ') -> код $LASTEXITCODE" }
        return $out
    }
    finally { $ErrorActionPreference = $prev }
}

# robocopy: коды 0-7 - успех. Нет источника - ошибка части (иначе копия объявлялась бы удачной без данных), кроме -Optional.
function CopyTree([string]$from, [string]$to, [switch]$Optional) {
    if (-not (Test-Path $from)) { if ($Optional) { Log "  нет $from - пропуск"; return } throw "нет источника $from" }
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        & robocopy $from $to /E /COPY:DAT /R:2 /W:2 /NP /NFL /NDL /XJ 2>&1 | Out-File -FilePath $log -Append -Encoding UTF8
        if ($LASTEXITCODE -ge 8) { throw "robocopy $from -> код $LASTEXITCODE" }
    }
    finally { $ErrorActionPreference = $prev }
}

function Running([string]$container) {
    $state = ExecOut docker @('ps', '--filter', "name=^$container$", '--format', '{{.Names}}')
    return [bool]$state
}

# Том RustFS контура: <проект>_rustfs_<контур> (compose установщика); имя проекта по умолчанию - ещё и раскладка по папкам
# (name: pilot-<контур>, ключ тома rustfs_data). Нет тома - $null. (Та же функция в restore.ps1.)
function Resolve-RustfsVolume([string]$contour, $project) {
    $names = @("$($project.Name)_rustfs_$contour")
    if (-not $project.Explicit) { $names += "pilot-${contour}_rustfs_data", "${contour}_rustfs_data" }
    foreach ($n in $names) { if (Test-DockerVolume $n) { return $n } }
    return $null
}

function Part([string]$name, [scriptblock]$body) {
    if ($Skip -contains $name) { $results[$name] = 'skipped'; Log "== ${name}: пропущено"; return }
    Log "== $name"
    $t = Get-Date
    try {
        & $body
        $results[$name] = 'ok'
        Log ("== {0}: ok ({1:n0} с)" -f $name, ((Get-Date) - $t).TotalSeconds)
    }
    catch {
        $results[$name] = "error: $($_.Exception.Message)"
        Log "== ${name}: ОШИБКА $($_.Exception.Message)"
    }
}

Log "Бэкап в $run"

# После сна или входа в Windows Docker Desktop поднимается не сразу: без него Postgres и GitLab в копию не попадут.
$dockerDeadline = (Get-Date).AddMinutes(10)
while ($true) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    & docker info *> $null
    $dockerOk = $LASTEXITCODE -eq 0
    $ErrorActionPreference = $prev
    if ($dockerOk) { break }
    if ((Get-Date) -gt $dockerDeadline) { Log 'Docker не запустился за 10 минут - части с контейнерами завершатся ошибкой'; break }
    Log 'Жду Docker...'
    Start-Sleep 30
}

Part 'postgres' {
    $staging = "/tmp/pilot-backup-$([guid]::NewGuid().ToString('N'))"
    foreach ($e in $envs) {
        $c = "postgres-$e"
        if (-not (Running $c)) { throw "$c не запущен" }
        $dir = Join-Path $run "postgres\$e"
        New-Item -ItemType Directory -Force $dir | Out-Null
        Exec docker @('exec', $c, 'mkdir', '-m', '700', $staging)
        try {
            Exec docker @('exec', $c, 'pg_dumpall', '-U', 'postgres', '--globals-only', '-f', "$staging/globals.sql")
            Exec docker @('cp', "${c}:$staging/globals.sql", (Join-Path $dir 'globals.sql'))
            $dbs = @(ExecOut docker @('exec', $c, 'psql', '-U', 'postgres', '-Atc', "select datname from pg_database where not datistemplate and datname <> 'postgres'") | Where-Object { $_ })
            foreach ($db in $dbs) {
                Exec docker @('exec', $c, 'pg_dump', '-U', 'postgres', '-Fc', '-f', "$staging/$db.dump", $db)
                Exec docker @('cp', "${c}:$staging/$db.dump", (Join-Path $dir "$db.dump"))
                Log ("  {0}/{1}: {2:n1} МБ" -f $e, $db, ((Get-Item (Join-Path $dir "$db.dump")).Length / 1MB))
            }
            $postgresDatabases[$e] = $dbs
        } finally {
            Exec docker @('exec', $c, 'rm', '-rf', '--', $staging)
        }
    }
}

Part 'pilot' {
    foreach ($e in $envs) {
        if ($ForTransfer -and -not (Test-Path (Join-Path $DataRoot "$e/pilot-server/settings/settings.xml") -PathType Leaf)) {
            throw "Не найден settings.xml контура $e в $DataRoot."
        }
        foreach ($sub in 'pilot-server', 'server-settings', 'license', 'databases') {
            # license, databases и server-settings бывают пустыми или отсутствуют; settings.xml обязателен.
            CopyTree (Join-Path $DataRoot "$e\$sub") (Join-Path $run "pilot\$e\$sub") -Optional:($sub -ne 'pilot-server' -or -not (Test-Path (Join-Path $DataRoot $e)))
        }
    }
}

Part 'rustfs' {
    $dir = Join-Path $run 'rustfs'
    $got = [ordered]@{}
    foreach ($e in $envs) {
        $vol = Resolve-RustfsVolume $e $composeProject
        if (-not $vol) {
            # Контура на этой машине нет - пропуск; контейнер есть, а тома нет - ошибка части.
            if ($ForTransfer -or (Running "rustfs-$e")) { throw "нет тома $($composeProject.Name)_rustfs_$e (проект compose '$($composeProject.Name)'): задайте -Project или COMPOSE_PROJECT_NAME в .env" }
            Log "  ${e}: контура нет - пропуск"; continue
        }
        New-Item -ItemType Directory -Force $dir | Out-Null
        $file = "rustfs_$e.tgz"; $tmp = Join-Path $dir "$file.tmp"
        # Том только для чтения; после упаковки архив перечитывается, чтобы оборванный tar не стал «копией».
        try {
            Exec docker @('run', '--rm', '-v', "${vol}:/v:ro", '-v', "${dir}:/b", $TarImage, 'sh', '-c', "tar czf /b/$file.tmp -C /v . && tar tzf /b/$file.tmp > /dev/null")
            Move-Item -Force $tmp (Join-Path $dir $file)
        }
        finally { if (Test-Path $tmp) { Remove-Item $tmp } }
        $got[$e] = $vol
        Log ("  {0}: {1:n1} МБ" -f $vol, ((Get-Item (Join-Path $dir $file)).Length / 1MB))
    }
    if (-not $got.Count) { throw 'не найден ни один том RustFS (контуров prod/test/stage на этой машине нет?): если файлового хранилища здесь нет, исключите часть: -Skip rustfs' }
    $volumes['rustfs'] = $got
}

Part 'gitlab' {
    if (-not (Running 'gitlab')) { throw 'gitlab не запущен' }
    if ($ForTransfer -and -not (Test-Path -LiteralPath (Join-Path $gitLabRoot 'config/gitlab-secrets.json') -PathType Leaf)) {
        throw "В $gitLabRoot/config нет gitlab-secrets.json."
    }
    # STRATEGY=copy - консистентная копия при работающем GitLab; файл ложится в data/backups (bind-том).
    $archiveName = "${stamp}_gitlab_backup.tar"
    Exec docker @('exec', '-e', 'GITLAB_BACKUP_MAX_CONCURRENCY=2', 'gitlab', 'gitlab-backup', 'create', "BACKUP=$stamp", 'STRATEGY=copy')
    $tar = Get-Item -LiteralPath (Join-Path $gitLabRoot "data/backups/$archiveName")
    $dir = Join-Path $run 'gitlab'
    New-Item -ItemType Directory -Force $dir | Out-Null
    # A same-volume move can retain permissions from GitLab's backup directory.
    if ($ForTransfer) {
        Copy-Item -LiteralPath $tar.FullName -Destination $dir
        Remove-Item -LiteralPath $tar.FullName
    } else { Move-Item -LiteralPath $tar.FullName -Destination $dir }
    # Без gitlab-secrets.json бэкап не восстановить (токены, 2FA, переменные CI зашифрованы им).
    CopyTree (Join-Path $gitLabRoot 'config') (Join-Path $dir 'config')
    CopyTree (Join-Path $gitLabRoot 'runner-config') (Join-Path $dir 'runner-config') -Optional
    Log ("  gitlab: {0:n1} МБ" -f ($tar.Length / 1MB))
}

Part 'admin' {
    if ($installEnv) {
        CopyTree (Join-Path $DataRoot 'services/proadmin') (Join-Path $run 'admin/proadmin-data')
        CopyTree (Join-Path $DataRoot 'services/migration') (Join-Path $run 'admin/migration-data')
        $target = Join-Path $run 'admin/env/proadmin'
        New-Item -ItemType Directory -Force $target | Out-Null
        Copy-Item -LiteralPath $installEnv -Destination $target
        return
    }
    CopyTree (Join-Path $ws 'proadmin\data') (Join-Path $run 'admin\proadmin-data')
    CopyTree (Join-Path $ws 'proadmin\migration\data') (Join-Path $run 'admin\migration-data')
    # Пути в бэкапе: prod\.env, metrics\.env (от корня стека) и gitlab\.env, proadmin\.env (от C:\CICD).
    $envSources = @(@($cicd, $cicd), @((Join-Path $ws 'gitlab'), $ws), @((Join-Path $ws 'proadmin'), $ws))
    $envSources | ForEach-Object { $base = $_[1]; Get-ChildItem $_[0] -Recurse -Force -File -Filter '.env' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(mcp|node_modules|bin|obj|Install|data|logs)\\' } |
        ForEach-Object {
            $rel = $_.FullName.Substring($base.Length + 1)
            $dst = Join-Path $run "admin\env\$rel"
            New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
            Copy-Item $_.FullName $dst
            Log "  $rel"
        } }
    # Без .env контура бэкап не восстановить (пароли PostgreSQL, MinIO и settings.xml связаны с ними): это ошибка, а не пропуск.
    $missingEnv = @($envs | Where-Object { (Test-Path (Join-Path $DataRoot $_)) -and -not (Test-Path (Join-Path $run "admin\env\$_\.env")) })
    if ($missingEnv) { throw "нет .env контуров: $($missingEnv -join ', ') (ищутся в $cicd\<контур>\.env; бэкап нужно снимать из папки, откуда запущен стек)" }
}

foreach ($part in $monitoring.Keys) {
    Part $part {
        $m = $monitoring[$part]
        $vol = Resolve-MonitoringVolume $m.Volume $composeProject
        $dir = Join-Path $run $part
        New-Item -ItemType Directory -Force $dir | Out-Null
        $tmp = Join-Path $dir "$($m.File).tmp"
        $exclude = if ($m.Exclude) { " --exclude=$($m.Exclude)" } else { '' }
        # Том только для чтения; после упаковки архив перечитывается, чтобы оборванный tar не стал «копией».
        try {
            Exec docker @('run', '--rm', '-v', "${vol}:/v:ro", '-v', "${dir}:/b", $TarImage, 'sh', '-c', "tar czf /b/$($m.File).tmp -C /v$exclude . && tar tzf /b/$($m.File).tmp > /dev/null")
            Move-Item -Force $tmp (Join-Path $dir $m.File)
        }
        finally { if (Test-Path $tmp) { Remove-Item $tmp } }
        $volumes[$part] = $vol
        Log ("  {0}: {1:n1} МБ" -f $vol, ((Get-Item (Join-Path $dir $m.File)).Length / 1MB))
    }
}

Part 'images' {
    $dir = Join-Path $Root 'images'
    New-Item -ItemType Directory -Force $dir | Out-Null
    $refs = foreach ($f in 'prod\docker-compose.yml', 'stage\init-stage.sh') {
        [regex]::Matches([IO.File]::ReadAllText((Join-Path $cicd $f)), '(?:rustfs/rustfs|[\w.][\w./-]*pilot-server|pilotdev/pilot-web-myadmin):[^@\s}]+@sha256:[0-9a-f]{64}') | ForEach-Object { $_.Value }
    }
    foreach ($ref in @($refs | Sort-Object -Unique)) {
        $tar = Join-Path $dir (($ref -replace '@sha256:(.{12}).*$', '_$1' -replace '[^\w.-]', '_') + '.tar')
        if (Test-Path $tar) { continue }
        Exec docker @('save', '-o', "$tar.tmp", $ref)
        Move-Item "$tar.tmp" $tar
        Log ("  {0}: {1:n0} МБ" -f (Split-Path $tar -Leaf), ((Get-Item $tar).Length / 1MB))
    }
}

# Итог
$failed = @($results.Keys | Where-Object { $results[$_] -like 'error*' })
$size = (Get-ChildItem $run -Recurse -File | Measure-Object Length -Sum).Sum
$commit = ''
try { $commit = (ExecOut git @('-C', $cicd, 'rev-parse', '--short', 'HEAD')) } catch { }
$manifest = [ordered]@{
    created  = (Get-Date).ToString('o')
    host     = $env:COMPUTERNAME
    folder   = $run
    sizeMB   = [math]::Round($size / 1MB, 1)
    cicdCommit = "$commit"
    parts    = $results
    volumes  = $volumes
    postgresDatabases = $postgresDatabases
    ok       = ($failed.Count -eq 0)
}
$json = $manifest | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText((Join-Path $run 'manifest.json'), $json, (New-Object Text.UTF8Encoding $false))
[IO.File]::WriteAllText((Join-Path $Root 'last-backup.json'), $json, (New-Object Text.UTF8Encoding $false))

# Хранение: удаляются только каталоги запусков (с manifest.json) старше KeepDays; последний удачный остаётся всегда.
if ($KeepDays -gt 0 -and $failed.Count -eq 0) {
    $limit = (Get-Date).AddDays(-$KeepDays)
    Get-ChildItem $Root -Directory | Where-Object {
        $_.Name -match '^\d{4}-\d{2}-\d{2}_\d{4}$' -and $_.FullName -ne $run -and
        $_.CreationTime -lt $limit -and (Test-Path (Join-Path $_.FullName 'manifest.json'))
    } | ForEach-Object { Log "Удаляю старый бэкап $($_.Name)"; Remove-Item $_.FullName -Recurse -Force }
}

if ($failed.Count) { Log "ГОТОВО С ОШИБКАМИ: $($failed -join ', ') - $($manifest.sizeMB) МБ"; exit 1 }
Log "ГОТОВО: $($manifest.sizeMB) МБ"
if ($Archive) {
    $archivePath = Join-Path $Root "$stamp.tar"
    New-Item -ItemType File -Path $archivePath | Out-Null
    try {
        Protect-TransferOutput $archivePath ''
        Exec tar.exe @('-cf', $archivePath, '-C', $run, '.')
    }
    catch {
        Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
        throw
    }
    Write-Host "Архив переноса: $archivePath"
}
if ($ForTransfer) { Write-Host "Папка для параметра install.ps1 -Backup: $run" }
exit 0
