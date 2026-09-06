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

    /// <summary>Write text to the session's stdin (send "\r" for Enter, "" for Ctrl-C).</summary>
    void Write(string text);

    /// <summary>Resize the PTY — TUI apps re-layout on this.</summary>
    void Resize(int cols, int rows);
}
