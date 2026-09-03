import 'dart:convert';
import 'dart:io';

import 'package:build/build.dart';
import 'package:build_test/build_test.dart';
import 'package:telekinesis_medium/builder.dart';
import 'package:test/test.dart';

const _app = '''
import 'package:telekinesis_medium/telekinesis_medium.dart';

class InvoiceEditor {
  @MediumIntent('invoice.create')
  void createInvoiceCommand() {}

  @MediumRiskOf(MediumRisk.destructive)
  @mediumRequiresConfirmation
  void deleteInvoice() {}

  @MediumRole('textbox')
  @MediumSemanticId('invoice.customer')
  String customerField = '';
}

@MediumIntent('app.logout')
void logoutButton() {}

@MediumRole('checkbox')
bool nightModeToggle = false;

extension PatientOps on String {
  @MediumIntent('patient.archive')
  void archivePatient() {}
}
''';

void main() {
  test('builder emits the manifest with inferred and explicit semantics',
      () async {
    final result = await testBuilder(
      mediumManifestBuilder(BuilderOptions({'application': 'NurseApp'})),
      {
        'telekinesis_medium|lib/telekinesis_medium.dart':
            File('lib/telekinesis_medium.dart').readAsStringSync(),
        'app|lib/main.dart': _app,
      },
      rootPackage: 'app',
    );

    // build_to: source outputs land in the harness's generated dir in tests.
    final id = result.readerWriter.testing.assets
        .firstWhere((a) => a.path.endsWith('telekinesis.medium.json'));
    final manifest = jsonDecode(result.readerWriter.testing.readString(id))
        as Map<String, dynamic>;

    expect(manifest['schemaVersion'], '1.0');
    expect(manifest['application'], 'NurseApp');

    final view = manifest['views']['InvoiceEditor']['elements'] as List;
    final create = view.singleWhere((e) => e['intent'] == 'invoice.create');
    expect(create['semanticId'], 'create.invoice'); // Command suffix stripped
    expect(create['name'], 'create Invoice');
    expect(create['role'], 'button');
    expect(create['risk'], 'unknown');
    expect(create['actions'], ['invoke']);

    final delete = view.singleWhere((e) => e['semanticId'] == 'delete.invoice');
    expect(delete['risk'], 'destructive');
    expect(delete['requiresConfirmation'], true);

    final customer =
        view.singleWhere((e) => e['semanticId'] == 'invoice.customer');
    expect(customer['role'], 'textbox');

    final global = manifest['elements'] as List;
    final logout = global.singleWhere((e) => e['intent'] == 'app.logout');
    expect(logout['semanticId'], 'logout'); // Button suffix stripped

    // top-level variable path
    final toggle =
        global.singleWhere((e) => e['semanticId'] == 'night.mode.toggle');
    expect(toggle['role'], 'checkbox');

    // extension members scan too, into a view named after the extension
    final ext = manifest['views']['PatientOps']['elements'] as List;
    expect(ext.single['intent'], 'patient.archive');
  });

  test('no annotations → no manifest', () async {
    final result = await testBuilder(
      mediumManifestBuilder(BuilderOptions.empty),
      {'app|lib/main.dart': 'void main() {}'},
    );
    expect(
        result.readerWriter.testing.assets
            .where((a) => a.path.endsWith('telekinesis.medium.json')),
        isEmpty);
  });
}
