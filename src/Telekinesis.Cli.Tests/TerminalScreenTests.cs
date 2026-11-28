using System.Text;
using Telekinesis.Abstractions;
using Xunit;

namespace Telekinesis.Cli.Tests;

public class TerminalScreenTests
{
    private static TerminalScreen Fed(string input, int cols = 20, int rows = 5)
    {
        var screen = new TerminalScreen(cols, rows);
        var bytes = Encoding.UTF8.GetBytes(input);
        screen.Feed(bytes, bytes.Length);
        return screen;
    }

    [Fact]
    public void Plain_text_and_newlines_render()
        => Assert.Equal("hello\nworld", Fed("hello\r\nworld").Render());

    [Fact]
    public void Sgr_colors_are_stripped()
        => Assert.Equal("red ok", Fed("\x1b[31mred\x1b[0m ok").Render());

    [Fact]
    public void Carriage_return_overwrites_the_line()
        => Assert.Equal("done.", Fed("99%..\rdone").Render());

    [Fact]
    public void Backspace_moves_left()
        => Assert.Equal("ac", Fed("ab\bc").Render());

    [Fact]
    public void Cursor_home_and_erase_display_clear_screen()
        => Assert.Equal("fresh", Fed("old stuff\r\nmore\x1b[H\x1b[2Jfresh").Render());

    [Fact]
    public void Erase_to_end_of_line_truncates()
        => Assert.Equal("keep", Fed("keepDROP\x1b[5G\x1b[K").Render());

    [Fact]
    public void Cursor_positioning_writes_at_the_target_cell()
        => Assert.Equal("a\n\n  b", Fed("a\x1b[3;3Hb").Render());

    [Fact]
    public void Scrolling_keeps_the_last_rows()
    {
        var screen = Fed("1\r\n2\r\n3\r\n4\r\n5\r\n6\r\n7", rows: 3);
        Assert.Equal("5\n6\n7", screen.Render());
    }

    [Fact]
    public void Osc_title_sequences_are_dropped()
        => Assert.Equal("after", Fed("\x1b]0;window title\aafter").Render());

    [Fact]
    public void Long_lines_wrap()
        => Assert.Equal("0123456789\nab", Fed("0123456789ab", cols: 10).Render());

    [Fact]
    public void LastLines_limits_the_render()
        => Assert.Equal("c", Fed("a\r\nb\r\nc").Render(lastLines: 1));

    [Fact]
    public void Resize_to_tiny_or_zero_does_not_crash_subsequent_feed()
    {
        var screen = new TerminalScreen(20, 5);
        screen.Resize(0, 0);   // clamped to 2x2, grid must match dims
        screen.Resize(-4, -4); // negatives clamped too
        var bytes = Encoding.UTF8.GetBytes("abcdefghij\r\nklmno"); // overflows a 2-wide row
        screen.Feed(bytes, bytes.Length);   // must not IndexOutOfRange
        Assert.NotNull(screen.Render());
    }

    [Fact]
    public void Split_escape_sequences_across_chunks_still_parse()
    {
        var screen = new TerminalScreen(20, 5);
        var part1 = Encoding.UTF8.GetBytes("ok\x1b[3");
        var part2 = Encoding.UTF8.GetBytes("1mred\x1b[0m");
        screen.Feed(part1, part1.Length);
        screen.Feed(part2, part2.Length);
        Assert.Equal("okred", screen.Render());
    }
}

public class UnixPtySessionTests
{
    [Fact]
    public async Task Echo_round_trips_through_a_real_pty()
    {
        if (OperatingSystem.IsWindows()) return; // ConPTY path is validated live on Windows

        using var consoles = new ConsoleSessionService();
        var entry = consoles.Open("/bin/sh", 80, 24);
        // Exercises the poll(POLLOUT)-bounded write against a REAL pty (issue #61).
        Assert.True(entry.Session.Write("echo pty-roundtrip-$((40+2))\r", TimeSpan.FromSeconds(5)));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !entry.Screen.Render().Contains("pty-roundtrip-42"))
            await Task.Delay(100);

        Assert.Contains("pty-roundtrip-42", entry.Screen.Render());
        Assert.True(entry.Session.IsAlive);
        consoles.Close(entry.Id);
    }

    // ---- dimension clamping (issue #58) ----
    // The grid is allocated up front, so an unbounded size is an out-of-memory
    // switch. These pin BOTH ends of the range and, critically, that the reported
    // dimensions match the grid actually allocated.

    [Fact]
    public void Absurd_size_is_clamped_instead_of_allocating()
    {
        var screen = new TerminalScreen(100_000, 100_000);
        Assert.Equal(TerminalScreen.MaxDimension, screen.Cols);
        Assert.Equal(TerminalScreen.MaxDimension, screen.Rows);
    }

    [Fact]
    public void Absurd_resize_is_clamped()
    {
        var screen = new TerminalScreen(80, 24);
        screen.Resize(100_000, 100_000);
        Assert.Equal(TerminalScreen.MaxDimension, screen.Cols);
        Assert.Equal(TerminalScreen.MaxDimension, screen.Rows);
    }

    [Fact]
    public void Tiny_and_negative_sizes_are_clamped_up()
    {
        var screen = new TerminalScreen(-5, 0);
        Assert.Equal(TerminalScreen.MinDimension, screen.Cols);
        Assert.Equal(TerminalScreen.MinDimension, screen.Rows);
        screen.Resize(1, -1);
        Assert.Equal(TerminalScreen.MinDimension, screen.Cols);
        Assert.Equal(TerminalScreen.MinDimension, screen.Rows);
    }

    [Fact]
    public void Ordinary_sizes_pass_through_untouched()
    {
        var screen = new TerminalScreen(120, 30);
        Assert.Equal(120, screen.Cols);
        Assert.Equal(30, screen.Rows);
        screen.Resize(200, 50);
        Assert.Equal(200, screen.Cols);
        Assert.Equal(50, screen.Rows);
    }

    [Fact]
    public void Clamped_grid_still_renders_and_wraps_at_the_clamped_width()
    {
        // The reported width must be the width the grid really has: if Cols said
        // 100000 while the grid held 1000, writing would run off the end.
        var screen = new TerminalScreen(100_000, 4);
        var line = new string('x', TerminalScreen.MaxDimension + 50);
        var bytes = Encoding.UTF8.GetBytes(line);
        screen.Feed(bytes, bytes.Length);
        var rendered = screen.Render().Split('\n');
        Assert.Equal(TerminalScreen.MaxDimension, rendered[0].Length);
        Assert.Equal(50, rendered[1].Length);
    }
}
