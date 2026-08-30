// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Svg.CodeGen.Skia.Projects;
using Svg.Skia;

namespace Svg.Studio;

/// <summary>
/// One node of a project in a tab: its settings, and what it builds.
/// </summary>
/// <remarks>
/// Built in code rather than declared, for the reason the tabs are: the rows depend on what kind of
/// node this is, so a template would have to be chosen at runtime anyway.
/// </remarks>
public sealed class GroupPanel : UserControl
{
    private readonly StackPanel _properties = new() { Spacing = 8, Margin = new Thickness(10) };
    private readonly StackPanel _summary = new() { Spacing = 2, Margin = new Thickness(10) };
    private readonly TextBlock _heading = new() { FontWeight = FontWeight.SemiBold, Margin = new Thickness(10, 10, 10, 0) };

    public GroupPanel(ProjectWorkspace workspace, SvgcProjectNode node)
    {
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Node = node ?? throw new ArgumentNullException(nameof(node));

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,6,340")
        };

        var centre = new DockPanel();
        centre.Children.Add(_heading);
        DockPanel.SetDock(_heading, Dock.Top);
        centre.Children.Add(new ScrollViewer { Content = _summary });

        grid.Children.Add(centre);

        var splitter = new GridSplitter { Background = Brushes.Transparent };
        Grid.SetColumn(splitter, 1);
        grid.Children.Add(splitter);

        var right = new Border
        {
            BorderThickness = new Thickness(1, 0, 0, 0),
            BorderBrush = new SolidColorBrush(Color.Parse("#20808080")),
            Child = new ScrollViewer { Content = _properties }
        };

        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        Content = grid;

        // A group edited in another tab changes what this one inherits, so every tab follows the
        // one document rather than the copy it was opened with.
        workspace.Edited += (_, _) => Refresh();

        Refresh();
    }

    public ProjectWorkspace Workspace { get; }

    /// <summary>The node this tab is about.</summary>
    public SvgcProjectNode Node { get; }

    /// <summary>Puts the node's settings on the right, and what it builds in the centre.</summary>
    public void Refresh()
    {
        _heading.Text = Node is SvgcProjectDrawing
            ? ProjectWorkspace.Label(Node)
            : $"{ProjectWorkspace.Label(Node)} — what it builds";

        ShowSummary(Node);
        ShowProperties(Node);
    }

    private void ShowSummary(SvgcProjectNode node)
    {
        _summary.Children.Clear();

        var drawings = node is SvgcProjectGroup group
            ? group.Drawings.ToList()
            : new List<SvgcProjectDrawing> { (SvgcProjectDrawing)node };

        if (drawings.Count == 0)
        {
            _summary.Children.Add(new TextBlock { Text = "This group holds no drawings.", Opacity = 0.65 });
            return;
        }

        foreach (var drawing in drawings)
        {
            var name = drawing.EffectiveNamespace is { } space && drawing.EffectiveClass is { } className
                ? $"{space}.{className}"
                : drawing.EffectiveClass ?? drawing.EffectiveNamespace ?? "(unnamed)";

            _summary.Children.Add(new TextBlock
            {
                Text = $"{Path.GetFileName(drawing.Input)}  →  {name}   {Size(drawing)}",
                FontFamily = new FontFamily("Menlo, Consolas, monospace"),
                FontSize = 12
            });
        }
    }

    /// <summary>What the sizing comes to, said the way the project says it.</summary>
    private static string Size(SvgcProjectNode node)
    {
        var parts = new List<string>();

        if (node.EffectiveWidth is { } width)
        {
            parts.Add($"w{Number(width)}");
        }

        if (node.EffectiveHeight is { } height)
        {
            parts.Add($"h{Number(height)}");
        }

        if (node.EffectiveScale is { } scale)
        {
            parts.Add($"×{Number(scale)}");
        }

        if (node.EffectivePadding is { } padding)
        {
            parts.Add($"pad {padding}");
        }

        return parts.Count == 0 ? "as written" : string.Join(" ", parts);
    }

    private void ShowProperties(SvgcProjectNode node)
    {
        _properties.Children.Clear();

        if (node is SvgcProjectDrawing drawing)
        {
            Add("input", () => drawing.Input, value => drawing.Input = value ?? string.Empty);
            Add("output", () => drawing.Output, value => drawing.Output = value);
        }

        Add("namespace", () => node.Namespace, value => node.Namespace = value);
        Add("class", () => node.Class, value => node.Class = value);
        Add("recipe", () => node.Recipe, value => node.Recipe = value);

        if (node is SvgcProjectRoot root)
        {
            Add("singleFile", () => root.SingleFile, value => root.SingleFile = value);
            Add("emit", () => Text(root.Emit), value => root.Emit = SvgcProject.ParseEmit(value));
            Add("cache", () => Text(root.Cache), value => root.Cache = SvgcProject.ParseCache(value));
            Add("helperScope", () => Text(root.HelperScope), value => root.HelperScope = SvgcProject.ParseHelperScope(value));
            Add("skiaSharp", () => Text(root.SkiaSharp), value => root.SkiaSharp = SvgcProject.ParseSkiaSharpTarget(value));
        }

        _properties.Children.Add(new Separator { Margin = new Thickness(0, 6) });

        AddNumber("width", () => node.Width, value => node.Width = value);
        AddNumber("height", () => node.Height, value => node.Height = value);
        AddNumber("scale", () => node.Scale, value => node.Scale = value);
        Add("padding", () => node.Padding, value => node.Padding = value);

        void Add(string name, Func<string?> read, Action<string?> write)
            => _properties.Children.Add(Row(node, name, read(), write));

        void AddNumber(string name, Func<float?> read, Action<float?> write)
            => _properties.Children.Add(Row(
                node,
                name,
                read() is { } value ? Number(value) : null,
                text => write(SvgcProject.ParseLength(text, name))));
    }

    /// <summary>
    /// One setting: what this node says, with what it would inherit shown behind it.
    /// </summary>
    /// <remarks>
    /// An empty box means "inherited", so clearing one is how an override is taken back — which is
    /// why the watermark carries the inherited value rather than a hint.
    /// </remarks>
    private Control Row(SvgcProjectNode node, string name, string? value, Action<string?> write)
    {
        var box = new TextBox
        {
            Text = value,
            PlaceholderText = Inherited(node, name),
            FontSize = 12
        };

        box.LostFocus += (_, _) =>
        {
            var text = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text!.Trim();

            if (text == value)
            {
                return;
            }

            try
            {
                write(text);
            }
            catch (SvgcProjectException failure)
            {
                // Put back, and said, rather than left looking accepted.
                box.Text = value;
                Fault = failure.Message;
                return;
            }

            Fault = null;

            // The whole window, because one setting changes what everything under it inherits —
            // this panel included, through the workspace it raises the change on.
            Workspace.Touch();
        };

        return new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = name, Opacity = 0.65, FontSize = 11 },
                box
            }
        };
    }

    /// <summary>What the node would take for <paramref name="name"/> if it said nothing itself.</summary>
    private static string? Inherited(SvgcProjectNode node, string name)
    {
        if (node.Parent is not { } parent)
        {
            return null;
        }

        var owner = parent.OwnerOf(name);

        return owner is null ? null : $"{Value(owner, name)} — from {ProjectWorkspace.Label(owner)}";
    }

    private static string? Value(SvgcProjectNode node, string name) => name switch
    {
        "namespace" => node.Namespace,
        "class" => node.Class,
        "recipe" => node.Recipe,
        "padding" => node.Padding,
        "width" => node.Width is { } width ? Number(width) : null,
        "height" => node.Height is { } height ? Number(height) : null,
        "scale" => node.Scale is { } scale ? Number(scale) : null,
        _ => null
    };

    /// <summary>Why the last edit was refused, or null.</summary>
    public string? Fault { get; private set; }

    private static string Number(float value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? Text<T>(T? value) where T : struct
        => value is { } set ? set.ToString()!.ToLowerInvariant() : null;
}
