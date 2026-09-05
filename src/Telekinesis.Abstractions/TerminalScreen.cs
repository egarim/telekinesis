using System.Text;

namespace Telekinesis.Abstractions;

/// <summary>
/// Compact VT/ANSI screen renderer (issue #27): tracks the cursor over a
/// cols×rows cell grid, handles the control bytes and CSI/OSC sequences that
/// interactive programs actually emit, and renders the visible screen as plain
/// text (SGR colors are parsed and dropped). Lines that scroll off the top go
/// to a capped scrollback. Deterministic and allocation-light; thread-safe via
/// an internal lock (the PTY reader feeds it from a background thread).
/// </summary>
public sealed class TerminalScreen
{
    private readonly object _gate = new();
    private readonly List<char[]> _scrollback = [];
    private const int ScrollbackCap = 2000;

    private char[][] _grid;
    private int _cols, _rows, _cx, _cy;

    // Sequence-parser state: CSI/OSC/ESC accumulate across Feed() chunk boundaries.
    private enum Mode { Text, Esc, Csi, Osc }
    private Mode _mode = Mode.Text;
    private readonly StringBuilder _seq = new();
    private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();

    public TerminalScreen(int cols = 120, int rows = 30)
    {
        _cols = Math.Max(cols, 2);
        _rows = Math.Max(rows, 2);
        _grid = NewGrid(_cols, _rows);
    }

    private static char[][] NewGrid(int cols, int rows)
    {
        var g = new char[rows][];
        for (var y = 0; y < rows; y++) g[y] = MakeRow(cols);
        return g;
    }

    private static char[] MakeRow(int cols)
    {
        var row = new char[cols];
        Array.Fill(row, ' ');
        return row;
    }

    public void Resize(int cols, int rows)
    {
        lock (_gate)
        {
            // ponytail: rebuild and copy what fits; full reflow is a TUI luxury the
            // child re-paints after the resize anyway.
            var g = NewGrid(cols, rows);
            for (var y = 0; y < Math.Min(rows, _rows); y++)
                Array.Copy(_grid[y], g[y], Math.Min(cols, _cols));
            _grid = g;
            _cols = Math.Max(cols, 2);
            _rows = Math.Max(rows, 2);
            _cx = Math.Min(_cx, _cols - 1);
            _cy = Math.Min(_cy, _rows - 1);
        }
    }

    public void Feed(byte[] data, int count)
    {
        var chars = new char[_utf8.GetCharCount(data, 0, count, flush: false)];
        _utf8.GetChars(data, 0, count, chars, 0, flush: false);
        lock (_gate)
        {
            foreach (var c in chars) FeedChar(c);
        }
    }

    private void FeedChar(char c)
    {
        switch (_mode)
        {
            case Mode.Esc:
                _mode = c switch
                {
                    '[' => Mode.Csi,
                    ']' => Mode.Osc,
                    _ => Mode.Text, // 2-char escapes (charset selection etc.) — swallow
                };
                if (_mode == Mode.Text && c is '(' or ')') _mode = Mode.Esc; // charset: eat one more
                return;
            case Mode.Csi:
                if (c >= '@' && c <= '~') { ApplyCsi(_seq.ToString(), c); _seq.Clear(); _mode = Mode.Text; }
                else _seq.Append(c);
                return;
            case Mode.Osc:
                // OSC ends with BEL or ST (ESC \) — titles/hyperlinks, all dropped.
                if (c == '\a') { _seq.Clear(); _mode = Mode.Text; }
                else if (c == '\\' && _seq.Length > 0 && _seq[^1] == '\x1b') { _seq.Clear(); _mode = Mode.Text; }
                else _seq.Append(c);
                return;
        }

        switch (c)
        {
            case '\x1b': _mode = Mode.Esc; return;
            case '\r': _cx = 0; return;
            case '\n': LineFeed(); return;
            case '\b': if (_cx > 0) _cx--; return;
            case '\t': _cx = Math.Min((_cx / 8 + 1) * 8, _cols - 1); return;
            case '\a': return; // bell
            default:
                if (c < ' ') return;
                if (_cx >= _cols) { _cx = 0; LineFeed(); }
                _grid[_cy][_cx++] = c;
                return;
        }
    }

    private void LineFeed()
    {
        if (_cy < _rows - 1) { _cy++; return; }
        // scroll: top line leaves the screen into scrollback
        _scrollback.Add(_grid[0]);
        if (_scrollback.Count > ScrollbackCap) _scrollback.RemoveAt(0);
        Array.Copy(_grid, 1, _grid, 0, _rows - 1);
        _grid[_rows - 1] = MakeRow(_cols);
    }

    private void ApplyCsi(string args, char cmd)
    {
        int Arg(int index, int fallback)
        {
            var parts = args.Split(';');
            return index < parts.Length && int.TryParse(parts[index], out var v) && v > 0 ? v : fallback;
        }

        switch (cmd)
        {
            case 'H' or 'f': _cy = Math.Min(Arg(0, 1) - 1, _rows - 1); _cx = Math.Min(Arg(1, 1) - 1, _cols - 1); break;
            case 'A': _cy = Math.Max(_cy - Arg(0, 1), 0); break;
            case 'B': _cy = Math.Min(_cy + Arg(0, 1), _rows - 1); break;
            case 'C': _cx = Math.Min(_cx + Arg(0, 1), _cols - 1); break;
            case 'D': _cx = Math.Max(_cx - Arg(0, 1), 0); break;
            case 'G': _cx = Math.Min(Arg(0, 1) - 1, _cols - 1); break;
            case 'J': EraseDisplay(args); break;
            case 'K': EraseLine(args); break;
            // SGR (m), mode set/reset (h/l), scroll region (r), device queries (n/c),
            // cursor save/restore (s/u): visual/negotiation noise for a text screen.
        }
    }

    private void EraseDisplay(string args)
    {
        switch (args)
        {
            case "2" or "3":
                for (var y = 0; y < _rows; y++) _grid[y] = MakeRow(_cols);
                _cx = _cy = 0;
                break;
            case "1":
                for (var y = 0; y < _cy; y++) _grid[y] = MakeRow(_cols);
                Array.Fill(_grid[_cy], ' ', 0, _cx + 1);
                break;
            default: // 0 / empty: cursor to end
                Array.Fill(_grid[_cy], ' ', _cx, _cols - _cx);
                for (var y = _cy + 1; y < _rows; y++) _grid[y] = MakeRow(_cols);
                break;
        }
    }

    private void EraseLine(string args)
    {
        switch (args)
        {
            case "2": _grid[_cy] = MakeRow(_cols); break;
            case "1": Array.Fill(_grid[_cy], ' ', 0, _cx + 1); break;
            default: Array.Fill(_grid[_cy], ' ', _cx, _cols - _cx); break;
        }
    }

    /// <summary>The visible screen as plain text, trailing blanks trimmed.</summary>
    public string Render(int? lastLines = null)
    {
        lock (_gate)
        {
            var lines = new List<string>(_rows);
            foreach (var row in _grid) lines.Add(new string(row).TrimEnd());
            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            if (lastLines is { } n && n > 0 && lines.Count > n)
                lines.RemoveRange(0, lines.Count - n);
            return string.Join('\n', lines);
        }
    }
}
