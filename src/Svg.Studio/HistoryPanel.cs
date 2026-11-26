// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Svg.Viewer.Skia.Avalonia;

namespace Svg.Studio;

/// <summary>What Edit → Undo would take back, in the order it would: the drawing in front, then the project.</summary>
/// <remarks>
/// A row is that many presses of Undo, so clicking one takes back everything listed above it and
/// itself. The text box tier of the window's Undo is left out: a row is clicked, so nothing is
/// being typed in.
/// </remarks>
public sealed class HistoryPanel : UserControl
{
    private readonly StackPanel _rows = new();

    /// <summary>What the rows were made from, so a click on a list made before something else changed does nothing.</summary>
    /// <remarks>
    /// The top of each history rather than <c>Holds</c> on it: Holds stays true when a newer step lands on top,
    /// and the row's count would then reach the wrong step.
    /// </remarks>
    private (SvgViewer? Drawing, object? DrawingAt, ProjectWorkspace? Project, object? ProjectAt) _listed;

    /// <summary>Set while a click is taking steps back, each of which the window tells this of.</summary>
    private bool _stepping;

    public HistoryPanel()
    {
        Content = new ScrollViewer { Content = _rows, Padding = new Thickness(10, 4, 10, 10) };

        Refresh();
    }

    /// <summary>The drawing in front, whose steps come first.</summary>
    public Func<SvgViewer?> Drawing { get; set; } = static () => null;

    /// <summary>The open project, whose steps come after the drawing's.</summary>
    public Func<ProjectWorkspace?> Project { get; set; } = static () => null;

    /// <summary>Lists both histories again, where either has moved since they were listed.</summary>
    public void Refresh()
    {
        var now = Now();

        if (_stepping || (now == _listed && _rows.Children.Count > 0))
        {
            return;
        }

        _listed = now;
        _rows.Children.Clear();

        var steps = 0;

        Add("Drawing", now.Drawing?.UndoLabels);
        Add("Project", now.Project?.UndoLabels);

        if (steps == 0)
        {
            _rows.Children.Add(new TextBlock { Text = "Nothing to take back.", FontSize = 12, Opacity = 0.6, Margin = new Thickness(0, 6, 0, 0) });
        }

        void Add(string heading, IReadOnlyList<string>? labels)
        {
            if (labels is not { Count: > 0 })
            {
                return;
            }

            _rows.Children.Add(ChangesPanel.Heading(heading));

            foreach (var label in labels)
            {
                _rows.Children.Add(Row(label, ++steps));
            }
        }
    }

    private (SvgViewer? Drawing, object? DrawingAt, ProjectWorkspace? Project, object? ProjectAt) Now()
    {
        var drawing = Drawing();
        var project = Project();

        return (drawing, drawing?.UndoStep, project, project?.UndoStep);
    }

    private Button Row(string label, int steps)
    {
        var row = new Button
        {
            Content = label.Length > 0 ? label : "edit",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 1),
            MinHeight = 0,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            [ToolTip.TipProperty] = steps == 1 ? "Undo this" : $"Undo this and the {steps - 1} above it"
        };

        row.Click += (_, _) => Back(steps);

        return row;
    }

    private void Back(int steps)
    {
        var (drawing, _, project, _) = _listed;

        if (Now() == _listed)
        {
            var taken = 0;

            _stepping = true;

            try
            {
                while (taken < steps && (drawing?.Undo() == true || project?.Undo() == true))
                {
                    taken++;
                }
            }
            finally
            {
                _stepping = false;
            }
        }

        Refresh();
    }
}
