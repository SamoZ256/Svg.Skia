using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Skia;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SkiaSharp;
using Svg.CodeGen.Skia;
using Svg.CodeGen.Skia.Expressions;
using Svg.Expressions;

namespace SvgExpressionsDemo;

public partial class MainWindow : Window
{
    private const float LogoSize = 256f;

    // Long enough that typing does not trigger a compile per keystroke.
    private static readonly TimeSpan RecompileDelay = TimeSpan.FromMilliseconds(400);

    private readonly LiveCompiler _compiler = new();

    private readonly SKCanvasControl _canvas;
    private readonly TextBox _svgEditor;
    private readonly TextBox _generatedView;
    private readonly StackPanel _parameterPanel;
    private readonly TextBlock _statusText;
    private readonly Border _errorPanel;
    private readonly SelectableTextBlock _errorText;

    private readonly List<ParameterBinding> _bindings = new();
    private CancellationTokenSource? _pending;

    // Written on the UI thread, read on the render thread.
    private volatile Snapshot _snapshot = new(null, Array.Empty<object?>(), 0f, 0f);

    private sealed record Snapshot(LiveCompileResult? Result, object?[] Arguments, float Width, float Height);

    private sealed record ParameterBinding(SvgExpressionParameter Parameter, Func<object?> Read);

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        _canvas = this.FindControl<SKCanvasControl>("Canvas")!;
        _svgEditor = this.FindControl<TextBox>("SvgEditor")!;
        _generatedView = this.FindControl<TextBox>("GeneratedView")!;
        _parameterPanel = this.FindControl<StackPanel>("ParameterPanel")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _errorPanel = this.FindControl<Border>("ErrorPanel")!;
        _errorText = this.FindControl<SelectableTextBlock>("ErrorText")!;

        _canvas.Draw += OnDraw;
        _canvas.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.BoundsProperty)
            {
                Publish();
            }
        };

        _svgEditor.TextChanged += (_, _) => ScheduleCompile();

        _svgEditor.Text = LoadStartingDocument();
        CompileNow();
    }

    private static string LoadStartingDocument()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Svg", "logo.svg");

        return File.Exists(path)
            ? File.ReadAllText(path)
            : """
              <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0"
                   width="256" height="256">
                <defs>
                  <e:code>
                    <e:param name="t" type="number" default="0" />
                  </e:code>
                </defs>
                <circle cx="128" cy="128" r="96" fill="{{ hsl(t * 360, 74%, 55%) }}" />
              </svg>
              """;
    }

    // ---- compiling ---------------------------------------------------------------------------

    private void ScheduleCompile()
    {
        _pending?.Cancel();
        _pending = new CancellationTokenSource();
        var token = _pending.Token;

        _statusText.Text = "editing…";

        DispatcherTimer.RunOnce(
            () =>
            {
                if (!token.IsCancellationRequested)
                {
                    CompileNow();
                }
            },
            RecompileDelay,
            DispatcherPriority.Background);
    }

    private void CompileNow()
    {
        var source = _svgEditor.Text ?? string.Empty;
        _statusText.Text = "compiling…";

        // Roslyn is slow enough to be felt on the UI thread.
        Task.Run(() => _compiler.Compile(source))
            .ContinueWith(
                task => Dispatcher.UIThread.Post(() => Apply(task.IsFaulted
                    ? LiveCompileResult.Failed(new[] { task.Exception?.GetBaseException().Message ?? "Unknown failure." })
                    : task.Result)),
                TaskScheduler.Default);
    }

    private void Apply(LiveCompileResult result)
    {
        if (result.GeneratedCode is { } code)
        {
            _generatedView.Text = code;
        }

        _errorPanel.IsVisible = result.Errors.Count > 0;
        _errorText.Text = string.Join("\n\n", result.Errors);

        if (!result.Success)
        {
            // Keep drawing whatever last compiled, so a half-typed edit does not blank the view.
            _statusText.Text = "error — showing last good version";
            return;
        }

        RebuildParameterControls(result.Parameters);

        _statusText.Text = result.Parameters.Count == 0
            ? "compiled — no parameters"
            : $"compiled — {result.Parameters.Count} parameter(s)";

        _snapshot = _snapshot with { Result = result };
        Publish();
    }

    // ---- parameter controls ------------------------------------------------------------------

    private void RebuildParameterControls(IReadOnlyList<SvgExpressionParameter> parameters)
    {
        // Rebuilt wholesale: the document decides which parameters exist, and it can change with
        // any keystroke. Values are re-read from the new controls, so they reset on a change.
        if (_bindings.Count == parameters.Count &&
            _bindings.Select(b => b.Parameter.Name).SequenceEqual(parameters.Select(p => p.Name)) &&
            _bindings.Select(b => b.Parameter.Type).SequenceEqual(parameters.Select(p => p.Type)))
        {
            return;
        }

        _bindings.Clear();
        _parameterPanel.Children.Clear();

        foreach (var parameter in parameters)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("110,*,70")
            };

            row.Children.Add(new TextBlock
            {
                Text = parameter.Name,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = "monospace"
            });

            switch (parameter.Type)
            {
                case ExprType.Number:
                    {
                        // Numbers are exposed as 0..1; scale inside the expression when a wider
                        // range is wanted, as the sample does with hsl(hue * 360, ...).
                        var slider = new Slider { Minimum = 0, Maximum = 1, Value = 0 };
                        var readout = new TextBlock
                        {
                            VerticalAlignment = VerticalAlignment.Center,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            FontFamily = "monospace",
                            Text = "0.000"
                        };

                        slider.PropertyChanged += (_, e) =>
                        {
                            if (e.Property == RangeBase.ValueProperty)
                            {
                                readout.Text = slider.Value.ToString("0.000");
                                Publish();
                            }
                        };

                        Grid.SetColumn(slider, 1);
                        Grid.SetColumn(readout, 2);
                        row.Children.Add(slider);
                        row.Children.Add(readout);

                        _bindings.Add(new ParameterBinding(parameter, () => (float)slider.Value));
                        break;
                    }

                case ExprType.Boolean:
                    {
                        var check = new CheckBox { IsChecked = false };
                        check.IsCheckedChanged += (_, _) => Publish();

                        Grid.SetColumn(check, 1);
                        row.Children.Add(check);

                        _bindings.Add(new ParameterBinding(parameter, () => check.IsChecked == true));
                        break;
                    }

                default:
                    {
                        var box = new TextBox { Text = "#3fb5b5", FontFamily = "monospace" };
                        box.TextChanged += (_, _) => Publish();

                        Grid.SetColumn(box, 1);
                        row.Children.Add(box);

                        _bindings.Add(new ParameterBinding(parameter, () => ParseColor(box.Text)));
                        break;
                    }
            }

            _parameterPanel.Children.Add(row);
        }
    }

    private static SKColor ParseColor(string? text)
        => SKColor.TryParse(text, out var color) ? color : SKColors.Magenta;

    // ---- rendering ----------------------------------------------------------------------------

    private void Publish()
    {
        _snapshot = _snapshot with
        {
            Arguments = _bindings.Select(b => b.Read()).ToArray(),
            Width = (float)_canvas.Bounds.Width,
            Height = (float)_canvas.Bounds.Height
        };

        _canvas.InvalidateVisual();
    }

    private void OnDraw(object? sender, SKCanvasEventArgs e)
    {
        // Render thread: only the snapshot and the canvas may be touched here.
        var state = _snapshot;
        var canvas = e.Canvas;

        canvas.Clear(new SKColor(0x1A, 0x1A, 0x1E));

        if (state.Result is not { Success: true } result || state.Width <= 0f || state.Height <= 0f)
        {
            return;
        }

        SKPicture? picture;
        try
        {
            picture = result.Invoke(state.Arguments);
        }
        catch
        {
            // Generated code is compiled from arbitrary input; a bad edit must not kill rendering.
            return;
        }

        if (picture is null)
        {
            return;
        }

        using (picture)
        {
            var bounds = picture.CullRect;
            var width = bounds.Width > 0 ? bounds.Width : LogoSize;
            var height = bounds.Height > 0 ? bounds.Height : LogoSize;
            var scale = Math.Min(state.Width / width, state.Height / height);

            canvas.Save();
            canvas.Translate((state.Width - width * scale) / 2f, (state.Height - height * scale) / 2f);
            canvas.Scale(scale);
            canvas.DrawPicture(picture);
            canvas.Restore();
        }
    }
}
