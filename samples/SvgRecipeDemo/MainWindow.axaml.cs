using System;
using System.Collections.Generic;
using System.Globalization;
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
using Svg.Expressions;

namespace SvgRecipeDemo;

public partial class MainWindow : Window
{
    private const float FallbackSize = 256f;

    // Long enough that typing does not trigger a run per keystroke.
    private static readonly TimeSpan RerunDelay = TimeSpan.FromMilliseconds(400);

    private readonly RecipePipeline _pipeline = new();

    private readonly SKCanvasControl _canvas;
    private readonly TextBox _recipeEditor;
    private readonly TextBox _svgEditor;
    private readonly TextBox _convertedView;
    private readonly StackPanel _parameterPanel;
    private readonly TextBlock _statusText;
    private readonly SelectableTextBlock _matchText;
    private readonly Border _errorPanel;
    private readonly SelectableTextBlock _errorText;

    private readonly List<ParameterBinding> _bindings = new();
    private CancellationTokenSource? _pending;

    // Written on the UI thread, read on the render thread.
    private volatile Snapshot _snapshot = new(null, EmptyValues, 0f, 0f);

    private static readonly IReadOnlyDictionary<string, ExprValue> EmptyValues =
        new Dictionary<string, ExprValue>();

    private sealed record Snapshot(
        RecipeRunResult? Result,
        IReadOnlyDictionary<string, ExprValue> Values,
        float Width,
        float Height);

    private sealed record ParameterBinding(SvgExpressionParameter Parameter, Func<ExprValue> Read);

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        _canvas = this.FindControl<SKCanvasControl>("Canvas")!;
        _recipeEditor = this.FindControl<TextBox>("RecipeEditor")!;
        _svgEditor = this.FindControl<TextBox>("SvgEditor")!;
        _convertedView = this.FindControl<TextBox>("ConvertedView")!;
        _parameterPanel = this.FindControl<StackPanel>("ParameterPanel")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _matchText = this.FindControl<SelectableTextBlock>("MatchText")!;
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

        // The recipe is the point of the demo, but the drawing is editable too, so another icon
        // can be pasted in without rebuilding.
        _recipeEditor.TextChanged += (_, _) => ScheduleRun();
        _svgEditor.TextChanged += (_, _) => ScheduleRun();

        _recipeEditor.Text = DemoFiles.Recipe;
        _svgEditor.Text = DemoFiles.Svg;

        RunNow();
    }

    // ---- running the chain ---------------------------------------------------------------------

    private void ScheduleRun()
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
                    RunNow();
                }
            },
            RerunDelay,
            DispatcherPriority.Background);
    }

    private void RunNow()
    {
        var svg = _svgEditor.Text ?? string.Empty;
        var recipe = _recipeEditor.Text ?? string.Empty;

        _statusText.Text = "converting…";

        // Roslyn is slow enough to be felt on the UI thread.
        Task.Run(() => _pipeline.Run(svg, recipe))
            .ContinueWith(
                task => Dispatcher.UIThread.Post(() => Apply(task.IsFaulted
                    ? RecipeRunResult.RecipeFailed(task.Exception?.GetBaseException().Message ?? "Unknown failure.")
                    : task.Result)),
                TaskScheduler.Default);
    }

    private void Apply(RecipeRunResult result)
    {
        if (result.ConvertedSvg is { } converted)
        {
            _convertedView.Text = converted;
        }

        _matchText.Text = DescribeMatches(result);

        var errors = result.AllErrors.ToList();
        _errorPanel.IsVisible = errors.Count > 0;
        _errorText.Text = string.Join("\n\n", errors);

        if (!result.Success)
        {
            // Keep drawing whatever last succeeded, so a half-typed edit does not blank the view.
            _statusText.Text = result.ConvertedSvg is null
                ? "recipe error — showing last good version"
                : "compile error — showing last good version";
            return;
        }

        RebuildParameterControls(result.Preview!.Parameters);

        _statusText.Text = result.Preview.Parameters.Count == 0
            ? "loaded — no parameters"
            : $"loaded — {result.Preview.Parameters.Count} parameter(s)";

        _snapshot = _snapshot with { Result = result };
        Publish();
    }

    private static string DescribeMatches(RecipeRunResult result)
    {
        if (result.Matches.Count == 0)
        {
            return result.ConvertedSvg is null ? string.Empty : "no replace rules";
        }

        return string.Join(
            "\n",
            result.Matches.Select(match => match.Count == 0
                ? $"{match.Rule.ColorText} → nothing in the drawing uses it"
                : $"{match.Rule.ColorText} → {{{{ {match.Rule.Expression} }}}}  ×{match.Count}"));
    }

    // ---- parameter controls --------------------------------------------------------------------

    private void RebuildParameterControls(IReadOnlyList<SvgExpressionParameter> parameters)
    {
        // Rebuilt wholesale: the recipe decides which parameters exist, and it can change with any
        // keystroke. Values are re-read from the new controls, so they reset on a change.
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
                        // range is wanted, as the recipe does with hue * 360.
                        var start = LiteralDefault(parameter, 0d);
                        var slider = new Slider { Minimum = 0, Maximum = 1, Value = Math.Clamp(start, 0d, 1d) };
                        var readout = new TextBlock
                        {
                            VerticalAlignment = VerticalAlignment.Center,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            FontFamily = "monospace",
                            Text = slider.Value.ToString("0.000", CultureInfo.InvariantCulture)
                        };

                        slider.PropertyChanged += (_, e) =>
                        {
                            if (e.Property == RangeBase.ValueProperty)
                            {
                                readout.Text = slider.Value.ToString("0.000", CultureInfo.InvariantCulture);
                                Publish();
                            }
                        };

                        Grid.SetColumn(slider, 1);
                        Grid.SetColumn(readout, 2);
                        row.Children.Add(slider);
                        row.Children.Add(readout);

                        _bindings.Add(new ParameterBinding(parameter, () => ExprValue.Number((float)slider.Value)));
                        break;
                    }

                case ExprType.Boolean:
                    {
                        var check = new CheckBox
                        {
                            IsChecked = string.Equals(parameter.DefaultExpression, "true", StringComparison.Ordinal)
                        };
                        check.IsCheckedChanged += (_, _) => Publish();

                        Grid.SetColumn(check, 1);
                        row.Children.Add(check);

                        _bindings.Add(new ParameterBinding(parameter, () => ExprValue.Boolean(check.IsChecked == true)));
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

    // A default is an expression, not a value, and only the code generator can evaluate one. A
    // plain literal is worth reading anyway: it is what recipes normally write, and starting the
    // slider there shows the drawing as the recipe intended rather than at zero.
    private static double LiteralDefault(SvgExpressionParameter parameter, double fallback)
        => double.TryParse(parameter.DefaultExpression, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static ExprValue ParseColor(string? text)
    {
        var color = SKColor.TryParse(text, out var parsed) ? parsed : SKColors.Magenta;

        return ExprValue.Color(color.Red, color.Green, color.Blue, color.Alpha);
    }

    // ---- rendering ------------------------------------------------------------------------------

    private void Publish()
    {
        _snapshot = _snapshot with
        {
            Values = _bindings.ToDictionary(b => b.Parameter.Name, b => b.Read(), StringComparer.Ordinal),
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
            picture = result.Preview!.Render(state.Values);
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
            var width = bounds.Width > 0 ? bounds.Width : FallbackSize;
            var height = bounds.Height > 0 ? bounds.Height : FallbackSize;

            // The icon is 24x24, so it is scaled to the pane with a margin rather than drawn at
            // its natural size.
            var scale = Math.Min(state.Width / width, state.Height / height) * 0.82f;

            canvas.Save();
            canvas.Translate((state.Width - width * scale) / 2f, (state.Height - height * scale) / 2f);
            canvas.Scale(scale);
            canvas.DrawPicture(picture);
            canvas.Restore();
        }
    }
}
