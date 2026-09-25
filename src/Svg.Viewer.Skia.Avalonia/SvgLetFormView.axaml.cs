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

/// <summary>The two fields of an expression variable, and whether the language will have them.</summary>
/// <remarks>
/// A control rather than a window, so the fields can be tested without a modal. Beside
/// <see cref="SvgParameterFormView"/> rather than inside it: four of that form's six rows — the type
/// and the range — say nothing about a let, and an entry in its type list is the shape that was
/// tried and dropped. It decides nothing itself, as that form does not: the name goes through
/// <see cref="SvgExpressionDeclarations.Builder"/> and the body through <see cref="ExprChecker"/>,
/// which is the pair the row is already checked by.
/// </remarks>
public partial class SvgLetFormView : UserControl
{
    private readonly TextBox _name;
    private readonly TextBox _expression;
    private readonly TextBlock _hint;
    private readonly TextBlock _trouble;
    private readonly Button _accept;
    private readonly Button _cancel;

    private IReadOnlyCollection<string> _taken = Array.Empty<string>();
    private IReadOnlyDictionary<string, ExprType> _scope = new Dictionary<string, ExprType>();

    private bool _editing;

    public SvgLetFormView()
    {
        InitializeComponent();

        _name = this.FindControl<TextBox>("NameBox")!;
        _expression = this.FindControl<TextBox>("ExpressionBox")!;
        _hint = this.FindControl<TextBlock>("Hint")!;
        _trouble = this.FindControl<TextBlock>("Trouble")!;
        _accept = this.FindControl<Button>("AcceptButton")!;
        _cancel = this.FindControl<Button>("CancelButton")!;

        _accept.Click += OnAccept;
        _cancel.Click += (_, _) => Cancelled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>An expression variable the language accepted.</summary>
    public event EventHandler<SvgExpressionLet>? Accepted;

    public event EventHandler? Cancelled;

    /// <summary>The names this drawing has already given out, of either kind.</summary>
    /// <remarks>
    /// Both kinds, because the language keeps one set across them: a let cannot shadow a parameter.
    /// </remarks>
    public IReadOnlyCollection<string> Taken
    {
        get => _taken;
        set => _taken = value ?? Array.Empty<string>();
    }

    /// <summary>What a body may name here, by name and type.</summary>
    /// <remarks>
    /// The types matter rather than only the names: told that everything is a number, the checker
    /// would call <c>mix(tint, #000000, 0.5)</c> wrong.
    /// </remarks>
    public IReadOnlyDictionary<string, ExprType> Scope
    {
        get => _scope;
        set => _scope = value ?? new Dictionary<string, ExprType>();
    }

    /// <summary>Fills the form in from a let the drawing already declares.</summary>
    /// <remarks>
    /// Which also makes the body required. Saving one without a body would take the let out of the
    /// drawing, and a form with a Save button on it is not where a removal should be spelled.
    /// </remarks>
    public void Initialize(SvgExpressionLet existing)
    {
        if (existing is null)
        {
            throw new ArgumentNullException(nameof(existing));
        }

        _name.Text = existing.Name;
        _expression.Text = existing.Expression;

        _editing = true;
        _hint.IsVisible = false;
        _accept.Content = "Save";
    }

    /// <summary>What the form would produce, or null with <paramref name="trouble"/> saying why.</summary>
    /// <remarks>
    /// Public so a test can ask the question the button asks, without a button.
    /// </remarks>
    public SvgExpressionLet? TryBuild(out string? trouble)
    {
        var name = _name.Text?.Trim() ?? string.Empty;
        var expression = _expression.Text?.Trim() ?? string.Empty;

        var builder = new SvgExpressionDeclarations.Builder();

        try
        {
            foreach (var taken in _taken)
            {
                // Only to hold the name: what it is is beside the point, and a number with no
                // default is the cheapest thing the rules will accept.
                builder.AddParameter(taken, "number", null);
            }

            // Under a body the builder will take, since it neither parses one nor accepts an empty
            // one. What was typed is checked below, against the types it can actually name.
            builder.AddLet(name, "0");
        }
        catch (ExprException bad)
        {
            trouble = bad.Message;

            return null;
        }

        if (expression.Length == 0)
        {
            if (_editing)
            {
                trouble = "An expression variable needs an expression.";

                return null;
            }

            trouble = null;

            return new SvgExpressionLet(name, string.Empty);
        }

        try
        {
            new ExprChecker(_scope).Check(expression);
        }
        catch (ExprException bad)
        {
            trouble = bad.Message;

            return null;
        }

        trouble = null;

        return new SvgExpressionLet(name, expression);
    }

    private void OnAccept(object? sender, RoutedEventArgs e)
    {
        var let = TryBuild(out var trouble);

        _trouble.Text = trouble ?? string.Empty;
        _trouble.IsVisible = trouble is { };

        if (let is { })
        {
            Accepted?.Invoke(this, let);
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
