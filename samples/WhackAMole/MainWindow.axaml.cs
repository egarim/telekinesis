using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace WhackAMole;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly Random _rng = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<double> _latencies = new();

    private bool _running;
    private double _spawnAt = -1;      // clock ms when the current target appeared, -1 = none
    private double _nextSpawnAt = -1;  // clock ms when the next target should appear
    private int _hits, _misses;

    public MainWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        LifetimeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty) LifetimeText.Text = $"{(int)LifetimeSlider.Value} ms";
        };
    }

    private void OnStartClick(object? sender, RoutedEventArgs e)
    {
        _running = !_running;
        StartButton.Content = _running ? "Stop round" : "Start round";
        if (_running)
        {
            _hits = 0; _misses = 0; _latencies.Clear();
            UpdateStats();
            LogText.Text = "";
            StatusText.Text = "Running";
            ScheduleNextSpawn();
        }
        else
        {
            HideTarget();
            _nextSpawnAt = -1;
            StatusText.Text = "Idle";
        }
    }

    private void OnTargetClick(object? sender, RoutedEventArgs e)
    {
        if (_spawnAt < 0) return;
        var latency = _clock.Elapsed.TotalMilliseconds - _spawnAt;
        _latencies.Add(latency);
        _hits++;
        LogText.Text = $"hit #{_hits}: {latency:F0} ms\n{LogText.Text}";
        if (LogText.Text.Length > 600) LogText.Text = LogText.Text[..600];
        HideTarget();
        UpdateStats();
        ScheduleNextSpawn();
    }

    private void Tick()
    {
        if (!_running) return;
        var now = _clock.Elapsed.TotalMilliseconds;

        if (_spawnAt >= 0 && now - _spawnAt > LifetimeSlider.Value)
        {
            _misses++;
            LogText.Text = $"miss (>{(int)LifetimeSlider.Value} ms)\n{LogText.Text}";
            HideTarget();
            UpdateStats();
            ScheduleNextSpawn();
        }
        else if (_spawnAt < 0 && _nextSpawnAt >= 0 && now >= _nextSpawnAt)
        {
            SpawnTarget();
        }
    }

    private void SpawnTarget()
    {
        var w = Math.Max(Arena.Bounds.Width - Target.Width, 1);
        var h = Math.Max(Arena.Bounds.Height - Target.Height, 1);
        Canvas.SetLeft(Target, _rng.NextDouble() * w);
        Canvas.SetTop(Target, _rng.NextDouble() * h);
        Target.IsVisible = true;
        _spawnAt = _clock.Elapsed.TotalMilliseconds;
        _nextSpawnAt = -1;
    }

    private void HideTarget()
    {
        Target.IsVisible = false;
        _spawnAt = -1;
    }

    private void ScheduleNextSpawn() =>
        _nextSpawnAt = _clock.Elapsed.TotalMilliseconds + 400 + _rng.NextDouble() * 800;

    private void UpdateStats()
    {
        LastText.Text = _latencies.Count > 0 ? $"Last: {_latencies[^1]:F0} ms" : "Last: —";
        AvgText.Text = _latencies.Count > 0 ? $"Average: {_latencies.Average():F0} ms" : "Average: —";
        BestText.Text = _latencies.Count > 0 ? $"Best: {_latencies.Min():F0} ms" : "Best: —";
        ScoreText.Text = $"Hits {_hits} · Misses {_misses}";
    }
}
