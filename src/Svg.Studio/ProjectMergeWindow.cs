// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Svg.Studio;

/// <summary>The nodes a merge could not decide, each with our and their version to keep one of.</summary>
/// <remarks>
/// Shown modally, and answers true for Resolve, false for Abort merge, and null when it is closed,
/// which leaves the merge as it is.
/// </remarks>
public sealed class ProjectMergeWindow : Window
{
    private readonly List<IDisposable> _owned = new();

    public ProjectMergeWindow(ProjectMerge merge)
    {
        if (merge is null)
        {
            throw new ArgumentNullException(nameof(merge));
        }

        Title = "Resolve the merge";
        Width = 640;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var rows = new StackPanel { Spacing = 18, Margin = new Thickness(16) };

        foreach (var conflict in merge.Conflicts)
        {
            rows.Children.Add(Row(conflict));
        }

        var resolve = new Button { Content = "Resolve", IsDefault = true };
        var abort = new Button { Content = "Abort merge" };

        resolve.Click += (_, _) => Close(true);
        abort.Click += (_, _) => Close(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16),
            Children = { abort, resolve }
        };

        var body = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        body.Children.Add(buttons);
        body.Children.Add(new ScrollViewer { Content = rows });

        Content = body;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        foreach (var owned in _owned)
        {
            owned.Dispose();
        }

        _owned.Clear();
    }

    private Control Row(ProjectConflict conflict)
    {
        var node = (conflict.Ours ?? conflict.Theirs)!;
        var group = "conflict" + conflict.GetHashCode();

        // On a deletion the choice is whether the side that still has it wins, so the buttons say
        // what happens to the node rather than whose it is.
        var mine = new RadioButton
        {
            GroupName = group,
            Content = !conflict.IsDeletion ? "Keep mine" : conflict.Ours is null ? "Delete" : "Keep",
            IsChecked = conflict.Choice == ProjectSide.Ours
        };

        var theirs = new RadioButton
        {
            GroupName = group,
            Content = !conflict.IsDeletion ? "Keep theirs" : conflict.Theirs is null ? "Delete" : "Keep",
            IsChecked = conflict.Choice == ProjectSide.Theirs
        };

        mine.IsCheckedChanged += (_, _) => conflict.Choice = mine.IsChecked == true ? ProjectSide.Ours : ProjectSide.Theirs;

        var sides = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Height = 180 };
        var ours = Choosing(ProjectCompareWindow.Side("Mine", ProjectCompareWindow.Rendered(conflict.Ours, _owned)), mine);
        var others = Choosing(ProjectCompareWindow.Side("Theirs", ProjectCompareWindow.Rendered(conflict.Theirs, _owned)), theirs);

        Grid.SetColumn(others, 1);
        sides.Children.Add(ours);
        sides.Children.Add(others);

        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = ProjectWorkspace.Label(node), FontWeight = FontWeight.SemiBold },
                sides
            }
        };
    }

    private static Control Choosing(Control side, RadioButton choice)
    {
        var column = new DockPanel();
        DockPanel.SetDock(choice, Dock.Bottom);
        column.Children.Add(choice);
        column.Children.Add(side);

        return column;
    }
}
