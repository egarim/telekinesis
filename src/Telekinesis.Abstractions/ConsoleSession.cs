namespace Telekinesis.Abstractions;

/// <summary>
/// A live pseudo-terminal session (issue #27): a real PTY (ConPTY on Windows,
/// openpty/forkpty elsewhere) running an interactive program the agent can write
/// to and read the rendered screen back from. Implementations push raw output
/// bytes to the callback supplied at construction from a background reader.
/// </summary>
public interface IConsoleSession : IDisposable
{
    /// <summary>The shell/program command line this session runs.</summary>
    string Shell { get; }

    /// <summary>False once the child process has exited.</summary>
    bool IsAlive { get; }

    /// <summary>
    /// Write text to the session's stdin (send a carriage return for Enter, U+0003
    /// for Ctrl-C), bounded by <paramref name="timeout"/>.
    ///
    /// Returns <c>false</c> when the write could not complete in time. A PTY master
    /// BLOCKS once the child's input queue is full, so a child that has stopped
    /// reading stdin - a hung program, a TUI waiting on something else - would
    /// otherwise hang the caller indefinitely (issue #61). A human never fills the
    /// kernel buffer by typing; a model can paste megabytes in one call.
    /// </summary>
    bool Write(string text, TimeSpan timeout);

    /// <summary>Resize the PTY — TUI apps re-layout on this.</summary>
    void Resize(int cols, int rows);
}
