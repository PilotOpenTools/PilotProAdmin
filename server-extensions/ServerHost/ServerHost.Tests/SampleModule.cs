using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ServerHost.Tests;

/// <summary>Модуль-фикстура: тесты компилируют его сами, отдельный проект и чужие репозитории не нужны.</summary>
internal static class SampleModule
{
    private const string Source = """
        using System.ComponentModel.Composition;
        using System.Reflection;
        using System.Threading.Tasks;
        using Ascon.Pilot.DataModifier;
        using Ascon.Pilot.ServerExtensions.SDK;

        [assembly: AssemblyVersion("1.2.3.0")]
        [assembly: AssemblyFileVersion("1.2.3.0")]

        namespace Sample
        {
            [Export(typeof(IServerActivity))]
            public class SampleActivity : IServerActivity
            {
                /// <summary>Пишет в Params["result"] свою версию - по ней тест видит, какая сборка отработала.</summary>
                public string Name => "Sample";

                public Task RunAsync(IModifierBase modifier, IModifierBackend backend, IServerActivityContext context, IAutomationEventContext ev)
                {
                    context.Params["result"] = "sample " + typeof(SampleActivity).Assembly.GetName().Version + " " + Newtonsoft.Json.JsonConvert.SerializeObject(1);
                    return Task.CompletedTask;
                }
            }
        }
        """;

    private static readonly Lazy<byte[]> Compiled = new(Compile);

    /// <summary>Сборка Sample.servext.dll, версия 1.2.3, одна активность Sample.</summary>
    public static byte[] Assembly => Compiled.Value;

    private static byte[] Compile()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "Sample.servext",
            new[] { CSharpSyntaxTree.ParseText(Source) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        if (!result.Success)
            throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return output.ToArray();
    }
}
