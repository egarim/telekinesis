using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace PongWars;

public partial class MainWindow : Window
{
    private readonly GameModel _model = new();
    private readonly DispatcherTimer _timer;
    private bool _paused;

    public MainWindow()
    {
        InitializeComponent();
        Board.Model = _model;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        DaySpeedSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty) _model.DaySpeedFactor = DaySpeedSlider.Value / 100.0;
        };
        NightSpeedSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty) _model.NightSpeedFactor = NightSpeedSlider.Value / 100.0;
        };
        GridLinesCheck.IsCheckedChanged += (_, _) =>
        {
            Board.ShowGridLines = GridLinesCheck.IsChecked == true;
            Board.InvalidateVisual();
        };
        BallCountCombo.SelectionChanged += (_, _) => ResetBoard();
    }

    private void Tick()
    {
        if (_paused) return;
        _model.Step(1.0);
        Board.InvalidateVisual();
        DayScoreText.Text = $"Day {_model.DayScore}";
        NightScoreText.Text = $"Night {_model.NightScore}";
    }

    private void OnPauseClick(object? sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        PauseButton.Content = _paused ? "Resume" : "Pause";
        StatusText.Text = _paused ? "Paused" : "Running";
    }

    private void OnResetClick(object? sender, RoutedEventArgs e) => ResetBoard();

    private void ResetBoard()
    {
        var balls = BallCountCombo.SelectedIndex + 1;
        _model.Reset(balls <= 0 ? 1 : balls);
        Board.InvalidateVisual();
        StatusText.Text = _paused ? "Paused" : "Running";
    }
}
