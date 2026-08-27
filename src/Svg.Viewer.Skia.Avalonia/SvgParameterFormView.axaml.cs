// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Svg.Expressions;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>The fields of a parameter, and whether the language will have them.</summary>
/// <remarks>
/// <para>
/// A control rather than a window, so that what is worth testing here — that a reserved name is
/// refused, that a range on a colour is, that the boxes for one appear only for a number — can be
/// tested without a modal, which is the one thing a headless test cannot drive.
/// </para>
/// <para>
/// It decides nothing about what is legal. The proposed declaration goes through
/// <see cref="SvgExpressionDeclarations.Builder"/>, the rules both readers of a document enforce,
/// and what comes back is shown as it was written. A second opinion here would be a second set of
/// answers about the same names.
/// </para>
/// </remarks>
public partial class SvgParameterFormView : UserControl
{
    private readonly TextBox _name;
    private readonly ComboBox _type;
    private readonly TextBox _default;
    private readonly TextBox _minimum;
    private readonly TextBox _maximum;
    private readonly TextBox _step;
    private readonly StackPanel _range;
    private readonly TextBlock _trouble;
    private readonly Button _add;
    private readonly Button _cancel;

    private IReadOnlyCollection<string> _taken = Array.Empty<string>();

    public SvgParameterFormView()
    {
        InitializeComponent();

        _name = this.FindControl<TextBox>("NameBox")!;
        _type = this.FindControl<ComboBox>("TypeBox")!;
        _default = this.FindControl<TextBox>("DefaultBox")!;
        _minimum = this.FindControl<TextBox>("MinBox")!;
        _maximum = this.FindControl<TextBox>("MaxBox")!;
        _step = this.FindControl<TextBox>("StepBox")!;
        _range = this.FindControl<StackPanel>("RangeRows")!;
        _trouble = this.FindControl<TextBlock>("Trouble")!;
        _add = this.FindControl<Button>("AddButton")!;
        _cancel = this.FindControl<Button>("CancelButton")!;

        _type.SelectionChanged += (_, _) => ShowRange();
        _add.Click += OnAdd;
        _cancel.Click += (_, _) => Cancelled?.Invoke(this, EventArgs.Empty);

        ShowRange();
    }

    /// <summary>A parameter the language accepted.</summary>
    public event EventHandler<SvgExpressionParameter>? Accepted;

    public event EventHandler? Cancelled;

    /// <summary>The names this drawing has already given out.</summary>
    public IReadOnlyCollection<string> Taken
    {
        get => _taken;
        set => _taken = value ?? Array.Empty<string>();
    }

    /// <summary>Fills the form in from a declaration that already exists.</summary>
    /// <remarks>
    /// The type is shown but cannot be changed. Every expression naming this parameter was checked
    /// against the type it has, so changing one is a change to all of them rather than to the
    /// declaration alone — which is a different operation from editing it.
    /// </remarks>
    public void Initialize(SvgExpressionParameter existing)
    {
        if (existing is null)
        {
            throw new ArgumentNullException(nameof(existing));
        }

        _name.Text = existing.Name;
        _default.Text = existing.DefaultExpression ?? string.Empty;
        _minimum.Text = existing.MinExpression ?? string.Empty;
        _maximum.Text = existing.MaxExpression ?? string.Empty;
        _step.Text = existing.StepExpression ?? string.Empty;

        _type.SelectedIndex = existing.Type switch
        {
            ExprType.Number => 0,
            ExprType.Color => 1,
            _ => 2,
        };

        _type.IsEnabled = false;
        _add.Content = "Save";

        ShowRange();
    }

    /// <summary>What the form would produce, or null with <paramref name="trouble"/> saying why.</summary>
    /// <remarks>
    /// Public so a test can ask the question the button asks, without a button.
    /// </remarks>
    public SvgExpressionParameter? TryBuild(out string? trouble)
    {
        var type = ExprType.Number;

        try
        {
            type = ExprFunctions.ParseType(Selected(), 0, SvgDeclarationPart.Type);
        }
        catch (ExprException bad)
        {
            trouble = bad.Message;

            return null;
        }

        var ranged = type == ExprType.Number;

        var parameter = new SvgExpressionParameter(
            _name.Text?.Trim() ?? string.Empty,
            type,
            Empty(_default.Text),
            ranged ? Empty(_minimum.Text) : null,
            ranged ? Empty(_maximum.Text) : null,
            ranged ? Empty(_step.Text) : null);

        var builder = new SvgExpressionDeclarations.Builder();

        try
        {
            foreach (var name in _taken)
            {
                // Only to hold the name: what they are is beside the point, and a number with no
                // default is the cheapest thing the rules will accept.
                builder.AddParameter(name, "number", null);
            }

            builder.AddParameter(
                parameter.Name,
                ExprFunctions.NameOf(parameter.Type),
                parameter.DefaultExpression,
                parameter.MinExpression,
                parameter.MaxExpression,
                parameter.StepExpression);
        }
        catch (ExprException bad)
        {
            trouble = bad.Message;

            return null;
        }

        trouble = null;

        return parameter;
    }

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        var parameter = TryBuild(out var trouble);

        _trouble.Text = trouble ?? string.Empty;
        _trouble.IsVisible = trouble is { };

        if (parameter is { })
        {
            Accepted?.Invoke(this, parameter);
        }
    }

    private void ShowRange() => _range.IsVisible = Selected() == "number";

    private string Selected()
        => (_type.SelectedItem as ComboBoxItem)?.Content as string ?? "number";

    private static string? Empty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value!.Trim();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
