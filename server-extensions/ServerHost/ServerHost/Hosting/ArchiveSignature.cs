using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ServerHost.Hosting;

/// <summary>
/// Подпись архива модуля, которую ставит CI: файл signature.json в корне zip.
/// Подписывается канонический манифест всех остальных файлов архива - строки «sha256-hex␣␣путь␣\n», пути в порядке Ordinal, UTF-8 без BOM
/// (тот же формат, что у sha256sum). Подпись ECDSA P-256 / SHA-256 в формате DER (его выдаёт и openssl).
/// Открытый ключ лежит в самом файле: хост сверяет архив с подписью всегда, а доверие даёт только список <see cref="TrustedKeys"/>.
/// </summary>
internal static class ArchiveSignature
{
    public const string FileName = "signature.json";
    public const string Algorithm = "ECDSA-P256-SHA256";
    public const int MaxFileBytes = 4 * 1024 * 1024;
    private const string P256Oid = "1.2.840.10045.3.1.7";
    private const int MaxListed = 5;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    internal sealed class SignatureFile
    {
        public int Version { get; set; }
        public string? Algorithm { get; set; }
        public string? KeyId { get; set; }
        /// <summary>SubjectPublicKeyInfo в base64.</summary>
        public string? PublicKey { get; set; }
        /// <summary>Подписанный манифест: путь / sha256 (hex, строчные).</summary>
        public Dictionary<string, string>? Files { get; set; }
        public string? Signature { get; set; }
    }

    /// <param name="KeyId">Отпечаток ключа из подписи; null - разобрать ключ не удалось.</param>
    /// <param name="Trusted">Ключ есть в списке доверенных.</param>
    public sealed record Verdict(string? KeyId, bool Trusted, IReadOnlyList<string> Errors);

    public static SignatureFile? Parse(byte[] json, out string? error)
    {
        try
        {
            var file = JsonSerializer.Deserialize<SignatureFile>(json, Json);
            error = file is null ? "пустой файл" : null;
            return file;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Канонический текст, который подписывается.</summary>
    public static string Manifest(IEnumerable<KeyValuePair<string, string>> files)
    {
        var text = new StringBuilder();
        foreach (var (path, hash) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
            text.Append(hash).Append("  ").Append(path).Append('\n');
        return text.ToString();
    }

    /// <summary>Отпечаток ключа: SHA-256 от SubjectPublicKeyInfo (DER), hex в нижнем регистре.</summary>
    public static string KeyId(byte[] spki) => Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant();

    /// <summary>Открытый ключ ECDSA P-256; другой ключ или мусор - CryptographicException.</summary>
    public static ECDsa ImportP256(byte[] spki)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(spki, out _);
            var oid = key.ExportParameters(false).Curve.Oid;
            if (key.KeySize != 256 || !(oid.Value == P256Oid || oid.FriendlyName is "nistP256" or "ECDSA_P256" or "prime256v1"))
                throw new CryptographicException("нужен ключ ECDSA P-256 (prime256v1)");
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Проверяет подпись и сверяет с ней фактические файлы архива (<paramref name="actual"/>: путь / sha256, без самого файла подписи).
    /// </summary>
    public static Verdict Verify(SignatureFile sig, IReadOnlyDictionary<string, string> actual, TrustedKeys trusted)
    {
        var errors = new List<string>();
        if (sig.Algorithm != Algorithm)
            return Fail($"алгоритм «{sig.Algorithm}» не поддерживается (нужен {Algorithm})");
        if (sig.Files is null || string.IsNullOrEmpty(sig.PublicKey) || string.IsNullOrEmpty(sig.Signature))
            return Fail("в файле нет полей files, publicKey или signature");

        byte[] spki, signature;
        try
        {
            spki = Convert.FromBase64String(sig.PublicKey);
            signature = Convert.FromBase64String(sig.Signature);
        }
        catch (FormatException)
        {
            return Fail("публичный ключ или подпись не в формате base64");
        }

        var keyId = KeyId(spki);
        if (!string.IsNullOrEmpty(sig.KeyId) && !string.Equals(sig.KeyId, keyId, StringComparison.OrdinalIgnoreCase))
            return Fail("отпечаток ключа (keyId) не соответствует публичному ключу", keyId);

        // Перевод строки в пути сделал бы манифест неоднозначным.
        if (sig.Files.Keys.Concat(actual.Keys).FirstOrDefault(p => p.Any(char.IsControl)) is { } bad)
            return Fail($"недопустимый символ в пути «{bad.ReplaceLineEndings(" ")}»", keyId);

        bool valid;
        try
        {
            using var key = ImportP256(spki);
            valid = key.VerifyData(Encoding.UTF8.GetBytes(Manifest(sig.Files)), signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return Fail("ключ или подпись не читаются: " + ex.Message, keyId);
        }
        if (!valid)
            return Fail("подпись не сходится с манифестом: файл подписи изменён или подписан другим ключом", keyId);

        var trustedKey = trusted.Ids.Contains(keyId);
        if (trusted.Enabled && !trustedKey)
            errors.Add($"Архив подписан неизвестным ключом {Short(keyId)}: его нет среди доверенных ключей сервера.");

        List(errors, "изменён после подписания", actual.Where(a => sig.Files.TryGetValue(a.Key, out var h) && h != a.Value).Select(a => a.Key));
        List(errors, "добавлен после подписания", actual.Keys.Where(p => !sig.Files.ContainsKey(p)));
        List(errors, "удалён из архива после подписания", sig.Files.Keys.Where(p => !actual.ContainsKey(p)));
        return new Verdict(keyId, trustedKey, errors);

        Verdict Fail(string reason, string? id = null) => new(id, false, new[] { "Подпись архива неверна: " + reason + "." });
    }

    public static string Short(string keyId) => keyId[..Math.Min(16, keyId.Length)];

    private static void List(List<string> errors, string what, IEnumerable<string> paths)
    {
        var all = paths.OrderBy(p => p, StringComparer.Ordinal).ToList();
        if (all.Count > 0)
            errors.Add($"Подпись архива: файл{(all.Count > 1 ? "ы" : "")} {what}: {string.Join(", ", all.Take(MaxListed))}" +
                       (all.Count > MaxListed ? $" и ещё {all.Count - MaxListed}" : "") + ".");
    }
}
