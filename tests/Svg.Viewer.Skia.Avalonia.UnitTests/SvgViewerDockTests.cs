// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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

        var strip = panels[3].Bounds.Height;
        var variables = panels[1].Bounds.Height;
        var element = panels[2].Bounds.Height;

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

        dock.Layout = "left 300 variables/1/variables/open;bottom 180 project+element+elements/1/element/open";

        Lay(window);

        Assert.True(panels[1].IsEffectivelyVisible);
        Assert.True(panels[2].IsEffectivelyVisible);
        Assert.False(panels[3].IsEffectivelyVisible);

        Assert.Equal(
            "left 300 variables/1/variables/open;bottom 180 project+element+elements/1/element/open",
            dock.Layout);

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
}
