using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Telekinesis.Medium;
using Telekinesis.Medium.Generators;
using Xunit;

namespace Telekinesis.Medium.Generators.Tests;

public class MediumGeneratorTests
{
    private const string Source = """
        using Telekinesis.Medium;
        namespace DemoApp;

        public class ViewModel
        {
            [MediumIntent("invoice.create")][MediumRisk(MediumRisk.Write)]
            public object CreateInvoiceCommand { get; } = new();

            [MediumSemanticId("navigation.settings")][MediumRole("link")][MediumIntent("navigation.open")]
            public object OpenSettingsCommand { get; } = new();

            [MediumIntent("invoice.delete")]
            public object DeleteInvoiceCommand { get; } = new();
        }
        """;

    private const string DestructiveSource = """
        using Telekinesis.Medium;
        namespace DemoApp;

        public class ViewModel
        {
            [MediumRisk(MediumRisk.Destructive)]
            public object DeleteInvoiceCommand { get; } = new();

            [MediumRisk(MediumRisk.Destructive)][MediumRequiresConfirmation]
            public object DeleteCustomerCommand { get; } = new();
        }
        """;

    [Fact]
    public void Generates_manifest_for_annotated_members()
    {
        var text = Build(Source);

        // explicit semantic id override
        Assert.Contains("\"navigation.settings\"", text);
        // deterministic id derived from the member name ("CreateInvoiceCommand" -> "create.invoice")
        Assert.Contains("\"create.invoice\"", text);
        Assert.Contains("MediumRisk.Write", text);
        Assert.Contains("Role = \"link\"", text);
        Assert.Contains("\"invoke\"", text);
    }

    [Fact]
    public void Generates_humanized_accessible_name()
    {
        Assert.Contains("Name = \"Create Invoice\"", Build(Source));
    }

    [Fact]
    public void Reports_destructive_action_without_confirmation()
    {
        var run = Run(DestructiveSource);
        Assert.Contains(run.Diagnostics, d => d.Id == "MEDIUM001" && d.Severity == DiagnosticSeverity.Warning);
        // a destructive action that DOES require confirmation must not be flagged
        Assert.DoesNotContain(run.Diagnostics, d => d.Id == "MEDIUM001" && d.GetMessage().Contains("DeleteCustomerCommand"));
    }

    [Fact]
    public void Emits_nothing_when_no_medium_members_exist()
    {
        var run = Run("namespace DemoApp; public class ViewModel { public object SaveCommand { get; } = new(); }");
        Assert.Empty(run.Results.SelectMany(r => r.GeneratedSources));
    }

    private static string Build(string source) =>
        string.Join("\n", Run(source).Results.SelectMany(r => r.GeneratedSources).Select(s => s.SourceText.ToString()));

    private static GeneratorDriverRunResult Run(string source)
    {
        var compilation = Compilation(source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new MediumGenerator().AsSourceGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
        return driver.GetRunResult();
    }

    private static CSharpCompilation Compilation(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return CSharpCompilation.Create("Test", [tree], References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static ImmutableArray<MetadataReference> References()
    {
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var wanted = new HashSet<string> { "System.Runtime.dll", "System.Private.CoreLib.dll", "System.Collections.dll", "System.Linq.dll", "netstandard.dll" };
        var refs = new List<MetadataReference>();
        foreach (var p in tpa)
            if (wanted.Contains(Path.GetFileName(p)))
                refs.Add(MetadataReference.CreateFromFile(p));
        refs.Add(MetadataReference.CreateFromFile(typeof(MediumElement).Assembly.Location));
        return [.. refs];
    }
}
