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

    public IReadOnlyList<SvgViewerParameter> Parameters
    {
        get => _parameters;
        set
        {
            Detach();

            _parameters = value ?? Array.Empty<SvgViewerParameter>();

            foreach (var parameter in _parameters)
            {
                parameter.ValueChanged += OnRowValueChanged;
            }

            _rows.ItemsSource = _parameters;
            _emptyLabel.IsVisible = _parameters.Count == 0;
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
