/// The `medium_manifest` builder (issue #39): scans the package for members
/// annotated with the Medium annotations and emits `telekinesis.medium.json`
/// at the package root (`build_to: source`, so the manifest is versioned and
/// reviewable). The Dart mirror of `Telekinesis.Medium.Generators`:
/// deterministic inference, explicit annotations override, no LLM, no network.
///
/// Grouping: members of a class land in a view named after the class;
/// top-level members land in the app-global `elements` list.
library;

import 'dart:convert';

import 'package:analyzer/dart/constant/value.dart';
import 'package:analyzer/dart/element/element.dart';
import 'package:build/build.dart';
import 'package:glob/glob.dart';

import 'src/naming.dart';

Builder mediumManifestBuilder(BuilderOptions options) =>
    _MediumManifestBuilder(options);

class _MediumManifestBuilder implements Builder {
  final BuilderOptions options;
  _MediumManifestBuilder(this.options);

  @override
  Map<String, List<String>> get buildExtensions => const {
        r'$package$': ['telekinesis.medium.json'],
      };

  @override
  Future<void> build(BuildStep buildStep) async {
    final views = <String, List<Map<String, Object?>>>{};
    final global = <Map<String, Object?>>[];

    // Sort inputs so the emitted manifest is byte-stable across runs — asset
    // enumeration order is not guaranteed, and this file is committed.
    final inputs = await buildStep.findAssets(Glob('lib/**.dart')).toList()
      ..sort((a, b) => a.path.compareTo(b.path));
    for (final input in inputs) {
      if (!await buildStep.resolver.isLibrary(input)) continue;
      final library = await buildStep.resolver.libraryFor(input);
      _scanLibrary(library, views, global);
    }

    if (views.isEmpty && global.isEmpty) return; // no Medium members → no manifest

    final manifest = <String, Object?>{
      'schemaVersion': '1.0',
      'application':
          options.config['application'] as String? ?? buildStep.inputId.package,
      'views': {
        for (final e in views.entries) e.key: {'elements': e.value},
      },
      'elements': global,
    };
    await buildStep.writeAsString(
      AssetId(buildStep.inputId.package, 'telekinesis.medium.json'),
      const JsonEncoder.withIndent('  ').convert(manifest),
    );
  }

  void _scanLibrary(
    LibraryElement library,
    Map<String, List<Map<String, Object?>>> views,
    List<Map<String, Object?>> global,
  ) {
    // Mixins and enums scan too — the C# generator catches members on any type.
    for (final type in [...library.classes, ...library.mixins, ...library.enums]) {
      for (final member in [
        ...type.fields,
        ...type.getters,
        ...type.setters,
        ...type.methods,
      ]) {
        final element = _analyze(member);
        if (element != null) {
          views.putIfAbsent(type.name ?? '', () => []).add(element);
        }
      }
    }
    for (final member in [
      ...library.topLevelVariables,
      ...library.getters,
      ...library.setters,
      ...library.topLevelFunctions,
    ]) {
      final element = _analyze(member);
      if (element != null) global.add(element);
    }
  }

  Map<String, Object?>? _analyze(Element member) {
    String? intent, role, semanticId;
    var risk = 'unknown';
    var requiresConfirmation = false;
    var isMedium = false;

    for (final annotation in member.metadata.annotations) {
      final value = annotation.computeConstantValue();
      final type = value?.type;
      final typeName = type?.getDisplayString();
      if (value == null || type == null || typeName == null) continue;
      // Same rule as the C# generator's namespace check: only annotations from
      // this package count, so unrelated classes that share a name are ignored.
      final uri = type.element?.library?.uri.toString();
      if (uri == null || !uri.startsWith('package:telekinesis_medium/')) {
        continue;
      }
      switch (typeName) {
        case 'MediumIntent':
          intent = value.getField('intent')?.toStringValue();
        case 'MediumRiskOf':
          risk = _riskName(value.getField('risk'));
        case 'MediumRequiresConfirmation':
          requiresConfirmation = true;
        case 'MediumRole':
          role = value.getField('role')?.toStringValue();
        case 'MediumSemanticId':
          semanticId = value.getField('semanticId')?.toStringValue();
        default:
          continue;
      }
      isMedium = true;
    }
    if (!isMedium) return null;

    final name = member.name ?? '';
    // ponytail: MEDIUM001 parity as a build log warning, not a hard failure.
    if (risk == 'destructive' && !requiresConfirmation) {
      log.warning(
          "Medium: member '$name' is destructive but does not require confirmation (MEDIUM001).");
    }
    return {
      'semanticId': semanticId ?? normalizeSemanticId(stripSuffix(name)),
      'role': role ?? 'button',
      'name': humanize(stripSuffix(name)),
      if (intent != null) 'intent': intent,
      'risk': risk,
      'requiresConfirmation': requiresConfirmation,
      'actions': ['invoke'],
      'relationships': <Object?>[],
      'metadata': <String, Object?>{},
    };
  }

  String _riskName(DartObject? risk) =>
      risk?.getField('_name')?.toStringValue() ?? 'unknown';
}
