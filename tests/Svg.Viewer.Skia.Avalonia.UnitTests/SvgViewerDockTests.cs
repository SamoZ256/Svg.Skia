// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>
/// The panels arranged around a drawing: where they are, how big, and what is folded away.
/// </summary>
/// <remarks>
/// Driven through <see cref="SvgViewerDock.Layout"/> and through what ends up on screen, rather than
/// by reaching into the grids — the grids are what this is free to change.
/// </remarks>
public class SvgViewerDockTests
{
    private static (Window Window, SvgViewerDock Dock, Border[] Panels) Host(string? layout = null)
    {
        var panels = new[]
        {
            new Border { Name = "project" },
            new Border { Name = "variables" },
            new Border { Name = "element" },
            new Border { Name = "elements" }
        };

        var dock = new SvgViewerDock(new Border { Name = "drawing" })
        {
            Regions = new[]
            {
                new SvgViewerRegion("project", "Project", panels[0]),
                new SvgViewerRegion("variables", "Variables", panels[1]),
                new SvgViewerRegion("element", "Element", panels[2]),
                new SvgViewerRegion("elements", "Elements", panels[3])
            }
        };

        if (layout is { })
        {
            dock.Layout = layout;
        }

        var window = new Window { Width = 900, Height = 700, Background = Brushes.White, Content = dock.Root };

        window.Show();
        Lay(window);

        return (window, dock, panels);
    }

    private static void Lay(Window window)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        Dispatcher.UIThread.RunJobs();
    }

    private static double Wide(Visual visual) => visual.Bounds.Width;

    /// <summary>Whether a panel is on screen at all — a folded run does not build its contents.</summary>
    private static bool Up(Window window, Visual panel) => window.GetVisualDescendants().Contains(panel);

    /// <summary>
    /// Two open and two in a strip, and the strip is at the top.
    /// </summary>
    /// <remarks>
    /// The arrangement this comes up in. The tree and the host's own pane are read one at a time, so
    /// they share a run; the variables and the attributes have one each, because a variable is
    /// dragged onto an attribute and behind a tab each would hide the other.
    /// </remarks>
    [AvaloniaFact]
    public void A_Viewer_Nobody_Has_Arranged_Puts_Two_In_A_Strip_And_Opens_Two()
    {
        var (window, dock, panels) = Host();

        Assert.Equal(SvgViewerDock.Default, dock.Layout);

        // The strip is one run holding both, and the tree is what it opens on.
        Assert.Equal("elements", dock.Selected("project"));
        Assert.Equal("elements", dock.Selected("elements"));

        Assert.False(panels[0].IsEffectivelyVisible);
        Assert.True(panels[3].IsEffectivelyVisible);


        // And the two that are dragged between are both there at once.
        Assert.True(panels[1].IsEffectivelyVisible);
        Assert.True(panels[2].IsEffectivelyVisible);

        window.Close();
    }

    /// <summary>The runs are weighted rather than equal, the deepest going to what needs it.</summary>
    /// <remarks>
    /// Equal thirds gave the attributes 217px of the 1209 they ask for while the tree — the one run
    /// whose content is elastic — had a fixed 200. The proportion is what is pinned here; the
    /// numbers it comes to depend on the window.
    /// </remarks>
    [AvaloniaFact]
    public void The_Run_That_Needs_The_Most_Room_Gets_The_Most()
    {
        var (window, _, panels) = Host();

        // A run is its header and its body, and the weights are shares of the run: measuring the
        // bodies alone takes a header's worth off each of them and skews the proportion.
        var header = window.GetVisualDescendants().OfType<Border>()
            .First(found => found.Classes.Contains("slot")).Bounds.Height;

        var strip = panels[3].Bounds.Height + header;
        var variables = panels[1].Bounds.Height + header;
        var element = panels[2].Bounds.Height + header;

        Assert.True(element > variables, $"the attributes got {element} against {variables}");
        Assert.True(variables > strip, $"the variables got {variables} against {strip}");

        // 1 : 1.3 : 1.7, near enough that a splitter's 6px does not read as a change of mind.
        Assert.Equal(1.3d, variables / strip, 0.1d);
        Assert.Equal(1.7d, element / strip, 0.1d);

        window.Close();
    }

    [AvaloniaFact]
    public void A_Folded_Run_Gives_Its_Room_Back_And_Comes_Back_The_Size_It_Was()
    {
        var (window, dock, panels) = Host();

        var was = panels[2].Bounds.Height;

        dock.Fold("variables", true);
        Lay(window);

        Assert.True(dock.Folded("variables"));
        Assert.False(Up(window, panels[1]));
        Assert.True(panels[2].Bounds.Height > was, $"{panels[2].Bounds.Height} is not more than {was}");

        dock.Fold("variables", false);
        Lay(window);

        Assert.True(Up(window, panels[1]));
        Assert.Equal(was, panels[2].Bounds.Height, 1d);

        window.Close();
    }

    /// <summary>
    /// A run nothing fills takes no room at all, splitter included.
    /// </summary>
    /// <remarks>
    /// The bug this found: hiding the tree zeroed the run and left its 6px splitter row behind, so
    /// every drawing tab paid for a splitter nobody could see — and a run still naming a panel the
    /// host no longer offers went on holding a third of the column.
    /// </remarks>
    [AvaloniaFact]
    public void A_Run_With_Nothing_In_It_Takes_No_Room()
    {
        var (window, dock, panels) = Host();

        var was = panels[1].Bounds.Height + panels[2].Bounds.Height;

        dock.Show("project", false);
        dock.Show("elements", false);
        Lay(window);

        var now = panels[1].Bounds.Height + panels[2].Bounds.Height;
        var headers = window.GetVisualDescendants().OfType<Border>()
            .Where(found => found.Classes.Contains("slot"))
            .Sum(found => found.Bounds.Height);

        Assert.True(now > was, $"{now} is not more than {was}");

        // All of it: the two that are left, their two headers, and the one splitter between them.
        Assert.Equal(window.Height - headers - 6d, now, 1d);

        window.Close();
    }

    [AvaloniaFact]
    public void Hiding_A_Panel_And_Showing_It_Again_Puts_It_Back_Beside_Its_Neighbour()
    {
        var (window, dock, _) = Host();

        dock.Show("elements", false);

        Assert.False(dock.Shows("elements"));
        Assert.DoesNotContain("elements", dock.Layout, StringComparison.Ordinal);

        dock.Show("elements", true);

        Assert.True(dock.Shows("elements"));
        Assert.Contains("project+elements", dock.Layout, StringComparison.Ordinal);

        window.Close();
    }

    [AvaloniaFact]
    public void An_Arrangement_Reads_Back_The_Way_It_Was_Written()
    {
        var (window, dock, panels) = Host();

        var was = dock.Layout;

        const string Arranged = "row(variables/300px/variables/open,col(*/1,project+element+elements/180px/element/open)/1)";

        dock.Layout = Arranged;

        Lay(window);

        Assert.True(panels[1].IsEffectivelyVisible);
        Assert.True(panels[2].IsEffectivelyVisible);
        Assert.False(panels[3].IsEffectivelyVisible);

        Assert.Equal(Arranged, dock.Layout);

        dock.Layout = was;
        Lay(window);

        Assert.Equal(was, dock.Layout);

        window.Close();
    }

    /// <summary>A line this cannot read is replaced whole rather than half applied.</summary>
    [AvaloniaFact]
    public void A_Line_That_Makes_No_Sense_Falls_Back_To_The_Default()
    {
        var (window, dock, _) = Host();

        foreach (var nonsense in new[] { "", "   ", "sideways 300 variables/1", "right", "right 340 ", "right 340 /1/x/open" })
        {
            dock.Layout = nonsense;

            Assert.Equal(SvgViewerDock.Default, dock.Layout);
        }

        // And a line naming one panel twice is not an arrangement either.
        dock.Layout = "right 340 variables/1/variables/open variables/1/variables/open";

        Assert.Equal(SvgViewerDock.Default, dock.Layout);

        window.Close();
    }

    /// <summary>A panel the arrangement never heard of is given a place rather than left out.</summary>
    [AvaloniaFact]
    public void A_Panel_The_Line_Says_Nothing_About_Still_Gets_A_Place()
    {
        var (window, dock, _) = Host("right 340 variables/1/variables/open");

        Assert.True(dock.Shows("project"));
        Assert.True(dock.Shows("element"));
        Assert.True(dock.Shows("elements"));

        window.Close();
    }

    /// <summary>Hiding them all gives the drawing the whole of the body.</summary>
    [AvaloniaFact]
    public void With_The_Panels_Off_The_Drawing_Has_All_Of_It()
    {
        var (window, dock, _) = Host();

        var drawing = window.GetVisualDescendants().OfType<Border>().First(found => found.Name == "drawing");
        var narrow = Wide(drawing);

        dock.ShowsRegions = false;
        Lay(window);

        Assert.True(Wide(drawing) >= narrow + 340d, $"{Wide(drawing)} is not {narrow} and the strip");

        dock.ShowsRegions = true;
        Lay(window);

        Assert.Equal(narrow, Wide(drawing), 1d);

        window.Close();
    }
    // ---- carrying a panel somewhere else ---------------------------------------------------------

    /// <summary>A simulated move does not carry the button by itself.</summary>
    private const RawInputModifiers Held = RawInputModifiers.LeftMouseButton;

    private static Border Tab(Window window, string header)
        => window.GetVisualDescendants().OfType<Border>()
            .First(found => found.Classes.Contains("pane")
                            && found.Child is TextBlock said
                            && string.Equals(said.Text, header, StringComparison.Ordinal));

    private static Point Middle(Window window, Visual visual)
        => visual.TranslatePoint(new Point(visual.Bounds.Width / 2d, visual.Bounds.Height / 2d), window)!.Value;

    /// <summary>Takes a panel by its header and lets go of it at <paramref name="to"/>.</summary>
    /// <remarks>
    /// Past the threshold first, in a move of its own, because one long move would rearrange the
    /// body without ever proving that a press on its own does not.
    /// </remarks>
    private static void Carry(Window window, string header, Point to)
    {
        var from = Middle(window, Tab(window, header));

        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point(from.X + 10d, from.Y), Held);
        Dispatcher.UIThread.RunJobs();

        window.MouseMove(to, Held);
        Dispatcher.UIThread.RunJobs();

        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Lay(window);
    }

    [AvaloniaFact]
    public void A_Press_That_Goes_Nowhere_Only_Chooses_The_Panel()
    {
        var (window, dock, panels) = Host();

        var at = Middle(window, Tab(window, "Project"));

        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Lay(window);

        // Only which of the two is on top has changed; nothing has moved.
        Assert.Equal(SvgViewerDock.Default, dock.Layout.Replace("/project/", "/elements/", StringComparison.Ordinal));
        Assert.Equal("project", dock.Selected("elements"));
        Assert.True(panels[0].IsEffectivelyVisible);

        window.Close();
    }

    /// <summary>
    /// Choosing the other panel of a run swaps it in place. A rebuild moves the middle, and a tab
    /// strip that is moved comes back on its first tab, so Studio flashed the project's tab.
    /// </summary>
    [AvaloniaFact]
    public void Choosing_The_Other_Panel_Of_A_Run_Leaves_The_Middle_Where_It_Is()
    {
        var (window, dock, panels) = Host();
        var rebuilt = 0;

        dock.Rebuilding += (_, _) => rebuilt++;

        var at = Middle(window, Tab(window, "Project"));

        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Lay(window);

        Assert.Equal(0, rebuilt);
        Assert.True(panels[0].IsEffectivelyVisible);
        Assert.False(panels[3].IsEffectivelyVisible);
        Assert.Contains("selected", Tab(window, "Project").Classes);
        Assert.DoesNotContain("selected", Tab(window, "Elements").Classes);

        window.Close();
    }

    [AvaloniaFact]
    public void A_Panel_Dropped_On_A_Header_Goes_In_Beside_What_It_Holds()
    {
        var (window, dock, _) = Host();

        Carry(window, "Variables", Middle(window, Tab(window, "Element")));

        Assert.Contains("element+variables", dock.Layout, StringComparison.Ordinal);

        // And the one carried is the one on top of the run it landed in.
        Assert.Equal("variables", dock.Selected("element"));

        window.Close();
    }

    [AvaloniaFact]
    public void A_Panel_Dropped_On_The_Edge_Of_A_Pane_Goes_Above_Or_Below_It()
    {
        var (window, dock, panels) = Host();

        // The top of the pane the attributes are in, which is below the variables to begin with.
        var element = panels[2];
        var top = element.TranslatePoint(new Point(element.Bounds.Width / 2d, 2d), window)!.Value;

        Carry(window, "Elements", top);

        Assert.Equal(
            new[] { "project", "variables", "elements", "element" },
            Order(dock.Layout));

        window.Close();
    }

    /// <summary>The panels named in a layout, in the order the line puts them.</summary>
    private static IReadOnlyList<string> Order(string layout)
        => layout.Split('(', ')', ',', '/')
            .Where(word => word.Length > 0 && char.IsLetter(word[0]) && word is not ("row" or "col" or "open" or "folded"))
            .Distinct()
            .ToList();

    /// <summary>
    /// Dropped against the foot of the drawing, a panel sits under the drawing and nothing else.
    /// </summary>
    /// <remarks>
    /// The whole of why this is a tree. Three named sides made the foot a row of the body, so a panel
    /// taken there ran the full width and cut the strip beside the drawing in half — with no way to
    /// ask for anything else.
    /// </remarks>
    [AvaloniaFact]
    public void A_Panel_Dropped_Under_The_Drawing_Leaves_The_Strip_Alone()
    {
        var (window, dock, panels) = Host();

        var strip = panels[1].Bounds.Width;
        var drawing = window.GetVisualDescendants().OfType<Border>().First(found => found.Name == "drawing");

        // Well inside the foot of the drawing, rather than against the body's own rim, which is what
        // means "under everything".
        var under = drawing.TranslatePoint(new Point(drawing.Bounds.Width / 2d, drawing.Bounds.Height - 60d), window);

        Carry(window, "Element", under!.Value);

        // The drawing is what was divided, so the attributes are in a column with it.
        Assert.Contains("col(*/", dock.Layout, StringComparison.Ordinal);

        // And the strip beside it is untouched, which it was not before.
        Assert.Equal(strip, panels[1].Bounds.Width, 1d);

        window.Close();
    }

    /// <summary>Dropped against the body's own rim, the same panel runs under everything.</summary>
    /// <remarks>The other half of the same gesture, and the reason there is no setting for it.</remarks>
    [AvaloniaFact]
    public void A_Panel_Dropped_On_The_Rim_Runs_Under_The_Whole_Body()
    {
        var (window, dock, panels) = Host();

        var strip = panels[1].Bounds.Width;
        var tall = panels[1].Bounds.Height;

        Carry(window, "Element", new Point(window.Width / 2d, window.Height - 6d));

        // The root itself was divided, so everything that was there is in a row inside a column.
        Assert.StartsWith("col(row(", dock.Layout, StringComparison.Ordinal);

        // The strip keeps its width and gives up height, which is what "under everything" costs.
        Assert.Equal(strip, panels[1].Bounds.Width, 1d);
        Assert.True(panels[1].Bounds.Height < tall, $"{panels[1].Bounds.Height} is not less than {tall}");

        window.Close();
    }

    [AvaloniaFact]
    public void Carrying_A_Panel_Says_Where_It_Would_Land()
    {
        var (window, _, _) = Host();

        var hint = window.GetVisualDescendants().OfType<Border>().Single(found => found.Name == "Landing");

        Assert.False(hint.IsVisible);

        var from = Middle(window, Tab(window, "Variables"));
        var onto = Middle(window, Tab(window, "Element"));

        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point(from.X + 10d, from.Y), Held);
        window.MouseMove(onto, Held);
        Dispatcher.UIThread.RunJobs();

        Assert.True(hint.IsVisible);

        window.MouseUp(onto, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.False(hint.IsVisible);

        window.Close();
    }
    /// <summary>
    /// The headers are styled wherever the dock is put, not only inside a viewer.
    /// </summary>
    /// <remarks>
    /// The styles used to come from the viewer's own markup, which reaches a dock inside a viewer
    /// and nothing else. Svg.Studio arranges a whole window with one, and its headers came out with
    /// no padding, no pipe under the chosen one and no dimming of the rest — so a run holding three
    /// panels read as "ProjectVariablesElement". This window is a bare one: no viewer, no board.
    /// </remarks>
    [AvaloniaFact]
    public void A_Run_Of_Several_Panels_Keeps_Their_Headers_Apart()
    {
        var (window, dock, _) = Host();

        dock.Layout = "row(*/1,project+variables+elements/300px/project/open)";
        Lay(window);

        var headers = new[] { Tab(window, "Project"), Tab(window, "Variables"), Tab(window, "Elements") };

        Assert.All(headers, header => Assert.True(header.Padding.Left > 0d, $"{header.Padding} is no gap"));

        // And they really are laid out clear of one another, rather than running together.
        for (var index = 1; index < headers.Length; index++)
        {
            var left = headers[index - 1].TranslatePoint(new Point(headers[index - 1].Bounds.Width, 0), window)!.Value;
            var right = headers[index].TranslatePoint(default, window)!.Value;

            Assert.True(right.X >= left.X, $"{headers[index].Child} starts before the one before it ends");
        }

        // The one being read wears the line under it; the rest are dimmed.
        Assert.Contains("selected", headers[0].Classes);
        Assert.DoesNotContain("selected", headers[1].Classes);
        Assert.True(headers[1].Opacity < headers[0].Opacity);

        window.Close();
    }

    /// <summary>A bare dock paints its headers, the lines between panes and the landing from UnoDraw's palette, in either variant.</summary>
    /// <remarks>The test application has Fluent and nothing of Studio's, so whatever paints here the dock brought.</remarks>
    [AvaloniaTheory]
    [InlineData("Light", "#DEDEDE", "#C4C4C4", "#2862B0")]
    [InlineData("Dark", "#292929", "#222222", "#94BFF2")]
    public void A_Dock_Paints_Its_Headers_Without_A_Host(string variant, string header, string divider, string accent)
    {
        var (window, dock, _) = Host();

        window.RequestedThemeVariant = variant == "Dark" ? global::Avalonia.Styling.ThemeVariant.Dark : global::Avalonia.Styling.ThemeVariant.Light;
        dock.Layout = "row(*/1,col(project+variables/1/project/open,elements/1/elements/open)/300px)";
        Lay(window);

        static Color Painted(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

        var slot = window.GetVisualDescendants().OfType<Border>().First(found => found.Classes.Contains("slot"));

        Assert.Equal(Color.Parse(header), Painted(slot.Background));
        Assert.Equal(Color.Parse(divider), Painted(slot.BorderBrush));
        Assert.Equal(Color.Parse(accent), Painted(Tab(window, "Project").BorderBrush));
        Assert.Equal(Color.Parse(divider), Painted(window.GetVisualDescendants().OfType<Border>().First(found => found.Classes.Contains("divider")).BorderBrush));
        Assert.Equal(Color.Parse(accent), Painted(window.GetVisualDescendants().OfType<Border>().Single(found => found.Name == "Landing").Background));

        window.Close();
    }

    /// <summary>The palette a dock carries leaves alone the ground of a canvas inside it, so a standalone viewer keeps its own.</summary>
    [AvaloniaFact]
    public void A_Docks_Palette_Leaves_A_Canvas_Its_Own_Ground()
    {
        var canvas = new SvgViewerCanvas();
        var window = new Window { Width = 600, Height = 400, RequestedThemeVariant = global::Avalonia.Styling.ThemeVariant.Light, Content = new SvgViewerDock(canvas).Root };

        window.Show();
        Lay(window);

        Assert.Equal(new SkiaSharp.SKColor(0xF2, 0xF2, 0xF4), canvas.Background);

        window.Close();
    }

    // ---- a tab's own menu, the keys, and a run too narrow for its tabs ---------------------------

    private const string Three = "row(*/1,col(project+elements+variables/1/project/open,element/1/element/open)/340px)";

    private static MenuItem Entry(Window window, string tab, string header)
        => Tab(window, tab).ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, header));

    private static void Pick(Window window, MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Lay(window);
    }

    [AvaloniaFact]
    public void A_Tab_Moves_Left_Right_And_Below()
    {
        var (window, dock, _) = Host(Three);
        var moved = 0;

        dock.LayoutChanged += (_, _) => moved++;

        Assert.False(Entry(window, "Project", "Move Left").IsEnabled);
        Assert.False(Entry(window, "Variables", "Move Right").IsEnabled);

        Pick(window, Entry(window, "Project", "Move Right"));
        Assert.Contains("col(elements+project+variables/", dock.Layout, StringComparison.Ordinal);

        Pick(window, Entry(window, "Project", "Move Left"));
        Assert.Contains("col(project+elements+variables/", dock.Layout, StringComparison.Ordinal);

        // Under the run it was in, at a share of the column, and the run keeps the rest.
        Pick(window, Entry(window, "Elements", "Move Below"));
        Assert.Contains("col(project+variables/1/project/open,elements/1/elements/open,element/", dock.Layout, StringComparison.Ordinal);
        Assert.False(Entry(window, "Elements", "Move Below").IsEnabled);

        Assert.Equal(3, moved);

        window.Close();
    }

    /// <summary>A tab is right-clicked for its menu.</summary>
    [AvaloniaFact]
    public void A_Right_Click_Opens_A_Tabs_Menu()
    {
        var (window, _, _) = Host(Three);
        var at = Middle(window, Tab(window, "Elements"));

        window.MouseDown(at, MouseButton.Right);
        window.MouseUp(at, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        Assert.True(Tab(window, "Elements").ContextMenu!.IsOpen);

        window.Close();
    }

    /// <summary>Close is offered only for a panel the host can bring back, and is this session's, as the host's own is.</summary>
    [AvaloniaFact]
    public void Close_Only_Where_The_Host_Can_Bring_It_Back()
    {
        var (window, dock, panels) = Host(Three);
        var moved = 0;

        dock.Regions = new[]
        {
            new SvgViewerRegion("project", "Project", panels[0]),
            new SvgViewerRegion("variables", "Variables", panels[1]) { Closable = true },
            new SvgViewerRegion("element", "Element", panels[2]),
            new SvgViewerRegion("elements", "Elements", panels[3])
        };
        Lay(window);

        dock.LayoutChanged += (_, _) => moved++;

        Assert.DoesNotContain(Tab(window, "Project").ContextMenu!.Items.OfType<MenuItem>(), item => Equals(item.Header, "Close"));

        Pick(window, Entry(window, "Variables", "Close"));

        Assert.False(dock.Shows("variables"));
        Assert.Equal(0, moved);

        window.Close();
    }

    /// <summary>A click chooses a panel and leaves the keyboard with the drawing, whose tool letters still answer.</summary>
    [AvaloniaFact]
    public async System.Threading.Tasks.Task Choosing_A_Panel_Leaves_The_Keyboard_With_The_Drawing()
    {
        var viewer = new SvgViewer();
        var window = new Window { Width = 900, Height = 600, Background = Brushes.White, Content = viewer };

        window.Show();
        Assert.True(await viewer.LoadTextAsync("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect width="24" height="24" />
            </svg>
            """));
        Lay(window);

        viewer.Canvas.Focus();
        Dispatcher.UIThread.RunJobs();

        var tab = Tab(window, "Elements");
        var at = Middle(window, tab.Child!);

        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("selected", Tab(window, "Elements").Classes);
        Assert.Same(viewer.Canvas, window.FocusManager!.GetFocusedElement());

        window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("rect", viewer.Drafting.Shape);

        window.Close();
    }

    [AvaloniaFact]
    public void The_Arrow_Keys_Walk_A_Runs_Tabs()
    {
        var (window, dock, _) = Host(Three);

        Tab(window, "Project").Focus();
        Dispatcher.UIThread.RunJobs();

        string Pressed(PhysicalKey key)
        {
            window.KeyPressQwerty(key, RawInputModifiers.None);
            Lay(window);

            var focused = Assert.IsType<Border>(window.FocusManager!.GetFocusedElement());

            Assert.Equal(dock.Selected("project"), focused.Tag);

            return dock.Selected("project")!;
        }

        Assert.Equal("elements", Pressed(PhysicalKey.ArrowRight));
        Assert.Equal("variables", Pressed(PhysicalKey.ArrowRight));
        Assert.Equal("project", Pressed(PhysicalKey.ArrowRight));
        Assert.Equal("variables", Pressed(PhysicalKey.ArrowLeft));
        Assert.Equal("project", Pressed(PhysicalKey.Home));
        Assert.Equal("variables", Pressed(PhysicalKey.End));

        window.Close();
    }

    /// <summary>A run too narrow for its tabs offers every one of them in a menu at its end; one wide enough does not.</summary>
    [AvaloniaFact]
    public void A_Run_Too_Narrow_Offers_The_Rest_In_A_Menu()
    {
        var (window, dock, _) = Host("row(*/1,project+elements+variables+element/200px/project/open)");
        var moved = 0;

        dock.LayoutChanged += (_, _) => moved++;

        Button More() => window.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("more"));

        Assert.True(More().IsVisible);

        var rest = Assert.IsType<MenuFlyout>(More().Flyout);

        Assert.Equal(new[] { "Project", "Elements", "Variables", "Element" }, rest.Items.OfType<MenuItem>().Select(item => (string)item.Header!));

        rest.Items.OfType<MenuItem>().Last().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Lay(window);

        Assert.Equal("element", dock.Selected("element"));
        Assert.Equal(1, moved);

        // And brought into view, the strip scrolled along to it.
        Assert.True(Tab(window, "Element").FindAncestorOfType<ScrollViewer>()!.Offset.X > 0d);

        dock.Layout = "row(*/1,project+elements+variables+element/800px/project/open)";
        Lay(window);

        Assert.False(More().IsVisible);

        window.Close();
    }

    /// <summary>A click on a folded run's tab opens the run, and is remembered like the chevron's.</summary>
    [AvaloniaFact]
    public void A_Click_On_A_Folded_Runs_Tab_Opens_It()
    {
        var (window, dock, panels) = Host("row(*/1,col(project+elements/1/project/folded,variables/1/variables/open)/340px)");
        var moved = 0;

        dock.LayoutChanged += (_, _) => moved++;

        Assert.True(dock.Folded("project"));

        var at = Middle(window, Tab(window, "Project"));

        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Lay(window);

        Assert.False(dock.Folded("project"));
        Assert.True(panels[0].IsEffectivelyVisible);
        Assert.Equal(1, moved);

        window.Close();
    }

    /// <summary>The gap between two panels shows itself under the pointer, so it can be found to drag.</summary>
    [AvaloniaFact]
    public void The_Gap_Lights_Up_Under_The_Pointer()
    {
        var (window, _, _) = Host(Three);
        var gap = window.GetVisualDescendants().OfType<GridSplitter>().First(splitter => splitter.Classes.Contains("gap"));

        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(gap.Background).Color);

        window.MouseMove(Middle(window, gap));
        Lay(window);

        Assert.NotEqual(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(gap.Background).Color);

        window.Close();
    }

    /// <summary>A run whose front panel goes shows its first real one, not a panel nothing supplies yet.</summary>
    [AvaloniaFact]
    public void A_Run_Never_Shows_A_Panel_Nothing_Supplies()
    {
        // Every region named, so settling puts none of them into the run.
        var (window, dock, panels) = Host("row(*/1,col(elements+resources+brushes+variables/1/elements/open,project+element/1/project/open)/340px)");

        dock.Show("elements", false);
        Lay(window);

        Assert.Contains("selected", Tab(window, "Variables").Classes);
        Assert.True(panels[1].IsEffectivelyVisible);

        window.Close();
    }

    /// <summary>A panel put back goes where the default has it among its run, not at the end of it.</summary>
    [AvaloniaFact]
    public void A_Panel_Put_Back_Takes_Its_Place_In_The_Run()
    {
        var (window, dock, _) = Host(Three);

        dock.Fallback = Three;
        dock.Show("project", false);
        dock.Show("elements", false);

        dock.Show("project", true);
        dock.Show("elements", true);

        Assert.Contains("col(project+elements+variables/", dock.Layout, StringComparison.Ordinal);
        Assert.True(dock.Matches(Three));

        window.Close();
    }

    // ---- whether an arrangement is the one a line describes --------------------------------------

    [AvaloniaFact]
    public void A_Line_Matches_Whatever_Its_Sizes_Folds_And_Tabs()
    {
        var (window, dock, _) = Host(Three);

        Assert.True(dock.Matches(Three));
        Assert.True(dock.Matches("row(*/2,col(project+elements+variables/3/variables/folded,element/1/element/open)/500px)"));

        Assert.False(dock.Matches("row(*/1,col(elements+project+variables/1/project/open,element/1/element/open)/340px)"));
        Assert.False(dock.Matches("row(*/1,col(project+elements/1/project/open,variables+element/1/element/open)/340px)"));
        Assert.False(dock.Matches("not a line"));

        window.Close();
    }

    /// <summary>A panel taken off the arrangement is taken out of the line too, so the arrangement still reads as itself.</summary>
    [AvaloniaFact]
    public void A_Panel_Taken_Off_Still_Matches()
    {
        var (window, dock, _) = Host(Three);

        dock.Show("elements", false);

        Assert.True(dock.Matches(Three));

        // A panel nothing supplies yet is kept in the line it was laid out from, and matches it.
        var later = "row(*/1,col(project+elements+variables/1/project/open,element+transform/1/element/open)/340px)";

        dock.Layout = later;

        Assert.True(dock.Matches(later));
        Assert.False(dock.Matches(Three));

        window.Close();
    }
}
