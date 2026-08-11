using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Skia;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SkiaSharp;
using SvgExpressionsDemo.Generated;

namespace SvgExpressionsDemo;

public partial class MainWindow : Window
{
    // The generated Record() reports the size it recorded at; the logo is authored at 256x256.
    private const float LogoSize = 256f;

    // Seconds for one full pass of t from 0 to 1. The colours swing 70 degrees of hue over that,
    // so a short cycle reads as a strobe rather than an animation.
    private const double CycleSeconds = 8d;

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastTick;

    // Set while the timer writes the slider, so the slider's own handler can tell an animation
    // step from a real user drag and not fight it.
    private bool _syncing;

    // Snapshot of everything the drawing needs, written on the UI thread and read on the render
    // thread. ICustomDrawOperation.Render runs on the render thread, so reading Slider.Value or
    // Bounds from inside the draw callback is a cross-thread access on UI objects.
    private volatile Snapshot _snapshot = new(0f, 0.52f, 1f, false, 0f, 0f);

    private sealed record Snapshot(float T, float Hue, float Pulse, bool Bold, float Width, float Height);

    private readonly SKCanvasControl _canvas;
    private readonly Slider _timeSlider;
    private readonly Slider _hueSlider;
    private readonly Slider _pulseSlider;
    private readonly ToggleButton _animateToggle;
    private readonly CheckBox _boldCheck;
    private readonly TextBlock _timeText;
    private readonly TextBlock _hueText;
    private readonly TextBlock _pulseText;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        _canvas = this.FindControl<SKCanvasControl>("Canvas")!;
        _timeSlider = this.FindControl<Slider>("TimeSlider")!;
        _hueSlider = this.FindControl<Slider>("HueSlider")!;
        _pulseSlider = this.FindControl<Slider>("PulseSlider")!;
        _animateToggle = this.FindControl<ToggleButton>("AnimateToggle")!;
        _boldCheck = this.FindControl<CheckBox>("BoldCheck")!;
        _timeText = this.FindControl<TextBlock>("TimeText")!;
        _hueText = this.FindControl<TextBlock>("HueText")!;
        _pulseText = this.FindControl<TextBlock>("PulseText")!;

        _canvas.Draw += OnDraw;
        _canvas.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.BoundsProperty)
            {
                Publish();
            }
        };

        _timeSlider.PropertyChanged += OnSliderChanged;
        _hueSlider.PropertyChanged += OnSliderChanged;
        _pulseSlider.PropertyChanged += OnSliderChanged;
        _boldCheck.IsCheckedChanged += (_, _) => Publish();

        // Background sits below Input on the dispatcher. At Render priority a 16ms timer that
        // also invalidates every tick starves the input queue and the controls stop responding.
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _timer.Tick += OnTick;
        _timer.Start();

        UpdateReadouts();
        Publish();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        var elapsed = now - _lastTick;
        _lastTick = now;

        if (_animateToggle.IsChecked != true)
        {
            return;
        }

        // Advance by wall clock rather than per tick, so the speed does not depend on how often
        // the timer actually fires.
        var next = _timeSlider.Value + elapsed.TotalSeconds / CycleSeconds;
        while (next > 1d)
        {
            next -= 1d;
        }

        _syncing = true;
        try
        {
            _timeSlider.Value = next;
        }
        finally
        {
            _syncing = false;
        }

        UpdateReadouts();
        Publish();
    }

    private void OnSliderChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != RangeBase.ValueProperty || _syncing)
        {
            return;
        }

        UpdateReadouts();
        Publish();
    }

    private void UpdateReadouts()
    {
        _timeText.Text = _timeSlider.Value.ToString("0.000");
        _hueText.Text = _hueSlider.Value.ToString("0.000");
        _pulseText.Text = _pulseSlider.Value.ToString("0.000");
    }

    /// <summary>Takes a snapshot on the UI thread and asks for a repaint.</summary>
    private void Publish()
    {
        _snapshot = new Snapshot(
            (float)_timeSlider.Value,
            (float)_hueSlider.Value,
            (float)_pulseSlider.Value,
            _boldCheck.IsChecked == true,
            (float)_canvas.Bounds.Width,
            (float)_canvas.Bounds.Height);

        _canvas.InvalidateVisual();
    }

    private void OnDraw(object? sender, SKCanvasEventArgs e)
    {
        // Render thread: only the snapshot and the canvas may be touched here.
        var state = _snapshot;
        var canvas = e.Canvas;

        canvas.Clear(new SKColor(0x1A, 0x1A, 0x1E));

        if (state.Width <= 0f || state.Height <= 0f)
        {
            return;
        }

        // This is the whole point of the demo: the picture is rebuilt from the current arguments
        // rather than being a fixed, pre-baked drawing.
        using var picture = Logo.Record(state.T, state.Hue, state.Pulse, state.Bold);

        var scale = Math.Min(state.Width, state.Height) / LogoSize;

        canvas.Save();
        canvas.Translate((state.Width - LogoSize * scale) / 2f, (state.Height - LogoSize * scale) / 2f);
        canvas.Scale(scale);
        canvas.DrawPicture(picture);
        canvas.Restore();
    }
}
