# Секреты, которые каждая установка создаёт себе сама: в комплект (Install\data) их класть нельзя, иначе все установки из него получат один и тот же ключ.
# Подключается из build.ps1 и проверяется самотестом установщика.
function Remove-KitSecrets([string]$dataDir) {
    $removed = @()
    # Ключи DataProtection админки: защищают cookie входа и пароли сессий в памяти; без папки админка создаст новые.
    foreach ($rel in 'admin\proadmin-data\keys') {
        $p = Join-Path $dataDir $rel
        if (Test-Path $p) { Remove-Item -Recurse -Force $p; $removed += $rel }
    }
    $removed
}
