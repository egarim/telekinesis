using System;
using System.Collections.Generic;

namespace PongWars;

/// <summary>The classic pong-wars simulation: two teams of balls flip a grid of
/// day/night squares; each ball bounces off walls and off squares of its own color.</summary>
public sealed class GameModel
{
    public const int Cols = 24;
    public const int Rows = 24;

    /// <summary>true = day, false = night.</summary>
    public bool[,] Cells { get; } = new bool[Cols, Rows];
    public List<Ball> Balls { get; } = new();

    public double DaySpeedFactor = 1.0;
    public double NightSpeedFactor = 1.0;

    public int DayScore { get; private set; }
    public int NightScore { get; private set; }

    private readonly Random _rng = new();

    public GameModel() => Reset(1);

    public void Reset(int ballsPerSide)
    {
        for (var x = 0; x < Cols; x++)
            for (var y = 0; y < Rows; y++)
                Cells[x, y] = x < Cols / 2;

        Balls.Clear();
        for (var i = 0; i < ballsPerSide; i++)
        {
            Balls.Add(NewBall(isDay: true, i, ballsPerSide));
            Balls.Add(NewBall(isDay: false, i, ballsPerSide));
        }
        RecountScores();
    }

    private Ball NewBall(bool isDay, int index, int total)
    {
        var angle = (_rng.NextDouble() * 0.5 + 0.25) * Math.PI; // mostly horizontal-ish
        var dir = isDay ? 1 : -1;
        return new Ball
        {
            IsDay = isDay,
            // Day balls start in the day half and fly toward night territory, and vice versa.
            X = isDay ? Cols * 0.25 : Cols * 0.75,
            Y = Rows * (index + 1.0) / (total + 1.0),
            Vx = dir * Math.Abs(Math.Sin(angle)) * 0.35,
            Vy = Math.Cos(angle) * 0.35,
        };
    }

    /// <summary>Advance the simulation by one tick (dt in ~frames, 1.0 = one 60fps frame).</summary>
    public void Step(double dt)
    {
        foreach (var b in Balls)
        {
            var factor = b.IsDay ? DaySpeedFactor : NightSpeedFactor;
            var nx = b.X + b.Vx * dt * factor;
            var ny = b.Y + b.Vy * dt * factor;

            if (nx < 0 || nx >= Cols) { b.Vx = -b.Vx; nx = Math.Clamp(nx, 0, Cols - 0.001); }
            if (ny < 0 || ny >= Rows) { b.Vy = -b.Vy; ny = Math.Clamp(ny, 0, Rows - 0.001); }

            var cx = (int)nx;
            var cy = (int)ny;
            // A ball flips squares of the opposing color and bounces off them.
            if (Cells[cx, cy] != b.IsDay)
            {
                Cells[cx, cy] = b.IsDay;
                // Bounce along the dominant axis of travel, with a nudge of randomness
                // so the game doesn't settle into loops.
                if (Math.Abs(b.Vx) > Math.Abs(b.Vy)) b.Vx = -b.Vx; else b.Vy = -b.Vy;
                b.Vx += (_rng.NextDouble() - 0.5) * 0.02;
                b.Vy += (_rng.NextDouble() - 0.5) * 0.02;
                nx = b.X; ny = b.Y;
            }

            b.X = nx;
            b.Y = ny;
        }
        RecountScores();
    }

    private void RecountScores()
    {
        int day = 0;
        foreach (var c in Cells) if (c) day++;
        DayScore = day;
        NightScore = Cols * Rows - day;
    }
}

public sealed class Ball
{
    public bool IsDay;
    public double X, Y, Vx, Vy;
}
