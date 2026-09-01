// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using Svg.Highlighting;
using Svg.Viewer.Skia.Avalonia;

namespace Svg.Studio;

/// <summary>
/// One recipe, as text.
/// </summary>
/// <remarks>
/// A recipe is the other half of what a project builds, and until now the only editable half was
/// the drawing. It gets the same treatment for the same reason: an editor is where the file is
/// already being looked at, and leaving for a text editor to change one colour is what made a
/// recipe feel like something you set up once and never touched.
///
/// Not the viewer's source pane, which is about the drawing a viewer is showing and holds a picture
/// against it. This is a file with nothing to draw, so it is a tab of its own — the same shape a
/// group's settings take.
///
/// A view onto a <see cref="RecipeWorkspace"/> and not the owner of its text: the same recipe is
/// edited from the colours and parameters of the drawings under it, and all of it is one buffer.
/// </remarks>
public sealed class RecipePanel : UserControl
{
    private readonly TextEditor _editor = new()
    {
        ShowLineNumbers = true,
        WordWrap = false,
        FontFamily = new FontFamily("Cascadia Mono,Menlo,Consolas,DejaVu Sans Mono,monospace"),
        FontSize = 12,
        Padding = new Thickness(8, 6),
        HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
    };

    private readonly SvgViewerSourceColorizer _colorizer;

    private readonly TextBlock _fault = new()
    {
        Margin = new Thickness(10, 6),
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        IsVisible = false
    };

    public RecipePanel(RecipeWorkspace workspace)
    {
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));

        // Both carried here rather than left to the host, the way the viewer's own panels carry
        // theirs. AvaloniaEdit ships its control theme in its own assembly and the viewer includes
        // it in styles that reach the source pane inside it and nothing else, so a tab beside it
        // was templated with nothing and drew an empty box; the palette sits in a file of its own
        // for exactly this, but the viewer merges it where only the viewer can see it.
        Styles.Add(new StyleInclude(Home)
        {
            Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml")
        });

        Resources.MergedDictionaries.Add(new ResourceInclude(Home)
        {
            Source = new Uri("avares://Svg.Viewer.Skia.Avalonia/SvgViewerSourceBrushes.axaml")
        });

        _colorizer = new SvgViewerSourceColorizer(Brush);

        _editor.TextArea.TextView.LineTransformers.Add(_colorizer);

        // The workspace's buffer, not one of its own. An edit made anywhere else in the window
        // arrives here as a keystroke does, and is taken back on the stack this editor shows.
        _editor.Document = workspace.Document;
        _editor.TextChanged += (_, _) => Edited();

        workspace.ModifiedChanged += (_, modified) => ModifiedChanged?.Invoke(this, modified);

        // A control does not know its theme until it is in a tree with one, and this is built before
        // it is in any: painted in the constructor alone it took the light palette into a dark
        // window, which put near-black plain text on a dark ground and hid the caret with it.
        ActualThemeVariantChanged += (_, _) => Colour();

        var panel = new DockPanel();

        panel.Children.Add(_fault);
        DockPanel.SetDock(_fault, Dock.Bottom);
        panel.Children.Add(_editor);

        Content = panel;

        Colour();
        Check();
    }

    /// <summary>What a relative include is read against, which nothing here writes one of.</summary>
    private static readonly Uri Home = new("avares://Svg.Studio/");

    /// <summary>The recipe this is a view of.</summary>
    public RecipeWorkspace Workspace { get; }

    /// <summary>The file this is showing.</summary>
    public string Path => Workspace.Path;

    /// <summary>What the editor is holding, which is what a save would write.</summary>
    /// <remarks>
    /// Set through the document rather than through the editor, because <c>TextEditor.Text</c>
    /// clears the undo stack: written that way there was nothing to take back afterwards, which is
    /// not what setting the text of an open file means.
    /// </remarks>
    public string Text
    {
        get => Workspace.Text;
        set => Workspace.Document.Text = value ?? string.Empty;
    }

    /// <summary>Whether the text has edits that are not on disk.</summary>
    public bool IsModified => Workspace.IsModified;

    /// <summary>Raised when <see cref="IsModified"/> changes, for a host that marks its tab.</summary>
    public event EventHandler<bool>? ModifiedChanged;

    /// <inheritdoc cref="RecipeWorkspace.Fault"/>
    public string? Fault => Workspace.Fault;

    /// <summary>Takes back the last edit, or puts it back.</summary>
    /// <remarks>
    /// The window asks rather than the editor answering the keystroke itself: a menu item's gesture
    /// belongs to the window, so Undo is taken there before AvaloniaEdit can see it.
    /// </remarks>
    public bool Undo() => _editor.CanUndo && _editor.Undo();

    /// <inheritdoc cref="Undo"/>
    public bool Redo() => _editor.CanRedo && _editor.Redo();

    /// <inheritdoc cref="RecipeWorkspace.Save"/>
    public void Save() => Workspace.Save();

    private void Edited()
    {
        Colour();
        Check();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        Colour();
    }

    private void Colour()
    {
        _colorizer.Show(SvgSourceHighlighter.Lines(_editor.Text));

        _editor.Foreground = Brush(SvgSourceTokenKind.Text);
        _editor.LineNumbersForeground = Resource("SvgViewerSourceLineNumberBrush");

        // Said rather than left to the editor. With none, AvaloniaEdit draws the caret by inverting
        // what is behind it, and what is behind this one is whatever the window paints — which came
        // out as a caret nobody could see.
        _editor.TextArea.CaretBrush = Brush(SvgSourceTokenKind.Text);

        _editor.TextArea.TextView.Redraw();
    }

    /// <summary>Shows what the parser makes of the text, or nothing when it reads.</summary>
    private void Check()
    {
        _fault.Text = Fault;
        _fault.IsVisible = Fault is { };
        _fault[!TextBlock.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SvgViewerSourceErrorBrush");
    }

    private IBrush? Brush(SvgSourceTokenKind kind) => Resource(SvgViewer.SourceResourceKey(kind));

    private IBrush? Resource(string key)
        => this.TryFindResource(key, ActualThemeVariant, out var brush) ? brush as IBrush : null;
}
