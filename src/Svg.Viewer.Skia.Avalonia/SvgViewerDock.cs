// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// The middle of a window, and the panels arranged around it: what sits where, how big, what is
/// behind what, and what is folded away.
/// </summary>
/// <remarks>
/// A tree of splits rather than named sides. Every panel sits in a <c>leaf</c>; a leaf holding
/// several ids shows them as tabs, read one at a time; and a <c>split</c> puts its children in a row
/// or a column. Dropping a panel on the edge of a pane splits that pane, so a panel taken to the
/// foot of the drawing sits under the drawing, and one taken to the foot of the window runs under
/// everything. There is no setting for which — the difference is where you let go, which is how
/// Visual Studio, Rider and Qt Creator all answer it.
///
/// It replaced three fixed sides, where the foot was a row of the body and the sides were columns
/// inside it: anything dropped along the bottom cut through the right-hand strip, and there was no
/// way to ask for anything else.
///
/// The middle is a leaf like any other, written <c>*</c>. Making it part of the tree is what lets a
/// panel be dropped against it without the middle being a special case in the drop code. It cannot
/// be folded, carried or closed, which is the whole of what the tree knows about it.
///
/// Every region in a leaf keeps its content in the tree and only the chosen one is visible, so what
/// a panel says can be found whether or not it is on top. The content is held in a
/// <see cref="Border"/>, not a <see cref="ContentControl"/>: a ContentControl builds its child from a
/// template when it is measured, and one that is not on show is never measured, so the panel would be
/// in the logical tree and not the visual one.
/// </remarks>
public sealed class SvgViewerDock
{
    /// <summary>What a written layout calls the middle.</summary>
    public const string Centre = "*";

    /// <summary>The arrangement a viewer nobody has rearranged comes up in.</summary>
    /// <remarks>
    /// The drawing, and a strip of three beside it: the tree and the host's own pane read one at a
    /// time at the top, then the variables, then the picked element's attributes. Those last two
    /// cannot share, because a variable is dragged onto an attribute and behind a tab each would
    /// hide the other.
    ///
    /// The strip is <c>340px</c> and the runs dividing it are proportions. A strip that grew with the
    /// window would be a regression — a column of controls wants the width it wants — where the runs
    /// inside it want to keep their share of it. The 1 : 1.3 : 1.7 was measured rather than chosen:
    /// the attributes want about 1200px and were getting 217 when the three were equal.
    /// </remarks>
    public const string Default =
        "row(*/1,col(project+elements/1/elements/open,variables/1.3/variables/open,element/1.7/element/open)/340px)";

    /// <summary>The narrowest and the shallowest anything is worth being.</summary>
    /// <remarks>Across, a column of controls; down, a header and a line of whatever is under it.</remarks>
    private const double WideMinimum = 200d;
    private const double DeepMinimum = 84d;

    /// <summary>The middle keeps more than a panel does, having a drawing in it.</summary>
    private const double MiddleMinimum = 160d;

    private const double SplitterSize = 6d;

    private static readonly IBrush Divider = new SolidColorBrush(Color.Parse("#20808080"));

    /// <summary>The body, and the line showing where a panel being carried would land.</summary>
    private readonly Panel _shell = new();

    private readonly Border _root = new();

    /// <summary>
    /// Where a dragged panel would land, drawn over everything.
    /// </summary>
    /// <remarks>
    /// The indicator both trees already use: a border over the top, not hit-testable, hidden until
    /// there is somewhere to land. Over the body rather than inside a pane, because a drag crosses
    /// from one side of the middle to the other.
    /// </remarks>
    private readonly Border _hint = new()
    {
        Name = "Landing",
        IsVisible = false,
        IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top
    };

    /// <summary>The middle itself, kept rather than remade: it is the one thing with no host.</summary>
    private readonly Border _middle = new();

    /// <summary>Everything filled in on the last build, so the next one can empty them first.</summary>
    /// <remarks>A control cannot be added to a second parent, and every region is about to move.</remarks>
    private readonly List<Border> _filled = new();

    private Node _tree;

    private IReadOnlyList<SvgViewerRegion> _regions = Array.Empty<SvgViewerRegion>();

    /// <summary>Panels a host has taken off the arrangement, so settling does not put them back.</summary>
    /// <remarks>
    /// Not written into the line. A host turning a panel off is this session's doing — the toolbar's
    /// Elements toggle — where the line is what somebody arranged and expects to find again.
    /// </remarks>
    private readonly HashSet<string> _dropped = new(StringComparer.Ordinal);

    private bool _shows = true;

    /// <summary>What was built last time, so a drag can ask what is under the pointer.</summary>
    private readonly List<Landing> _built = new();

    /// <summary>Which grid a split was built into, so its splitters can hand the sizes back.</summary>
    private readonly Dictionary<Split, Grid> _grids = new();

    /// <summary>The panel a press took hold of, and where the press was.</summary>
    private string? _carried;
    private Point _pressedAt;
    private bool _dragging;

    /// <summary>Where it would go if it were let go of now.</summary>
    private Drop? _landing;

    /// <summary>True while the picture is being rebuilt, so nothing it does reads as a hand.</summary>
    private bool _building;

    public SvgViewerDock(Control centre)
    {
        _middle.Child = centre ?? throw new ArgumentNullException(nameof(centre));

        _hint[!Border.BackgroundProperty] =
            new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("TabItemHeaderSelectedPipeFill");

        _shell.Children.Add(_root);
        _shell.Children.Add(_hint);

        _shell.PointerMoved += OnMoved;
        _shell.PointerReleased += (_, e) => Land(e);
        _shell.PointerCaptureLost += (_, _) => Let();

        _tree = Read(Default);

        Rebuild();
    }

    /// <summary>The body itself, to be put wherever the host keeps it.</summary>
    public Control Root => _shell;

    /// <summary>Raised when a hand rearranged something — never for a layout the host set.</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>The panels there are to arrange, in the order they are offered a place.</summary>
    public IReadOnlyList<SvgViewerRegion> Regions
    {
        get => _regions;
        set
        {
            var regions = value ?? Array.Empty<SvgViewerRegion>();

            if (_regions.Count == regions.Count && _regions.Zip(regions).All(pair => Same(pair.First, pair.Second)))
            {
                return;
            }

            _regions = regions;

            Settle();
            Rebuild();
        }
    }

    /// <summary>The whole arrangement, as one line.</summary>
    /// <remarks>
    /// <code>
    /// node := body "/" size [ "/" on top "/" open or folded ]
    /// body := ids | "row(" node ("," node)* ")" | "col(" node ("," node)* ")"
    /// ids  := id ("+" id)*        several means tabs, read one at a time
    /// size := number              a share of whatever it sits among
    ///       | number "px"         a width or a height it keeps
    /// </code>
    /// A line this cannot make sense of is replaced whole by <see cref="Default"/> rather than half
    /// applied, which is how every other stored setting treats a value it does not recognise. That
    /// covers the flat <c>right 340 …</c> lines the arrangement before this one wrote: they are not
    /// trees, so they fall back, and falling back is the migration.
    /// </remarks>
    public string Layout
    {
        get => Written(_tree, root: true);
        set
        {
            if (string.Equals(Layout, value, StringComparison.Ordinal))
            {
                return;
            }

            _tree = Read(value);

            Settle();
            Rebuild();
        }
    }

    /// <summary>Whether the panels are shown at all, the middle having the room when they are not.</summary>
    public bool ShowsRegions
    {
        get => _shows;
        set
        {
            if (_shows == value)
            {
                return;
            }

            _shows = value;

            Rebuild();
        }
    }

    /// <summary>Whether <paramref name="id"/> has a place in the arrangement.</summary>
    public bool Shows(string id) => Holding(id) is { };

    /// <summary>Gives <paramref name="id"/> a place, or takes the one it has away.</summary>
    /// <remarks>
    /// Taking it away rather than folding it: a fold is one leaf and a leaf can hold several panels,
    /// so folding to hide one of them would hide its neighbour too.
    /// </remarks>
    public void Show(string id, bool shown)
    {
        if (string.Equals(id, Centre, StringComparison.Ordinal) || Shows(id) == shown)
        {
            return;
        }

        if (shown)
        {
            _dropped.Remove(id);
            Restore(id);
        }
        else
        {
            _dropped.Add(id);
            Take(id);
        }

        Rebuild();
    }

    /// <summary>Whether the leaf holding <paramref name="id"/> is folded to its header.</summary>
    public bool Folded(string id) => Holding(id) is { Folded: true };

    /// <summary>Folds the leaf holding <paramref name="id"/>, or opens it again.</summary>
    public void Fold(string id, bool folded)
    {
        if (Holding(id) is not { } leaf || leaf.Folded == folded || leaf.IsMiddle)
        {
            return;
        }

        leaf.Folded = folded;

        Rebuild();
    }

    /// <summary>Which of a leaf's panels is the one being read.</summary>
    public string? Selected(string id) => Holding(id)?.Selected;

    /// <summary>Puts <paramref name="id"/> on top of whatever leaf it is in.</summary>
    public void Select(string id)
    {
        if (Holding(id) is not { } leaf || string.Equals(leaf.Selected, id, StringComparison.Ordinal))
        {
            return;
        }

        leaf.Selected = id;

        Rebuild();
    }

    // ---- the tree ------------------------------------------------------------------------------

    /// <summary>How big a node is among its neighbours: a share of them, or an extent it keeps.</summary>
    private readonly record struct Reach(double Value, bool Kept)
    {
        public GridLength Length
            => Kept ? new GridLength(Value, GridUnitType.Pixel) : new GridLength(Value, GridUnitType.Star);

        public override string ToString()
            => Value.ToString("0.##", CultureInfo.InvariantCulture) + (Kept ? "px" : string.Empty);
    }

    private abstract class Node
    {
        public Reach Reach = new(1d, false);
    }

    private sealed class Leaf : Node
    {
        public readonly List<string> Ids = new();

        public string? Selected;
        public bool Folded;

        public bool IsMiddle => Ids.Contains(SvgViewerDock.Centre, StringComparer.Ordinal);
    }

    private sealed class Split : Node
    {
        /// <summary>True for a row — children beside one another; false for a column, stacked.</summary>
        public bool Across;

        public readonly List<Node> Children = new();
    }

    /// <summary>Whether anything supplies <paramref name="id"/>.</summary>
    private bool Known(string id)
        => _regions.Any(region => string.Equals(region.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// Whether a node has anything in it to show.
    /// </summary>
    /// <remarks>
    /// A leaf can name a panel nothing supplies — a layout written when a host offered a pane it no
    /// longer does, or the viewer's own tree after a host has turned it off. The name is kept, so
    /// that pane comes back where somebody put it, but an empty leaf must not go on holding a third
    /// of the column.
    /// </remarks>
    private bool Alive(Node node)
        => node switch
        {
            Leaf leaf => leaf.IsMiddle || leaf.Ids.Any(Known),
            Split split => split.Children.Any(Alive),
            _ => false
        };

    private static IEnumerable<Node> Walk(Node node)
    {
        yield return node;

        if (node is Split split)
        {
            foreach (var under in split.Children.SelectMany(Walk))
            {
                yield return under;
            }
        }
    }

    private Leaf? Holding(string id)
        => Walk(_tree).OfType<Leaf>().FirstOrDefault(leaf => leaf.Ids.Contains(id, StringComparer.Ordinal));

    private Split? Parent(Node node)
        => Walk(_tree).OfType<Split>().FirstOrDefault(split => split.Children.Contains(node));

    /// <summary>Takes a panel out, and closes up whatever that leaves behind.</summary>
    private void Take(string id)
    {
        if (Holding(id) is not { } leaf)
        {
            return;
        }

        leaf.Ids.Remove(id);

        if (string.Equals(leaf.Selected, id, StringComparison.Ordinal))
        {
            leaf.Selected = leaf.Ids.FirstOrDefault();
        }

        if (leaf.Ids.Count == 0)
        {
            Prune(leaf);
        }
    }

    /// <summary>
    /// Removes a node and collapses what it leaves, all the way up.
    /// </summary>
    /// <remarks>
    /// A split with one child is not a split. Left standing it would put a splitter between a pane
    /// and nothing, and every drop afterwards would aim at a node with no shape of its own. The one
    /// child takes the split's size, or the pane beside it would jump when the last of its
    /// neighbours went.
    /// </remarks>
    private void Prune(Node node)
    {
        if (Parent(node) is not { } parent)
        {
            return;
        }

        parent.Children.Remove(node);

        if (parent.Children.Count > 1)
        {
            return;
        }

        if (parent.Children.Count == 1)
        {
            var only = parent.Children[0];

            only.Reach = parent.Reach;

            if (Parent(parent) is { } above)
            {
                above.Children[above.Children.IndexOf(parent)] = only;
            }
            else
            {
                _tree = only;
            }

            return;
        }

        Prune(parent);
    }

    /// <summary>Puts a panel back beside whatever the default has it sitting with.</summary>
    /// <remarks>
    /// Beside its neighbours rather than wherever it was last: a panel taken off and put back belongs
    /// where somebody would look for it, and the leaf it used to be in may be gone.
    /// </remarks>
    private void Restore(string id)
    {
        if (Parsed(Default) is { } fresh
            && Walk(fresh).OfType<Leaf>().FirstOrDefault(leaf => leaf.Ids.Contains(id, StringComparer.Ordinal))
                is { } wanted
            && Walk(_tree).OfType<Leaf>().FirstOrDefault(
                   leaf => !leaf.IsMiddle
                           && wanted.Ids.Any(other => leaf.Ids.Contains(other, StringComparer.Ordinal)))
               is { } beside)
        {
            beside.Ids.Add(id);
            beside.Selected = id;

            return;
        }

        Beside(Holding(Centre)!, Made(id), across: true, before: false);
    }

    private static Leaf Made(string id)
    {
        var leaf = new Leaf { Selected = id };

        leaf.Ids.Add(id);

        return leaf;
    }

    /// <summary>Puts <paramref name="made"/> next to <paramref name="target"/>, splitting if it must.</summary>
    /// <remarks>
    /// Into the parent where the parent already divides things the same way, so three panels dropped
    /// down one side give one column of three rather than a column inside a column inside a column.
    /// </remarks>
    private void Beside(Node target, Leaf made, bool across, bool before)
    {
        made.Reach = across ? new Reach(340d, true) : new Reach(1d, false);

        if (Parent(target) is { } parent && parent.Across == across)
        {
            parent.Children.Insert(parent.Children.IndexOf(target) + (before ? 0 : 1), made);

            return;
        }

        var split = new Split { Across = across, Reach = target.Reach };

        // What is being divided keeps a share rather than whatever extent it had: two panes dividing
        // a strip that was 340px wide are not both 340px wide.
        target.Reach = new Reach(1d, false);

        split.Children.Add(before ? made : target);
        split.Children.Add(before ? target : made);

        if (Parent(target) is { } above)
        {
            above.Children[above.Children.IndexOf(target)] = split;
        }
        else
        {
            _tree = split;
        }
    }

    /// <summary>Finds a place for any panel the line said nothing about.</summary>
    /// <remarks>
    /// A line is written by whoever arranged one, and a host can hand over a panel that arrangement
    /// never saw — one added since, or one named differently. Left unplaced it would simply not
    /// appear, which reads as the panel being broken rather than as the layout being old.
    /// </remarks>
    private void Settle()
    {
        foreach (var region in _regions)
        {
            if (!_dropped.Contains(region.Id) && !Shows(region.Id))
            {
                Restore(region.Id);
            }
        }
    }

    private static bool Same(SvgViewerRegion one, SvgViewerRegion other)
        => string.Equals(one.Id, other.Id, StringComparison.Ordinal)
           && string.Equals(one.Header, other.Header, StringComparison.Ordinal)
           && ReferenceEquals(one.Content, other.Content);

    // ---- reading and writing the line ----------------------------------------------------------

    private static string Written(Node node, bool root = false)
    {
        var line = new StringBuilder();

        switch (node)
        {
            case Leaf leaf:
                line.Append(string.Join("+", leaf.Ids));
                break;

            case Split split:
                line.Append(split.Across ? "row(" : "col(")
                    .Append(string.Join(",", split.Children.Select(child => Written(child))))
                    .Append(')');
                break;
        }

        // The root's own size is a share of nothing.
        if (!root)
        {
            line.Append('/').Append(node.Reach);
        }

        if (node is Leaf { IsMiddle: false } named)
        {
            line.Append('/')
                .Append(named.Selected ?? named.Ids.FirstOrDefault() ?? string.Empty)
                .Append('/')
                .Append(named.Folded ? "folded" : "open");
        }

        return line.ToString();
    }

    private static Node Read(string? line)
        => Parsed(line)
           ?? Parsed(Default)
           ?? throw new InvalidOperationException("The default layout does not read back.");

    /// <summary>The tree a line describes, or null where it describes nothing usable.</summary>
    private static Node? Parsed(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var at = 0;
        var named = new HashSet<string>(StringComparer.Ordinal);

        var tree = Grown(line!, ref at, named);

        // Every letter accounted for, and exactly one middle: a line that only half reads is not one
        // to half apply, and one with nowhere to put the drawing is not an arrangement at all.
        return tree is { } && at == line!.Length && named.Contains(Centre) ? tree : null;
    }

    private static Node? Grown(string line, ref int at, HashSet<string> named)
    {
        Node node;

        if (Ahead(line, at, "row(") || Ahead(line, at, "col("))
        {
            var split = new Split { Across = Ahead(line, at, "row(") };

            at += 4;

            while (true)
            {
                if (Grown(line, ref at, named) is not { } child)
                {
                    return null;
                }

                split.Children.Add(child);

                if (at < line.Length && line[at] == ',')
                {
                    at++;

                    continue;
                }

                break;
            }

            // A split of one is not a split, and neither is one nobody closed.
            if (at >= line.Length || line[at] != ')' || split.Children.Count < 2)
            {
                return null;
            }

            at++;
            node = split;
        }
        else
        {
            var leaf = new Leaf();

            foreach (var id in Word(line, ref at).Split('+', StringSplitOptions.RemoveEmptyEntries))
            {
                // A panel can only be in one place, and a line naming it twice is not one.
                if (!named.Add(id))
                {
                    return null;
                }

                leaf.Ids.Add(id);
            }

            if (leaf.Ids.Count == 0 || (leaf.IsMiddle && leaf.Ids.Count > 1))
            {
                return null;
            }

            leaf.Selected = leaf.Ids[0];
            node = leaf;
        }

        if (at < line.Length && line[at] == '/')
        {
            at++;

            var said = Word(line, ref at);
            var kept = said.EndsWith("px", StringComparison.Ordinal);

            if (!double.TryParse(
                    kept ? said[..^2] : said,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var reach)
                || reach <= 0d)
            {
                return null;
            }

            node.Reach = new Reach(reach, kept);
        }

        if (node is not Leaf { IsMiddle: false } row)
        {
            return node;
        }

        if (at < line.Length && line[at] == '/')
        {
            at++;

            var chosen = Word(line, ref at);

            if (row.Ids.Contains(chosen, StringComparer.Ordinal))
            {
                row.Selected = chosen;
            }
        }

        if (at < line.Length && line[at] == '/')
        {
            at++;

            row.Folded = string.Equals(Word(line, ref at), "folded", StringComparison.Ordinal);
        }

        return node;
    }

    private static bool Ahead(string line, int at, string word)
        => at + word.Length <= line.Length && string.CompareOrdinal(line, at, word, 0, word.Length) == 0;

    /// <summary>Everything up to the next thing that divides one part of the line from another.</summary>
    private static string Word(string line, ref int at)
    {
        var from = at;

        while (at < line.Length && line[at] is not ('/' or ',' or ')'))
        {
            at++;
        }

        return line[from..at];
    }

    // ---- the picture ---------------------------------------------------------------------------

    private void Rebuild()
    {
        _building = true;

        try
        {
            foreach (var host in _filled)
            {
                host.Child = null;
            }

            _filled.Clear();
            _built.Clear();
            _grids.Clear();

            // The grids that held it are thrown away rather than emptied, so the middle would still
            // be reading itself a child of one of them. Everything else here is made fresh each
            // build; the middle is the one thing carried over.
            Unhand(_middle);

            _root.Child = null;
            _root.Child = _shows ? Build(_tree) : _middle;
        }
        finally
        {
            _building = false;
        }
    }

    /// <summary>Takes a control off whatever is holding it, so it can be held by something else.</summary>
    private static void Unhand(Control control)
    {
        switch (control.Parent)
        {
            case Panel panel:
                panel.Children.Remove(control);
                break;

            case Decorator decorator:
                decorator.Child = null;
                break;

            case ContentControl content:
                content.Content = null;
                break;
        }
    }

    private Control Build(Node node)
    {
        if (node is Leaf leaf)
        {
            return leaf.IsMiddle ? _middle : Shown(leaf);
        }

        var split = (Split)node;
        var children = split.Children.Where(Alive).ToList();

        // Nothing fills the rest of it, so there is nothing to divide.
        if (children.Count <= 1)
        {
            return children.Count == 1 ? Build(children[0]) : _middle;
        }

        var grid = new Grid();

        _grids[split] = grid;

        foreach (var child in children)
        {
            if (!ReferenceEquals(child, children[0]))
            {
                Define(grid, split.Across, new GridLength(SplitterSize, GridUnitType.Pixel), 0d);
            }

            // A folded leaf is its header and nothing else, so it asks for what that measures
            // rather than for the share it had. Its size is kept, and comes back when it opens.
            var folded = child is Leaf { Folded: true };

            Define(
                grid,
                split.Across,
                folded ? GridLength.Auto : child.Reach.Length,
                folded ? 0d : Least(child, split.Across));
        }

        var at = 0;

        for (var index = 0; index < children.Count; index++)
        {
            if (index > 0)
            {
                var splitter = Splitter(split.Across ? GridResizeDirection.Columns : GridResizeDirection.Rows);

                Place(grid, split.Across, splitter, at++);
                Watch(splitter, split);
            }

            var built = Build(children[index]);

            Place(grid, split.Across, index > 0 ? Lined(built, split.Across) : built, at++);
        }

        return grid;
    }

    /// <summary>The least a node is worth being, along the way its neighbours are laid out.</summary>
    private static double Least(Node node, bool across)
        => node is Leaf { IsMiddle: true } ? MiddleMinimum
            : across ? WideMinimum
            : DeepMinimum;

    /// <summary>The line between one pane and the next, on the leading edge of all but the first.</summary>
    private static Control Lined(Control control, bool across)
        => new Border
        {
            BorderThickness = across ? new Thickness(1, 0, 0, 0) : new Thickness(0, 1, 0, 0),
            BorderBrush = Divider,
            Child = control
        };

    /// <summary>One leaf: its header, and whatever of it is on top.</summary>
    private Control Shown(Leaf leaf)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };

        var header = Header(leaf);

        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        if (!leaf.Folded)
        {
            var body = new Panel();

            foreach (var id in leaf.Ids)
            {
                if (_regions.FirstOrDefault(region => string.Equals(region.Id, id, StringComparison.Ordinal))
                    is not { } region)
                {
                    continue;
                }

                var host = new Border
                {
                    Child = region.Content,
                    IsVisible = string.Equals(leaf.Selected, id, StringComparison.Ordinal)
                };

                _filled.Add(host);
                body.Children.Add(host);
            }

            Grid.SetRow(body, 1);
            grid.Children.Add(body);
        }

        _built.Add(new Landing(leaf, header, grid));

        return grid;
    }

    private Control Header(Leaf leaf)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal };

        var chevron = new Border
        {
            Classes = { "chevron" },
            Background = Brushes.Transparent,
            Width = 18d,
            Child = new TextBlock
            {
                Text = leaf.Folded ? "▸" : "▾",
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        chevron.PointerPressed += (_, e) =>
        {
            e.Handled = true;

            leaf.Folded = !leaf.Folded;

            Rebuild();
            Moved();
        };

        bar.Children.Add(chevron);

        foreach (var id in leaf.Ids)
        {
            if (_regions.FirstOrDefault(region => string.Equals(region.Id, id, StringComparison.Ordinal))
                is not { } region)
            {
                continue;
            }

            var tab = new Border
            {
                Classes = { "pane" },
                Background = Brushes.Transparent,
                Child = new TextBlock { Text = region.Header, VerticalAlignment = VerticalAlignment.Center }
            };

            if (string.Equals(leaf.Selected, id, StringComparison.Ordinal))
            {
                tab.Classes.Add("selected");
            }

            var chosen = id;

            tab.PointerPressed += (_, e) =>
            {
                e.Handled = true;

                if (!e.GetCurrentPoint(_shell).Properties.IsLeftButtonPressed)
                {
                    return;
                }

                // Taken hold of either way: a press that goes nowhere is the panel being chosen, and
                // one that travels is it being carried somewhere else.
                _carried = chosen;
                _pressedAt = e.GetPosition(_shell);
                _dragging = false;

                if (string.Equals(leaf.Selected, chosen, StringComparison.Ordinal))
                {
                    return;
                }

                leaf.Selected = chosen;

                Rebuild();
                Moved();
            };

            bar.Children.Add(tab);
        }

        return new Border { Classes = { "slot" }, Background = Brushes.Transparent, Child = bar };
    }

    /// <summary>Takes the sizes back off the grid once a hand has finished dragging a splitter.</summary>
    private void Watch(GridSplitter splitter, Split split)
        => splitter.DragCompleted += (_, _) =>
        {
            if (_building)
            {
                return;
            }

            Measure(split);
            Moved();
        };

    /// <summary>Reads a split's children back out of the grid they were built into.</summary>
    /// <remarks>
    /// What was kept stays kept and what was a share stays a share, so dragging a strip wider leaves
    /// it a strip, and dragging one run of it taller leaves that run a proportion of its neighbours.
    /// </remarks>
    private void Measure(Split split)
    {
        if (!_grids.TryGetValue(split, out var grid))
        {
            return;
        }

        var lengths = split.Across
            ? grid.ColumnDefinitions.Select(column => column.ActualWidth).ToList()
            : grid.RowDefinitions.Select(row => row.ActualHeight).ToList();

        var children = split.Children.Where(Alive).ToList();

        for (int index = 0, at = 0; index < children.Count; index++, at++)
        {
            if (index > 0)
            {
                at++;
            }

            if (at >= lengths.Count || lengths[at] <= 0d)
            {
                continue;
            }

            children[index].Reach = children[index].Reach.Kept
                ? new Reach(Math.Round(lengths[at], 0), true)
                : new Reach(Math.Round(lengths[at] / 100d, 2), false);
        }
    }

    private void Moved()
    {
        if (!_building)
        {
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---- carrying a panel somewhere else --------------------------------------------------------

    /// <summary>One leaf as it was built, so a drag can say what the pointer is over.</summary>
    private sealed record Landing(Leaf Leaf, Control Header, Control Body);

    /// <summary>Where a carried panel would go: into a pane, or beside one.</summary>
    private readonly record struct Drop(Node Target, bool Join, bool Across, bool Before);

    /// <summary>How far a press travels before it is a drag rather than a click.</summary>
    private const double DragThreshold = 4d;

    /// <summary>How much of a pane counts as its edge rather than its middle.</summary>
    private const double Edge = 0.3d;

    /// <summary>How close to the body's own edge means "across the whole of it".</summary>
    /// <remarks>
    /// The difference between a panel under the drawing and a panel under everything, which is the
    /// one thing three fixed sides could not say. In pixels rather than a share, because it is a
    /// thing to aim at rather than a region of anything.
    /// </remarks>
    private const double Rim = 26d;

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_carried is null)
        {
            return;
        }

        if (!e.GetCurrentPoint(_shell).Properties.IsLeftButtonPressed)
        {
            Let();

            return;
        }

        var at = e.GetPosition(_shell);

        if (!_dragging)
        {
            if (Math.Abs(at.X - _pressedAt.X) < DragThreshold && Math.Abs(at.Y - _pressedAt.Y) < DragThreshold)
            {
                return;
            }

            _dragging = true;
        }

        _landing = Where(at);

        Hint();
    }

    /// <summary>
    /// What letting go here would mean, or null for nowhere.
    /// </summary>
    /// <remarks>
    /// Narrowest first. A header takes the panel in beside what it already holds. The rim of the body
    /// splits the whole of it, which is how a panel comes to run the full width under everything —
    /// the thing that has to be asked for rather than assumed. Otherwise the pane under the pointer
    /// is split, or tabbed into where the pointer is well inside it; the middle takes no tabs, so
    /// there it is the nearest edge either way.
    /// </remarks>
    private Drop? Where(Point at)
    {
        foreach (var landing in _built)
        {
            if (Over(landing.Header, at) is { })
            {
                return landing.Leaf.Ids.Contains(_carried!, StringComparer.Ordinal) && landing.Leaf.Ids.Count == 1
                    ? null
                    : new Drop(landing.Leaf, Join: true, false, false);
            }
        }

        if (Over(_root, at) is not { } body)
        {
            return null;
        }

        if (Rimmed(body, _root.Bounds.Size) is { } rim)
        {
            return new Drop(_tree, Join: false, rim.Across, rim.Before);
        }

        foreach (var landing in _built)
        {
            if (Over(landing.Body, at) is { } inside)
            {
                return Against(landing.Leaf, inside, landing.Body.Bounds.Size, tabs: true);
            }
        }

        return Over(_middle, at) is { } middle
            ? Against(Holding(Centre)!, middle, _middle.Bounds.Size, tabs: false)
            : null;
    }

    /// <summary>Which edge of the body the pointer is against, or null for none of them.</summary>
    private static (bool Across, bool Before)? Rimmed(Point at, Size size)
    {
        if (at.X <= Rim)
        {
            return (true, true);
        }

        if (at.X >= size.Width - Rim)
        {
            return (true, false);
        }

        if (at.Y <= Rim)
        {
            return (false, true);
        }

        return at.Y >= size.Height - Rim ? (false, false) : null;
    }

    /// <summary>Splitting one pane, or landing in it where it takes tabs and the pointer is inside.</summary>
    private static Drop Against(Node target, Point inside, Size size, bool tabs)
    {
        var west = inside.X / Math.Max(size.Width, 1d);
        var north = inside.Y / Math.Max(size.Height, 1d);
        var east = 1d - west;
        var south = 1d - north;

        var nearest = Math.Min(Math.Min(west, east), Math.Min(north, south));

        if (tabs && nearest > Edge)
        {
            return new Drop(target, Join: true, false, false);
        }

        return nearest == west ? new Drop(target, false, true, true)
            : nearest == east ? new Drop(target, false, true, false)
            : nearest == north ? new Drop(target, false, false, true)
            : new Drop(target, false, false, false);
    }

    private Point? Over(Visual visual, Point at)
    {
        if (visual.Bounds.Width <= 0d || visual.Bounds.Height <= 0d)
        {
            return null;
        }

        if (visual.TranslatePoint(default, _shell) is not { } origin)
        {
            return null;
        }

        var inside = new Point(at.X - origin.X, at.Y - origin.Y);

        return inside.X >= 0d && inside.Y >= 0d
               && inside.X <= visual.Bounds.Width && inside.Y <= visual.Bounds.Height
            ? inside
            : null;
    }

    /// <summary>Draws the landing, or takes the line away where there is none.</summary>
    private void Hint()
    {
        if (!_dragging || _landing is not { } landing || Shape(landing.Target) is not { } over)
        {
            _hint.IsVisible = false;

            return;
        }

        if (landing.Join)
        {
            Show(over, over.Bounds.Width, over.Bounds.Height, 0d, 0d);
        }
        else
        {
            var band = landing.Across
                ? Math.Min(340d, over.Bounds.Width / 2d)
                : Math.Min(200d, over.Bounds.Height / 2d);

            Show(
                over,
                landing.Across ? band : over.Bounds.Width,
                landing.Across ? over.Bounds.Height : band,
                landing.Across && !landing.Before ? over.Bounds.Width - band : 0d,
                !landing.Across && !landing.Before ? over.Bounds.Height - band : 0d);
        }

        _hint.Opacity = 0.35d;
        _hint.IsVisible = true;
    }

    /// <summary>What a node looks like on screen, for the landing to be drawn over.</summary>
    private Control? Shape(Node node)
        => node switch
        {
            Leaf { IsMiddle: true } => _middle,
            Leaf leaf => _built.FirstOrDefault(landing => ReferenceEquals(landing.Leaf, leaf))?.Body,
            Split split => _grids.TryGetValue(split, out var grid) ? grid : _root,
            _ => _root
        };

    private void Land(PointerReleasedEventArgs e)
    {
        var carried = _carried;
        var landing = _landing;
        var dragged = _dragging;

        Let();

        if (!dragged || carried is null || landing is not { } where)
        {
            return;
        }

        e.Handled = true;

        var was = Holding(carried);

        // Dropping it on its own header, or against the pane it is the whole of, is not a move.
        if (was is { } && ReferenceEquals(where.Target, was) && (where.Join || was.Ids.Count == 1))
        {
            return;
        }

        var target = where.Target;

        Take(carried);

        // Taking it out can prune the very node it was landing against. Anything no longer in the
        // tree lands against the middle instead, which is the one node that cannot go.
        if (!Walk(_tree).Contains(target))
        {
            target = Holding(Centre)!;
        }

        if (where.Join && target is Leaf into)
        {
            into.Ids.Add(carried);
            into.Selected = carried;
        }
        else
        {
            Beside(target, Made(carried), where.Across, where.Before);
        }

        Rebuild();
        Moved();
    }

    private void Let()
    {
        _carried = null;
        _dragging = false;
        _landing = null;
        _hint.IsVisible = false;
    }

    // ---- grid plumbing -------------------------------------------------------------------------

    private void Show(Visual over, double wide, double tall, double right, double down)
    {
        if (over.TranslatePoint(default, _shell) is not { } corner)
        {
            _hint.IsVisible = false;

            return;
        }

        _hint.Width = Math.Max(wide, 0d);
        _hint.Height = Math.Max(tall, 0d);
        _hint.Margin = new Thickness(corner.X + right, corner.Y + down, 0d, 0d);
    }

    private static void Define(Grid grid, bool across, GridLength length, double least)
    {
        if (across)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(length) { MinWidth = least });
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition(length) { MinHeight = least });
        }
    }

    private static void Place(Grid grid, bool across, Control control, int at)
    {
        if (across)
        {
            Grid.SetColumn(control, at);
        }
        else
        {
            Grid.SetRow(control, at);
        }

        grid.Children.Add(control);
    }

    private static GridSplitter Splitter(GridResizeDirection direction)
        => new() { ResizeDirection = direction, Background = Brushes.Transparent };
}
