using MediumDemo.Components;
using Telekinesis.Medium;
using Telekinesis.Medium.Blazor;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddTelekinesisMedium();

var app = builder.Build();

// Register the app's Medium semantics so the manifest endpoint has content even
// before any page renders. (Pages may also contribute via <MediumSemantic/>.)
var medium = app.Services.GetRequiredService<MediumManifestBuilder>();
medium.Application = "MediumDemo";
medium.RegisterView("InvoiceEditor", new MediumElement
{
    SemanticId = "invoice.create",
    Role = "button",
    Name = "Create Invoice",
    Intent = "invoice.create",
    Risk = MediumRisk.Write,
    Actions = ["invoke", "click"],
});
medium.RegisterView("InvoiceEditor", new MediumElement
{
    SemanticId = "invoice.customer",
    Role = "textbox",
    Name = "Customer",
    Actions = ["set_text"],
});

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapMediumManifest();

app.Run();
