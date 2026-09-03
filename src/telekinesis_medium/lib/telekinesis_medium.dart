/// Medium annotations for Dart/Flutter (issue #39) — the Dart mirror of the
/// C# `Telekinesis.Medium` attributes. Annotate fields, getters, or methods;
/// the `medium_manifest` builder scans them and emits the
/// `telekinesis.medium.json` sidecar manifest deterministically.
library;

/// Safety classification. Mirrors `Telekinesis.Medium.MediumRisk` — an
/// undeclared risk stays [unknown]; it is never guessed safe.
enum MediumRisk { unknown, read, write, destructive, privileged }

/// Business intent, e.g. `@MediumIntent('invoice.create')`.
class MediumIntent {
  final String intent;
  const MediumIntent(this.intent);
}

/// Declares the element's risk, e.g. `@MediumRiskOf(MediumRisk.destructive)`.
class MediumRiskOf {
  final MediumRisk risk;
  const MediumRiskOf(this.risk);
}

/// Marks an action as requiring human confirmation. Use as
/// `@mediumRequiresConfirmation`.
class MediumRequiresConfirmation {
  const MediumRequiresConfirmation();
}

const mediumRequiresConfirmation = MediumRequiresConfirmation();

/// Overrides the semantic role (default: `button`).
class MediumRole {
  final String role;
  const MediumRole(this.role);
}

/// Overrides the derived semantic id (default: normalized member name).
class MediumSemanticId {
  final String semanticId;
  const MediumSemanticId(this.semanticId);
}
