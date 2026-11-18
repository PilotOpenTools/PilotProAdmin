<#
  Смена паролей контура Pilot (prod | test | stage): PostgreSQL, RustFS и служебная S3-учётка Pilot.

    .\shared\rotate-contour-secrets.ps1 -Env test
    .\shared\rotate-contour-secrets.ps1 -Env prod -ConfirmProd prod

  Что делает:
    1. Генерирует новые пароли (32 символа [A-Za-z0-9]) и пишет их в <контур>\.env.
    2. RustFS: заводит/обновляет учётку MINIO_PILOT_USER (по умолчанию pilot) с доступом только к бакету
       pilot-ice-ru - под ней теперь работают Pilot и сервис миграции, root RustFS им больше не нужен.
       Команды выполняет клиент rc внутри контейнера rustfs-<контур>; ключ root передаётся окружением (RC_HOST_<алиас>),
       в аргументы docker попадает только пароль новой учётки (rc admin user add принимает его аргументом).
    3. PostgreSQL: ALTER ROLE postgres и pilotuser; для prod ещё роли метрик metrics_writer и metrics_reader (после пересоздания контейнера).
    4. Останавливает pilot-server, правит settings.xml (копия рядом: settings.xml.before-rotate-<время>):
       пароль в строке подключения, AccessKeyId и EncryptedSecretAccessKey S3-архива.
       Секрет шифруется так же, как это делает Pilot (Ascon.Pilot.Common.AesEx, ключ зашит в Pilot).
    5. docker compose up -d контура: RustFS поднимается с новым root-паролем, pilot-server - с новыми настройками.
    6. Сервис миграции (proadmin\migration\data\config.json) и proadmin\.env получают S3-ключи
       учётки pilot; для prod ещё metrics\.env (PG_USER, PG_PASSWORD - сборщики пишут в pilot_metrics под metrics_writer;
       METRICS_READER_PASSWORD - Grafana читает под metrics_reader).
    7. Проверяет: вход pilotuser в Postgres по сети, доступ pilot к бакету, старт базы в pilot-server.
    8. Если в proadmin\.env задан ключ выкладки модулей этого контура (MODULES_DEPLOY_KEY_<КОНТУР>), выдаёт новый и
       пересоздаёт админку. Новый ключ нужно сразу прописать в переменной группы GitLab DEPLOY_KEY_<КОНТУР>:
       до этого выкладка из CI в этот контур возвращает 401.

  Скрипт рассчитан на раскладку по папкам (prod, test, stage, proadmin, metrics рядом). Для установки из папки Install
  (общий .env, проект cicd) он не подходит: там пароли меняют вручную, см. README.md в stack\Install.

  Снимите бэкап перед запуском (backup\backup.ps1). Скрипт идемпотентен: повторный запуск выдаёт новые пароли.
#>
param(
    [Parameter(Mandatory)][ValidateSet('prod', 'test', 'stage')][string]$Env,
    [string]$ConfirmProd,
    [string]$DataRoot = 'C:\Ascon-Docker'
)
$ErrorActionPreference = 'Stop'
if ($Env -eq 'prod' -and $ConfirmProd -ne 'prod') { throw 'Для прода добавьте -ConfirmProd prod' }
$cicd = Split-Path $PSScriptRoot -Parent
$envDir = Join-Path $cicd $Env
$pg = "postgres-$Env"; $rustfs = "rustfs-$Env"; $server = "pilot-server-$Env"
$utf8 = New-Object Text.UTF8Encoding $false

function Exec([string]$exe, [string[]]$argList) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        $out = & $exe @argList 2>&1 | ForEach-Object { "$_" }
        if ($LASTEXITCODE -ne 0) { $out | Write-Host; throw "$exe $($argList[0..1] -join ' ') ... -> код $LASTEXITCODE" }
        return $out
    }
    finally { $ErrorActionPreference = $prev }
}

# Текст в контейнер - через base64: PS 5.1 портит кавычки в аргументах и добавляет BOM в stdin.
function B64([string]$text) { [Convert]::ToBase64String($utf8.GetBytes($text)) }

function NewSecret([int]$len = 32) {
    $chars = [char[]]'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
    $bytes = New-Object byte[] $len
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
}

# Копия Ascon.Pilot.Common.AesEx (Pilot-Server 26.x): PBKDF2-SHA1 из фиксированных пароля и соли, AES-256-CBC.
function PilotAes([string]$text, [switch]$Decrypt) {
    $kdf = New-Object Security.Cryptography.Rfc2898DeriveBytes(
        [Text.Encoding]::Unicode.GetBytes('123456'),
        [Text.Encoding]::Unicode.GetBytes('{5AF2954F-02FC-445A-BC27-976DD11A6258}'), 1000)
    $aes = [Security.Cryptography.Aes]::Create()
    $aes.Key = $kdf.GetBytes($aes.KeySize -shr 3)
    $aes.IV = $kdf.GetBytes($aes.BlockSize -shr 3)
    if ($Decrypt) {
        $bytes = [Convert]::FromBase64String($text)
        $dec = $aes.CreateDecryptor()
        return $utf8.GetString($dec.TransformFinalBlock($bytes, 0, $bytes.Length))
    }
    $plain = $utf8.GetBytes($text)
    $enc = $aes.CreateEncryptor()
    return [Convert]::ToBase64String($enc.TransformFinalBlock($plain, 0, $plain.Length))
}

# KEY=value в .env: заменить или дописать, остальное не трогать.
function SetEnvValues([string]$path, [hashtable]$values) {
    $lines = New-Object Collections.Generic.List[string]
    if (Test-Path $path) { [IO.File]::ReadAllLines($path) | ForEach-Object { $lines.Add($_) } }
    foreach ($k in $values.Keys) {
        $i = -1
        for ($j = 0; $j -lt $lines.Count; $j++) { if ($lines[$j] -match "^$k=") { $i = $j; break } }
        if ($i -ge 0) { $lines[$i] = "$k=$($values[$k])" } else { $lines.Add("$k=$($values[$k])") }
    }
    [IO.File]::WriteAllLines($path, $lines, $utf8)
}

# Значение окружения RC_HOST_<алиас> для rc: http://КЛЮЧ:СЕКРЕТ@хост:порт, ключ и секрет в URL-кодировке.
function RcHost([string]$key, [string]$secret) {
    'http://{0}:{1}@127.0.0.1:9000' -f [Uri]::EscapeDataString($key), [Uri]::EscapeDataString($secret)
}

function ContainerEnv([string]$container, [string]$name) {
    $json = Exec docker @('inspect', $container, '--format', '{{json .Config.Env}}')
    foreach ($kv in ($json | ConvertFrom-Json)) { if ($kv.StartsWith("$name=")) { return $kv.Substring($name.Length + 1) } }
    return $null
}

# --- 0. Проверки и текущие значения
foreach ($c in $pg, $rustfs, $server) {
    if (-not (Exec docker @('ps', '--filter', "name=^$c$", '--format', '{{.Names}}'))) {
        throw "$c не запущен"
    }
}
$settingsPath = Join-Path $DataRoot "$Env\pilot-server\settings\settings.xml"
$xmlText = [IO.File]::ReadAllText($settingsPath)
# Адрес в settings.xml: rustfs-<контур>.
$s3Re = '(<FileArchiveParametersS3\b[^>]*\bUrl="http://rustfs-' + [regex]::Escape($Env) + ':9000"[^>]*/>)'
if (-not [regex]::IsMatch($xmlText, $s3Re)) { throw "В settings.xml нет S3-архива на $rustfs (адрес rustfs-$Env)" }
$s3El = [regex]::Match($xmlText, $s3Re).Value
$connRe = '(Host=' + [regex]::Escape($pg) + ':5432;Username=pilotuser;Password=)[^;"]*'
if (-not [regex]::IsMatch($xmlText, $connRe)) { throw "В settings.xml нет строки подключения к $pg" }
$oldEnc = [regex]::Match($s3El, 'EncryptedSecretAccessKey="([^"]*)"').Groups[1].Value
$rootUser = ContainerEnv $rustfs 'RUSTFS_ACCESS_KEY'
$rootPass = ContainerEnv $rustfs 'RUSTFS_SECRET_KEY'
if (-not $rootUser -or -not $rootPass) { throw "Не удалось прочитать RUSTFS_ACCESS_KEY и RUSTFS_SECRET_KEY из $rustfs" }
# Самопроверка шифрования: текущий секрет из settings.xml должен расшифровываться в действующий ключ.
$oldKeyId = [regex]::Match($s3El, 'AccessKeyId="([^"]*)"').Groups[1].Value
if ($oldKeyId -eq $rootUser -and (PilotAes $oldEnc -Decrypt) -ne $rootPass) { throw 'Шифрование Pilot не совпало (EncryptedSecretAccessKey не равен паролю root RustFS) - версия Pilot изменилась?' }
Write-Host "[$Env] шифрование Pilot проверено" -ForegroundColor Cyan

$migCfg = Join-Path $cicd '..\proadmin\migration\data\config.json'
if (Test-Path $migCfg) { try { $null = [IO.File]::ReadAllText($migCfg) | ConvertFrom-Json } catch { throw "Не удалось разобрать ${migCfg}: $_" } }
$envFile = Join-Path $envDir '.env'
$new = @{
    PG_SUPERUSER_PASSWORD = NewSecret
    PILOT_PG_PASSWORD     = NewSecret
    MINIO_USER            = $rootUser
    MINIO_PASSWORD        = NewSecret
    MINIO_PILOT_USER      = 'pilot'
    MINIO_PILOT_SECRET    = NewSecret
}
if ($Env -eq 'prod') { $new.METRICS_WRITER_PASSWORD = NewSecret; $new.METRICS_READER_PASSWORD = NewSecret }
if (-not (Test-Path $envFile)) { Copy-Item (Join-Path $envDir '.env.example') $envFile }
$envBackup = "$envFile.before-rotate-$(Get-Date -Format yyyyMMdd-HHmmss)"
Copy-Item $envFile $envBackup

# --- 1. RustFS: учётка pilot с доступом только к бакету pilot-ice-ru (пока под старым root)
# Алиас rot задаётся окружением RC_HOST_rot (ключ не оседает в файле настроек rc).
# Проверено живьём на RustFS 1.0.5 (Windows, Docker Desktop): rc admin policy create / user add / policy attach, в том числе повтор для существующих. RC_HOST_<алиас> по проверке не работает со «спецсимволами» в ключе или секрете (у новых паролей ротации только [A-Za-z0-9]); на Linux-хосте не проверялось.
$policy = '{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Action":["s3:ListAllMyBuckets","s3:GetBucketLocation"],"Resource":["arn:aws:s3:::*"]},{"Effect":"Allow","Action":["s3:*"],"Resource":["arn:aws:s3:::pilot-ice-ru","arn:aws:s3:::pilot-ice-ru/*"]}]}'
Exec docker @('exec', $rustfs, 'sh', '-c', "echo $(B64 $policy) | base64 -d > /tmp/pilot-policy.json") | Out-Null
$env:RC_HOST_rot = RcHost $rootUser $rootPass
try {
    Exec docker @('exec', '-e', 'RC_HOST_rot', $rustfs, 'rc', 'admin', 'policy', 'create', 'rot/', 'pilot-bucket', '/tmp/pilot-policy.json') | Out-Null
    Exec docker @('exec', '-e', 'RC_HOST_rot', $rustfs, 'rc', 'admin', 'user', 'add', 'rot/', $new.MINIO_PILOT_USER, $new.MINIO_PILOT_SECRET) | Out-Null
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    & docker exec -e RC_HOST_rot $rustfs rc admin policy attach rot/ pilot-bucket --user $new.MINIO_PILOT_USER 2>&1 | Out-Null   # «уже привязана» - не ошибка
    $ErrorActionPreference = $prev
}
finally { Remove-Item Env:RC_HOST_rot -ErrorAction SilentlyContinue }
Exec docker @('exec', $rustfs, 'rm', '-f', '/tmp/pilot-policy.json') | Out-Null
Write-Host "[$Env] RustFS: учётка $($new.MINIO_PILOT_USER) с доступом к pilot-ice-ru" -ForegroundColor Cyan

# --- 2. PostgreSQL: пароли ролей
$sql = "ALTER ROLE postgres PASSWORD '$($new.PG_SUPERUSER_PASSWORD)'; ALTER ROLE pilotuser PASSWORD '$($new.PILOT_PG_PASSWORD)';"
Exec docker @('exec', $pg, 'sh', '-c', "echo $(B64 $sql) | base64 -d | psql -q -U postgres -v ON_ERROR_STOP=1") | Out-Null
Write-Host "[$Env] PostgreSQL: пароли postgres и pilotuser сменены" -ForegroundColor Cyan

# --- 3. settings.xml при остановленном pilot-server
$since = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
Exec docker @('stop', $server) | Out-Null
$xmlText = [IO.File]::ReadAllText($settingsPath)
$bom = [IO.File]::ReadAllBytes($settingsPath)[0] -eq 0xEF
Copy-Item $settingsPath "$settingsPath.before-rotate-$(Get-Date -Format yyyyMMdd-HHmmss)"
$xmlText = [regex]::Replace($xmlText, $connRe, { param($m) $m.Groups[1].Value + $new.PILOT_PG_PASSWORD })
$s3Old = [regex]::Match($xmlText, $s3Re).Value
$s3New = [regex]::Replace($s3Old, 'AccessKeyId="[^"]*"', { param($m) "AccessKeyId=`"$($new.MINIO_PILOT_USER)`"" })
$s3New = [regex]::Replace($s3New, 'EncryptedSecretAccessKey="[^"]*"', { param($m) "EncryptedSecretAccessKey=`"$(PilotAes $new.MINIO_PILOT_SECRET)`"" })
$xmlText = $xmlText.Replace($s3Old, $s3New)
[IO.File]::WriteAllText($settingsPath, $xmlText, (New-Object Text.UTF8Encoding $bom))

# --- 4. .env и пересоздание контейнеров контура (RustFS с новым root-паролем)
SetEnvValues $envFile $new
Push-Location $envDir
try { Exec docker @('compose', 'up', '-d') | Out-Null } finally { Pop-Location }
Write-Host "[$Env] контур пересоздан с новыми паролями" -ForegroundColor Cyan
if ($Env -eq 'prod') {
    # Контейнер PostgreSQL пересоздан с новыми паролями ролей метрик в окружении: скрипт задаёт их ролям (и создаёт роли, если их ещё нет).
    $n = 0
    while ($true) {
        docker exec $pg pg_isready -h 127.0.0.1 -U postgres | Out-Null
        if ($LASTEXITCODE -eq 0) { break }
        if (++$n -gt 90) { throw "$pg не поднялся после пересоздания" }
        Start-Sleep 2
    }
    Exec docker @('exec', $pg, 'bash', '/docker-entrypoint-initdb.d/02-metrics-roles.sh') | Out-Null
    Write-Host "[$Env] PostgreSQL: пароли metrics_writer и metrics_reader сменены" -ForegroundColor Cyan
}

# --- 5. Потребители: миграция, админка, (prod) метрики
$keyName = $Env.ToUpper()
SetEnvValues (Join-Path $cicd '..\proadmin\.env') @{ "${keyName}_S3_ACCESS_KEY" = $new.MINIO_PILOT_USER; "${keyName}_S3_SECRET_KEY" = $new.MINIO_PILOT_SECRET }
if (Test-Path $migCfg) {
    $cfg = [IO.File]::ReadAllText($migCfg) | ConvertFrom-Json
    foreach ($e in $cfg.Environments) {
        if ($e.Key -ne $Env) { continue }
        foreach ($s in @($e.S3)) { if ($s.ServerUrl -match "rustfs-$Env\b") { $s.AccessKey = $new.MINIO_PILOT_USER; $s.SecretKey = $new.MINIO_PILOT_SECRET } }
    }
    Copy-Item $migCfg "$migCfg.before-rotate-$(Get-Date -Format yyyyMMdd-HHmmss)"
    [IO.File]::WriteAllText($migCfg, ($cfg | ConvertTo-Json -Depth 20), $utf8)
    Exec docker @('restart', 'pilot-migration') | Out-Null
    Write-Host "[$Env] сервис миграции: S3-ключи обновлены" -ForegroundColor Cyan
}
if ($Env -eq 'prod') {
    SetEnvValues (Join-Path $cicd 'metrics\.env') @{ PG_USER = 'metrics_writer'; PG_PASSWORD = $new.METRICS_WRITER_PASSWORD; METRICS_READER_PASSWORD = $new.METRICS_READER_PASSWORD }
    Push-Location (Join-Path $cicd 'metrics')
    try { Exec docker @('compose', 'up', '-d') | Out-Null } finally { Pop-Location }
    Write-Host '[prod] метрики: пароли ролей обновлены, сборщики и Grafana пересозданы' -ForegroundColor Cyan
}

# --- 5a. Ключ выкладки модулей из CI: у каждого контура свой
$deployKeyName = "MODULES_DEPLOY_KEY_$keyName"
$proadminEnv = Join-Path $cicd '..\proadmin\.env'
$newDeployKey = $null
if (Test-Path $proadminEnv) {
    $hasKey = [IO.File]::ReadAllLines($proadminEnv) | Where-Object { $_ -match "^$deployKeyName=.+" }
    if ($hasKey) {
        $newDeployKey = NewSecret 40
        SetEnvValues $proadminEnv @{ $deployKeyName = $newDeployKey }
        Push-Location (Join-Path $cicd '..\proadmin')
        try { Exec docker @('compose', 'up', '-d') | Out-Null } finally { Pop-Location }
        Write-Host "[$Env] ключ выкладки модулей обновлён, админка пересоздана" -ForegroundColor Cyan
        # Сразу, а не в конце: если проверка шага 6 упадёт, ключ уже сменён и CI получает 401, пока его не обновят.
        Write-Host "ВАЖНО: ключ выкладки уже сменён. Задайте в переменных группы GitLab DEPLOY_KEY_$keyName новое значение ($deployKeyName из $proadminEnv), иначе выкладка из CI в $Env вернёт 401." -ForegroundColor Yellow
    }
}

# --- 6. Проверка
$img = (Exec docker @('inspect', $pg, '--format', '{{.Config.Image}}')) | Select-Object -Last 1
Exec docker @('run', '--rm', '--network', "pilot-$Env", '-e', "PGPASSWORD=$($new.PILOT_PG_PASSWORD)", $img,
    'psql', '-h', $pg, '-U', 'pilotuser', '-d', 'pilot-ice_ru', '-Atc', 'select 1') | Out-Null
Write-Host "[$Env] проверка: pilotuser входит в Postgres по сети" -ForegroundColor Green
$env:RC_HOST_chk = RcHost $new.MINIO_PILOT_USER $new.MINIO_PILOT_SECRET
try { $n = @(Exec docker @('exec', '-e', 'RC_HOST_chk', $rustfs, 'rc', 'ls', '--recursive', 'chk/pilot-ice-ru')).Count }
finally { Remove-Item Env:RC_HOST_chk -ErrorAction SilentlyContinue }
Write-Host "[$Env] проверка: учётка pilot видит $n объектов в pilot-ice-ru" -ForegroundColor Green
# Pilot пишет «UpdateDatabases: pilot-ice_ru is launched» или «pilot-ice_ru to state Broken».
$state = $null
for ($i = 0; $i -lt 60 -and -not $state; $i++) {
    Start-Sleep 3
    $ErrorActionPreference = 'Continue'   # docker logs пишет лог контейнера в stderr
    $logs = (& docker logs --since $since $server 2>&1 | ForEach-Object { "$_" } | Out-String)
    $ErrorActionPreference = 'Stop'
    if ($logs -match 'pilot-ice_ru to state Broken') { $state = 'Broken' }
    elseif ($logs -match 'pilot-ice_ru is launched') { $state = 'Launched' }
}
if ($state -ne 'Launched') {
    throw "[$Env] база в $server не запустилась (состояние: $state). Действующие пароли уже в .env и в Postgres; сверьте settings.xml с .env (прежний - settings.xml.before-rotate-*)"
}
Write-Host "[$Env] pilot-server: база pilot-ice_ru запущена" -ForegroundColor Green
if ($newDeployKey) { Write-Host "ВАЖНО: задайте в переменных группы GitLab DEPLOY_KEY_$keyName новое значение ($deployKeyName из $proadminEnv), иначе выкладка из CI в $Env вернёт 401." -ForegroundColor Yellow }
Write-Host "Готово. Копия прежнего .env: $envBackup" -ForegroundColor Green
