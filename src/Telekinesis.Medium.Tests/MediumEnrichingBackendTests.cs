using Telekinesis.Abstractions;
using Telekinesis.Medium;
using Xunit;

namespace Telekinesis.Medium.Tests;

public class MediumEnrichingBackendTests
{
    private static MediumManifest Manifest() => new()
    {
        Application = "AcmeERP",
        Views = new Dictionary<string, MediumView>
        {
            ["V"] = new() { Elements = new List<MediumElement>
            {
                new() { SemanticId = "save.button", Role = "button", Name = "Save", Intent = "save", Risk = MediumRisk.Write, Actions = ["invoke"] },
                new() { SemanticId = "invoice.delete", Role = "button", Name = "Delete Invoice", Risk = MediumRisk.Destructive, RequiresConfirmation = true },
            } },
        },
    };

    private static AccessibleElement Button(string name) => new()
    {
        Ref = new ElementRef("r", "pid:1"),
        Role = AccessibleRole.Button,
        NativeRole = "Button",
        Name = name,
    };

    [Fact]
    public async Task FindElements_enriches_results()
    {
        var stub = new StubBackend { Found = [Button("Save")] };
        var backend = new MediumEnrichingBackend(stub, Manifest());
        var results = await backend.FindElementsAsync(new ElementQuery());
        var e = results.Single();
        Assert.Equal("save.button", e.SemanticId);
        Assert.Equal("write", e.Risk);
        Assert.Equal(["invoke"], e.MediumActions);
    }

    [Fact]
    public async Task ReadElement_enriches_and_preserves_native_data()
    {
        var stub = new StubBackend { Read = Button("Delete Invoice") };
        var backend = new MediumEnrichingBackend(stub, Manifest());
        var e = await backend.ReadElementAsync(new ElementRef("r", "pid:1"));
        Assert.Equal("invoice.delete", e.SemanticId);
        Assert.Equal("destructive", e.Risk);
        Assert.True(e.RequiresConfirmation);
        Assert.Equal("r", e.Ref.Id);              // native address untouched
        Assert.Equal(AccessibleRole.Button, e.Role);
    }

    [Fact]
    public async Task Stale_medium_reference_does_not_invent_elements()
    {
        // Manifest mentions "Update Invoice", but the app only returns "Save".
        var stub = new StubBackend { Found = [Button("Save")] };
        var backend = new MediumEnrichingBackend(stub, Manifest());
        var results = await backend.FindElementsAsync(new ElementQuery());
        Assert.Single(results);                    // no phantoms
        Assert.Equal("save.button", results[0].SemanticId);
    }

    [Fact]
    public async Task GetTree_enriches_children_recursively()
    {
        var child = Button("Save") with { Ref = new ElementRef("c", "pid:1") };
        var tree = new AccessibleElement
        {
            Ref = new ElementRef("root", "pid:1"),
            Role = AccessibleRole.Window,
            NativeRole = "Window",
            Name = "Window",
            Children = [child],
        };
        var stub = new StubBackend { Tree = tree };
        var backend = new MediumEnrichingBackend(stub, Manifest());
        var enriched = await backend.GetTreeAsync("pid:1");
        Assert.Equal("save.button", enriched.Children!.Single().SemanticId);
    }

    /// <summary>Minimal backend double returning canned perception data.</summary>
    private sealed class StubBackend : IAccessibilityBackend
    {
        public AccessibleElement Tree { get; init; } = default!;
        public IReadOnlyList<AccessibleElement> Found { get; init; } = [];
        public AccessibleElement Read { get; init; } = default!;

        public string Name => "stub";
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<DiagnosticReport> DiagnoseAsync(CancellationToken ct = default) => Task.FromResult(new DiagnosticReport(true, []));
        public Task<IReadOnlyList<ApplicationInfo>> ListApplicationsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ApplicationInfo>>([]);
        public Task<AccessibleElement> GetTreeAsync(string applicationId, int maxDepth = 3, CancellationToken ct = default) => Task.FromResult(Tree);
        public Task<AccessibleElement> GetSubtreeAsync(ElementRef element, int maxDepth = 3, CancellationToken ct = default) => Task.FromResult(Tree);
        public Task<IReadOnlyList<AccessibleElement>> FindElementsAsync(ElementQuery query, CancellationToken ct = default) => Task.FromResult(Found);
        public Task<AccessibleElement> ReadElementAsync(ElementRef element, CancellationToken ct = default) => Task.FromResult(Read);
        public Task<AccessibleElement?> GetFocusedAsync(CancellationToken ct = default) => Task.FromResult<AccessibleElement?>(null);
        public Task<AccessibilityEvent?> WaitForEventAsync(string kind, TimeSpan timeout, CancellationToken ct = default) => Task.FromResult<AccessibilityEvent?>(null);
        public Task<ActionResult> InvokeAsync(ElementRef element, string? action = null, CancellationToken ct = default) => Throw();
        public Task<ActionResult> SetTextAsync(ElementRef element, string text, CancellationToken ct = default) => Throw();
        public Task<ActionResult> SetValueAsync(ElementRef element, double value, CancellationToken ct = default) => Throw();
        public Task<ActionResult> ClickAsync(ElementRef element, PointerButton button = PointerButton.Left, CancellationToken ct = default) => Throw();
        public Task<ActionResult> TypeTextAsync(string text, CancellationToken ct = default) => Throw();
        public Task<ActionResult> PressKeysAsync(string combination, CancellationToken ct = default) => Throw();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static Task<ActionResult> Throw() => throw new NotSupportedException();
    }
}
