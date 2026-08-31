namespace Telekinesis.Medium;

/// <summary>A named relationship to another semantic element, e.g. <c>labelledby</c>, <c>describedby</c>.</summary>
public sealed record MediumRelationship(string Type, string Target);

/// <summary>
/// A framework-independent semantic description of a single UI element.
///
/// This mirrors the shape of Telekinesis's normalized accessibility element model, so
/// Phase 2 can merge Medium metadata onto the runtime accessibility tree without a
/// second automation stack. The core deliberately has <em>no</em> dependency on WPF, Uno,
/// Blazor, React, etc. — roles are strings so each framework adapter maps its own
/// vocabulary onto this contract.
///
/// All fields are advisory/contextual: actions are still executed through Telekinesis's
/// normal <c>find_elements</c>/<c>invoke</c> surface, subject to its enablement, audit
/// log and policy.
/// </summary>
public sealed record MediumElement
{
    /// <summary>Stable id, unique within the application semantic namespace (e.g. <c>invoice.create</c>).</summary>
    public required string SemanticId { get; init; }

    /// <summary>Semantic role, e.g. <c>button</c>, <c>textbox</c>, <c>link</c>.</summary>
    public required string Role { get; init; }

    /// <summary>Accessible name, if any.</summary>
    public string? Name { get; init; }

    /// <summary>Help/description text, if any.</summary>
    public string? Description { get; init; }

    /// <summary>Business intent, e.g. <c>invoice.create</c>.</summary>
    public string? Intent { get; init; }

    /// <summary>Safety classification; defaults to <see cref="MediumRisk.Unknown"/>, never guessed safe.</summary>
    public MediumRisk Risk { get; init; } = MediumRisk.Unknown;

    /// <summary>Whether a human confirmation is required before performing this action.</summary>
    public bool RequiresConfirmation { get; init; }

    /// <summary>Semantic action names the element supports, e.g. <c>invoke</c>, <c>set_text</c>.</summary>
    public IReadOnlyList<string> Actions { get; init; } = [];

    /// <summary>Relationships to other semantic elements.</summary>
    public IReadOnlyList<MediumRelationship> Relationships { get; init; } = [];

    /// <summary>Arbitrary extensible metadata. Never carry secrets here (see issue #28).</summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>();
}
