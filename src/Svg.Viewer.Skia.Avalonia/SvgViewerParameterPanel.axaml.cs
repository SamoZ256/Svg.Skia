// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>One control per declared parameter.</summary>
public partial class SvgViewerParameterPanel : UserControl
{
    private readonly ItemsControl _rows;
    private readonly TextBlock _emptyLabel;

    private IReadOnlyList<SvgViewerParameter> _parameters = Array.Empty<SvgViewerParameter>();

    public SvgViewerParameterPanel()
    {
        AvaloniaXamlLoader.Load(this);

        _rows = this.FindControl<ItemsControl>("Rows")!;
        _emptyLabel = this.FindControl<TextBlock>("EmptyLabel")!;
    }

    /// <summary>Raised when any row's value changes.</summary>
    public event EventHandler<SvgViewerParameter>? ValueChanged;

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
                return;
            }

            Detach();

            _parameters = value ?? Array.Empty<SvgViewerParameter>();

            foreach (var parameter in _parameters)
            {
                parameter.ValueChanged += OnRowValueChanged;
            }

            _rows.ItemsSource = _parameters;
            _emptyLabel.IsVisible = value is { Count: 0 };
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

    private void OnRowValueChanged(object? sender, EventArgs e)
    {
        if (sender is SvgViewerParameter parameter)
        {
            ValueChanged?.Invoke(this, parameter);
        }
    }
}
