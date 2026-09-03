## 0.1.0

Initial release (egarim/telekinesis#39, #40).

- Annotations mirroring the C# Medium attributes: `@MediumIntent`,
  `@MediumRiskOf`, `@mediumRequiresConfirmation`, `@MediumRole`,
  `@MediumSemanticId` — on fields, getters, and methods of classes, mixins,
  enums, and extensions.
- Aggregating `build_runner` builder emitting the `telekinesis.medium.json`
  sidecar manifest (`build_to: source`), with the C# generator's exact
  deterministic inference rules and a MEDIUM001 build warning.
- Locale-proof matching convention: set `Semantics(identifier:)` to the
  semantic id (surfaces as the UIA AutomationId on Windows).
