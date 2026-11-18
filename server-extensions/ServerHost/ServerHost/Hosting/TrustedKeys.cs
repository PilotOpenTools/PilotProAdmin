using System.Security.Cryptography;

namespace ServerHost.Hosting;

/// <summary>
/// Доверенные открытые ключи подписи модулей: PEM «PUBLIC KEY» из переменной SERVER_MODULES_TRUSTED_KEYS
/// и файла trusted-keys.pem в каталоге модулей (общий для узлов кластера); берутся оба источника.
/// Ничего не задано - проверка подлинности выключена (<see cref="Enabled"/> = false), модули принимаются как раньше.
/// Задано, но ни один ключ не прочитался - модули не принимаются: опечатка не должна тихо отключать защиту.
/// </summary>
internal sealed class TrustedKeys
{
    public const string EnvironmentVariable = "SERVER_MODULES_TRUSTED_KEYS";
    public const string FileName = "trusted-keys.pem";

    public static readonly TrustedKeys None = new(false, new HashSet<string>(), Array.Empty<string>());

    private TrustedKeys(bool enabled, IReadOnlySet<string> ids, IReadOnlyList<string> problems)
    {
        Enabled = enabled;
        Ids = ids;
        Problems = problems;
    }

    public bool Enabled { get; }
    /// <summary>Отпечатки доверенных ключей (<see cref="ArchiveSignature.KeyId"/>).</summary>
    public IReadOnlySet<string> Ids { get; }
    /// <summary>Что в списке не прочиталось.</summary>
    public IReadOnlyList<string> Problems { get; }

    public static TrustedKeys Load(string root) =>
        Load(root, Environment.GetEnvironmentVariable(EnvironmentVariable));

    public static TrustedKeys Load(string root, string? fromEnvironment)
    {
        string? fromFile = null;
        var path = Path.Combine(root, FileName);
        try
        {
            if (File.Exists(path))
                fromFile = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Файл есть, но не читается: считать защиту выключенной нельзя.
            return new TrustedKeys(true, new HashSet<string>(), new[] { $"{FileName} не прочитан: {ex.Message}" });
        }
        return Parse(fromEnvironment, fromFile);
    }

    public static TrustedKeys Parse(params string?[] sources)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var problems = new List<string>();
        var enabled = false;
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source))
                continue;
            enabled = true;
            // В переменной окружения перевод строки часто пишут как \n.
            var text = source.Replace("\\n", "\n");
            var span = text.AsSpan();
            var found = 0;
            while (PemEncoding.TryFind(span, out var fields))
            {
                found++;
                var label = span[fields.Label].ToString();
                try
                {
                    if (label != "PUBLIC KEY")
                        throw new CryptographicException($"нужен блок PUBLIC KEY (закрытый ключ сюда класть нельзя), а не {label}");
                    var der = new byte[fields.DecodedDataLength];
                    if (!Convert.TryFromBase64Chars(span[fields.Base64Data], der, out var written) || written != der.Length)
                        throw new CryptographicException("повреждены данные ключа");
                    using (ArchiveSignature.ImportP256(der)) { }
                    ids.Add(ArchiveSignature.KeyId(der));
                }
                catch (CryptographicException ex)
                {
                    problems.Add($"блок {found}: {ex.Message}");
                }
                span = span[fields.Location.End..];
            }
            if (found == 0)
                problems.Add("не найден ни один блок -----BEGIN PUBLIC KEY-----");
        }
        return new TrustedKeys(enabled, ids, problems);
    }
}
