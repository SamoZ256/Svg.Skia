// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Svg.Expressions;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>What a <see cref="SvgPaintChip"/> shows a paint as.</summary>
public enum SvgPaintChipState
{
    Unset,
    Solid,
    None,
    Reference,
    Mixed,
    Driven
}

/// <summary>
/// A paint, shown as a swatch that opens a colour picker: solid, none, a reference, mixed across
/// several elements, or driven by an expression.
/// </summary>
/// <remarks>
/// It writes nothing itself. <see cref="Previewed"/> follows the picker as it moves, and
/// <see cref="Picked"/> says once, as it closes, what was picked, so a host writes one step and not
/// one per tick.
/// </remarks>
public class SvgPaintChip : Button
{
    private readonly Border _shown = new()
    {
        Width = 16,
        Height = 16,
        CornerRadius = new CornerRadius(3)
    };

    private readonly TextBlock _glyph = new()
    {
        FontSize = 10,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    private string? _paint;

    private Color? _drivenColor;

    private bool _driven;

    private bool _mixed;

    public SvgPaintChip()
    {
        _shown.Child = _glyph;
        Content = _shown;
        Classes.Add("swatch");
        Padding = new Thickness(4);
        MinHeight = 0;
        VerticalAlignment = VerticalAlignment.Center;

        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };

        Flyout = flyout;
        ToolTip.SetShowOnDisabled(this, true);

        string? opened = null;
        string? current = null;

        // Made as it opens rather than with the chip, which a panel makes for every pick; and before
        // it is shown, since showing it hands the keyboard to what it holds.
        flyout.Opening += (_, _) =>
        {
            var had = _shown.Background is ISolidColorBrush { Color: var known } ? known : (Color?)null;

            opened = current = _paint;

            // Grey where there is no colour. Opened on black, the spectrum stays black wherever it is
            // clicked, and black itself could never be picked.
            var picker = new ColorView
            {
                IsAlphaEnabled = false,
                IsAlphaVisible = false,
                Color = had is { } colour ? Color.FromRgb(colour.R, colour.G, colour.B) : Colors.Gray
            };

            picker.Styles.Add(new StyleInclude(new Uri("avares://Svg.Viewer.Skia.Avalonia/"))
            {
                Source = new Uri("avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml")
            });

            // The colour it opened on puts back what was written, so opening and closing it cannot
            // turn red into #ff0000.
            picker.ColorChanged += (_, e) =>
            {
                current = had is { } same && e.NewColor.R == same.R && e.NewColor.G == same.G && e.NewColor.B == same.B
                    ? opened
                    : Spelt(e.NewColor, had?.A ?? byte.MaxValue);

                Previewed?.Invoke(this, current ?? string.Empty);
            };

            flyout.Content = picker;
        };

        flyout.Closed += (_, _) =>
        {
            if (!string.Equals(current, opened, StringComparison.Ordinal))
            {
                Picked?.Invoke(this, current ?? string.Empty);
            }
        };

        Update();
    }

    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>Raised as the picker moves, with the paint as it would be written.</summary>
    public event EventHandler<string>? Previewed;

    /// <summary>Raised once as the picker closes, where something else than what it opened on was picked.</summary>
    public event EventHandler<string>? Picked;

    /// <summary>The paint as written, or null where none is.</summary>
    public string? Paint
    {
        get => _paint;
        set
        {
            _paint = value;
            Update();
        }
    }

    /// <summary>What a driven paint comes to now.</summary>
    public Color? DrivenColor
    {
        get => _drivenColor;
        set
        {
            _drivenColor = value;
            Update();
        }
    }

    /// <summary>Whether an expression on a group around the element decides the paint, where the element writes none.</summary>
    public bool IsDriven
    {
        get => _driven;
        set
        {
            _driven = value;
            Update();
        }
    }

    /// <summary>Whether the elements it stands for have different paints.</summary>
    public bool IsMixed
    {
        get => _mixed;
        set
        {
            _mixed = value;
            Update();
        }
    }

    public SvgPaintChipState State
        => _mixed ? SvgPaintChipState.Mixed
            : Bound || _driven && string.IsNullOrEmpty(_paint) ? SvgPaintChipState.Driven
            : string.IsNullOrEmpty(_paint) ? SvgPaintChipState.Unset
            : _paint == "none" ? SvgPaintChipState.None
            : _paint!.StartsWith("url(", StringComparison.Ordinal) ? SvgPaintChipState.Reference
            : Parsed(_paint) is { } ? SvgPaintChipState.Solid
            : SvgPaintChipState.Unset;

    /// <summary>Whether the paint itself is an expression, which only typing over can change.</summary>
    private bool Bound => _paint is { } paint && SvgExpressionAttributes.TryUnwrap(paint, out _);

    private void Update()
    {
        var state = State;

        foreach (var each in (SvgPaintChipState[])Enum.GetValues(typeof(SvgPaintChipState)))
        {
            PseudoClasses.Set(":" + each.ToString().ToLowerInvariant(), each == state);
        }

        var colour = state switch
        {
            SvgPaintChipState.Solid => Parsed(_paint!),
            SvgPaintChipState.Driven => _drivenColor,
            _ => null
        };

        _shown.Background = colour is { } known ? new SolidColorBrush(known) : null;
        _glyph.Text = state switch
        {
            SvgPaintChipState.Mixed => "–",
            SvgPaintChipState.None => "/",
            SvgPaintChipState.Reference => "#",
            _ => string.Empty
        };

        var driven = state == SvgPaintChipState.Driven;

        _shown.BorderThickness = new Thickness(driven ? 2 : 1);
        _shown[!Border.BorderBrushProperty] = new DynamicResourceExtension(driven ? "SvgViewerDrivenBrush" : "TextControlBorderBrush");

        IsEnabled = !Bound;

        ToolTip.SetTip(this, Bound
            ? "Follows the expression. Type a colour into the box to set one instead."
            : driven
                ? "Follows an expression on a group around it. Pick a colour to set one here instead."
                : state == SvgPaintChipState.Unset && _paint is { Length: > 0 } other
                    ? $"{other}. Pick a colour"
                    : "Pick a colour");
    }

    /// <summary>A picked colour as it is written, at the opacity the paint already had.</summary>
    /// <remarks>
    /// The picker has no alpha of its own, so a translucent fill keeps its own rather than being made
    /// opaque by a change of hue. Spelt rgba() rather than #rrggbbaa, which a paint reads and a
    /// stop-color does not.
    /// </remarks>
    private static string Spelt(Color colour, byte alpha)
        => alpha == byte.MaxValue
            ? SvgViewerParameterFactory.Describe(ExprValue.Color(colour.R, colour.G, colour.B, alpha))
            : string.Format(CultureInfo.InvariantCulture, "rgba({0}, {1}, {2}, {3:0.###})", colour.R, colour.G, colour.B, alpha / 255d);

    /// <summary>A written colour, or null where it is none, a reference, or not a colour at all.</summary>
    private static Color? Parsed(string written)
    {
        // Not through the converter, which answers these with an exception on every keystroke.
        if (written.Length == 0
            || written is "none" or "currentColor" or "inherit"
            || written.StartsWith("url(", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return new SvgColourConverter().ConvertFrom(null, CultureInfo.InvariantCulture, written)
                is System.Drawing.Color { IsEmpty: false } colour
                ? Color.FromArgb(colour.A, colour.R, colour.G, colour.B)
                : null;
        }
        catch (Exception failure) when (failure is SvgException or FormatException or ArgumentException)
        {
            return null;
        }
    }
}
