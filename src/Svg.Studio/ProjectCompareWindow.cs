// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Skia;
using Avalonia.Layout;
using Avalonia.Media;
using Svg.Expressions;
using Svg.Viewer.Skia.Avalonia;

namespace Svg.Studio;

/// <summary>One changed node as it was at the last commit and as the window holds it now, side by side.</summary>
public sealed class ProjectCompareWindow : Window
{
    private readonly List<IDisposable> _owned = new();

    public ProjectCompareWindow(ProjectChange change)
    {
        if (change is null)
        {
            throw new ArgumentNullException(nameof(change));
        }

        Title = $"Compare {ProjectWorkspace.Label((change.After ?? change.Before)!)}";
        Width = 560;
        Height = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var sides = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(16) };
        var before = Side("Last commit", Rendered(change.Before, _owned));
        var after = Side("Now", Rendered(change.After, _owned));

        Grid.SetColumn(after, 1);
        sides.Children.Add(before);
        sides.Children.Add(after);

        Content = sides;
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

    internal static Control Side(string heading, Control content)
    {
        var side = new DockPanel { Margin = new Thickness(6) };
        var title = new TextBlock { Text = heading, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) };

        DockPanel.SetDock(title, Dock.Top);
        side.Children.Add(title);
        side.Children.Add(content);

        return side;
    }

    /// <summary>
    /// A drawing as the board draws it, against its own version's declarations; a group or the
    /// project as its settings, and a node that is not there as saying so.
    /// </summary>
    /// <param name="owned">Where the loaded document goes, for the caller to dispose once the picture is gone.</param>
    internal static Control Rendered(ProjectNode? node, ICollection<IDisposable> owned)
    {
        if (node is null)
        {
            return Note("Not there");
        }

        if (node is not ProjectDrawing drawing)
        {
            return Note(string.Join(
                Environment.NewLine,
                node.Element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration)
                    .Select(attribute => $"{attribute.Name.LocalName}=\"{attribute.Value}\"")));
        }

        try
        {
            var document = SvgViewerDocument.LoadFromSvg(
                drawing.Text,
                null,
                ProjectWorkspace.SizeOf(drawing),
                own => ProjectDeclarations.Built(drawing, own));

            owned.Add(document);

            try
            {
                document.Svg.SetExpressionValues(GroupPanel.Seeded(document));
            }
            catch (ExprException)
            {
            }

            return new SKPictureControl { Picture = document.Svg.Picture, Stretch = Stretch.Uniform };
        }
        catch (Exception failure)
        {
            // Anything, as on the board: this is a version of user data nobody has opened yet.
            return Note(failure.Message);
        }
    }

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Opacity = 0.7,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center
    };
}
