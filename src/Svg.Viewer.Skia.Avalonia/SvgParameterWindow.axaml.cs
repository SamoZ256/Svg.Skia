// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Svg.Expressions;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>A window around <see cref="SvgParameterFormView"/>, and nothing else.</summary>
/// <remarks>
/// The form is the reusable half; this exists only to be shown modally and to turn what the form
/// raises into what <c>ShowDialog</c> returns.
/// </remarks>
public partial class SvgParameterWindow : Window
{
    public SvgParameterWindow()
        : this(Array.Empty<string>())
    {
    }

    public SvgParameterWindow(IReadOnlyCollection<string> taken, SvgExpressionParameter? existing = null)
    {
        InitializeComponent();

        var form = this.FindControl<SvgParameterFormView>("FormView")!;

        form.Taken = taken;

        if (existing is { })
        {
            Title = "Edit parameter";
            form.Initialize(existing);
        }

        form.Accepted += (_, parameter) => Close(parameter);
        form.Cancelled += (_, _) => Close(null);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
