// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Svg.CodeGen.Skia;
using Svg.CodeGen.Skia.Projects;

namespace Svg.Studio;

/// <summary>The settings only an export reads, as the form an export asks them in.</summary>
/// <remarks>
/// Kept in the project all the same, where svgc reads them too, so the next export opens on the
/// last one's answers.
/// </remarks>
public sealed class ExportSettings
{
    /// <summary>Each setting in the order it is asked, with what it is called and what its empty box says.</summary>
    private static readonly (string Name, string Label, string? Hint)[] s_settings =
    {
        ("cache", "Picture cache", null),
        ("helperScope", "Helper methods", null),
        ("skiaSharp", "SkiaSharp version", null),
        ("body", "Draw", null),
        ("typeface", "Typeface", "C# expression, e.g. Fonts.Get(\"{family}\", {weightValue})"),
        ("api", "API class", "e.g. PaintCodeResources.VectorIconsResource"),
        ("resizing", "Resizing enum", "adds the framed overloads, e.g. PaintCode.ResizingBehavior"),
        ("context", "Text context type", "taken first by a drawing with text, e.g. PaintCode.Context"),
        ("order", "Argument order", "parameter names, separated by spaces")
    };

    /// <summary>
    /// The settings that take one of a few values: what each option says, what it writes, and why
    /// somebody would pick it. A null value writes nothing, leaving the build's default.
    /// </summary>
    private static readonly Dictionary<string, (string Label, string? Value, string Tip)[]> s_choices = new(StringComparer.Ordinal)
    {
        ["cache"] = new (string, string?, string)[]
        {
            ("Default (none)", null, "Not written in the project, so a build records a picture on every draw."),
            ("None: record on every draw", ProjectRoot.CacheText(SvgPictureCache.None), "Draw records a picture per call and disposes it, and stays stateless."),
            ("Keep the last picture", ProjectRoot.CacheText(SvgPictureCache.LastValue), "Reuses the last picture while the arguments are unchanged. Not safe to draw from several threads."),
            ("Keep the last picture, locked for threads", ProjectRoot.CacheText(SvgPictureCache.LastValueLocked), "As above, guarded by a lock held across the draw.")
        },
        ["helperScope"] = new (string, string?, string)[]
        {
            ("Default (one file-local class)", null, "Not written in the project, so a build puts the helpers in one file-local class."),
            ("One file-local class (C# 11)", ProjectRoot.ScopeText(SvgHelperScope.FileLocal), "A file-scoped class beside the namespaces, invisible outside the file. Needs C# 11."),
            ("One internal class", ProjectRoot.ScopeText(SvgHelperScope.Internal), "An internal class beside the namespaces, for compilers below C# 11."),
            ("Inside each class", ProjectRoot.ScopeText(SvgHelperScope.PerClass), "Private members of every class, so each stands alone and repeats them.")
        },
        ["skiaSharp"] = new (string, string?, string)[]
        {
            ("Default (4.x)", null, "Not written in the project, so a build targets SkiaSharp 4."),
            ("SkiaSharp 4.x", ProjectRoot.SkiaSharpText(SkiaSharpTarget.V4), "Builds paths through SKPathBuilder, since SKPath's mutating methods are obsolete in 4."),
            ("SkiaSharp 3.x", ProjectRoot.SkiaSharpText(SkiaSharpTarget.V3), "Builds paths by calling SKPath directly; SKPathBuilder does not exist yet in 3.")
        },
        ["body"] = new (string, string?, string)[]
        {
            ("Default (record a picture)", null, "Not written in the project, so Draw records a picture and draws it."),
            ("Record a picture, then draw it", ProjectRoot.BodyText(SvgBody.Record), "Draw records the drawing into a picture and draws that."),
            ("Straight onto the canvas", ProjectRoot.BodyText(SvgBody.Direct), "Draw draws onto the canvas it is given, from paths and paints built once. Takes no picture cache.")
        }
    };

    private readonly Dictionary<string, Control> _inputs = new(StringComparer.Ordinal);
    private readonly TextBlock _fault = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, IsVisible = false };

    public ExportSettings(ProjectRoot root)
    {
        if (root is null)
        {
            throw new ArgumentNullException(nameof(root));
        }

        var form = new StackPanel { Spacing = 8d, Width = 460d };

        form.Children.Add(GroupPanel.Heading("Generated code"));

        foreach (var (name, label, hint) in s_settings)
        {
            if (name == "api")
            {
                form.Children.Add(GroupPanel.Heading("PaintCode-style API"));
                form.Children.Add(new TextBlock
                {
                    Text = "One more class, calling every drawing the way PaintCode's generated code was called. Nothing below applies until the class is named.",
                    Opacity = 0.65,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            form.Children.Add(new StackPanel { Spacing = 2, Children = { GroupPanel.Caption(label, name), Input(name, hint, Value(root, name)) } });
        }

        form.Children.Add(_fault);
        Form = form;
    }

    /// <summary>The boxes and pickers, to put in whatever asks.</summary>
    public Control Form { get; }

    /// <summary>What every box and picker holds, empty as null.</summary>
    public IReadOnlyDictionary<string, string?> Values
        => _inputs.ToDictionary(
            input => input.Key,
            input => input.Value switch
            {
                ComboBox picker => (picker.SelectedItem as ComboBoxItem)?.Tag as string,
                TextBox box => string.IsNullOrWhiteSpace(box.Text) ? null : box.Text!.Trim(),
                _ => null
            },
            StringComparer.Ordinal);

    /// <summary>Why the last Accept was refused, or null.</summary>
    public string? Fault => _fault.IsVisible ? _fault.Text : null;

    /// <summary>Checks what was entered, saying what is wrong under the form.</summary>
    /// <returns>Whether every value is one the project can hold.</returns>
    public bool Accept()
    {
        var values = Values;

        try
        {
            foreach (var (name, value) in values)
            {
                Validate(name, value);
            }

            // The one pairing a build refuses, said while the pickers are still on screen.
            if (values["body"] == ProjectRoot.BodyText(SvgBody.Direct)
                && values["cache"] is { } cache && cache != ProjectRoot.CacheText(SvgPictureCache.None))
            {
                throw new SvgcProjectException("Drawn straight onto the canvas, Draw has no picture to cache. Set the picture cache to none.");
            }
        }
        catch (SvgcProjectException refused)
        {
            _fault.Text = refused.Message;
            _fault.IsVisible = true;

            return false;
        }

        _fault.IsVisible = false;

        return true;
    }

    /// <summary>Picks or types a value, as a person would.</summary>
    public void Set(string name, string? value)
    {
        switch (_inputs[name])
        {
            case ComboBox picker:
                var index = Array.FindIndex(s_choices[name], option => option.Value == value);

                // Not -1, which reads back as Default and would take the attribute away.
                picker.SelectedIndex = index >= 0 ? index : throw new ArgumentException($"'{value}' is not an option of {name}.", nameof(value));
                break;
            case TextBox box:
                box.Text = value;
                break;
        }
    }

    internal static bool Holds(string name) => s_settings.Any(setting => setting.Name == name);

    /// <summary>Throws unless <paramref name="value"/> is one this setting can hold.</summary>
    /// <remarks>The pickers' too: the assistant's set_node types them.</remarks>
    internal static void Validate(string name, string? value)
    {
        switch (name)
        {
            case "api":
                SvgcProject.ParseName(value, "class name", qualified: true);
                break;
            case "resizing":
            case "context":
                SvgcProject.ParseName(value, "type name", qualified: true);
                break;
            case "order":
                SvgcProject.ParseNames(value);
                break;
            case "cache":
                SvgcProject.ParseCache(value);
                break;
            case "helperScope":
                SvgcProject.ParseHelperScope(value);
                break;
            case "skiaSharp":
                SvgcProject.ParseSkiaSharpTarget(value);
                break;
            case "body":
                SvgcProject.ParseBody(value);
                break;
        }
    }

    /// <summary>Writes one setting onto <paramref name="root"/>.</summary>
    internal static void Write(ProjectRoot root, string name, string? value)
    {
        switch (name)
        {
            // Nothing is the attribute taken away. The parsers read an empty value as the default,
            // which wrote the default out instead.
            case "cache":
                root.Cache = value is null ? null : SvgcProject.ParseCache(value);
                break;
            case "helperScope":
                root.HelperScope = value is null ? null : SvgcProject.ParseHelperScope(value);
                break;
            case "skiaSharp":
                root.SkiaSharp = value is null ? null : SvgcProject.ParseSkiaSharpTarget(value);
                break;
            case "body":
                root.Body = value is null ? null : SvgcProject.ParseBody(value);
                break;
            case "typeface":
                root.Typeface = value;
                break;
            case "api":
                root.Api = value;
                break;
            case "resizing":
                root.Resizing = value;
                break;
            case "context":
                root.Context = value;
                break;
            case "order":
                root.Order = value;
                break;
        }
    }

    /// <summary>What the project says for <paramref name="name"/>, spelled as the file and the pickers spell it.</summary>
    internal static string? Value(ProjectRoot root, string name) => name switch
    {
        "cache" => root.Cache is { } cache ? ProjectRoot.CacheText(cache) : null,
        "helperScope" => root.HelperScope is { } scope ? ProjectRoot.ScopeText(scope) : null,
        "skiaSharp" => root.SkiaSharp is { } target ? ProjectRoot.SkiaSharpText(target) : null,
        "body" => root.Body is { } body ? ProjectRoot.BodyText(body) : null,
        "typeface" => root.Typeface,
        "api" => root.Api,
        "resizing" => root.Resizing,
        "context" => root.Context,
        "order" => root.Order,
        _ => null
    };

    private Control Input(string name, string? hint, string? value)
    {
        Control input = s_choices.TryGetValue(name, out var options)
            ? new ComboBox
            {
                ItemsSource = options
                    .Select(option =>
                    {
                        var item = new ComboBoxItem { Content = option.Label, Tag = option.Value };

                        ToolTip.SetTip(item, option.Tip);

                        return item;
                    })
                    .ToList(),
                SelectedIndex = Math.Max(0, Array.FindIndex(options, option => option.Value == value)),
                FontSize = 12,
                Tag = name,
                HorizontalAlignment = HorizontalAlignment.Stretch
            }
            : new TextBox { Text = value, PlaceholderText = hint, FontSize = 12, Tag = name };

        _inputs[name] = input;

        return input;
    }
}
