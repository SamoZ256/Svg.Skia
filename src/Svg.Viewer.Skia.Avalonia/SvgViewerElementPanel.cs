// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Svg.Expressions;
using Svg.Skia.TypefaceProviders;
using Svg.SourceEditing;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// The attributes of the element that is picked, each editable as the file writes it.
/// </summary>
/// <remarks>
/// The other question a selection asks. The parameters are the drawing's, and a recipe's rules are a
/// value everywhere; this is one element and one attribute, which is the only way to say that
/// <em>this</em> shape follows a parameter and its neighbour does not.
///
/// A row's box holds the value itself, whatever it is — a literal, or a <c>{{ … }}</c>. So binding
/// and unbinding are one gesture: type an expression and the attribute follows it, type a value back
/// and it does not. Nothing has to remember what a value used to be, which is the thing a rule-shaped
/// editor cannot do once it has replaced one.
///
/// Values come from the text and not from the parsed drawing. The text is what is being edited, so an
/// attribute already holding an expression reads back as that rather than as the placeholder the
/// parser puts in its place. Rows are filed by what they do rather than where they were written, and
/// a row the file does not set is listed only where the element usually has one — or, with
/// <see cref="ShowsAll"/>, wherever the parser would read it.
///
/// Host-agnostic in the way <see cref="SvgViewerDeclarationCommands"/> is: what differs between a
/// drawing's own tab and a group's is where the text is and what applying an edit means, so both are
/// handed in. The address handed to <see cref="Show"/> is an address in <em>that</em> text — a host
/// whose drawing is built from something made of the file translates it first.
/// </remarks>
public sealed class SvgViewerElementPanel : UserControl
{
    private static readonly Uri Home = new("avares://Svg.Viewer.Skia.Avalonia/");

    private readonly StackPanel _rows = new() { Spacing = 10, Margin = new Thickness(10) };

    private readonly TextBlock _note = new()
    {
        Margin = new Thickness(10),
        Opacity = 0.6,
        TextWrapping = TextWrapping.Wrap
    };

    private readonly TextBlock _fault = new()
    {
        Margin = new Thickness(10, 6),
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        IsVisible = false
    };

    private readonly Func<string> _text;

    /// <summary>Where the names an expression may use are declared.</summary>
    /// <remarks>
    /// Not always the text being edited. A drawing under a recipe is built with the recipe's
    /// declarations injected into it, so <c>{{ tint }}</c> is a name in scope while the file itself
    /// declares nothing — checking against the file would refuse every expression a recipe makes
    /// available.
    /// </remarks>
    private readonly Func<string> _declarations;

    private readonly Func<string, Func<SvgSourceDocument, string?>, string?> _write;

    private readonly Func<ExprEvaluator?> _values;

    private readonly List<Line> _shown = new();

    /// <summary>One row: its attribute, where it writes, its box and lines, and the control it is drawn as.</summary>
    /// <param name="Styled">Whether a style declaration sets it.</param>
    /// <param name="Inherited">The expression a group around it drives it with, where it writes none itself.</param>
    private sealed record Line(
        string Name,
        string Address,
        SvgExpressionField Box,
        TextBlock Readout,
        TextBlock Trouble,
        Action<string, ExprValue?>? Follow,
        Control Row,
        bool Styled,
        string? Inherited);

    /// <summary>The sections drawn, with the rows each holds, which a search shows and hides.</summary>
    private readonly List<(Expander Section, List<Line> Lines)> _sections = new();

    private readonly ContentControl _host = new();

    /// <summary>Made once and kept: a second one around the same rows is a second parent for them.</summary>
    private readonly ScrollViewer _scroll;

    /// <summary>The heading and the rows under it, which is what <see cref="_host"/> shows once something is picked.</summary>
    private readonly DockPanel _body = new();

    private readonly TextBlock _title = new()
    {
        FontWeight = FontWeight.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    /// <summary>The element's tag, as the file writes it.</summary>
    private readonly TextBlock _badge = new() { FontSize = 11 };

    private readonly TextBox _search = new()
    {
        Watermark = "Find an attribute",
        FontSize = 12,
        Margin = new Thickness(0, 6, 0, 0),
        Classes = { "search" }
    };

    private readonly TextBlock _nothing = new() { Text = "Nothing here is called that.", Opacity = 0.6, IsVisible = false };

    private string _filter = string.Empty;

    private readonly CheckBox _all = new()
    {
        Content = "Show all",
        FontSize = 12,
        MinHeight = 0,
        VerticalAlignment = VerticalAlignment.Center
    };

    // Static because they are one setting however many panels there are — Studio has one on every
    // drawing's tab and every group's — and a section folded on one tab should not open on the next.
    private static bool s_all;

    private static readonly HashSet<SvgViewerAttributeGroup> s_folded = new();

    /// <summary>The <see cref="Listing"/> this panel's rows were last drawn under.</summary>
    private bool _listedAll;

    /// <summary>Whether every attribute the element can take is listed: shown all, or being searched.</summary>
    private bool Listing => s_all || _filter.Length > 0;

    /// <summary>The state the rows were drawn from, or last wrote: the text, the history's step, and what the element was.</summary>
    /// <remarks>
    /// When the rows are drawn rather than when a box takes the keyboard: rows are drawn together, and a
    /// refresh put off can leave them showing that state while the keyboard moves between them.
    /// </remarks>
    private (string Text, object? Step, string? Element)? _basis;

    /// <summary>Whether a write is under way, which the host's refresh may call back into.</summary>
    private bool _writing;

    /// <summary>Whether a refresh was put off while a row was held, and is owed once it is let go.</summary>
    private bool _stale;

    /// <summary>Whether a pointer went down in the panel and has not come up yet.</summary>
    private bool _pressing;

    /// <summary>How many rows have a picker or a menu open.</summary>
    private int _picking;

    private string? _address;

    // What the shown element is and whether a clip draws it, which decide whether its geometry
    // takes an expression.
    private string? _element;

    private bool _clipped;

    /// <summary>The row naming the constant a box is reported as.</summary>
    private const string BoxRow = "e:bounds";

    private const string ArgumentRow = "e:" + SvgExpressionAttributes.Argument;

    /// <inheritdoc cref="SvgSourceWorkspace.UndoStep"/>
    /// <remarks>Of whatever holds the drawing, so a box typed while it moved underneath is not written over it.</remarks>
    public Func<object?>? UndoStep { get; set; }

    /// <inheritdoc cref="SvgSourceWorkspace.Holds"/>
    public Func<object, bool>? Holds { get; set; }

    /// <summary>The class the drawing generates, which a box may not be named after; asked each time, since it can be renamed.</summary>
    public Func<string?>? ClassName { get; set; }

    /// <summary>What the drawing's text is measured with, whose families the font menu offers; asked as it opens.</summary>
    /// <remarks>
    /// The fonts a host gives, never the machine's: a family only this machine has is the one an
    /// export elsewhere would measure with another font.
    /// </remarks>
    public Func<IEnumerable<ITypefaceProvider>?>? Typefaces { get; set; }

    private static readonly string[] s_genericFamilies = { "sans-serif", "serif", "monospace", "cursive", "fantasy" };

    /// <summary>
    /// Clips or masks the element shown with a new clip path or mask covering it, as the
    /// <c>clip-path</c> or <c>mask</c> it is handed says; offered in that row's menu only where a host wires it.
    /// </summary>
    public Func<string, bool>? NewClipRequested { get; set; }

    /// <summary>Clips or masks the element shown with the tree's row whose key it is handed, as <c>clip-path</c> or <c>mask</c> says.</summary>
    public Func<string, string, bool>? ClipRequested { get; set; }

    /// <summary>What the drag over this panel is carrying, or null while it carries nothing a row takes.</summary>
    /// <remarks>A variable's binding, <c>{{ name }}</c>, or a tree row's address; the two cannot be spelt alike.</remarks>
    private string? _carried;

    /// <summary>The rows that would take it, so the check is made once a drag and not once a move.</summary>
    /// <remarks>
    /// <see cref="Trouble"/> reparses everything in scope each time it is asked, which is fine per
    /// keystroke and not fine per pointer move.
    /// </remarks>
    private readonly HashSet<string> _takes = new(StringComparer.Ordinal);

    /// <summary>The row the pointer is over, which wears the heavier outline of the two.</summary>
    private SvgExpressionField? _aimed;

    public SvgViewerElementPanel(
        Func<string> text,
        Func<string> declarations,
        Func<string, Func<SvgSourceDocument, string?>, string?> write,
        Func<ExprEvaluator?> values)
    {
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _declarations = declarations ?? throw new ArgumentNullException(nameof(declarations));
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _values = values ?? throw new ArgumentNullException(nameof(values));

        _scroll = new ScrollViewer { Content = _rows };

        Resources.MergedDictionaries.Add(new ResourceInclude(Home)
        {
            Source = new Uri("avares://Svg.Viewer.Skia.Avalonia/SvgExpressionBox.axaml")
        });

        // Fluent draws a section as a bordered card, which around every few rows reads as a form
        // inside a form.
        Resources["ExpanderHeaderBackground"] = Brushes.Transparent;
        Resources["ExpanderContentBackground"] = Brushes.Transparent;
        Resources["ExpanderHeaderBorderThickness"] = new Thickness(0);
        Resources["ExpanderContentDownBorderThickness"] = new Thickness(0);
        Resources["ExpanderHeaderPadding"] = new Thickness(0);
        Resources["ExpanderContentPadding"] = new Thickness(0, 4, 0, 8);
        Resources["ExpanderMinHeight"] = 28d;

        _all.IsChecked = s_all;
        _all.IsCheckedChanged += (_, _) => ShowsAll = _all.IsChecked == true;
        ToolTip.SetTip(_all, "List every attribute this element can take, not only the usual ones.");

        var heading = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            Margin = new Thickness(10, 8, 10, 0)
        };

        var badge = new Border
        {
            Child = _badge,
            Classes = { "badge" },
            Padding = new Thickness(5, 1),
            Margin = new Thickness(0, 0, 6, 0),
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center
        };

        badge[!Border.BorderBrushProperty] =
            new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("TextControlBorderBrush");

        heading.Children.Add(badge);
        Grid.SetColumn(_title, 1);
        heading.Children.Add(_title);
        Grid.SetColumn(_all, 2);
        heading.Children.Add(_all);
        Grid.SetRow(_search, 1);
        Grid.SetColumnSpan(_search, 3);
        heading.Children.Add(_search);

        _search.TextChanged += (_, _) => Filter = _search.Text ?? string.Empty;

        _body.Children.Add(heading);
        DockPanel.SetDock(heading, Dock.Top);
        _body.Children.Add(_scroll);

        var panel = new DockPanel();

        panel.Children.Add(_fault);
        DockPanel.SetDock(_fault, Dock.Bottom);
        panel.Children.Add(_host);

        Content = panel;

        DragDrop.SetAllowDrop(this, true);

        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => Release());
        AddHandler(DragDrop.DropEvent, OnDrop);

        // The boxes moved under a still pointer.
        AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => Aim(null));

        // Marked on the way down, before the focus it moves: pressing a swatch just after typing
        // leaves the box, whose write arrives while the press is still going on.
        AddHandler(PointerPressedEvent, (_, _) => _pressing = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(
            PointerReleasedEvent,
            (_, _) =>
            {
                _pressing = false;
                Owed();
            },
            handledEventsToo: true);
        AddHandler(LostFocusEvent, (_, _) => Owed(), handledEventsToo: true);

        Rebuild();
    }

    /// <summary>The attributes on show, in the order they are listed.</summary>
    public IReadOnlyList<string> Attributes => _shown.Where(row => row.Row.IsVisible).Select(row => row.Name).ToList();

    /// <summary>What the rows are searched for: a row is listed where its label or attribute holds it.</summary>
    /// <remarks>Searching lists every attribute the element can take, as <see cref="ShowsAll"/> does, without turning that on.</remarks>
    public string Filter
    {
        get => _filter;
        set
        {
            value ??= string.Empty;

            var was = _filter.Length > 0;

            _filter = value;

            if (!string.Equals(_search.Text ?? string.Empty, value, StringComparison.Ordinal))
            {
                _search.Text = value;
            }

            // Rebuilt as a search starts or ends, Show all on or off: a search draws its sections
            // open, and only a rebuild puts them back as they were folded.
            if (was != _filter.Length > 0 || _listedAll != Listing)
            {
                Refresh();
            }
            else
            {
                Sift();
            }
        }
    }

    /// <summary>Whether a box here has the keyboard and says something other than what it was drawn with.</summary>
    public bool IsPending => Typing() is { };

    /// <summary>Writes what the box being typed in says, before something else acts on the drawing.</summary>
    /// <returns>Whether it was written; false where nothing was typed, it was refused, or it still has trouble.</returns>
    public bool CommitPending() => !_writing && Typing() is { } line && Commit(line.Box, line.Name, line.Address);

    private Line? Typing()
        => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is SvgExpressionField { IsPending: true } box
            ? _shown.FirstOrDefault(line => ReferenceEquals(line.Box, box))
            : null;

    /// <summary>Why the last edit was refused, or null.</summary>
    public string? Fault { get; private set; }

    /// <summary>Whether every attribute the element can take is listed, rather than the usual ones.</summary>
    /// <remarks>One setting for every panel, for the reason <see cref="s_all"/> is static.</remarks>
    public bool ShowsAll
    {
        get => s_all;
        set
        {
            s_all = value;

            // Against what this panel lists rather than the setting, which another panel may have
            // changed since this one was drawn.
            if (_listedAll != Listing)
            {
                Refresh();
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>Catching up with a toggle another panel made while this one was out of sight.</remarks>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (_listedAll != Listing)
        {
            Refresh();
        }
    }

    /// <summary>Says again what each row's expression comes to, for a host whose values have moved.</summary>
    /// <remarks>Without rebuilding anything, so it is safe while a row is being typed in or pressed.</remarks>
    public void Readouts()
    {
        foreach (var row in _shown)
        {
            Says(row);
        }
    }

    /// <summary>What the box for <paramref name="name"/> says, or null when there is no such row.</summary>
    public string? Shown(string name)
        => _shown.FirstOrDefault(row => string.Equals(row.Name, name, StringComparison.Ordinal)) is { Box: { } box }
            ? box.Text
            : null;

    /// <summary>Writes <paramref name="value"/> into <paramref name="name"/>, or takes it away.</summary>
    /// <remarks>Taking the name rather than a row, so everything but the pointer can be driven.</remarks>
    public bool Set(string name, string? value) => _address is { } address && Set(address, name, value);

    private bool Set(string address, string name, string? value)
    {
        // Content is not trimmed. Under xml:space="preserve" a leading space is a value, and
        // trimming would rewrite a file for somebody who opened a box and left it without typing.
        var written = IsText(name) ? value : value?.Trim();

        if (Trouble(name, written ?? string.Empty) is { } trouble)
        {
            Say(trouble);

            return false;
        }

        return Write(address, name, written is { Length: > 0 } ? written : null);
    }

    /// <summary>Shows the element at <paramref name="addressKey"/>, or says nothing is picked.</summary>
    public void Show(string? addressKey)
    {
        if (string.Equals(_address, addressKey, StringComparison.Ordinal))
        {
            Refresh();

            return;
        }

        // What is typed goes to the element it was typed for, before its rows go. Its write's own
        // refresh may have shown the new element already.
        CommitPending();

        if (string.Equals(_address, addressKey, StringComparison.Ordinal))
        {
            return;
        }

        // Another element's rows are wrong whoever is holding them.
        _address = addressKey;

        Rebuild();
    }

    /// <summary>Reads the element again, for a host whose text has changed underneath it.</summary>
    public void Refresh()
    {
        // Not under somebody's caret or pointer. Rebuilding takes the box being typed in, or the
        // control being pressed, out of the tree — and a drawing is rebuilt on every keystroke.
        // Pressing a swatch just after typing leaves the box, which writes and lands here while
        // the press is still going on.
        if (Held())
        {
            _stale = true;

            // The rows already drawn still show what a search asks for.
            Sift();

            return;
        }

        Rebuild();
    }

    /// <summary>Rebuilds a refresh that was put off, once nothing is holding the rows.</summary>
    /// <remarks>Posted, so focus has landed wherever it is going before it is asked where that is.</remarks>
    private void Owed()
        => Dispatcher.UIThread.Post(() =>
        {
            if (_stale && !Held())
            {
                Rebuild();
            }
        });

    private void Rebuild()
    {
        // The keyboard goes back to the same control in the new rows, so a rebuild between two
        // presses of Tab is not the end of somebody's place.
        if (Keyboard() is { } kept)
        {
            Dispatcher.UIThread.Post(
                () => _rows.GetVisualDescendants().OfType<Control>().FirstOrDefault(again => Kind(again) == kept)?.Focus(),
                DispatcherPriority.Loaded);
        }

        _stale = false;
        _shown.Clear();
        _sections.Clear();
        _rows.Children.Clear();
        _listedAll = Listing;

        if (_address is not { } address)
        {
            _note.Text = "Pick an element to see what it is written with.";
            _host.Content = _note;

            return;
        }

        var open = Open();

        if (open is null || SvgAttributeEditor.ElementName(open, address) is not { } element)
        {
            // In the drawing but not in the file: the declarations block a recipe injected is the
            // one of these anybody meets.
            _note.Text = "This element is not written in the file, so it has nothing to edit.";
            _host.Content = _note;

            return;
        }

        _element = element;
        _clipped = SvgAttributeEditor.IsClipped(open, address);
        _basis = Now();

        var id = SvgAttributeEditor.Attribute(open, address, "id");

        _badge.Text = element;
        ToolTip.SetTip(_badge, SvgViewerAttributes.Title(element));
        _title.Text = id is { Length: > 0 } ? "#" + id : SvgViewerAttributes.Title(element);
        _title.Opacity = id is { Length: > 0 } ? 1 : 0.7;
        ToolTip.SetTip(_title, id is { Length: > 0 } ? "The element's id. Change it in the ID row below; what points at it follows." : $"<{element}>");
        _all.IsChecked = s_all;

        // First and outside the sections, because it is what the element says rather than how it is
        // said: somebody who picked a <text> came for the words far more often than for its spacing.
        if (SvgAttributeEditor.Content(open, address, out var missing) is { } content)
        {
            _rows.Children.Add(Row(SvgExpressionAttributes.ContentName, content, content.Length > 0, element, null, null));
        }
        else if (missing is { })
        {
            _rows.Children.Add(Told(missing));
        }

        var rows = SvgAttributeEditor.Attributes(open, address)
            .Select(attribute => (attribute.Name, attribute.Value, Set: true))
            .ToList();

        // By the name without its prefix, so an xlink:href the file writes is not offered an href
        // beside it — writing both is how a drawing comes to point two ways at once.
        var listed = new HashSet<string>(rows.Select(row => Unprefixed(row.Name)), StringComparer.Ordinal);

        foreach (var name in SvgViewerAttributes.Usual(element)
                     .Concat(Listing ? SvgElementNames.AttributesOf(element) : Array.Empty<string>()))
        {
            if (listed.Add(Unprefixed(name)))
            {
                rows.Add((name, string.Empty, false));
            }
        }

        foreach (var section in rows
                     .GroupBy(row => SvgViewerAttributes.Find(row.Name).Group)
                     .OrderBy(group => group.Key))
        {
            var body = new StackPanel { Spacing = 10 };
            var set = 0;
            var first = _shown.Count;

            foreach (var row in section.OrderBy(row => SvgViewerAttributes.Find(row.Name).Rank))
            {
                var styled = SvgAttributeEditor.Styled(open, address, row.Name);
                var inherited = row.Set || styled is { } ? null : SvgAttributeEditor.Driven(open, address, row.Name);

                body.Children.Add(Row(row.Name, row.Value, row.Set, element, styled, inherited));
                set += row.Set || styled is { } ? 1 : 0;
            }

            var expander = Section(section.Key, set, body, _filter.Length > 0);

            _sections.Add((expander, _shown.GetRange(first, _shown.Count - first)));
            _rows.Children.Add(expander);
        }

        _rows.Children.Add(_nothing);
        _host.Content = _body;

        Sift();
    }

    /// <summary>Shows the rows a search finds, and the sections holding them; all of them while nothing is searched.</summary>
    private void Sift()
    {
        foreach (var line in _shown)
        {
            line.Row.IsVisible = Matches(line);
        }

        foreach (var (section, lines) in _sections)
        {
            section.IsVisible = lines.Any(line => line.Row.IsVisible);
        }

        _nothing.IsVisible = _filter.Length > 0 && !_shown.Any(line => line.Row.IsVisible);
    }

    private bool Matches(Line line)
        => _filter.Length == 0
           || line.Name.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0
           || (IsText(line.Name) ? "Text" : SvgViewerAttributes.Find(line.Name).Label).IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0;

    private (string Text, object? Step, string? Element) Now() => (_text(), UndoStep?.Invoke(), _element);

    /// <summary>Whether the drawing is still the one the rows were drawn from, or has only moved on by edits built on it.</summary>
    /// <remarks>
    /// The element at the row's address is asked to be the same kind as well, which is the cheap check
    /// against an address that is a path of indexes and could now name another element.
    /// </remarks>
    private bool Unmoved(SvgSourceDocument open, string address)
        => _basis is not { } basis
           || string.Equals(_text(), basis.Text, StringComparison.Ordinal)
           || basis.Step is { } step
           && Holds?.Invoke(step) == true
           && string.Equals(SvgAttributeEditor.ElementName(open, address), basis.Element, StringComparison.Ordinal);

    /// <summary>One collapsible section of rows, which stays the way somebody left it.</summary>
    /// <param name="searching">Opened, and left out of what is remembered, while a search is showing it.</param>
    private static Expander Section(SvgViewerAttributeGroup group, int set, Control rows, bool searching)
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        header.Children.Add(new TextBlock { Text = group.ToString(), FontWeight = FontWeight.SemiBold, FontSize = 12 });

        if (set > 0)
        {
            var count = new TextBlock { Text = $"{set} set", Opacity = 0.5, FontSize = 11, Margin = new Thickness(8, 0, 0, 0) };

            Grid.SetColumn(count, 1);
            header.Children.Add(count);
        }

        var section = new Expander
        {
            Header = header,
            Content = rows,
            Tag = group.ToString(),
            IsExpanded = searching || !s_folded.Contains(group),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        if (searching)
        {
            return section;
        }

        // After IsExpanded is set, so reading the setting back is not taken for somebody changing it.
        section.Expanded += (_, _) => s_folded.Remove(group);
        section.Collapsed += (_, _) => s_folded.Add(group);

        return section;
    }

    private static string Unprefixed(string name) => name.Substring(name.IndexOf(':') + 1);

    /// <param name="styled">What the element's style attribute sets this to instead, or null.</param>
    /// <param name="inherited">The expression a group around the element drives this with, where nothing on it sets it.</param>
    private Control Row(string name, string value, bool set, string element, string? styled, string? inherited)
    {
        var about = SvgViewerAttributes.Find(name);

        // The row's own, and not whatever is picked by the time a posted write runs.
        var address = _address!;

        // A style declaration wins over the attribute, so a row it sets shows and writes the
        // declaration; not geometry, whose attribute grammar no browser reads in a style.
        var fixedInStyle = styled is { } && SvgAttributeEditor.IsGeometry(name);
        var inStyle = styled is { } && !fixedInStyle;
        var shown = inStyle ? Unimportant(styled!) : value;

        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };

        var label = new TextBlock
        {
            Text = IsText(name) ? "Text" : about.Label,
            FontSize = 12,
            FontWeight = set || inStyle ? FontWeight.SemiBold : FontWeight.Normal,
            Opacity = set || inStyle ? 1 : 0.7,
            VerticalAlignment = VerticalAlignment.Center
        };

        ToolTip.SetTip(label, IsText(name) ? "The words between the element's tags" : name);
        heading.Children.Add(label);

        // What an expression here would have to come to. A row that takes none says nothing, and
        // braces typed into it are refused with the reason the moment they are typed.
        var note = Typed(name) is { } type
            ? ExprFunctions.Describe(type)
              + (SvgExpressionAttributes.IsInArguments(name) ? " per argument" : string.Empty)
              + (SvgExpressionAttributes.IsResolvedBeforeRecording(name) ? ", built in" : string.Empty)
            : null;

        if (inStyle)
        {
            note = note is { } ? note + " · in style" : "in style";
        }

        if (note is { })
        {
            var kind = new TextBlock
            {
                Text = note,
                Opacity = 0.45,
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(kind, 1);
            heading.Children.Add(kind);
        }

        var box = new SvgExpressionField
        {
            Text = shown,
            Value = shown,
            Watermark = fixedInStyle ? $"{styled} in style"
                : inherited is { } ? $"{inherited} inherited"
                : IsText(name) ? "no text"
                : "not set",
            FontSize = 12,
            Tag = name
        };

        if (fixedInStyle)
        {
            box.IsReadOnly = true;
            ToolTip.SetTip(box, "Set in this element's style attribute, which wins over the attribute. Change it under Inline style.");
        }
        else if (inStyle)
        {
            ToolTip.SetTip(box, "Set in this element's style attribute, which wins over the attribute. What is typed here is written there.");
        }

        if (this.TryFindResource("SvgExpressionBox", ActualThemeVariant, out var theme) && theme is ControlTheme box_)
        {
            box.Theme = box_;
        }

        // Under the box rather than beside it, where a whole evaluated transform took the width a
        // narrow pane had for the box.
        var readout = new TextBlock
        {
            Opacity = 0.55,
            FontSize = 11,
            FontFamily = new FontFamily("Menlo, Consolas, monospace"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsVisible = false
        };

        var trouble = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 0),
            IsVisible = false
        };

        // The themed brush rather than the dark-theme red it used to be written as, which is the
        // same red and unreadable on a light ground. Every other line saying something is wrong
        // already goes through this key.
        trouble[!TextBlock.ForegroundProperty] =
            new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SvgViewerSourceErrorBrush");

        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        Grid.SetColumn(box, 1);
        line.Children.Add(box);

        var lines = new StackPanel { Spacing = 3, Children = { heading, line } };

        Action<string, ExprValue?>? follow = null;

        void Put(string text) => Commit(box, name, address, text);

        if (!IsText(name) && Controlled(element) && !fixedInStyle)
        {
            follow = about.Control switch
            {
                SvgViewerAttributeControl.Colour => Chip(line, box, Put, inherited),
                SvgViewerAttributeControl.Fraction => Fraction(lines, box, name, Put),
                _ => null
            };

            if (about.Choices.Count > 0)
            {
                Choices(line, name, Put, about.Choices);
            }

            (box.Step, box.Minimum, box.Maximum) = about.Control switch
            {
                SvgViewerAttributeControl.Number => (1d, double.NegativeInfinity, double.PositiveInfinity),
                SvgViewerAttributeControl.Fraction => (0.1, 0d, 1d),
                _ => (0d, double.NegativeInfinity, double.PositiveInfinity)
            };
            box.PercentIsFraction = about.Control == SvgViewerAttributeControl.Fraction;
        }

        lines.Children.Add(readout);
        lines.Children.Add(trouble);

        var row = new Line(name, address, box, readout, trouble, follow, lines, styled is { }, inherited);

        _shown.Add(row);

        // Not for a row its style paints: removing it would take the declaration too, which the row
        // says is changed under Inline style.
        if (set && styled is null)
        {
            // Not the edit class: every edit button in the viewer is visible, and this one is not
            // always.
            var reset = new Button
            {
                Content = "×",
                Classes = { "reset" },
                Tag = name,
                Padding = new Thickness(6, 0),
                MinHeight = 0,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center
            };

            ToolTip.SetTip(reset, IsText(name) ? "Clear the text" : $"Remove {name}");
            reset.Click += (_, _) => Put(string.Empty);

            Grid.SetColumn(reset, 2);
            heading.Children.Add(reset);
        }

        box.TextChanged += (_, _) => Says(row);

        box.LostFocus += (_, _) => Commit(box, name, address);

        box.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Return))
            {
                return;
            }

            // Not handled: the viewer takes the keyboard off the box next, and leaving commits again,
            // which is a no-op once the text matches the file.
            Commit(box, name, address);
        };

        Says(row);

        return lines;
    }

    /// <summary>A style declaration's value without its <c>!important</c>, which a write into it keeps.</summary>
    private static string Unimportant(string styled)
        => Regex.Replace(styled, @"\s*!\s*important\s*$", string.Empty, RegexOptions.IgnoreCase);

    /// <summary>Whether a row on <paramref name="element"/> may offer a control beside its box.</summary>
    /// <remarks>
    /// Not on an animation or a transfer function, where the names mean something else:
    /// <c>fill="freeze"</c> is not a paint and an <c>&lt;feFuncA&gt;</c>'s <c>offset</c> is not a stop's.
    /// </remarks>
    private static bool Controlled(string element)
        => element != "set"
           && !element.StartsWith("animate", StringComparison.Ordinal)
           && !element.StartsWith("feFunc", StringComparison.Ordinal);

    /// <summary>A paint chip before the box that opens a colour picker, answering what the box says.</summary>
    /// <param name="inherited">The expression a group around it drives the paint with, where the element writes none.</param>
    private Action<string, ExprValue?> Chip(Grid line, SvgExpressionField box, Action<string> put, string? inherited)
    {
        var chip = new SvgPaintChip { Tag = box.Tag, Margin = new Thickness(0, 0, 6, 0) };

        chip.Flyout!.Opened += (_, _) => _picking++;

        // Written once, when the picker goes: every write rebuilds the rows and is one more step to undo.
        chip.Flyout.Closed += (_, _) =>
        {
            _picking--;
            Owed();
        };

        chip.Previewed += (_, paint) => box.Text = paint;
        chip.Picked += (_, paint) => put(paint);

        Grid.SetColumn(chip, 0);
        line.Children.Add(chip);

        return (written, value) =>
        {
            chip.Paint = written;
            chip.IsDriven = inherited is { };
            chip.DrivenColor = value is { Type: ExprType.Color } ? SvgViewerParameterFactory.ToColor(value) : null;
        };
    }

    /// <summary>A slider under the box for a value from 0 to 1, answering what the box says.</summary>
    private static Action<string, ExprValue?> Fraction(StackPanel lines, SvgExpressionField box, string name, Action<string> put)
    {
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            SmallChange = 0.01,
            LargeChange = 0.1,
            Tag = name,
            Margin = new Thickness(0, -6, 0, -10)
        };

        // Which way a change is going, so the box answering the slider does not move it back.
        var answering = false;

        slider.ValueChanged += (_, e) =>
        {
            if (answering)
            {
                return;
            }

            answering = true;
            box.Text = e.NewValue.ToString("0.##", CultureInfo.InvariantCulture);
            answering = false;
        };

        // On release and not per tick, for the reason the picker writes when it closes.
        slider.AddHandler(PointerReleasedEvent, (_, _) => put(box.Text ?? string.Empty), handledEventsToo: true);
        slider.LostFocus += (_, _) => put(box.Text ?? string.Empty);

        ToolTip.SetShowOnDisabled(slider, true);
        lines.Children.Add(slider);

        return (written, value) =>
        {
            var bound = SvgExpressionAttributes.TryUnwrap(written, out _);

            slider.IsEnabled = !bound;
            slider.Opacity = written.Length == 0 ? 0.4 : 1;
            ToolTip.SetTip(slider, bound ? "Follows the expression. Type a number into the box to set one instead." : null);

            if (answering)
            {
                return;
            }

            answering = true;

            // An unset opacity is 1 and an unset stop offset 0, which is where the slider sits.
            slider.Value = bound
                ? value is { Type: ExprType.Number } number ? number.AsNumber : slider.Value
                : FractionOf(written) ?? (name == "offset" ? 0 : 1);

            answering = false;
        };
    }

    /// <summary>A written fraction, <c>0.5</c> or <c>50%</c>, or null where it is not one.</summary>
    private static double? FractionOf(string written)
    {
        var percent = written.EndsWith("%", StringComparison.Ordinal);

        return double.TryParse(
                percent ? written.Substring(0, written.Length - 1) : written,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number)
            ? Math.Clamp(percent ? number / 100 : number, 0, 1)
            : null;
    }

    /// <summary>A button after the box listing the values SVG spells for this attribute.</summary>
    private void Choices(Grid line, string name, Action<string> put, IReadOnlyList<string> choices)
    {
        var pick = new Button
        {
            Content = "▾",
            Classes = { "choices" },
            Tag = name,
            Padding = new Thickness(6, 0),
            MinHeight = 0,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Stretch
        };

        // A list the menu watches, not its Items: the popup reads Items once, when it is made, and
        // that is before Opening — filled there, the menu opened as an empty sliver.
        var offered = new ObservableCollection<MenuItem>();
        var menu = new MenuFlyout
        {
            Placement = PlacementMode.BottomEdgeAlignedRight,
            ItemsSource = offered
        };

        pick.Flyout = menu;
        ToolTip.SetTip(pick, "Choose a value");

        menu.Opened += (_, _) => _picking++;
        menu.Closed += (_, _) =>
        {
            _picking--;
            Owed();
        };

        // Filled as it opens, so a url(#…) names what the drawing holds now. Picking one on a row
        // an expression drives is how that row is let go of, so this is never disabled.
        menu.Opening += (_, _) =>
        {
            offered.Clear();

            foreach (var choice in Expanded(choices))
            {
                // A TextBlock and not the string, which a menu reads for an access key: the first
                // underscore of paint0_linear_1 would vanish, and two ids could read the same.
                var item = new MenuItem { Header = new TextBlock { Text = choice } };

                item.Click += (_, _) => put(choice);
                offered.Add(item);
            }

            if (name is "clip-path" or "mask" && NewClipRequested is { } make && Clippable(name))
            {
                var item = new MenuItem { Header = new TextBlock { Text = name == "mask" ? "New mask" : "New clip path" } };

                item.Click += (_, _) => make(name);
                offered.Add(item);
            }
        };

        Grid.SetColumn(pick, 2);
        line.Children.Add(pick);
    }

    /// <summary>The choices with each <c>#tag</c> spelt out as a reference to every such element with an id, and <c>@fonts</c> as the families to hand.</summary>
    private IEnumerable<string> Expanded(IReadOnlyList<string> choices)
    {
        var document = Open()?.Document;

        foreach (var choice in choices)
        {
            if (choice == "@fonts")
            {
                var families = (Typefaces?.Invoke() ?? Enumerable.Empty<ITypefaceProvider>())
                    .OfType<CustomTypefaceProvider>()
                    .Select(face => face.FamilyName)
                    .Where(family => family.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                foreach (var family in families.Count > 0 ? families : (IEnumerable<string>)s_genericFamilies)
                {
                    yield return family;
                }

                continue;
            }

            if (!choice.StartsWith("#", StringComparison.Ordinal))
            {
                yield return choice;

                continue;
            }

            foreach (var element in document?.Descendants() ?? Enumerable.Empty<System.Xml.Linq.XElement>())
            {
                if (element.Name.LocalName == choice.Substring(1) && (string?)element.Attribute("id") is { Length: > 0 } id)
                {
                    yield return $"url(#{id})";
                }
            }
        }
    }

    /// <summary>Puts <paramref name="text"/> in a row's box and writes it, as though it had been typed.</summary>
    /// <remarks>
    /// Posted, because writing rebuilds every row, and the control whose handler asked for this is
    /// still inside that handler. A second write of the same text is a no-op in the commit below.
    /// </remarks>
    private void Commit(SvgExpressionField box, string name, string address, string text)
    {
        box.Text = text;

        Dispatcher.UIThread.Post(() => Commit(box, name, address));
    }

    /// <summary>Writes what a row's box says, where it says something new.</summary>
    /// <returns>Whether the file now says what the box does.</returns>
    private bool Commit(SvgExpressionField box, string name, string address)
    {
        var content = IsText(name);
        var written = content ? box.Text ?? string.Empty : box.Text?.Trim() ?? string.Empty;
        var drawn = box.Value ?? string.Empty;

        // Against what the box was drawn with as well as the file. Against the file alone, a box
        // left behind by a refresh put off, the file undone under it, would write itself back.
        if (string.Equals(written, content ? drawn : drawn.Trim(), StringComparison.Ordinal))
        {
            // A space typed after the value is nothing to write, and nothing left pending.
            box.Value = box.Text;

            return true;
        }

        var open = Open();
        var had = Written(open, address, name);

        // Before the drawing is asked whether it moved, so the panel's own write, refreshed back
        // into it, is never refused.
        if (string.Equals(written, had, StringComparison.Ordinal))
        {
            box.Value = written;

            return true;
        }

        // Said on the row already. The box keeps what was typed, so it can be finished.
        if (Trouble(name, written) is { })
        {
            return false;
        }

        if (open is { } && !Unmoved(open, address))
        {
            Say($"The drawing changed while {(content ? "the text" : $"'{name}'")} was being typed, so it was not written.");

            // Put back first, so the box's leaving as the rows go writes nothing. Rebuilt held or not:
            // what is typed is refused either way, and the keyboard goes back to the same row.
            box.Text = box.Value;
            Rebuild();

            return false;
        }

        if (!Set(address, name, written))
        {
            box.Text = had;

            return false;
        }

        box.Value = written;

        return true;
    }

    /// <summary>What the file says for a row: its text, the style declaration that paints it, or its attribute.</summary>
    private static string Written(SvgSourceDocument? open, string address, string name)
    {
        if (open is null)
        {
            return string.Empty;
        }

        if (IsText(name))
        {
            return SvgAttributeEditor.Content(open, address, out _) ?? string.Empty;
        }

        return SvgAttributeEditor.Styled(open, address, name) is { } styled && !SvgAttributeEditor.IsGeometry(name)
            ? Unimportant(styled)
            : SvgAttributeEditor.Attribute(open, address, name) ?? string.Empty;
    }

    /// <summary>Fills one row's trouble and readout from what its box currently says, or from the group driving it.</summary>
    private void Says(Line row)
    {
        var written = row.Box.Text?.Trim() ?? string.Empty;
        var shown = written.Length == 0 && row.Inherited is { } inherited ? inherited : written;
        var trouble = Trouble(row.Name, written);

        row.Trouble.Text = trouble;
        row.Trouble.IsVisible = trouble is { };

        row.Box.IsDriven = SvgExpressionAttributes.TryUnwrap(shown, out _)
                           || SvgExpressionAttributes.IsInArguments(row.Name) && SvgTransformExpression.Parse(row.Name, shown).Any;

        var value = trouble is null ? Value(shown) : null;
        var readout = trouble is null ? Readout(row.Name, shown, value) : string.Empty;

        row.Readout.Text = readout;
        row.Readout.IsVisible = readout.Length > 0;

        row.Follow?.Invoke(written, value);
    }

    /// <summary>
    /// What is wrong with what a row says, or null.
    /// </summary>
    /// <remarks>
    /// Only an expression is judged here. Anything else is the file's own value and the parser
    /// answers for it — a bad colour is a diagnostic on the drawing, not a refusal to write what
    /// somebody typed.
    /// </remarks>
    private string? Trouble(string name, string written)
    {
        // Checked as it is typed rather than when the project fails to export.
        if (name == BoxRow && written.Trim() is { Length: > 0 } box)
        {
            return SvgExpressionAttributes.WhyNotBox(box, ClassName?.Invoke(), OtherBoxes());
        }

        // A value the use gives the drawing it calls, of whatever type that drawing's parameter is, or
        // a gradient's list: anything but braces does nothing.
        if (name.StartsWith(ArgumentRow, StringComparison.Ordinal) || SvgExpressionAttributes.ListTypeFor(name) is { })
        {
            return written.Trim().Length == 0
                ? null
                : !SvgExpressionAttributes.TryUnwrap(written, out var braced)
                    ? $"'{name}' is written as one expression in double braces."
                    : Typed(name) is { } listed ? Checked(braced, listed) : null;
        }

        if (SvgExpressionAttributes.IsInArguments(name))
        {
            // Braces that are not one whole argument drive nothing, which the row has to say before
            // the arguments it could read are judged.
            if (SvgExpressionAttributes.WhyUnsupported(name, written, _element, _clipped) is { } stray)
            {
                return stray;
            }

            foreach (var function in SvgTransformExpression.Parse(name, written).Functions)
            {
                foreach (var argument in function.Arguments)
                {
                    if (argument.Expression is { } code && Checked(code, ExprType.Number) is { } fault)
                    {
                        return fault;
                    }
                }
            }

            return null;
        }

        if (!SvgExpressionAttributes.TryUnwrap(written, out var expression))
        {
            return null;
        }

        if (Typed(name) is not { } type)
        {
            return SvgExpressionAttributes.WhyUnsupported(name, null, _element, _clipped);
        }

        return Checked(expression, type);
    }

    /// <summary>What is wrong with one expression asked to come to <paramref name="type"/>, or null.</summary>
    private string? Checked(string expression, ExprType type)
    {
        var declarations = SvgExpressionDeclarations.Parse(_declarations(), out var diagnostics);

        if (diagnostics.Count > 0)
        {
            return diagnostics[0].Message;
        }

        try
        {
            ExprChecker.For(declarations).CheckAs(expression, type, ExprFunctions.DescribeUse(type));

            return null;
        }
        catch (ExprException failure)
        {
            return failure.Message;
        }
    }

    /// <summary>What the expression a box holds comes to, or null where it holds none or it fails.</summary>
    private ExprValue? Value(string written)
    {
        if (!SvgExpressionAttributes.TryUnwrap(written, out var expression) || _values() is not { } evaluator)
        {
            return null;
        }

        try
        {
            return evaluator.Evaluate(expression);
        }
        catch (Exception failure) when (failure is ExprException or ArgumentException)
        {
            return null;
        }
    }

    private string Readout(string name, string written, ExprValue? value)
    {
        if (!SvgExpressionAttributes.IsInArguments(name))
        {
            return value is { } known
                ? $"{ExprFunctions.Describe(known.Type)}  {SvgViewerParameterFactory.Describe(known)}"
                : string.Empty;
        }

        if (_values() is not { } evaluator)
        {
            return string.Empty;
        }

        try
        {
            var arguments = SvgTransformExpression.Parse(name, written);

            // The whole transform as it currently stands, since one argument's number says
            // nothing about where the shape ends up.
            return arguments.Any
                ? arguments.With(argument =>
                    SvgViewerParameterFactory.Describe(evaluator.Evaluate(argument.Expression!)))
                : string.Empty;
        }
        catch (Exception failure) when (failure is ExprException or ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>The drawing as a tree, or null while its text will not read back.</summary>
    private SvgSourceDocument? Open() => SvgSourceDocument.Read(_text(), out _);

    /// <summary>The drawing's boxes other than this element's, leaving out copies under <c>&lt;defs&gt;</c> as a build does.</summary>
    private List<string> OtherBoxes()
    {
        if (Open() is not { } source || _address is not { } address)
        {
            return new List<string>();
        }

        var marked = XNamespace.Get(SvgExpressionAttributes.Namespace) + SvgExpressionAttributes.Bounds;
        var names = source.Document.Descendants()
            .Where(element => !element.Ancestors().Any(ancestor => ancestor.Name.LocalName == "defs"))
            .Select(element => (string?)element.Attribute(marked))
            .OfType<string>()
            .ToList();

        if (SvgAttributeEditor.Attribute(source, address, BoxRow) is { } own)
        {
            names.Remove(own);
        }

        return names;
    }

    /// <summary>
    /// Writes one attribute of the element, through whatever is holding the drawing.
    /// </summary>
    /// <remarks>
    /// The host is handed the edit rather than the text it comes to, because the two hosts this
    /// panel serves keep their drawing in different places and only they know where. A prefix is
    /// part of an attribute's name, so this is the one path that can tell xlink:href from href.
    /// </remarks>
    private bool Write(string address, string name, string? value)
    {
        string? refusal;

        _writing = true;

        try
        {
            refusal = IsText(name)
                ? _write(
                    value is null ? "clear text" : "set text",
                    source => SvgAttributeEditor.SetContent(source, address, value))
                : string.Equals(name.Trim(), "id", StringComparison.Ordinal)
                ? _write(
                    string.IsNullOrWhiteSpace(value) ? "remove id" : $"rename to #{value!.Trim()}",
                    source => SvgElementEditor.Rename(source, address, value, out _))
                : _write(
                    value is null ? $"remove {name}" : $"set {name}",
                    source => SvgAttributeEditor.SetAttribute(source, address, name, value));
        }
        finally
        {
            _writing = false;
        }

        Say(refusal);

        // The panel's own write moves the state its rows are measured against on.
        if (refusal is null)
        {
            _basis = Now();
        }

        return refusal is null;
    }

    /// <summary>Whether this row is the element's text rather than one of its attributes.</summary>
    private static bool IsText(string name)
        => string.Equals(name, SvgExpressionAttributes.ContentName, StringComparison.Ordinal);

    /// <summary>
    /// What an expression in <paramref name="name"/> has to come to, text included.
    /// </summary>
    /// <remarks>
    /// The content key is answered here rather than put in the placeholder table, which is keyed by
    /// attribute local name and read by the recipes, the element factory and the list of names a row
    /// can be offered for — none of which may grow a name no attribute can have. The substitution
    /// already special-cases the same name the same way.
    /// </remarks>
    private ExprType? Typed(string name)
        => IsText(name)
            ? SvgExpressionAttributes.ContentType
            : SvgExpressionAttributes.ListTypeFor(name)
              ?? (SvgExpressionAttributes.WhyUnsupported(name, null, _element, _clipped) is null ? SvgExpressionAttributes.TypeFor(name) : null);

    /// <summary>A line saying why an element's text is not one value to edit.</summary>
    private static Control Told(string why)
        => new TextBlock
        {
            Text = why,
            Opacity = 0.6,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        };

    private void Say(string? refusal)
    {
        Fault = refusal;

        _fault.Text = refusal;
        _fault.IsVisible = refusal is { };
        _fault[!TextBlock.ForegroundProperty] =
            new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SvgViewerSourceErrorBrush");
    }

    // ---- a variable or a tree row dropped on a row ---------------------------------------------------

    /// <summary>The rows a variable or a tree row being dragged over the panel would be written into.</summary>
    /// <remarks>Empty while nothing is being carried. Named rather than drawn, for the reason
    /// <see cref="Set"/> takes a name: everything but the pointer can be driven.</remarks>
    public IReadOnlyList<string> Offered
        => _shown.Where(row => _takes.Contains(row.Name)).Select(row => row.Name).ToList();

    /// <summary>
    /// Offers every row a dragged variable could be written into, or that a dragged tree row could be
    /// applied as where a host wires that, and takes the drag while over one.
    /// </summary>
    /// <remarks>
    /// Marked handled either way. The viewer this usually sits in turns away any drag carrying no
    /// files, which is every drag of a variable — left unhandled, not one of them could be made at
    /// all. Where the pointer is over no row that would take it the drag is still this panel's; it
    /// says so by offering nothing rather than by letting something else answer.
    /// </remarks>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (SvgViewerVariableDrag.Carried(e) is { } name)
        {
            var written = SvgViewerVariableDrag.Bound(name);

            Offer(written, row => Trouble(row, written) is null);
        }
        else if (ClipRequested is { } && SvgViewerElementTree.Carried(e) is { Applies.Count: > 0 } row)
        {
            Offer(row.Key, property => row.Applies.Contains(property) && Clippable(property) && !_shown.Any(line => line.Name == property && line.Styled));
        }
        else
        {
            Release();

            return;
        }

        var over = Under(e);

        Aim(over);

        e.DragEffects = over is { } ? SvgViewerElementTree.Applying(e) : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Whether the element shown can be given <paramref name="property"/>, as the file would let it.</summary>
    /// <remarks>Never the drawing itself, which has no place of its own to cover or carry a shape into.</remarks>
    private bool Clippable(string property)
        => _address is { Length: > 0 } address
           && Open() is { } open
           && SvgAttributeEditor.ElementName(open, address) is { } element
           && SvgElementEditor.Clippable(element, property);

    /// <inheritdoc cref="OnDragOver"/>
    private void OnDrop(object? sender, DragEventArgs e)
    {
        var name = SvgViewerVariableDrag.Carried(e);
        var row = ClipRequested is { } ? SvgViewerElementTree.Carried(e) : null;

        // Before the release, which is what forgets which rows would have taken it.
        var over = Under(e);

        Release();

        if ((name is null && row is null) || over is not { Tag: string attribute })
        {
            return;
        }

        e.DragEffects = SvgViewerElementTree.Applying(e);
        e.Handled = true;

        if (name is null)
        {
            ClipRequested?.Invoke(row!.Value.Key, attribute);

            return;
        }

        // Through the box and its own commit, so the refusal, the history's label, the readout and
        // the check against a style declaration are the ones typing it would have got.
        over.Text = SvgViewerVariableDrag.Bound(name);

        Commit(over, attribute, _address!);
    }

    /// <summary>Works out which rows would take <paramref name="carried"/>, and says so.</summary>
    private void Offer(string carried, Func<string, bool> takes)
    {
        if (string.Equals(_carried, carried, StringComparison.Ordinal))
        {
            return;
        }

        Release();

        _carried = carried;

        foreach (var row in _shown)
        {
            if (row.Box.IsReadOnly || !takes(row.Name))
            {
                continue;
            }

            _takes.Add(row.Name);

            row.Box[!TemplatedControl.BorderBrushProperty] =
                new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("TextControlBorderBrushFocused");
        }
    }

    /// <summary>The box under the pointer, where it is one that would take what is being carried.</summary>
    private SvgExpressionField? Under(DragEventArgs e)
        => SvgViewerDragScroll.Under(e, this)?.FindAncestorOfType<SvgExpressionField>(true) is { Tag: string name } box
           && _takes.Contains(name)
            ? box
            : null;

    private void Aim(SvgExpressionField? box)
    {
        if (ReferenceEquals(_aimed, box))
        {
            return;
        }

        _aimed?.ClearValue(TemplatedControl.BorderThicknessProperty);
        _aimed?.ClearValue(TemplatedControl.PaddingProperty);
        _aimed = box;

        if (box is { })
        {
            // Thickened into its padding: a box that grew would grow the list, and at the list's end
            // start the scroll again that takes the aim back off.
            var aim = new Thickness(2d);

            box.Padding += box.BorderThickness - aim;
            box.BorderThickness = aim;
        }
    }

    /// <summary>Puts every row back the way it was drawn.</summary>
    private void Release()
    {
        if (_carried is null)
        {
            return;
        }

        Aim(null);

        foreach (var row in _shown)
        {
            row.Box.ClearValue(TemplatedControl.BorderBrushProperty);
            row.Box.ClearValue(TemplatedControl.BorderThicknessProperty);
        }

        _takes.Clear();
        _carried = null;
    }

    /// <summary>
    /// Whether rebuilding the rows now would take something from under somebody: the box being
    /// typed in, a control still being pressed, or a picker or menu a row has open.
    /// </summary>
    /// <remarks>
    /// Not wherever the keyboard happens to rest. A row kept because a button still had focus stayed
    /// behind for as long as it did — through an undo, whose rows it then wrote back.
    /// </remarks>
    private bool Held()
        => _pressing
           || _picking > 0
           || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox { Tag: string } box
           && box.FindAncestorOfType<SvgViewerElementPanel>() is { } panel
           && ReferenceEquals(panel, this);

    /// <summary>What has the keyboard in these rows, as something the rebuilt rows have again, or null.</summary>
    private string? Keyboard()
        => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Control held
           && held.FindAncestorOfType<SvgViewerElementPanel>() is { } panel
           && ReferenceEquals(panel, this)
            ? Kind(held)
            : null;

    /// <summary>A control named by its row and its part, or by its section for a section's header.</summary>
    private static string? Kind(Control control)
        => control.TemplatedParent is Expander { Tag: string section }
            ? "section " + section
            : control.Tag is string row && control.TemplatedParent is null
                ? $"{row} {control.GetType().Name} {string.Join(" ", control.Classes.Where(name => !name.StartsWith(':')))}"
                : null;
}
