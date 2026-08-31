# Medium — build accessible apps for humans and AI agents

**Telekinesis** gives an agent perception and control over existing applications.
**Medium** lets an application communicate its semantics and capabilities to
Telekinesis, so its UI is easier for both humans *and* agents to understand.

Medium is **not** a second automation stack. It is a semantic enrichment layer that
builds on native accessibility (UIA / AT-SPI / AXAPI / ARIA), auto-generating as much
metadata as possible and letting developers fill the remaining gaps with explicit,
agent-oriented semantics (intent, risk, side effects, stable IDs, confirmation).

```
Application source / UI metadata
            ↓
      Native accessibility
            ↓
           Medium
            ↓
     Semantic enrichment
            ↓
 Telekinesis Semantic Model
            ↓
     Humans + AI agents
```

The human accessibility layer stays the baseline. Agent accessibility is an
*extension* of human accessibility, not a competing parallel system.

---

## Core principle: progressive enhancement

- Apps **without Medium** keep working exactly as they do today through Telekinesis.
- Medium only adds clarity and stable identity; it never changes how the agent
  perceives or drives the element.
- The agent keeps calling the same `find_elements` / `read_element` / `invoke` /
  `set_text` surface regardless of where semantics came from — native accessibility,
  Medium, an app-specific provider, or the vision fallback.

Human accessibility is encouraged, not replaced. A developer cannot hide a badly
accessible control behind agent-only metadata: the human baseline must be met first.

---

## The semantic model

Framework-independent core in `src/Telekinesis.Medium` (no WPF/Uno/Blazor dependency).

```
MediumRisk:    Unknown | Read | Write | Destructive | Privileged

MediumElement:
  SemanticId            (required, stable, app-unique)
  Role                  (required, string — e.g. "button", "textbox")
  Name / Description
  Intent                (e.g. "invoice.create")
  Risk                  (default Unknown — never guessed safe)
  RequiresConfirmation
  Actions               (e.g. "invoke", "set_text")
  Relationships         (e.g. labelledby -> label.customer)
  Metadata              (arbitrary, extensible)
```

An element maps naturally onto Telekinesis's normalized accessibility element; the
merge path (below) enriches the runtime tree rather than forming a parallel hierarchy.

### Merging with the runtime tree

`MediumMerger` associates a manifest's elements with runtime accessibility elements by
**accessible name** (case-insensitive), disambiguated by role when several Medium
elements share a name. `MediumEnrichingBackend` wraps a resolved accessibility backend and
applies that merge to every element returned by `get_tree`, `get_subtree`, `find_elements`
and `read_element`, so the agent keeps using the same tools and sees extra advisory fields
on matched elements:

```json
{
  "semanticId": "invoice.delete",
  "intent": "invoice.delete",
  "risk": "destructive",
  "requiresConfirmation": true
}
```

Enrichment is **additive and safe**:

- It never invents elements (no phantom controls) and never overwrites the element's own
  native name, role, address, or states.
- `Risk` and `RequiresConfirmation` only surface when explicitly declared — an element
  with undeclared risk is treated as Unknown by policy, never "safe".
- Non-Medium apps are untouched: a backend is only wrapped when the app ships a
  `telekinesis.medium.json`, and unmatched elements stay identical (nullable fields are
  omitted from JSON).

Discovery in the CLI (Phase 2) locates a **sidecar** `telekinesis.medium.json` next to the
app's executable (`MediumManifestFile` + `MediumDiscovery`); missing or malformed manifests
are treated as "no Medium" and never break resolution.

### Risk defaults

`MediumRisk.Unknown` is the default. Medium **never auto-classifies an unknown action
as safe**; if a risk can't be inferred it stays unknown, so policy can require
confirmation or reject it rather than silently allowing it.

---

## Generated manifest

A versioned, machine-readable manifest, `telekinesis.medium.json`:

```json
{
  "schemaVersion": "1.0",
  "application": "AcmeERP",
  "views": {
    "InvoiceEditor": {
      "elements": [
        { "semanticId": "invoice.customer", "role": "textbox", "name": "Customer" },
        {
          "semanticId": "invoice.create",
          "role": "button",
          "name": "Create Invoice",
          "intent": "invoice.create",
          "risk": "write",
          "actions": ["invoke"]
        }
      ]
    }
  }
}
```

The manifest is **not** an MCP server and **not** an alternate tool protocol — it is
semantic metadata Telekinesis merges with the runtime accessibility tree (`MediumJson`
handles serialization; the schema is versioned via `MediumSchema.Version`).

---

## Semantic IDs

IDs should be:

- stable across runs,
- independent of screen coordinates and transient runtime IDs,
- unique within an application semantic namespace,
- preferably stable across harmless UI refactors.

Canonical form is dot-separated lowercase segments: `invoice.customer`,
`navigation.settings`. `MediumSemanticId.TryNormalize` / `Normalize` / `Require` provide
a deterministic generator (same input → same output; never random, never
coordinate-based), and `IsValid` reports whether an id is already canonical. Developers
should override autogenerated IDs explicitly whenever the business meaning matters.

---

## Security model

- Medium metadata is **advisory and contextual**; action execution remains subject to
  Telekinesis's existing action enablement, audit logging, and policy.
- It cannot grant powers Telekinesis does not already have — no bypass.
- **Secrets** must never be emitted into the manifest or carried in the model. The
  credential-handoff rule (`fill_credential`) continues to govern secrets; Medium is
  metadata-only.
- Supports policy metadata (`risk`, `requiresConfirmation`, `sideEffects`). Unknown
  risk is not treated as safe.

---

## Framework adapters

The core has no framework dependency. Adapters map a framework's own metadata onto the
core contract, and the architecture keeps them possible without changing the protocol:

```
Telekinesis.Medium.Uno        (priority — reuses uno-atspi-bridge / AutomationPeer)
Telekinesis.Medium.Blazor
Telekinesis.Medium.Wpf
Telekinesis.Medium.Avalonia
Telekinesis.Medium.WinUI
Telekinesis.Medium.Maui
Telekinesis.Medium.WinForms
```

Uno is first-class: Medium should build on the same `AutomationPeer` accessibility
metadata already exposed by `uno-atspi-bridge`, not invent a separate representation.

### Blazor adapter (done, this PR)

`Telekinesis.Medium.Blazor` lets a Blazor app serve its Medium semantics over HTTP — the
same channel the browser provider already reads, so a Blazor page becomes richer to
Telekinesis without any scraping.

- **`MediumManifestBuilder`** — a DI singleton that collects `MediumElement`s (app-global
  or per-view), idempotent by semantic id (last wins), and `Build()`s a `MediumManifest`.
- **`AddTelekinesisMedium()`** registers it; **`MapMediumManifest()`** maps
  `/telekinesis.medium.json` to the latest manifest.
- **`MediumDomMapper`** — maps a rendered DOM element (tag + ARIA + `data-medium-*`) onto
  Medium semantics (role, accessible name, deterministic semantic id), so common controls
  (buttons, text inputs, checkboxes, links, lists, selects) are recognized automatically.
- **`<MediumSemantic/>`** — a Razor component that declaratively registers a semantic
  element (id, role, name, intent, risk, requiresConfirmation) when rendered; use it to
  give an ordinary button its business intent and risk.

See `samples/MediumDemo` for a working Blazor Server app that serves the manifest.

---

## Build roadmap

- **Phase 1 — core semantic contract** *(done, PR #29)*: `Telekinesis.Medium`,
  versioned model, JSON serialization, stable-ID rules, risk/intent/confirmation,
  unit tests, this doc.
- **Phase 2 — Telekinesis merge path** *(done, PR #30)*: discovery/loading of Medium metadata
  (sidecar manifest), merge onto accessibility elements via `MediumEnrichingBackend`,
  expose enriched fields in read/find output, non-Medium apps unchanged; tests for merge
  precedence, missing/stale metadata and role disambiguation.
- **Phase 3 — framework adapter** *(Blazor done, this PR)*: `Telekinesis.Medium.Blazor`
  with a manifest builder + `/telekinesis.medium.json` endpoint, DOM mapping, and a
  `<MediumSemantic/>` component; a working sample end-to-end. (Uno remains a priority
  follow-up reusing `uno-atspi-bridge`.)
- **Phase 4 — Roslyn generator/analyzer**: deterministic compile-time derivation of IDs,
  action metadata, and diagnostics; never requires an LLM during a normal build.

A `telekinesis medium inspect / check` CLI diagnostic (with a compatibility score) and a
**Medium Inspector** are longer-term; the semantic model makes the scoring possible.

---

## Non-goals (especially early)

Embedding an LLM, an agent loop, replacing MCP/UIA/AT-SPI/ARIA, exposing arbitrary app
internals, invoking domain methods directly as a bypass around the UI, implementing every
framework, a plugin marketplace, or requiring cloud infrastructure.

Medium's first job is **semantic accessibility and enrichment**.
