using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ProAdmin.Pages.Migration;

public sealed class TransferModel : PageModel
{
    private static readonly string[] Files = ["prepare-transfer.ps1", "backup.ps1", "register-task.ps1", "monitoring.ps1", "README.txt"];

    public IActionResult OnGetDownload()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in Files)
            {
                using var source = typeof(TransferModel).Assembly.GetManifestResourceStream($"Transfer.{name}")
                    ?? throw new InvalidOperationException($"Missing transfer resource {name}.");
                using var entry = zip.CreateEntry(name).Open();
                using var reader = new StreamReader(source);
                using var writer = new StreamWriter(entry, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                writer.Write(reader.ReadToEnd());
            }
        }
        return File(buffer.ToArray(), "application/zip", "PilotProAdmin-transfer.zip");
    }
}
