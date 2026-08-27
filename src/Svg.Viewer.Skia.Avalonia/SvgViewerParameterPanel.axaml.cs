// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>One control per declared parameter.</summary>
public partial class SvgViewerParameterPanel : UserControl
{
    private readonly ItemsControl _rows;
    private readonly TextBlock _emptyLabel;
    private readonly StackPanel _actions;
    private readonly TextBlock _commitLabel;
    private readonly Button _commitButton;
    private readonly Button _addButton;

    private IReadOnlyList<SvgViewerParameter> _parameters = Array.Empty<SvgViewerParameter>();

    /// <summary>Whether there is a drawing behind the rows, which null and empty tell apart.</summary>
    private bool _hasDocument;

    public SvgViewerParameterPanel()
    {
        AvaloniaXamlLoader.Load(this);

        _rows = this.FindControl<ItemsControl>("Rows")!;
        _emptyLabel = this.FindControl<TextBlock>("EmptyLabel")!;
        _actions = this.FindControl<StackPanel>("Actions")!;
        _commitLabel = this.FindControl<TextBlock>("CommitLabel")!;
        _commitButton = this.FindControl<Button>("CommitButton")!;
        _addButton = this.FindControl<Button>("AddButton")!;

        _addButton.Click += (_, _) => AddRequested?.Invoke(this, EventArgs.Empty);
        _commitButton.Click += (_, _) => CommitRequested?.Invoke(this, EventArgs.Empty);

        ShowActions();
    }

    /// <summary>Raised when any row's value changes.</summary>
    public event EventHandler<SvgViewerParameter>? ValueChanged;

    /// <summary>Raised when somebody asks to declare a parameter.</summary>
    /// <remarks>
    /// The panel asks rather than acts, as it does with a value: it holds rows and knows nothing
    /// about the document they came from or how one is edited.
    /// </remarks>
    public event EventHandler? AddRequested;

    /// <summary>Raised when somebody asks for the current values to become the declared defaults.</summary>
    public event EventHandler? CommitRequested;

    /// <summary>Raised when somebody asks to change what one parameter declares.</summary>
    public event EventHandler<SvgViewerParameter>? EditRequested;

    /// <summary>The rows to show, or null when there is no drawing to declare any.</summary>
    /// <remarks>
    /// Null and empty are different answers, and the label is the whole reason to tell them apart.
    /// A drawing that declares nothing says so; a pane with no drawing behind it says nothing at
    /// all. A file that would not open has no parameters the way it has no colours, and a note that
    /// it declares none reads as a fact about the file rather than about the failure to read it.
    /// </remarks>
    public IReadOnlyList<SvgViewerParameter>? Parameters
    {
        get => _parameters;
        set
        {
            if (ReferenceEquals(_parameters, value))
            {
                // The same rows again, which is a reload that changed none of them: rebuilding the
                // items would throw away whatever row someone is part-way through editing. Only the
                // label moves, because rows being identical is not the same fact as whether there
                // is a drawing behind them.
                _emptyLabel.IsVisible = value is { Count: 0 };
                ShowActions();
                return;
            }

            Detach();

            _hasDocument = value is { };
            _parameters = value ?? Array.Empty<SvgViewerParameter>();

            foreach (var parameter in _parameters)
            {
                parameter.ValueChanged += OnRowValueChanged;
            }

            _rows.ItemsSource = _parameters;
            _emptyLabel.IsVisible = value is { Count: 0 };

            ShowActions();
        }
    }

    public void ResetToDefaults()
    {
        foreach (var parameter in _parameters)
        {
            parameter.ResetToDefault();
        }
    }

    private void Detach()
    {
        foreach (var parameter in _parameters)
        {
            parameter.ValueChanged -= OnRowValueChanged;
        }
    }

    /// <summary>The row's own button, found through the item it was templated for.</summary>
    private void OnEditClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SvgViewerParameter parameter })
        {
            EditRequested?.Invoke(this, parameter);
        }
    }

    private void OnRowValueChanged(object? sender, EventArgs e)
    {
        ShowActions();

        if (sender is SvgViewerParameter parameter)
        {
            ValueChanged?.Invoke(this, parameter);
        }
    }

    /// <summary>
    /// Shows what can be done with the rows as they stand.
    /// </summary>
    /// <remarks>
    /// Nothing at all without a drawing, because adding a parameter to no document is not a thing to
    /// offer. The commit half appears only once some row differs from what the document declares:
    /// the difference is the reason to commit, so with none there is nothing to say and nothing to
    /// press.
    /// </remarks>
    private void ShowActions()
    {
        var open = _hasDocument;

        _actions.IsVisible = open;

        var changed = 0;

        if (open)
        {
            foreach (var parameter in _parameters)
            {
                if (parameter.IsModified)
                {
                    changed++;
                }
            }
        }

        _commitButton.IsVisible = changed > 0;
        _commitLabel.IsVisible = changed > 0;
        _commitLabel.Text = changed == 1
            ? "1 value differs from the declared default."
            : $"{changed} values differ from the declared defaults.";
    }
}
