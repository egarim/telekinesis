using Telekinesis.Medium;

namespace MediumDemo.Components;

/// <summary>
/// A demo command surface. The Medium Roslyn generator scans these
/// <c>[Medium*]</c> annotations at build time and emits a strongly-typed
/// <c>GeneratedMedium</c> (no LLM, deterministic).
/// </summary>
public sealed class InvoiceFormViewModel
{
    [MediumIntent("invoice.create")]
    [MediumRisk(MediumRisk.Write)]
    public object CreateInvoiceCommand { get; } = new();

    [MediumIntent("invoice.customer.update")]
    [MediumRisk(MediumRisk.Write)]
    public object UpdateCustomerCommand { get; } = new();

    [MediumIntent("invoice.delete")]
    [MediumRisk(MediumRisk.Destructive)]
    [MediumRequiresConfirmation]
    public object DeleteInvoiceCommand { get; } = new();

    [MediumSemanticId("navigation.settings")]
    [MediumRole("link")]
    [MediumIntent("navigation.open")]
    public object OpenSettingsCommand { get; } = new();
}
