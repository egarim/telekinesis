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
        entry.Session.Write("echo pty-roundtrip-$((40+2))\r");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !entry.Screen.Render().Contains("pty-roundtrip-42"))
            await Task.Delay(100);

        Assert.Contains("pty-roundtrip-42", entry.Screen.Render());
        Assert.True(entry.Session.IsAlive);
        consoles.Close(entry.Id);
    }
}
