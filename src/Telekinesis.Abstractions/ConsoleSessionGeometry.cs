namespace Telekinesis.Abstractions;

/// <summary>
/// Optional companion to <see cref="IConsoleSession"/> for a backend whose runtime
/// applies different dimensions than the caller asked for (issue #67). The Sentiora
/// backend clamps to cols 2-300 / rows 2-150, while Telekinesis clamps to 2-1000 —
/// so a 500-column console_resize is silently unachievable there unless the caller
/// is told. A session that implements this lets console_open/console_resize report
/// the size ACTUALLY applied and keep the <see cref="TerminalScreen"/> grid in step
/// with the PTY. Backends that honour the request exactly (ConPTY, openpty) do not
/// implement it and are unaffected.
/// </summary>
public interface IConsoleSessionGeometry
{
    /// <summary>Columns the underlying PTY actually has.</summary>
    int AppliedCols { get; }

    /// <summary>Rows the underlying PTY actually has.</summary>
    int AppliedRows { get; }
}
