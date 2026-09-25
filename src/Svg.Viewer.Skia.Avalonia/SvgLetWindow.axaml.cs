// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Svg.Expressions;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>A window around <see cref="SvgLetFormView"/>, and nothing else.</summary>
/// <remarks>
/// The form is the reusable half; this exists only to be shown modally and to turn what the form
/// raises into what <c>ShowDialog</c> returns.
/// </remarks>
public partial class SvgLetWindow : Window
{
    public SvgLetWindow()
        : this(Array.Empty<string>(), new Dictionary<string, ExprType>())
    {
    }

    public SvgLetWindow(
        IReadOnlyCollection<string> taken,
        IReadOnlyDictionary<string, ExprType> scope,
        SvgExpressionLet? existing = null)
    {
        InitializeComponent();

        var form = this.FindControl<SvgLetFormView>("FormView")!;

        form.Taken = taken;
        form.Scope = scope;

        if (existing is { })
        {
            Title = "Edit expression variable";
            form.Initialize(existing);
        }

        form.Accepted += (_, let) => Close(let);
        form.Cancelled += (_, _) => Close(null);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
