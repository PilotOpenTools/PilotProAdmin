<#
  Подготовка папки Install к передаче: заполняет images\, data\ и monitoring\. После этого всю папку
  можно скопировать на другой сервер и запустить там install.ps1 (см. README.md).

  .\build\build.ps1                    # (из корня стека) пересобрать наши образы, снять свежий бэкап (стек должен работать)
  .\build\build.ps1 -NoBuild           # образы взять те, что стоят локально
  .\build\build.ps1 -DataFrom D:\Backups\2026-11-03_0300   # данные из готового бэкапа
  .\build\build.ps1 -NoData            # без данных: data\ удаляется, остаётся только чистая установка
  .\build\build.ps1 -Offline           # в images\ все образы, в том числе для CI: установка без интернета

  images\, data\ и PASSWORDS.md в git не попадают: в них все пароли. PASSWORDS.md собирает build\make-passwords.ps1.
#>
param([switch]$NoBuild, [switch]$NoData, [string]$DataFrom, [switch]$Offline)
$ErrorActionPreference = 'Stop'
$src     = Split-Path $PSScriptRoot -Parent           # корень стека
$install = "$src\Install"
$ws      = Split-Path $src -Parent                    # C:\CICD: proadmin и gitlab лежат рядом со стеком

function Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
. "$PSScriptRoot\kit-secrets.ps1"
function Run { & $args[0] $args[1..($args.Count - 1)]; if ($LASTEXITCODE) { throw "Ошибка: $($args -join ' ')" } }

# Проверки до долгих шагов: без них сборка падает на середине с непонятной ошибкой.
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw 'Нет git: он нужен для бандлов модулей (modules\sources). Установите Git for Windows (https://git-scm.com) и откройте новое окно PowerShell.'
}
if (-not $NoData -and -not $DataFrom) {
    # Бэкап берёт .env контуров, gitlab\config и данные админки из папок рядом с этим стеком. Если стек запущен
    # из другой папки (например, это распакованный архив), здесь этих файлов нет: комплект получился бы без паролей.
    # Метки JSON-ом: кавычки внутри шаблона docker PowerShell 5.1 съедает при передаче аргументов.
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $labels = & docker inspect postgres-prod --format '{{json .Config.Labels}}' 2>$null
    $ErrorActionPreference = $prev
    $wd = if ($LASTEXITCODE -eq 0 -and $labels) { ($labels | ConvertFrom-Json).'com.docker.compose.project.working_dir' }
    if (-not $wd) { throw 'Контейнер postgres-prod не найден: свежий бэкап снимается с работающего стека. Запустите стек или укажите -DataFrom <каталог бэкапа> (или -NoData).' }
    if ((Resolve-Path -LiteralPath $wd -ErrorAction SilentlyContinue).Path -ne (Resolve-Path -LiteralPath "$src\prod").Path) {
        throw "Стек запущен из $wd, а не из $src. Бэкап отсюда не найдёт .env, gitlab\config и данные админки. Запустите build.ps1 из папки работающего стека или снимите бэкап там (backup\backup.ps1) и укажите -DataFrom."
    }
}

$ours = [ordered]@{
    'pilot-proadmin:latest'           = "$ws\proadmin"
    'pilot-migration:latest'          = "$ws\proadmin\migration"
    'pilot-report-metrics:latest'     = "$src\metrics\report-collector"
    'pilot-automation-metrics:latest' = "$src\metrics\automation-collector"
}
$compose = [IO.File]::ReadAllText("$install\docker-compose.yml")   # не Get-Content: PS 5.1 читает UTF-8 без BOM как ANSI
# RustFS: хранилище стенда и клиент rc (бакет, пользователь). В compose образ без digest (после docker load у образа его нет),
# поэтому тег берём из compose, а при скачивании сверяем с дайджестом индекса 1.0.5 (linux/amd64 и linux/arm64).
$rustfs = [regex]::Match($compose, '(?m)^\s*image:\s*(rustfs/rustfs:\S+)').Groups[1].Value
if (-not $rustfs) { throw "В $install\docker-compose.yml не найден образ rustfs/rustfs:<тег>" }
$rustfsPinned = if ($rustfs -eq 'rustfs/rustfs:1.0.5') { "$rustfs@sha256:b81d16a90ee25bd9511280127ca1471788c6e874cf0f0da759645f46b2dc1d74" }
$pack = @($ours.Keys) + $rustfs
if (-not (docker images -q $rustfs)) {
    if ($rustfsPinned) { Run docker pull $rustfsPinned; Run docker tag $rustfsPinned $rustfs } else { Run docker pull $rustfs }
}
# Сторонние образы с digest: в обычной сборке docker скачивает их при установке.
$pinned = @([regex]::Matches($compose, '(?m)^\s*image:\s*(?:\$\{\w+:-)?([^\s}]+@sha256:[0-9a-f]+)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
if ($Offline) {
    # Образы jobs: runner берёт их с той же машины. Модули со старым шаблоном CI ссылаются на образы без digest.
    $ciFiles = @("$ws\client-modules\ci-template\.gitlab-ci.yml", "$ws\server-extensions\ci-template\.gitlab-ci.yml") + @(Get-ChildItem "$ws\gitlab\ci-checks\*.yml" | ForEach-Object FullName)
    $ci = foreach ($f in $ciFiles) { [regex]::Matches([IO.File]::ReadAllText($f), '[\w./-]+:[\w.-]+@sha256:[0-9a-f]{64}') | ForEach-Object Value }
    $runnerVersion = [regex]::Match($compose, 'gitlab-runner:(v[\d.]+)@').Groups[1].Value
    $ci = @($ci) + "registry.gitlab.com/gitlab-org/gitlab-runner/gitlab-runner-helper:x86_64-$runnerVersion" + 'alpine:3.20' + 'registry.gitlab.com/gitlab-org/cli:latest'
    $pack = @($pack) + $pinned + $ci | Select-Object -Unique
    foreach ($img in $pack) { if (-not $ours.Contains($img) -and -not (docker images -q $img)) { Run docker pull $img } }
    $info = docker info --format '{{.DriverStatus}}'
    if ($info -notmatch 'containerd') { throw 'Для -Offline нужно хранилище образов containerd: на обычном docker load теряет digest, и compose не найдёт образы.' }
}

# --- 1. наши образы из исходников ---
if (-not $NoBuild) {
    Step "Сборка наших образов"
    # Коммит и время сборки в метках образа: по ним образ привязывается к исходникам.
    $rev = try { (& git -C $src rev-parse --short HEAD 2>$null) } catch { $null }
    if (-not $rev) { $rev = 'unknown' }
    $created = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    foreach ($img in $ours.Keys) { Run docker build -t $img --label "org.opencontainers.image.revision=$rev" --label "org.opencontainers.image.created=$created" $ours[$img] }
}

# --- 2. образы: каждый отдельным .tar.gz ---
Step "docker save / images\ ($($pack.Count) шт.)"
New-Item -ItemType Directory -Force "$install\images" | Out-Null
Get-ChildItem "$install\images\*.tar.gz" -ErrorAction SilentlyContinue | Remove-Item -Force   # другая сборка могла положить другой набор
foreach ($img in $pack) {
    # Тег нужен в имени файла: alpine:3.20 и alpine:3.20@sha256 - разные файлы, разные версии одного образа не должны затирать друг друга.
    $file = "$install\images\" + (($img -split '@')[0] -replace '[/:]', '_') + $(if ($img -match '@sha256:(.{12})') { "_$($Matches[1])" }) + '.tar.gz'
    $tmp  = "$file.tmp"
    Run docker save -o $tmp $img
    $in = [IO.File]::OpenRead($tmp); $outStream = [IO.File]::Create("$file.new")
    $gz = New-Object IO.Compression.GZipStream($outStream, [IO.Compression.CompressionLevel]::Optimal)
    try { $in.CopyTo($gz) } finally { $gz.Dispose(); $outStream.Dispose(); $in.Dispose() }
    Remove-Item $tmp
    Move-Item -Force "$file.new" $file
    '  {0,-45} {1,8:N0} МБ' -f (Split-Path $file -Leaf), ((Get-Item $file).Length / 1MB) | Write-Host
}

# --- 3. конфиги мониторинга из репозитория ---
Step "Конфиги мониторинга"
$mon = "$install\monitoring"
if (Test-Path $mon) { Remove-Item -Recurse -Force $mon }
New-Item -ItemType Directory -Force "$mon\grafana\provisioning\datasources" | Out-Null
Copy-Item "$PSScriptRoot\pilot-metrics.yml" "$mon\grafana\provisioning\datasources\"
Copy-Item -Recurse "$src\metrics\config\prometheus" "$mon\prometheus"
Copy-Item -Recurse "$src\metrics\config\loki"       "$mon\loki"
Copy-Item -Recurse "$src\metrics\config\promtail"   "$mon\promtail"
Copy-Item -Recurse -Force "$src\metrics\config\grafana\*" "$mon\grafana"
Copy-Item "$src\metrics\automation-collector\grafana_dashboard_pilot_automation.json" "$mon\grafana\dashboards\pilot-automation.json"
Copy-Item "$src\metrics\report-collector\grafana\dashboard-pilot-reports.json" "$mon\grafana\dashboards\pilot-reports.json"

# --- 3a. модули для чистой установки: исходники в пустой GitLab, готовый ServerHost, пакет ClientSdk ---
# В варианте с данными всё это уже лежит в бэкапе GitLab и баз Pilot; папка нужна для -NoData у заказчика.
Step "Модули для чистой установки"
$mod = "$install\modules"
if (Test-Path $mod) { Remove-Item -Recurse -Force $mod }
New-Item -ItemType Directory -Force "$mod\sources", "$mod\packages" | Out-Null
$repos = [ordered]@{
    'Pilot.ClientSdk' = "$ws\client-modules\Pilot.ClientSdk"
    'Reports'          = "$ws\Reports"
}
# Модуль - отдельный репозиторий (раскладка автора) или папка монорепозитория: во втором случае бандл собирается
# из содержимого папки одним коммитом (история монорепозитория в GitLab-проект модуля не нужна).
function New-ModuleBundle($name, $repo, $bundle) {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) "bundle-$name"
    if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
    # Папка без git (скачанный архив): один коммит из файлов папки с учётом её .gitignore.
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $inRepo = (& git -C $repo rev-parse --is-inside-work-tree 2>$null) -eq 'true'
    $ErrorActionPreference = $prev
    if (-not $inRepo) {
        Run git init -q $tmp
        $gd = "--git-dir=$tmp\.git"; $wt = "--work-tree=$repo"
        Run git $gd $wt add -A
        Run git $gd $wt -c user.name=cicd -c user.email=cicd@localhost commit -q -m "$name из архива"
        Run git -C $tmp branch -M main
        Run git -C $tmp symbolic-ref HEAD refs/heads/main
        Run git -C $tmp bundle create -q $bundle --all
        $short = (& git -C $tmp rev-parse --short main).Trim()
        Remove-Item -Recurse -Force $tmp
        $csprojFile = Join-Path $repo "$name.csproj"
        if (-not (Test-Path $csprojFile)) { throw "В $repo нет $name.csproj" }
        return [pscustomobject]@{ Csproj = [IO.File]::ReadAllText($csprojFile); Ref = "$short (из архива)" }
    }
    $top = (& git -C $repo rev-parse --show-toplevel).Trim()
    # Путь папки модуля не берём из вывода git (при OEM-кодировке кириллица искажается): только факт непустого префикса и пути относительно cwd.
    $prefix = (& git -C $repo rev-parse --show-prefix).Trim()
    if (-not $prefix) {
        # Свежий main из GitLab; если GitLab выключен, берём то, что уже скачано (stderr git при EAP=Stop бросает исключение).
        try { & git -C $repo fetch -q origin 2>&1 | Out-Null } catch { Write-Warning "${name}: GitLab недоступен, беру последний скачанный main" }
        $ref = if (& git -C $repo for-each-ref --format='%(refname)' refs/remotes/origin/main) { 'origin/main' } else { 'main' }
        $full = if ($ref -eq 'origin/main') { 'refs/remotes/origin/main' } else { 'refs/heads/main' }
        # Только main и теги, без веток задач: временный bare-репозиторий, чтобы ветка в бандле называлась main.
        Run git init -q --bare $tmp
        Run git -C $tmp fetch -q $repo "+${full}:refs/heads/main" '+refs/tags/*:refs/tags/*'
        Run git -C $tmp symbolic-ref HEAD refs/heads/main
        Run git -C $tmp bundle create -q $bundle --all
        $path = "./$name.csproj"
    } else {
        $ref = if (& git -C $repo for-each-ref --format='%(refname)' refs/heads/main) { 'main' } else { 'HEAD' }
        $short = (& git -C $repo rev-parse --short $ref).Trim()
        # Корневой коммит из дерева папки модуля: без распаковки файлов и без истории монорепозитория.
        Run git init -q --bare $tmp
        Run git -C $tmp fetch -q $top "+${ref}:refs/heads/src"
        $tree = (& git -C $repo rev-parse "${ref}:./").Trim()
        if ($LASTEXITCODE) { throw "В $name нет папки модуля на $ref" }
        $commit = (& git -C $tmp -c user.name=cicd -c user.email=cicd@localhost commit-tree $tree -m "$name из монорепозитория ($short)").Trim()
        Run git -C $tmp update-ref refs/heads/main $commit
        Run git -C $tmp update-ref -d refs/heads/src
        Run git -C $tmp symbolic-ref HEAD refs/heads/main
        Run git -C $tmp bundle create -q $bundle --all
        $path = "./$name.csproj"
    }
    Remove-Item -Recurse -Force $tmp
    $csproj = & git -C $repo show "${ref}:$path"
    if ($LASTEXITCODE) { throw "В $name нет $path на $ref" }
    [pscustomobject]@{ Csproj = ($csproj -join "`n"); Ref = (& git -C $repo rev-parse --short $ref) }
}
foreach ($name in $repos.Keys) {
    $repo = $repos[$name]
    $r = New-ModuleBundle $name $repo "$mod\sources\$name.bundle"
    $ver = [regex]::Match($r.Csproj, '<Version>([\d.]+)</Version>').Groups[1].Value
    '  {0,-28} {1}  {2}' -f "$name.bundle", $ver, $r.Ref | Write-Host
    # Пакеты Pilot.*, на которые ссылается модуль: в пустом реестре GitLab их нет, а CI модуля без них не соберётся.
    foreach ($m in [regex]::Matches($r.Csproj, '<PackageReference\s+Include="(Pilot\\.[^"]+)"\s+Version="([^"]+)"')) {
        $id = $m.Groups[1].Value; $v = $m.Groups[2].Value
        $nupkg = Join-Path $env:USERPROFILE ".nuget\packages\$($id.ToLower())\$v\$($id.ToLower()).$v.nupkg"
        if (-not (Test-Path $nupkg)) { throw "Нет пакета $id $v в кэше NuGet ($nupkg): выполните dotnet restore для $name" }
        Copy-Item $nupkg "$mod\packages\$id.$v.nupkg" -Force
    }
}
$hostProj = "$ws\server-extensions\ServerHost\ServerHost"
if (-not $NoBuild) { Run dotnet build "$hostProj\ServerHost.csproj" -c Release -v q -nologo }
$hostZip = "$hostProj\dist\ServerHost.zip"
if (-not (Test-Path $hostZip)) { throw "Нет ${hostZip}: соберите ServerHost в Release" }
$newer = Get-ChildItem $hostProj -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.LastWriteTime -gt (Get-Item $hostZip).LastWriteTime }
if ($newer) { Write-Warning "Исходники ServerHost новее dist\ServerHost.zip - соберите заново без -NoBuild" }
Copy-Item $hostZip "$mod\ServerHost.zip"
Get-ChildItem "$mod\packages" | ForEach-Object { '  ' + $_.Name } | Write-Host

# Скрипты, исполняемые в Linux-контейнерах/хостах, - только LF.
foreach ($f in "$install\init\postgres\01-pilot.sh", "$install\wait-for-postgres.sh", "$install\install.sh", "$install\gitlab-post-restore.rb", "$install\.env.example") {
    [IO.File]::WriteAllText($f, ([IO.File]::ReadAllText($f) -replace "`r`n", "`n"))
}

# --- 4. опись ---
$kind = if ($Offline) { 'Комплект для установки без интернета: все образы, в том числе GitLab и заданий CI, лежат в images\.' }
        else { 'Комплект для установки с интернетом: часть образов docker скачает при установке, см. ниже.' }
# Колонки: образ, полный идентификатор, дата, отпечаток слоёв (SHA-256 списка слоёв). install сверяет по ним загруженные образы;
# слои нужны потому, что идентификатор зависит от хранилища образов (classic или containerd), а слои нет.
function Get-Sha256Hex([string]$text) {
    $h = [Security.Cryptography.SHA256]::Create()
    try { -join ($h.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)) | ForEach-Object { '{0:x2}' -f $_ }) } finally { $h.Dispose() }
}
$lines = @("Папка собрана $(Get-Date -Format 'yyyy-MM-dd HH:mm')", $kind, '', 'В папке (images\): образ, идентификатор, дата, отпечаток слоёв')
foreach ($img in $pack) {
    $id, $created, $layers = (docker image inspect $img --format '{{.Id}}|{{.Created}}|{{json .RootFS.Layers}}') -split '\|'
    if ($id -notmatch '^sha256:[0-9a-f]{64}$') { throw "Не удалось прочитать идентификатор образа ${img}: '$id'" }
    $lines += '  {0,-50} {1}  {2}  {3}' -f $img, $id, $created.Substring(0, 10), (Get-Sha256Hex $layers)
}
if (-not $Offline) {
    $lines += '', 'Из интернета (docker скачает при установке):'
    $lines += $pinned | ForEach-Object { '  ' + $_ }
}
Set-Content -Encoding utf8 "$install\IMAGES.txt" $lines

# --- 5. данные: тот же формат, что у ночного бэкапа; install находит data\ сам ---
# Старый data\ заменяем только после удачного снятия нового.
$data = "$install\data"; $new = "$install\.data-new"
if ($NoData) {
    if (Test-Path $data) { Step "Удаляю data\"; Remove-Item -Recurse -Force $data }
} else {
    if (Test-Path $new) { Remove-Item -Recurse -Force $new }
    if ($DataFrom) {
        Step "Данные из $DataFrom"
        if (-not (Test-Path "$DataFrom\manifest.json")) { throw "В $DataFrom нет manifest.json - это не каталог запуска бэкапа" }
        # Копия кладётся как есть, без разбора частей.
        & robocopy $DataFrom $new /E /COPY:DAT /R:2 /W:2 /NP /NFL /NDL /NJH /NJS | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy $DataFrom -> код $LASTEXITCODE" }
    } else {
        Step "Свежий бэкап"
        & "$src\backup\backup.ps1" -Root "$new-run" -KeepDays 0 -Skip images
        if ($LASTEXITCODE) { throw "Бэкап завершился с ошибкой, подробности - в backup.log в $new-run" }
        Move-Item (Get-ChildItem "$new-run" -Directory | Select-Object -First 1).FullName $new
        Remove-Item -Recurse -Force "$new-run"
    }
    if (-not ([IO.File]::ReadAllText("$new\manifest.json") | ConvertFrom-Json).ok) { throw "Бэкап снят с ошибками, в папку его класть нельзя" }
    # Бэкап своей машины ключи хранит (иначе после восстановления всех разлогинило бы), а в комплект для других машин они не идут.
    foreach ($r in Remove-KitSecrets $new) { Write-Host "  из data\ убрано: $r" }
    if (Test-Path $data) { Remove-Item -Recurse -Force $data }
    Move-Item $new $data
}

# --- 6. логины и пароли из data\ ---
Step "PASSWORDS.md"
& "$PSScriptRoot\make-passwords.ps1"

$size = (Get-ChildItem $install -Recurse -File -Force | Measure-Object Length -Sum).Sum
Write-Host ("`nГотово: {0} ({1:N0} МБ). Эту папку можно передавать целиком." -f $install, ($size / 1MB)) -ForegroundColor Green
if (-not $NoData) { Write-Host "В data\ и PASSWORDS.md все данные и пароли открытым текстом." -ForegroundColor Yellow }
exit 0   # robocopy оставляет ненулевой код выхода и при успехе
