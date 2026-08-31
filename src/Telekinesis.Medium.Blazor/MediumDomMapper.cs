namespace Telekinesis.Medium.Blazor;

/// <summary>
/// Maps rendered DOM (an HTML tag + its attributes, including ARIA and input type) onto
/// Medium semantics — the "recognize common controls" part of the adapter. It is pure
/// text-in/text-out (no browser dependency) so it is unit-testable and can also be used
/// by the browser provider or a JS bridge. Semantic ids are derived deterministically
/// from role + accessible name when not supplied.
/// </summary>
public static class MediumDomMapper
{
    public static string MapRole(string tag, string? type = null)
    {
        switch ((tag ?? string.Empty).ToLowerInvariant())
        {
            case "button": return "button";
            case "a":
            case "link": return "link";
            case "input":
                switch ((type ?? string.Empty).ToLowerInvariant())
                {
                    case "checkbox": return "checkbox";
                    case "radio": return "radio";
                    case "range": return "slider";
                    case "password": return "password";
                    default: return "textbox";
                }
            case "select": return "combobox";
            case "textarea": return "textbox";
            case "label": return "label";
            case "img": return "image";
            case "nav": return "navigation";
            default: return "group";
        }
    }

    /// <summary>
    /// Map a DOM element to <see cref="MediumElement"/>. The accessible name is taken from
    /// <c>aria-label</c>, then <c>data-medium-name</c>, then a <c>name</c>/<c>placeholder</c>
    /// attribute. A deterministic semantic id is derived when <paramref name="semanticId"/>
    /// is null.
    /// </summary>
    public static MediumElement Map(string tag, IReadOnlyDictionary<string, string> attrs, string? semanticId = null)
    {
        attrs ??= new Dictionary<string, string>();
        var type = attrs.TryGetValue("type", out var t) ? t : null;
        var role = MapRole(tag, type);

        string? name = null;
        if (attrs.TryGetValue("aria-label", out var aria)) name = aria;
        else if (attrs.TryGetValue("data-medium-name", out var dn)) name = dn;
        else if (attrs.TryGetValue("name", out var n)) name = n;
        else if (attrs.TryGetValue("placeholder", out var p)) name = p;

        string? intent = attrs.TryGetValue("data-medium-intent", out var intentVal) ? intentVal : null;

        var id = semanticId
                 ?? MediumSemanticId.Normalize($"{role}{(string.IsNullOrWhiteSpace(name) ? string.Empty : "." + name)}")
                 ?? $"{role}.{tag}";

        return new MediumElement { SemanticId = id, Role = role, Name = name, Intent = intent };
    }
}
