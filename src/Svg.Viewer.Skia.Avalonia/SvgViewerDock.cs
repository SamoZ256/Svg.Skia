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

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>Which side of the drawing a run of panels is on.</summary>
public enum SvgViewerDockSide
{
    Left,
    Right,
    Bottom
}

/// <summary>
/// The drawing, and the panels arranged around it: what goes where, how big it is, and what is
/// folded away.
/// </summary>
/// <remarks>
/// One class for the whole body rather than one for the strip down the side, because a panel can be
/// dragged from one side to another and neither side can arrange that alone. It replaces three
/// hand-built grids — the viewer's own body, the strip inside it, and the copy <c>Svg.Studio</c>
/// kept for a group's board, which never got the minimum width the other two had.
///
/// A <see cref="Slot"/> is a run of the strip, and it holds more than one region where they are read
/// one at a time. Every region in a slot keeps its content in the tree and only the chosen one is
/// visible, so what a panel says can be found whether or not it is the one on top — a strip that
/// swapped the content out meant nothing could be asked about a panel until it was clicked.
///
/// The arrangement is a string, because it has to be written into a settings file and read back a
/// week later. <see cref="Layout"/> is that string in both directions, and everything else here is
/// the picture of it.
/// </remarks>
public sealed class SvgViewerDock
{
    /// <summary>The arrangement a viewer nobody has rearranged comes up in.</summary>
    /// <remarks>
    /// Two open and two in a strip. The tree and the host's own pane are read one at a time and
    /// neither is what the drawing is being worked on through, so they share the top of the column;
    /// the variables and the picked element's attributes get a run each, because a variable is
    /// dragged onto an attribute and behind a tab each would hide the other.
    ///
    /// The weights are measured rather than chosen. At the default window the three runs came out
    /// 217/217/200 on a drawing's tab and 169/169/169 on a board, while the attributes alone want
    /// about 1200px — the panel that wanted most was getting least. At 1/1.3/1.7 they come out
    /// about 160/208/272 and 178/232/303.
    /// </remarks>
    public const string Default =
        "right 340 project+elements/1/elements/open variables/1.3/variables/open element/1.7/element/open";

    /// <summary>The narrowest a run down the side is worth being, and the shallowest along the foot.</summary>
    private const double SideMinimum = 260d;
    private const double FootMinimum = 120d;

    /// <summary>Header plus a line of whatever is under it, so a splitter cannot rub one out.</summary>
    private const double SlotMinimum = 84d;

    private const double SplitterSize = 6d;

    private static readonly IBrush Divider = new SolidColorBrush(Color.Parse("#20808080"));

    private readonly Grid _root = new();

    /// <summary>The drawing's row of the body, kept rather than remade: it holds the drawing.</summary>
    /// <remarks>
    /// A control cannot be added to a second parent, and the drawing is the one thing here that does
    /// not go back through a host that could be emptied first.
    /// </remarks>
    private readonly Grid _middle = new();

    private readonly Border _centre = new();

    /// <summary>Everything filled in on the last build, so the next one can empty them first.</summary>
    /// <remarks>
    /// A control cannot be added to a second parent, and every region is about to move.
    ///
    /// A <see cref="Border"/> rather than a <see cref="ContentControl"/>, which is what this was:
    /// a ContentControl builds its child out of a template when it is measured, and one that is not
    /// on show is never measured — so a panel behind another was in the logical tree and not the
    /// visual one, and nothing could be asked about it until it had been clicked. A Border holds its
    /// child outright.
    /// </remarks>
    private readonly List<Border> _filled = new();

    private readonly List<Site> _sides = new()
    {
        new Site(SvgViewerDockSide.Left, 260d),
        new Site(SvgViewerDockSide.Right, 340d),
        new Site(SvgViewerDockSide.Bottom, 200d)
    };

    private IReadOnlyList<SvgViewerRegion> _regions = Array.Empty<SvgViewerRegion>();

    /// <summary>Panels a host has taken off the arrangement, so settling does not put them back.</summary>
    /// <remarks>
    /// Not written into the line. A host turning a panel off is this session's doing — the toolbar's
    /// Elements toggle — where the line is what somebody arranged and expects to find again.
    /// </remarks>
    private readonly HashSet<string> _dropped = new(StringComparer.Ordinal);

    private bool _shows = true;

    /// <summary>True while the picture is being rebuilt, so nothing it does reads as a hand.</summary>
    private bool _building;

    public SvgViewerDock(Control centre)
    {
        _centre.Child = centre ?? throw new ArgumentNullException(nameof(centre));

        Read(Default);
        Rebuild();
    }

    /// <summary>The body itself, to be put wherever the host keeps it.</summary>
    public Control Root => _root;

    /// <summary>Raised when a hand rearranged something — never for a layout the host set.</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>The panels there are to arrange, in the order they are offered a place.</summary>
    public IReadOnlyList<SvgViewerRegion> Regions
    {
        get => _regions;
        set
        {
            var regions = value ?? Array.Empty<SvgViewerRegion>();

            if (_regions.Count == regions.Count
                && _regions.Zip(regions).All(pair => Same(pair.First, pair.Second)))
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
    /// Read back defensively: a line this cannot make sense of is replaced whole by
    /// <see cref="Default"/> rather than half-applied, which is how every other stored setting
    /// treats a value it does not recognise.
    /// </remarks>
    public string Layout
    {
        get => Written();
        set
        {
            if (string.Equals(Written(), value, StringComparison.Ordinal))
            {
                return;
            }

            Read(value);
            Settle();
            Rebuild();
        }
    }

    /// <summary>Whether the panels are shown at all, the drawing having the room when they are not.</summary>
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
    /// Taking it away rather than folding it: a fold is one run of the strip and a run can hold
    /// several panels, so folding to hide one of them would hide its neighbour too.
    /// </remarks>
    public void Show(string id, bool shown)
    {
        if (Shows(id) == shown)
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
            Drop(id);
        }

        Rebuild();
    }

    /// <summary>Whether the run holding <paramref name="id"/> is folded to its header.</summary>
    public bool Folded(string id) => Holding(id) is { } slot && slot.Folded;

    /// <summary>Folds the run holding <paramref name="id"/>, or opens it again.</summary>
    public void Fold(string id, bool folded)
    {
        if (Holding(id) is not { } slot || slot.Folded == folded)
        {
            return;
        }

        slot.Folded = folded;

        Rebuild();
    }

    /// <summary>Which of a run's panels is the one being read.</summary>
    public string? Selected(string id) => Holding(id)?.Selected;

    /// <summary>Puts <paramref name="id"/> on top of whatever run it is in.</summary>
    public void Select(string id)
    {
        if (Holding(id) is not { } slot || string.Equals(slot.Selected, id, StringComparison.Ordinal))
        {
            return;
        }

        slot.Selected = id;

        Rebuild();
    }

    // ---- the model ---------------------------------------------------------------------------

    private sealed class Slot
    {
        public readonly List<string> Ids = new();

        public string? Selected;
        public double Weight = 1d;
        public bool Folded;
    }

    private sealed class Site
    {
        public Site(SvgViewerDockSide where, double extent)
        {
            Where = where;
            Extent = extent;
        }

        public SvgViewerDockSide Where { get; }

        public double Extent;

        public readonly List<Slot> Slots = new();
    }

    /// <summary>Whether anything supplies <paramref name="id"/>.</summary>
    private bool Known(string id)
        => _regions.Any(region => string.Equals(region.Id, id, StringComparison.Ordinal));

    /// <summary>A side's runs that have something in them to show.</summary>
    /// <remarks>
    /// A run can name a panel nothing supplies — a layout written when a host offered a pane it no
    /// longer does, or the viewer's own tree after a host has turned it off. The name is kept, so
    /// that pane comes back where somebody put it, but an empty run must not go on holding a third
    /// of the column: hiding the tree used to give its height to nothing at all.
    /// </remarks>
    private List<Slot> Live(Site side) => side.Slots.Where(slot => slot.Ids.Any(Known)).ToList();

    private Site Of(SvgViewerDockSide where) => _sides.First(side => side.Where == where);

    private Slot? Holding(string id)
        => _sides.SelectMany(side => side.Slots).FirstOrDefault(slot => slot.Ids.Contains(id, StringComparer.Ordinal));

    private Site? Holder(Slot slot) => _sides.FirstOrDefault(side => side.Slots.Contains(slot));

    private void Drop(string id)
    {
        if (Holding(id) is not { } slot)
        {
            return;
        }

        slot.Ids.Remove(id);

        if (string.Equals(slot.Selected, id, StringComparison.Ordinal))
        {
            slot.Selected = slot.Ids.FirstOrDefault();
        }

        if (slot.Ids.Count == 0)
        {
            Holder(slot)?.Slots.Remove(slot);
        }
    }

    /// <summary>Puts a panel back beside whatever the default has it sitting with.</summary>
    /// <remarks>
    /// Beside its neighbours rather than wherever it was last: a panel taken off the strip and put
    /// back belongs where somebody would look for it, and the run it used to be in may be gone.
    /// </remarks>
    private void Restore(string id)
    {
        var wanted = Parsed(Default)?.SelectMany(side => side.Slots)
            .FirstOrDefault(slot => slot.Ids.Contains(id, StringComparer.Ordinal));

        if (wanted is { }
            && _sides.SelectMany(side => side.Slots)
                   .FirstOrDefault(slot => wanted.Ids.Any(other => slot.Ids.Contains(other, StringComparer.Ordinal)))
               is { } beside)
        {
            beside.Ids.Add(id);
            beside.Selected = id;

            return;
        }

        var made = new Slot { Selected = id };

        made.Ids.Add(id);
        Of(SvgViewerDockSide.Right).Slots.Add(made);
    }

    /// <summary>Finds a place for any panel the line said nothing about.</summary>
    /// <remarks>
    /// A line is written by whoever arranged one, and a host can hand over a panel that arrangement
    /// never saw — one added since, or one named differently. Left unplaced it would simply not
    /// appear, which reads as the panel being broken rather than as the layout being old.
    ///
    /// The other way round is left alone: a run may name a panel nothing supplies, and it keeps the
    /// name, so a host that adds that pane back lands where somebody put it rather than at the end.
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

    // ---- reading and writing the line --------------------------------------------------------

    /// <summary>
    /// <c>right 340 project+elements/1/elements/open variables/1.3/variables/open</c> — a side, how
    /// far across it reaches, and then its runs.
    /// </summary>
    private string Written()
    {
        var line = new StringBuilder();

        foreach (var side in _sides.Where(side => side.Slots.Count > 0))
        {
            if (line.Length > 0)
            {
                line.Append(';');
            }

            line.Append(side.Where.ToString().ToLowerInvariant())
                .Append(' ')
                .Append(side.Extent.ToString("0.##", CultureInfo.InvariantCulture));

            foreach (var slot in side.Slots)
            {
                line.Append(' ')
                    .Append(string.Join("+", slot.Ids))
                    .Append('/')
                    .Append(slot.Weight.ToString("0.##", CultureInfo.InvariantCulture))
                    .Append('/')
                    .Append(slot.Selected ?? slot.Ids.FirstOrDefault() ?? string.Empty)
                    .Append('/')
                    .Append(slot.Folded ? "folded" : "open");
            }
        }

        return line.ToString();
    }

    private void Read(string? line)
    {
        var sides = Parsed(line) ?? Parsed(Default)
            ?? throw new InvalidOperationException("The default layout does not read back.");

        _sides.Clear();
        _sides.AddRange(sides);
    }

    /// <summary>The sides a line describes, or null where it describes nothing usable.</summary>
    private static List<Site>? Parsed(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var sides = new List<Site>
        {
            new(SvgViewerDockSide.Left, 260d),
            new(SvgViewerDockSide.Right, 340d),
            new(SvgViewerDockSide.Bottom, 200d)
        };

        var named = new HashSet<string>(StringComparer.Ordinal);

        foreach (var written in line!.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var words = written.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (words.Length < 2
                || !Enum.TryParse<SvgViewerDockSide>(words[0], ignoreCase: true, out var where)
                || !double.TryParse(words[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var extent))
            {
                return null;
            }

            var side = sides.First(candidate => candidate.Where == where);

            side.Extent = Math.Max(extent, where == SvgViewerDockSide.Bottom ? FootMinimum : SideMinimum);

            foreach (var word in words.Skip(2))
            {
                var fields = word.Split('/');
                var slot = new Slot();

                foreach (var id in fields[0].Split('+', StringSplitOptions.RemoveEmptyEntries))
                {
                    // A panel can only be in one place, and a line naming it twice is not one.
                    if (!named.Add(id))
                    {
                        return null;
                    }

                    slot.Ids.Add(id);
                }

                if (slot.Ids.Count == 0)
                {
                    return null;
                }

                if (fields.Length > 1
                    && double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var weight)
                    && weight > 0d)
                {
                    slot.Weight = weight;
                }

                slot.Selected = fields.Length > 2 && slot.Ids.Contains(fields[2], StringComparer.Ordinal)
                    ? fields[2]
                    : slot.Ids[0];

                slot.Folded = fields.Length > 3 && string.Equals(fields[3], "folded", StringComparison.Ordinal);

                side.Slots.Add(slot);
            }
        }

        return sides.Any(side => side.Slots.Count > 0) ? sides : null;
    }

    // ---- the picture -------------------------------------------------------------------------

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

            _middle.Children.Clear();
            _middle.ColumnDefinitions.Clear();

            _root.Children.Clear();
            _root.RowDefinitions.Clear();
            _root.ColumnDefinitions.Clear();

            var foot = Of(SvgViewerDockSide.Bottom);
            var footed = _shows && Live(foot).Count > 0;

            _root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            _root.RowDefinitions.Add(new RowDefinition(new GridLength(footed ? SplitterSize : 0d, GridUnitType.Pixel)));
            _root.RowDefinitions.Add(
                new RowDefinition(new GridLength(footed ? foot.Extent : 0d, GridUnitType.Pixel))
                {
                    MinHeight = footed ? FootMinimum : 0d
                });

            Put(Middle(), 0);

            if (footed)
            {
                var splitter = Splitter(GridResizeDirection.Rows);

                Put(splitter, 1);
                Watch(splitter, foot);

                Put(Run(foot), 2);
            }
        }
        finally
        {
            _building = false;
        }
    }

    /// <summary>The drawing, with whatever is down either side of it.</summary>
    private Control Middle()
    {
        var left = Of(SvgViewerDockSide.Left);
        var right = Of(SvgViewerDockSide.Right);

        var lefted = _shows && Live(left).Count > 0;
        var righted = _shows && Live(right).Count > 0;

        var middle = _middle;

        middle.ColumnDefinitions.Add(
            new ColumnDefinition(new GridLength(lefted ? left.Extent : 0d, GridUnitType.Pixel))
            {
                MinWidth = lefted ? SideMinimum : 0d
            });

        middle.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(lefted ? SplitterSize : 0d, GridUnitType.Pixel)));
        middle.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star) { MinWidth = 160d });
        middle.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(righted ? SplitterSize : 0d, GridUnitType.Pixel)));

        middle.ColumnDefinitions.Add(
            new ColumnDefinition(new GridLength(righted ? right.Extent : 0d, GridUnitType.Pixel))
            {
                MinWidth = righted ? SideMinimum : 0d
            });

        if (lefted)
        {
            Column(middle, Run(left), 0);

            var splitter = Splitter(GridResizeDirection.Columns);

            Column(middle, splitter, 1);
            Watch(splitter, left);
        }

        Column(middle, _centre, 2);

        if (righted)
        {
            var splitter = Splitter(GridResizeDirection.Columns);

            Column(middle, splitter, 3);
            Watch(splitter, right);

            Column(middle, Run(right), 4);
        }

        return middle;
    }

    /// <summary>One side: its runs, one after another, with a splitter between each pair.</summary>
    private Control Run(Site side)
    {
        var down = side.Where != SvgViewerDockSide.Bottom;
        var grid = new Grid();
        var slots = Live(side);

        for (var index = 0; index < slots.Count; index++)
        {
            if (index > 0)
            {
                Define(grid, down, new GridLength(SplitterSize, GridUnitType.Pixel), 0d);
            }

            var slot = slots[index];

            Define(
                grid,
                down,
                slot.Folded ? GridLength.Auto : new GridLength(slot.Weight, GridUnitType.Star),
                slot.Folded ? 0d : SlotMinimum);
        }

        var at = 0;

        for (var index = 0; index < slots.Count; index++)
        {
            if (index > 0)
            {
                var splitter = Splitter(down ? GridResizeDirection.Rows : GridResizeDirection.Columns);

                Place(grid, down, splitter, at++);
                Watch(splitter, side);
            }

            Place(grid, down, Shown(slots[index], index > 0 && down), at++);
        }

        return new Border
        {
            BorderThickness = side.Where switch
            {
                SvgViewerDockSide.Left => new Thickness(0, 0, 1, 0),
                SvgViewerDockSide.Right => new Thickness(1, 0, 0, 0),
                _ => new Thickness(0, 1, 0, 0)
            },
            BorderBrush = Divider,
            Child = grid
        };
    }

    /// <summary>One run: its header, and whatever of it is on top.</summary>
    private Control Shown(Slot slot, bool lined)
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };

        var header = Header(slot);

        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        if (!slot.Folded)
        {
            var body = new Panel();

            foreach (var id in slot.Ids)
            {
                if (_regions.FirstOrDefault(region => string.Equals(region.Id, id, StringComparison.Ordinal))
                    is not { } region)
                {
                    continue;
                }

                var host = new Border
                {
                    Child = region.Content,
                    IsVisible = string.Equals(slot.Selected, id, StringComparison.Ordinal)
                };

                _filled.Add(host);
                body.Children.Add(host);
            }

            Grid.SetRow(body, 1);
            grid.Children.Add(body);
        }

        return lined
            ? new Border { BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Divider, Child = grid }
            : grid;
    }

    private Control Header(Slot slot)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal };

        var chevron = new Border
        {
            Classes = { "chevron" },
            Background = Brushes.Transparent,
            Width = 18d,
            Child = new TextBlock
            {
                Text = slot.Folded ? "▸" : "▾",
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        chevron.PointerPressed += (_, e) =>
        {
            e.Handled = true;

            slot.Folded = !slot.Folded;

            Rebuild();
            Moved();
        };

        bar.Children.Add(chevron);

        foreach (var id in slot.Ids)
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

            if (string.Equals(slot.Selected, id, StringComparison.Ordinal))
            {
                tab.Classes.Add("selected");
            }

            var chosen = id;

            tab.PointerPressed += (_, e) =>
            {
                e.Handled = true;

                if (string.Equals(slot.Selected, chosen, StringComparison.Ordinal))
                {
                    return;
                }

                slot.Selected = chosen;

                Rebuild();
                Moved();
            };

            bar.Children.Add(tab);
        }

        return new Border { Classes = { "slot" }, Background = Brushes.Transparent, Child = bar };
    }

    /// <summary>Takes the sizes back off the grid once a hand has finished dragging a splitter.</summary>
    private void Watch(GridSplitter splitter, Site side)
        => splitter.DragCompleted += (_, _) =>
        {
            if (_building)
            {
                return;
            }

            Measure(side);
            Moved();
        };

    /// <summary>Reads a side's extent and its runs' weights back out of what is on screen.</summary>
    private void Measure(Site side)
    {
        var down = side.Where != SvgViewerDockSide.Bottom;

        if (_shows)
        {
            var reach = side.Where switch
            {
                SvgViewerDockSide.Left => LengthOf(0, across: true),
                SvgViewerDockSide.Right => LengthOf(4, across: true),
                _ => LengthOf(2, across: false)
            };

            if (reach > 0d)
            {
                side.Extent = reach;
            }
        }

        // The weights are a proportion, so what they are read back as only has to keep the ratio.
        var run = Body(side);

        if (run is null)
        {
            return;
        }

        var lengths = down
            ? run.RowDefinitions.Select(row => row.ActualHeight).ToList()
            : run.ColumnDefinitions.Select(column => column.ActualWidth).ToList();

        var slots = Live(side);

        for (int index = 0, at = 0; index < slots.Count; index++, at++)
        {
            if (index > 0)
            {
                at++;
            }

            if (at < lengths.Count && lengths[at] > 0d && !slots[index].Folded)
            {
                slots[index].Weight = Math.Round(lengths[at] / 100d, 2);
            }
        }
    }

    private double LengthOf(int index, bool across)
    {
        if (_root.Children.FirstOrDefault() is not Grid middle)
        {
            return 0d;
        }

        return across
            ? index < middle.ColumnDefinitions.Count ? middle.ColumnDefinitions[index].ActualWidth : 0d
            : index < _root.RowDefinitions.Count ? _root.RowDefinitions[index].ActualHeight : 0d;
    }

    private Grid? Body(Site side)
    {
        var host = side.Where == SvgViewerDockSide.Bottom
            ? _root.Children.ElementAtOrDefault(2)
            : (_root.Children.FirstOrDefault() as Grid)?.Children
                .FirstOrDefault(child => Grid.GetColumn(child) == (side.Where == SvgViewerDockSide.Left ? 0 : 4));

        return (host as Border)?.Child as Grid;
    }

    private void Moved()
    {
        if (!_building)
        {
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---- grid plumbing -----------------------------------------------------------------------

    private void Put(Control control, int row)
    {
        Grid.SetRow(control, row);
        _root.Children.Add(control);
    }

    private static void Column(Grid grid, Control control, int column)
    {
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }

    private static void Define(Grid grid, bool down, GridLength length, double least)
    {
        if (down)
        {
            grid.RowDefinitions.Add(new RowDefinition(length) { MinHeight = least });
        }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(length) { MinWidth = least });
        }
    }

    private static void Place(Grid grid, bool down, Control control, int at)
    {
        if (down)
        {
            Grid.SetRow(control, at);
        }
        else
        {
            Grid.SetColumn(control, at);
        }

        grid.Children.Add(control);
    }

    private static GridSplitter Splitter(GridResizeDirection direction)
        => new() { ResizeDirection = direction, Background = Brushes.Transparent };
}
