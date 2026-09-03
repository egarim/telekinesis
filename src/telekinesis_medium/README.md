# telekinesis_medium

Medium for Dart/Flutter (issue #39) — annotations plus a `build_runner` builder
that emits the `telekinesis.medium.json` sidecar manifest
[Telekinesis](https://github.com/egarim/telekinesis) merges onto your app's
accessibility tree. The Dart mirror of the C# `Telekinesis.Medium.Generators`
Roslyn generator: deterministic inference, explicit annotations override, no
LLM, no network.

## Use

```yaml
# pubspec.yaml
dependencies:
  telekinesis_medium: ^0.1.0
dev_dependencies:
  build_runner: ^2.4.0
```

Annotate fields, getters, or methods (a class groups its members into a
manifest view named after it; top-level members become app-global elements):

```dart
import 'package:telekinesis_medium/telekinesis_medium.dart';

class EnrollmentScreen {
  @MediumIntent('patient.enroll')
  void enrollCommand() {}                 // id: enroll, name: "enroll", role: button

  @MediumRiskOf(MediumRisk.destructive)  // destructive WITHOUT @mediumRequiresConfirmation
  @mediumRequiresConfirmation            //   would log a MEDIUM001 build warning
  void deletePatient() {}

  @MediumRole('textbox')
  @MediumSemanticId('patient.code')
  String enrollmentCodeField = '';
}
```

```
dart run build_runner build
```

emits `telekinesis.medium.json` at the package root (`build_to: source`, so it
is versioned and reviewable). Set the application name in `build.yaml`:

```yaml
targets:
  $default:
    builders:
      telekinesis_medium|medium_manifest:
        options: {application: NurseApp}
```

## Shipping it (Flutter Windows)

Telekinesis discovers the manifest as a **sidecar next to the executable**.
Copy it into the runner output, e.g. in `windows/runner/CMakeLists.txt`:

```cmake
install(FILES "${CMAKE_CURRENT_SOURCE_DIR}/../../telekinesis.medium.json"
        DESTINATION "${CMAKE_INSTALL_PREFIX}" COMPONENT Runtime)
```

## Matching rules (what makes it work)

`MediumMerger` matches manifest elements to the runtime tree by **accessible
name, case-insensitively** (role disambiguates duplicates). So the element's
`Semantics(label: ...)` must equal the manifest `name` — by default the
humanized member name (`enrollCommand` → `enroll`, `SaveButton` → `Save`).
Localized labels break the match; keep agent-facing labels locale-stable until
id-based matching lands (#40).

**Flutter Windows does not expose its widget tree to UIA by default** — an
agent (or Telekinesis) connecting as a plain UIA client sees only a generic
`FLUTTERVIEW` pane with no labels, and nothing exists for Medium to merge onto.
Screen readers trigger semantics automatically; UIA automation clients do not.
Turn it on explicitly (this also makes the app properly screen-reader
accessible):

```dart
void main() {
  WidgetsFlutterBinding.ensureInitialized();
  SemanticsBinding.instance.ensureSemantics();
  runApp(const MyApp());
}
```

Further caveats: `SelectableText` and custom render objects without
`Semantics` are invisible to the tree and cannot be matched.

## Inference rules (identical to the C# generator)

- suffixes `Command`/`Action`/`Button`/`Handler` are stripped from the member name
- semantic id = camelCase split into dot-separated lowercase (`createInvoice` → `create.invoice`)
- accessible name = camelCase split into words, casing preserved
- role defaults to `button`, actions to `[invoke]`
- risk defaults to `unknown` — never guessed safe
- `MediumRiskOf(destructive)` without `@mediumRequiresConfirmation` logs a MEDIUM001 build warning
