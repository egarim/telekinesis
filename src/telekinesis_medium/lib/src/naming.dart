/// Deterministic naming rules, ported 1:1 from the C# generator
/// (`Telekinesis.Medium.Generators.MediumGenerator` / `MediumSemanticId`):
/// suffix stripping, camelCase word boundaries, dot-separated lowercase ids,
/// humanized accessible names. Same input → same output, always.
library;

const _suffixes = ['Command', 'Action', 'Button', 'Handler'];

/// "createInvoiceCommand" -> "createInvoice".
String stripSuffix(String name) {
  for (final suffix in _suffixes) {
    if (name.length > suffix.length && name.endsWith(suffix)) {
      return name.substring(0, name.length - suffix.length);
    }
  }
  return name;
}

/// "CreateInvoice" -> "Create Invoice" (uppercase runs like "PDF" kept intact).
String _insertCamelBoundaries(String name) {
  final sb = StringBuffer();
  for (var i = 0; i < name.length; i++) {
    final c = name[i];
    if (i > 0 &&
        _isUpper(c) &&
        (_isLower(name[i - 1]) || (i + 1 < name.length && _isLower(name[i + 1])))) {
      sb.write(' ');
    }
    sb.write(c);
  }
  return sb.toString();
}

bool _isUpper(String c) => c != c.toLowerCase() && c == c.toUpperCase();
bool _isLower(String c) => c != c.toUpperCase() && c == c.toLowerCase();
bool _isIdChar(String c) =>
    c == '-' || c == '_' || RegExp(r'^[a-z0-9]$').hasMatch(c);

/// "CreateInvoice" -> "create.invoice"; unusable input -> "unknown".
String normalizeSemanticId(String raw) {
  final s = _insertCamelBoundaries(raw).toLowerCase();
  final segments = <String>[];
  final cur = StringBuffer();
  for (var i = 0; i < s.length; i++) {
    final c = s[i];
    if (_isIdChar(c)) {
      cur.write(c);
    } else if (cur.isNotEmpty) {
      segments.add(cur.toString());
      cur.clear();
    }
  }
  if (cur.isNotEmpty) segments.add(cur.toString());
  return segments.isEmpty ? 'unknown' : segments.join('.');
}

/// "CreateInvoice" -> "Create Invoice" — the default accessible name, which is
/// what `MediumMerger` matches against the runtime tree (case-insensitively,
/// so original casing is preserved exactly like the C# generator).
String humanize(String name) => _insertCamelBoundaries(name).trim();
