using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PongWars;

/// <summary>Renders the pong-wars board. The board itself is a canvas (deliberately
/// opaque to accessibility — the point of the demo is that the *controls* are not).</summary>
public sealed class BoardControl : Control
{
    public GameModel? Model { get; set; }
    public bool ShowGridLines { get; set; }

    private static readonly IBrush DayBrush = new SolidColorBrush(Color.Parse("#D9E8E3"));
    private static readonly IBrush NightBrush = new SolidColorBrush(Color.Parse("#114C5A"));
    private static readonly IBrush DayBallBrush = new SolidColorBrush(Color.Parse("#114C5A"));
    private static readonly IBrush NightBallBrush = new SolidColorBrush(Color.Parse("#D9E8E3"));
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.Parse("#33000000")), 1);

    public override void Render(DrawingContext ctx)
    {
        if (Model is null) return;
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var cw = w / GameModel.Cols;
        var ch = h / GameModel.Rows;

        for (var x = 0; x < GameModel.Cols; x++)
            for (var y = 0; y < GameModel.Rows; y++)
            {
                var rect = new Rect(x * cw, y * ch, cw + 0.5, ch + 0.5);
                ctx.FillRectangle(Model.Cells[x, y] ? DayBrush : NightBrush, rect);
                if (ShowGridLines) ctx.DrawRectangle(GridPen, rect);
            }

        var r = System.Math.Min(cw, ch) * 0.4;
        foreach (var b in Model.Balls)
        {
            var center = new Point(b.X * cw, b.Y * ch);
            ctx.DrawEllipse(b.IsDay ? DayBallBrush : NightBallBrush, null, center, r, r);
        }
    }
}
