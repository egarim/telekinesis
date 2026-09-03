import 'package:telekinesis_medium/src/naming.dart';
import 'package:test/test.dart';

void main() {
  // Parity vectors with the C# generator (MediumGenerator/MediumSemanticId).
  test('stripSuffix removes verb/command suffixes', () {
    expect(stripSuffix('CreateInvoiceCommand'), 'CreateInvoice');
    expect(stripSuffix('DeleteAction'), 'Delete');
    expect(stripSuffix('SaveButton'), 'Save');
    expect(stripSuffix('ClickHandler'), 'Click');
    expect(stripSuffix('Command'), 'Command'); // never strip to empty
    expect(stripSuffix('Submit'), 'Submit');
  });

  test('normalizeSemanticId splits camelCase into dot segments', () {
    expect(normalizeSemanticId('CreateInvoice'), 'create.invoice');
    expect(normalizeSemanticId('createInvoice'), 'create.invoice');
    expect(normalizeSemanticId('ExportPDF'), 'export.pdf');
    expect(normalizeSemanticId('invoice.customer'), 'invoice.customer');
    expect(normalizeSemanticId('...'), 'unknown');
  });

  test('humanize spaces camel boundaries, preserves casing', () {
    expect(humanize('CreateInvoice'), 'Create Invoice');
    expect(humanize('createInvoice'), 'create Invoice');
    expect(humanize('ExportPDF'), 'Export PDF');
  });
}
