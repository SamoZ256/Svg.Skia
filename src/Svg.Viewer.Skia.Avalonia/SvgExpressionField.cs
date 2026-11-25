// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// A box for a value that may be an expression: it knows what the file says, so Escape puts that
/// back, and Up and Down step a number without writing it.
/// </summary>
/// <remarks>
/// Themed as a <see cref="TextBox"/>; give it the <c>SvgExpressionBox</c> theme for the expression
/// colours and the <c>:driven</c> outline.
/// </remarks>
public class SvgExpressionField : TextBox
{
    private bool _driven;

    protected override Type StyleKeyOverride => typeof(TextBox);

    /// <summary>What the file says: what Escape puts back, and what the box is pending against.</summary>
    public string? Value { get; set; }

    /// <summary>Whether an expression decides the value, here or on a group around it; outlined as driven.</summary>
    public bool IsDriven
    {
        get => _driven;
        set
        {
            _driven = value;
            PseudoClasses.Set(":driven", value);
        }
    }

    /// <summary>What Up and Down add to a number, ten times that with Shift and a tenth with Alt; nothing at 0.</summary>
    public double Step { get; set; }

    public double Minimum { get; set; } = double.NegativeInfinity;

    public double Maximum { get; set; } = double.PositiveInfinity;

    /// <summary>Whether the value is a fraction, so <c>50%</c> is 0.5: its step and range are a hundred times larger in percent.</summary>
    public bool PercentIsFraction { get; set; }

    /// <summary>Whether the box says something other than <see cref="Value"/>.</summary>
    public bool IsPending => !string.Equals(Text ?? string.Empty, Value ?? string.Empty, StringComparison.Ordinal);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && IsPending && !IsReadOnly)
        {
            Text = Value;
            CaretIndex = Text?.Length ?? 0;
            e.Handled = true;

            return;
        }

        // Before the box's own handling, which takes the arrows to move the caret.
        if (e.Key is Key.Up or Key.Down && Step > 0 && !IsReadOnly && Stepped(e.Key == Key.Up ? 1 : -1, e.KeyModifiers) is { } stepped)
        {
            Text = stepped;
            CaretIndex = stepped.Length;
            e.Handled = true;

            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>The number in the box moved by one step, keeping its unit, or null where it holds no number.</summary>
    private string? Stepped(int direction, KeyModifiers modifiers)
    {
        var match = s_measure.Match(Text?.Trim() ?? string.Empty);

        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        // A percentage of a fraction is a hundred times it, and so are its step and its range.
        var scale = PercentIsFraction && match.Groups[2].Value == "%" ? 100 : 1;
        var step = Step * scale * (modifiers.HasFlag(KeyModifiers.Shift) ? 10 : modifiers.HasFlag(KeyModifiers.Alt) ? 0.1 : 1);

        number = Math.Round(Math.Clamp(number + direction * step, Minimum * scale, Maximum * scale), 6);

        return number.ToString("0.######", CultureInfo.InvariantCulture) + match.Groups[2].Value;
    }

    /// <summary>A number and the unit after it, if any: <c>12</c>, <c>-0.5em</c>, <c>50%</c>.</summary>
    private static readonly Regex s_measure = new(@"^([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)([a-zA-Z%]*)$", RegexOptions.CultureInvariant);
}
