// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using Svg.Expressions.Recipes;
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

    /// <summary>Whether the text is being replaced rather than typed, so an edit is not a change.</summary>
    private bool _loading;

    private bool _modified;

    public RecipePanel(string path)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));

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
        _editor.TextChanged += (_, _) => Edited();

        // A control does not know its theme until it is in a tree with one, and this is built before
        // it is in any: painted in the constructor alone it took the light palette into a dark
        // window, which put near-black plain text on a dark ground and hid the caret with it.
        ActualThemeVariantChanged += (_, _) => Colour();

        var panel = new DockPanel();

        panel.Children.Add(_fault);
        DockPanel.SetDock(_fault, Dock.Bottom);
        panel.Children.Add(_editor);

        Content = panel;

        Read();
    }

    /// <summary>What a relative include is read against, which nothing here writes one of.</summary>
    private static readonly Uri Home = new("avares://Svg.Studio/");

    /// <summary>The file this is showing.</summary>
    public string Path { get; }

    /// <summary>What the editor is holding, which is what a save would write.</summary>
    /// <remarks>
    /// Set through the document rather than through the editor, because <c>TextEditor.Text</c>
    /// clears the undo stack: written that way there was nothing to take back afterwards, which is
    /// not what setting the text of an open file means.
    /// </remarks>
    public string Text
    {
        get => _editor.Text;
        set => _editor.Document.Text = value ?? string.Empty;
    }

    /// <summary>Whether the text has edits that are not on disk.</summary>
    public bool IsModified
        => _editor.Document is { } document && !document.UndoStack.IsOriginalFile;

    /// <summary>Raised when <see cref="IsModified"/> changes, for a host that marks its tab.</summary>
    public event EventHandler<bool>? ModifiedChanged;

    /// <summary>Why the recipe would not read, or null.</summary>
    /// <remarks>
    /// Said rather than thrown, and rather than refused: half a recipe is what one looks like while
    /// it is being written, and taking the text back between keystrokes would make it unwritable.
    /// The drawings under it go on showing what the last readable version made of them.
    /// </remarks>
    public string? Fault { get; private set; }

    /// <summary>Takes back the last edit, or puts it back.</summary>
    /// <remarks>
    /// The window asks rather than the editor answering the keystroke itself: a menu item's gesture
    /// belongs to the window, so Undo is taken there before AvaloniaEdit can see it.
    /// </remarks>
    public bool Undo() => _editor.CanUndo && _editor.Undo();

    /// <inheritdoc cref="Undo"/>
    public bool Redo() => _editor.CanRedo && _editor.Redo();

    /// <summary>Writes the text to the file.</summary>
    public void Save()
    {
        File.WriteAllText(Path, _editor.Text);

        _editor.Document.UndoStack.MarkAsOriginalFile();

        Announce();
    }

    /// <summary>Reads the file again, throwing away anything typed here.</summary>
    public void Read()
    {
        string text;

        try
        {
            text = File.ReadAllText(Path);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            text = string.Empty;
            Fault = failure.Message;
        }

        // Replacing the document resets the caret, the scroll and the undo stack, so it happens on
        // a read and never on an edit; TextChanged fires for this too, and the flag tells them apart.
        _loading = true;

        try
        {
            _editor.Document = new TextDocument(text);
            _editor.Document.UndoStack.MarkAsOriginalFile();
        }
        finally
        {
            _loading = false;
        }

        Colour();
        Check();
        Announce();
    }

    private void Edited()
    {
        if (_loading)
        {
            return;
        }

        Colour();
        Check();

        // Posted, not called: AvaloniaEdit raises TextChanged before its undo stack has taken the
        // edit, so the file still reads as unmodified at this point and the tab never got its mark.
        Dispatcher.UIThread.Post(Announce);
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

    /// <summary>Says whether the recipe would read, through the same parser the build uses.</summary>
    /// <remarks>
    /// Not a second opinion about the format: a message here that the build did not agree with
    /// would be worse than no message at all.
    /// </remarks>
    private void Check()
    {
        try
        {
            SvgRecipe.Parse(_editor.Text);
            Fault = null;
        }
        catch (SvgRecipeException failure)
        {
            Fault = failure.Message;
        }

        _fault.Text = Fault;
        _fault.IsVisible = Fault is { };
        _fault[!TextBlock.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SvgViewerSourceErrorBrush");
    }

    private void Announce()
    {
        var modified = IsModified;

        if (modified == _modified)
        {
            return;
        }

        _modified = modified;
        ModifiedChanged?.Invoke(this, modified);
    }

    private IBrush? Brush(SvgSourceTokenKind kind) => Resource(SvgViewer.SourceResourceKey(kind));

    private IBrush? Resource(string key)
        => this.TryFindResource(key, ActualThemeVariant, out var brush) ? brush as IBrush : null;
}
