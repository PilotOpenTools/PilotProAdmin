namespace ProAdmin.Modules;

/// <summary>
/// Сборки из папки установки клиента Pilot (Pilot-BIM и Pilot-ICE Enterprise 26.x). Недостающую dll модуля клиент сначала ищет
/// в своей папке, поэтому класть такие сборки в модули не нужно: загрузится копия клиента. Список для кнопки
/// «Добавить библиотеки клиента Pilot» на странице спецификации - при обновлении клиента его стоит перечитать из папки установки.
/// </summary>
public static class PilotClientAssemblies
{
    public const string Comment = "есть в папке клиента Pilot 26.x";

    public static readonly IReadOnlyList<string> Names = new[]
    {
        "Ascon.Pilot.AutomationScripts", "Ascon.Pilot.Camera360", "Ascon.Pilot.Client", "Ascon.Pilot.ClientCore", "Ascon.Pilot.Common",
        "Ascon.Pilot.Controls", "Ascon.Pilot.Core", "Ascon.Pilot.DataClasses", "Ascon.Pilot.DataModifier", "Ascon.Pilot.DocumentComparer",
        "Ascon.Pilot.EnterpriseFeatures", "Ascon.Pilot.LicenseCoreShared", "Ascon.Pilot.LocalStorage", "Ascon.Pilot.Pilot",
        "Ascon.Pilot.Pipes", "Ascon.Pilot.PluginLoader", "Ascon.Pilot.PluginObjects", "Ascon.Pilot.ProjectionsRepository",
        "Ascon.Pilot.SDK", "Ascon.Pilot.SDK.Common", "Ascon.Pilot.Server.Api", "Ascon.Pilot.Theme", "Ascon.Pilot.Transport",
        "Ascon.Pilot.UpdateManager", "Ascon.Pilot.Updater.Interface", "Ascon.Pilot.VirtualFileSystem", "Ascon.Pilot.VirtualFileSystem.Pfm",
        "Ascon.Pilot.VisualElements", "Ascon.Pilot.XpsMerger", "Ascon.Pilot.XpsRender", "DocumentFormat.OpenXml",
        "DocumentFormat.OpenXml.Framework", "Emoji.Wpf", "GongSolutions.WPF.DragDrop", "Hardcodet.NotifyIcon.Wpf", "Interop.SHDocVw",
        "J2N", "log4net", "Lucene.Net", "Lucene.Net.Analysis.Common", "Lucene.Net.Codecs", "Lucene.Net.Queries", "Lucene.Net.QueryParser",
        "Lucene.Net.Sandbox", "Microsoft.AspNetCore.Cryptography.Internal", "Microsoft.AspNetCore.DataProtection",
        "Microsoft.AspNetCore.DataProtection.Abstractions", "Microsoft.AspNetCore.DataProtection.Extensions",
        "Microsoft.Bcl.AsyncInterfaces", "Microsoft.Extensions.Configuration.Abstractions", "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.FileProviders.Abstractions",
        "Microsoft.Extensions.Hosting.Abstractions", "Microsoft.Extensions.Logging.Abstractions", "Microsoft.Extensions.Options",
        "Microsoft.Extensions.Primitives", "Microsoft.Win32.Registry", "Microsoft.Xaml.Behaviors", "MsgReader", "NAudio", "NAudio.Asio",
        "NAudio.Core", "NAudio.Midi", "NAudio.Wasapi", "NAudio.WinForms", "NAudio.WinMM", "Newtonsoft.Json", "NodaTime", "NSspi",
        "PdfSharp", "PdfSharp.BarCodes", "PdfSharp.Charting", "PdfSharp.Cryptography", "PdfSharp.Quality", "PdfSharp.Shared",
        "PdfSharp.Snippets", "PdfSharp.System", "PdfSharp.WPFonts", "protobuf-net", "protobuf-net.Core", "SharpVectors.Converters.Wpf",
        "SharpVectors.Core", "SharpVectors.Css", "SharpVectors.Dom", "SharpVectors.Model", "SharpVectors.Rendering.Gdi",
        "SharpVectors.Rendering.Wpf", "SharpVectors.Runtime.Wpf", "SkiaSharp", "Stfu", "System.Buffers", "System.Collections.Immutable",
        "System.Data.SQLite", "System.Diagnostics.EventLog", "System.IO.Pipes.AccessControl", "System.Memory", "System.Numerics.Vectors",
        "System.Reflection.DispatchProxy", "System.Runtime.CompilerServices.Unsafe", "System.Security.AccessControl",
        "System.Security.Cryptography.ProtectedData", "System.Security.Cryptography.Xml", "System.Security.Permissions",
        "System.Security.Principal.Windows", "System.ServiceModel.Primitives", "System.ServiceProcess.ServiceController",
        "System.Threading.Tasks.Extensions", "Typography.GlyphLayout", "Typography.OpenFont", "Unity.Abstractions", "Unity.Container",
        "Xceed.Wpf.AvalonDock", "Xceed.Wpf.AvalonDock.Themes.Metro", "Xceed.Wpf.Toolkit", "zxing",
    };

    /// <summary>Добавляет в спецификацию сборки клиента, которых в ней ещё нет (правило «есть в клиенте Pilot»); возвращает число добавленных.</summary>
    public static int AddTo(AssemblySpec spec)
    {
        var added = 0;
        foreach (var name in Names)
        {
            if (spec.Find(name) is not null)
                continue;
            spec.Entries.Add(new AssemblySpecEntry { Name = name, Rule = AssemblyRule.Pilot, Comment = Comment });
            added++;
        }
        return added;
    }
}
