using System.Text;
using System.Xml.Linq;

namespace ProAdmin.Reports;

/// <summary>
/// Текст .repx для сравнения: скрипт отчёта (атрибут ScriptsSource с экранированными переводами строк)
/// вынимается отдельно, в разметке он заменяется заглушкой - иначе построчный diff бесполезен.
/// Тот же приём, что в модуле Reports.
/// </summary>
public sealed record ReportText(string Script, string Markup)
{
    public static ReportText Parse(byte[]? content)
    {
        var text = Decode(content);
        try
        {
            var doc = XDocument.Parse(text);
            var attribute = doc.Descendants().Select(e => e.Attribute("ScriptsSource")).FirstOrDefault(a => a is not null);
            var script = attribute?.Value ?? "";
            if (attribute is not null)
                attribute.Value = "(скрипт - отдельно)";
            return new ReportText(script, doc.ToString());
        }
        catch (System.Xml.XmlException)
        {
            return new ReportText("", text);
        }
    }

    private static string Decode(byte[]? content)
    {
        if (content is null || content.Length == 0)
            return "";
        using var reader = new StreamReader(new MemoryStream(content), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
