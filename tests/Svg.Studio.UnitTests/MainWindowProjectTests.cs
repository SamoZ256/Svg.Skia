using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using Svg.CodeGen.Skia.Projects;
using Svg.Expressions;
using Svg.Skia;
using Svg.Viewer.Skia.Avalonia;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// Opening an svgc project: the tree beside the tabs, and a drawing shown at the size the project
/// builds it at rather than the one it was written with.
/// </summary>
public class MainWindowProjectTests : IDisposable
{
    /// <summary>The drawing in the selected tab, which is what an edit is made to.</summary>
    private static SvgViewer Viewer(Window window)
        => window.GetVisualDescendants().OfType<SvgViewer>().First();
    private const string Drawing = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <rect width="24" height="24" fill="#00ff00" />
        </svg>
        """;

    private const string Project = """
        <studio namespace="Demo.Icons">

          <drawing name="home" class="Home">
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect width="24" height="24" fill="#00ff00" />
            </svg>
          </drawing>

          <!-- kept, so an edit is proven not to reformat the file -->
          <group name="Large" namespace="Demo.Icons.Large" scale="2">
            <drawing name="badge" class="BadgeLarge">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
                <rect width="24" height="24" fill="#00ff00" />
              </svg>
            </drawing>
          </group>

        </studio>
        """;

    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    private string Write(string name, string text)
    {
        var path = Path.Combine(_directory, name);

        File.WriteAllText(path, text);

        return path;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>A window with <paramref name="path"/> opened through the route a drop also ends in.</summary>
    private static async Task<MainWindow> Host(string path)
    {
        var window = new MainWindow();

        // Answered rather than shown: a dialog nobody clicks blocks a headless run for ever, and
        // what a test wants from one is the sentence, which the tests that read it override this for.
        window.Announce = (_, _) => Task.CompletedTask;

        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Nothing is open until this: a window starts empty, with no tab standing in for a file.
        await window.OpenAsync(new[] { path });

        Dispatcher.UIThread.RunJobs();

        return window;
    }

    /// <summary>One drawing row holding the fixture art, for a project written in a test.</summary>
    private static string Holding(string name, string attributes = "") => $"""
          <drawing name="{name}"{attributes}>
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect width="24" height="24" fill="#00ff00" />
            </svg>
          </drawing>
        """;

    private static TreeView Tree(MainWindow window) => window.FindControl<TreeView>("ProjectTree")!;

    private static TabControl Tabs(MainWindow window) => window.FindControl<TabControl>("Tabs")!;

    /// <summary>The tree rows, flattened, by the label each shows.</summary>
    private static string[] Rows(TreeViewItem item)
        => new[] { (string)item.Header! }
            .Concat(item.Items.OfType<TreeViewItem>().SelectMany(Rows))
            .ToArray();

    [AvaloniaFact]
    public async Task A_Project_Opens_Into_The_Pane_And_Onto_Its_Own_Settings()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgstudio", Project));

        Assert.Equal("icons.svgstudio", window.Workspace!.Name);

        // The pane and one tab: the project's own settings. A window with a project open and
        // nothing on it showed the tree and an empty strip, and the row to click to see anything
        // was the one row of the tree that is always there.
        var tab = Assert.IsType<TabItem>(Assert.Single(Tabs(window).Items));

        Assert.Same(window.Workspace.Document.Root, Assert.IsType<GroupPanel>(tab.Content).Node);
        Assert.Same(tab, Tabs(window).SelectedItem);

        Assert.True(window.FindControl<Border>("ProjectPaneHost")!.IsVisible);

        var root = Assert.IsType<TreeViewItem>(Assert.Single(Tree(window).Items));

        // Every row is what it calls itself, which is what the format asks each of them for.
        Assert.Equal(new[] { "Project", "home", "Large", "badge" }, Rows(root));
    }

    [AvaloniaFact]
    public async Task Every_Row_Is_What_It_Calls_Itself()
    {
        // A name rather than the settings a row hands down, which is what the old format had to be
        // labelled by: two groups beside each other could be named off different attributes, and
        // one that set neither read "group" like every other.
        var window = await Host(Write("icons.svgstudio", $"""
            <studio>
              <group name="Nav" namespace="Nav" class="Both">
                <drawing name="home"><svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" /></drawing>
              </group>
              <group name="Badges" scale="2">
                <drawing name="badge"><svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" /></drawing>
                <drawing name="badge-huge" scale="4"><svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" /></drawing>
              </group>
            </studio>
            """));

        var root = Assert.IsType<TreeViewItem>(Assert.Single(Tree(window).Items));

        Assert.Equal(
            new[] { "Project", "Nav", "home", "Badges", "badge", "badge-huge" },
            Rows(root));
    }

    [AvaloniaFact]
    public async Task Drawings_Holding_The_Same_Art_Are_Rows_Of_Their_Own()
    {
        // The shape a project is for: one drawing, built at several sizes under several names. Each
        // of them holds its own copy of the art now, so editing one is not editing the others.
        var window = await Host(Write("icons.svgstudio", Twice));

        var root = Assert.IsType<TreeViewItem>(Assert.Single(Tree(window).Items));

        Assert.Equal(new[] { "Project", "Large", "badge", "Huge", "badge-huge" }, Rows(root));

        var drawings = window.Workspace!.Document.Root.Drawings.ToList();

        Assert.Equal(2, drawings.Count);
        Assert.NotSame(drawings[0].Element, drawings[1].Element);
    }

    [AvaloniaFact]
    public async Task A_Group_Opens_In_A_Tab_Showing_What_It_Builds()
    {

        var window = await Host(Write("icons.svgstudio", Project));

        var root = (TreeViewItem)Tree(window).Items[0]!;
        var group = (ProjectNode)((TreeViewItem)root.Items[1]!).Tag!;

        await window.ShowAsync(group);
        Dispatcher.UIThread.RunJobs();

        var panel = Assert.IsType<GroupPanel>(((TabItem)Tabs(window).SelectedItem!).Content);
        Assert.Same(group, panel.Node);

        // Chosen twice is the same tab, not a second one. Two in the strip: the project's own,
        // which opening it put there, and this group's.
        await window.ShowAsync(group);
        Assert.Equal(2, Tabs(window).Items.Count);
    }

    // ---- the canvas on a group's tab ----------------------------------------------------------

    [AvaloniaFact]
    public async Task A_Drawing_Keeps_The_Line_The_List_Showed_As_Its_Caption()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgstudio", Project));

        Assert.Equal(
            new[]
            {
                "home\nDemo.Icons.Home   as written",
                "badge\nDemo.Icons.Large.BadgeLarge   ×2"
            },
            Drawn(Panel(window, "Project")).Select(placed => placed.Label).ToArray());
    }

    /// <summary>The whole branch, which is what the group builds.</summary>
    [AvaloniaFact]
    public async Task A_Group_Draws_The_Drawings_Of_The_Groups_Under_It()
    {

        var window = await Host(Write("icons.svgstudio", Project));

        var root = (TreeViewItem)Tree(window).Items[0]!;

        // The project row shows both; the group under it shows the one it holds. Read before the
        // tab is switched, since a panel that is not the one being looked at holds no pictures.
        Assert.Equal(2, Drawn(Panel(window, "Project")).Count);

        await window.ShowAsync((ProjectNode)((TreeViewItem)root.Items[1]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(Drawn(Panel(window, "Large")));
    }

    /// <summary>
    /// A drawing built at twice the size is drawn twice the size.
    /// </summary>
    /// <remarks>
    /// The point of the canvas rather than a detail of it: a group's settings are about size, and a
    /// tile per drawing would show every size the same and answer nothing.
    /// </remarks>
    [AvaloniaFact]
    public async Task Drawings_Are_Drawn_True_To_Each_Other()
    {

        var window = await Host(Write("icons.svgstudio", Project));
        var drawn = Drawn(Panel(window, "Project"));

        // The fixture is 24 square; the group builds it at ×2. Nothing is fitted to a tile: the
        // canvas is placed in drawing units and its own zoom is what makes the spread fit.
        Assert.Equal(24f, Picture(drawn[0])!.CullRect.Width);
        Assert.Equal(48f, Picture(drawn[1])!.CullRect.Width);
    }

    [AvaloniaFact]
    public async Task A_Group_With_No_Drawings_Still_Says_So()
    {
        var window = await Host(Write("icons.svgstudio", $"""
            <studio>
              <group name="Empty" namespace="Empty" />
            </studio>
            """));

        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.ShowAsync((ProjectNode)((TreeViewItem)root.Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var panel = Panel(window, "Empty");

        Assert.Empty(Drawn(panel));
        Assert.Contains(
            panel.GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text == "This group holds no drawings." && block.IsVisible);
    }

    [AvaloniaFact]
    public async Task A_Drawing_That_Cannot_Be_Read_Costs_One_Cell()
    {
        var window = await Host(Write("icons.svgstudio", $"""
            <studio namespace="Demo.Icons">
            {Holding("home", " class=\"Home\"")}
              <drawing name="badge">
                <svg xmlns="https://example.invalid/not-svg" viewBox="0 0 24 24" />
              </drawing>
            </studio>
            """));

        var panel = Panel(window, "Project");

        // The one that reads is still drawn, and the one that does not is said out loud.
        Assert.Single(Drawn(panel));
        Assert.StartsWith("home", Drawn(panel)[0].Label);

        Assert.Contains(
            panel.GetVisualDescendants().OfType<TextBlock>(),
            block => block.IsVisible && block.Text is { } said && said.StartsWith("badge could not be read"));
    }

    /// <summary>
    /// The pictures live as long as the tab does, not as long as it is the one being looked at.
    /// </summary>
    /// <remarks>
    /// They used to be let go the moment another tab was picked and read again on the way back, so
    /// a glance at a neighbouring tab re-parsed the whole group and fitted the board afresh — and so
    /// did dragging this tab along the strip, which removes the item and puts it back. A tab nobody
    /// edited while it was away has nothing to do on its return.
    /// </remarks>
    [AvaloniaFact]
    public async Task Drawings_Are_Let_Go_When_The_Tab_Goes()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var panel = Panel(window, "Project");
        var canvas = Canvas(panel);

        Assert.Equal(2, Drawn(panel).Count);

        canvas.ZoomTo(canvas.Scale * 3d, new Point(10, 10));

        var scale = canvas.Scale;
        var offsetX = canvas.OffsetX;
        var pictures = Drawn(panel).Select(placed => placed.Svg).ToList();

        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.ShowAsync((ProjectNode)((TreeViewItem)root.Items[1]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        Tabs(window).SelectedItem = Tabs(window).Items.OfType<TabItem>()
            .Single(item => ReferenceEquals(item.Content, panel));
        Dispatcher.UIThread.RunJobs();

        // The same drawings, still built, and the view still on them.
        Assert.All(Drawn(panel), placed => Assert.NotNull(Picture(placed)));
        Assert.All(pictures.Zip(Drawn(panel).Select(placed => placed.Svg)), pair => Assert.Same(pair.First, pair.Second));

        Assert.Equal(scale, canvas.Scale, 6);
        Assert.Equal(offsetX, canvas.OffsetX, 6);

        // Closing the project closes its tabs, and that is what lets the pictures go.
        Assert.True(await window.CloseProjectAsync());

        Assert.Empty(Drawn(panel));
        Assert.All(pictures, svg => Assert.Null(svg.Picture));
    }

    /// <summary>An edit that arrives while a tab is away is waiting for it when it comes back.</summary>
    /// <remarks>
    /// The other side of keeping the pictures: a tab that skipped its rebuild on the way back would
    /// go on showing a drawing the project no longer holds.
    /// </remarks>
    [AvaloniaFact]
    public async Task An_Edit_While_A_Tab_Is_Away_Is_Read_On_Its_Return()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var panel = Panel(window, "Project");

        var before = Drawn(panel).Select(placed => placed.Svg).ToList();

        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.ShowAsync((ProjectNode)((TreeViewItem)root.Items[1]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var edited = (ProjectDrawing)window.Workspace!.Document.Root.Children[0];

        edited.SetText(edited.Text.Replace("#00ff00", "#0000ff", StringComparison.Ordinal));

        window.Workspace.Save();
        Dispatcher.UIThread.RunJobs();

        Tabs(window).SelectedItem = Tabs(window).Items.OfType<TabItem>()
            .Single(item => ReferenceEquals(item.Content, panel));
        Dispatcher.UIThread.RunJobs();

        Assert.NotSame(before[0], Drawn(panel)[0].Svg);
    }

    /// <summary>
    /// The canvas is a canvas: it zooms, it fits, and it outlines what it holds.
    /// </summary>
    /// <remarks>
    /// The viewer's surface rather than a second one written here, so the gestures, the clamps and
    /// the outline are the ones a drawing's own tab has — these press the buttons that drive it.
    /// </remarks>
    [AvaloniaFact]
    public async Task The_Canvas_Zooms_And_Outlines_Like_A_Drawings_Own()
    {

        var window = await Host(Write("icons.svgstudio", Project));
        var panel = Panel(window, "Project");
        var canvas = Canvas(panel);

        var fitted = canvas.Scale;

        Assert.True(fitted > 0d);

        Press(panel, "+");
        Assert.True(canvas.Scale > fitted);

        Press(panel, "−");
        Assert.Equal(fitted, canvas.Scale, 6);

        // One drawing unit per pixel, whatever the pane is; and back to the fit.
        Press(panel, "1:1");
        Assert.Equal(1d, canvas.Scale, 6);

        Press(panel, "Fit");
        Assert.Equal(fitted, canvas.Scale, 6);

        // Outlines are on to begin with — a drawing with transparent margins ends where the eye
        // cannot see — and the button turns them off.
        Assert.True(canvas.ShowBounds);

        Toggle(panel, "Bounds");
        Assert.False(canvas.ShowBounds);

        Toggle(panel, "Bounds");
        Assert.True(canvas.ShowBounds);
    }

    /// <summary>Presses one of the canvas's own buttons.</summary>
    private static void Press(GroupPanel panel, string content)
    {
        panel.GetVisualDescendants()
            .OfType<Button>()
            .First(button => Equals(button.Content, content))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Dispatcher.UIThread.RunJobs();
    }

    private static void Toggle(GroupPanel panel, string content)
    {
        var button = panel.GetVisualDescendants()
            .OfType<ToggleButton>()
            .First(candidate => Equals(candidate.Content, content));

        button.IsChecked = !(button.IsChecked == true);

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The one canvas a group tab draws on.</summary>
    private static SvgViewerCanvas Canvas(GroupPanel panel)
        => panel.GetVisualDescendants().OfType<SvgViewerCanvas>().Single();

    /// <summary>What is on it, in the order it was placed.</summary>
    private static IReadOnlyList<SvgViewerPlacement> Drawn(GroupPanel panel) => Canvas(panel).Placements;

    // ---- selecting an element in a group's preview ----------------------------------------------

    private const string Pair = """
        <studio namespace="Demo.Icons">

          <group name="Both" namespace="Demo.Icons.Both">
            <drawing name="home" class="Home">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
                <rect width="24" height="24" fill="#00ff00" />
              </svg>
            </drawing>
            <drawing name="badge" class="Badge">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
                <rect width="24" height="24" fill="#00ff00" />
              </svg>
            </drawing>
          </group>
        </studio>
        """;

    /// <summary>Where in the control a point of the arrangement is, checked by mapping it back.</summary>
    /// <remarks>
    /// The arrangement does not have to start at the origin — a spread whose first drawing is
    /// narrower than its caption begins part way in — so where it does start is asked for rather
    /// than assumed: the control's own origin maps to it.
    /// </remarks>
    private static Point Over(SvgViewerCanvas canvas, float x, float y)
    {
        Assert.True(canvas.TryGetDrawingPoint(new Point(canvas.OffsetX, canvas.OffsetY), out var origin));

        var at = new Point(
            (x - origin.X) * canvas.Scale + canvas.OffsetX,
            (y - origin.Y) * canvas.Scale + canvas.OffsetY);

        Assert.True(canvas.TryGetDrawingPoint(at, out var back));
        Assert.Equal(x, back.X, 3);
        Assert.Equal(y, back.Y, 3);

        return at;
    }

    /// <summary>
    /// Clicks a point given in the canvas's own coordinates.
    /// </summary>
    /// <remarks>
    /// The point is translated into the window's space first, and the window is what the event is
    /// told its root is. A pointer event reports its position relative to whatever asks, by way of
    /// the visual root — hand it a control-local point and call it the root's and every reader is
    /// off by wherever the control sits, which for a canvas under a toolbar and beside a tree is a
    /// couple of hundred pixels.
    /// </remarks>
    private static void Click(Window window, SvgViewerCanvas canvas, Point at)
    {
        var root = canvas.TranslatePoint(at, window)
                   ?? throw new InvalidOperationException("The canvas is not in the window.");

        canvas.RaiseEvent(new PointerPressedEventArgs(
            canvas,
            new Pointer(0, PointerType.Mouse, true),
            window,
            root,
            0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None)
        {
            RoutedEvent = InputElement.PointerPressedEvent
        });

        canvas.RaiseEvent(new PointerReleasedEventArgs(
            canvas,
            new Pointer(0, PointerType.Mouse, true),
            window,
            root,
            0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None,
            MouseButton.Left)
        {
            RoutedEvent = InputElement.PointerReleasedEvent
        });

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Drags from one point of the canvas to another, as a hand would.</summary>
    private static void Drag(Window window, SvgViewerCanvas canvas, Point from, Point to)
    {
        var start = canvas.TranslatePoint(from, window)
                    ?? throw new InvalidOperationException("The canvas is not in the window.");
        var end = canvas.TranslatePoint(to, window)
                  ?? throw new InvalidOperationException("The canvas is not in the window.");

        canvas.RaiseEvent(new PointerPressedEventArgs(
            canvas,
            new Pointer(0, PointerType.Mouse, true),
            window,
            start,
            0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None)
        {
            RoutedEvent = InputElement.PointerPressedEvent
        });

        canvas.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent,
            canvas,
            new Pointer(0, PointerType.Mouse, true),
            window,
            end,
            0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
            KeyModifiers.None));

        canvas.RaiseEvent(new PointerReleasedEventArgs(
            canvas,
            new Pointer(0, PointerType.Mouse, true),
            window,
            end,
            0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None,
            MouseButton.Left)
        {
            RoutedEvent = InputElement.PointerReleasedEvent
        });

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Where a placement sits in the arrangement, as a rectangle.</summary>
    private static SKRect Area(SvgViewerPlacement placement)
    {
        var cull = placement.Svg.Picture!.CullRect;

        return new SKRect(
            placement.At.X + cull.Left,
            placement.At.Y + cull.Top,
            placement.At.X + cull.Right,
            placement.At.Y + cull.Bottom);
    }

    [AvaloniaFact]
    public async Task Clicking_A_Drawing_In_A_Group_Rings_It_There_And_Nowhere_Else()
    {
        // The group view is where the same file is compared built several ways, and until now there
        // was no way to ask which element any of it was. Hit testing the wrong picture would answer
        // confidently about a shape nobody pointed at, so the assertion is *where* the ring landed.

        var window = await Host(Write("icons.svgstudio", Pair));

        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.ShowAsync((ProjectNode)((TreeViewItem)root.Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var panel = (GroupPanel)((TabItem)Tabs(window).SelectedItem!).Content!;

        window.Measure(new Size(900, 600));
        window.Arrange(new Rect(0, 0, 900, 600));
        Dispatcher.UIThread.RunJobs();

        var canvas = Canvas(panel);
        var placements = Drawn(panel);

        Assert.Equal(2, placements.Count);
        Assert.Null(canvas.Highlight);

        var second = Area(placements[1]);

        var at = Over(canvas, second.MidX, second.MidY);

        Click(window, canvas, at);

        var ring = canvas.Highlight;

        Assert.NotNull(ring);

        // On the drawing that was clicked, and not on the one beside it.
        Assert.True(second.Contains(ring!.Bounds), $"{ring.Bounds} is not inside {second}");
        Assert.False(Area(placements[0]).IntersectsWith(ring.Bounds), "the ring landed on the other drawing too");
    }

    /// <summary>A project whose rows say where they sit: two on the board, two inside a group.</summary>
    private static string Board(string extra = "") => $$"""
        <studio namespace="Demo.Icons">
        {{Holding("home", " class=\"Home\" x=\"0\" y=\"0\"")}}
        {{Holding("badge", " class=\"Badge\" x=\"100\" y=\"0\"")}}
          <group name="Large" namespace="Demo.Icons.Large" scale="2" x="0" y="80">
        {{Holding("large", " class=\"BadgeLarge\" x=\"0\" y=\"0\"")}}
        {{extra}}
          </group>
        </studio>
        """;

    [AvaloniaFact]
    public async Task The_First_Move_Settles_The_Whole_Board()
    {
        // A group nobody has arranged is the grid it always was; moving one drawing writes a place
        // for every row at the coordinates the grid had just given them, so nothing else jumps.
        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);
        var panel = Panel(window, "Project");

        var canvas = Canvas(panel);
        var drawn = Drawn(panel);
        var home = Area(drawn[0]);
        var badge = Area(drawn[1]);

        var workspace = window.Workspace!;

        Assert.All(workspace.Document.Root.Children, child => Assert.False(child.HasPosition));

        Drag(window, canvas, Over(canvas, home.MidX, home.MidY), Over(canvas, home.MidX + 40f, home.MidY));

        // Every row of the tab, not only the one that was moved.
        var moved = (ProjectDrawing)workspace.Document.Root.Children[0];
        var group = (ProjectGroup)workspace.Document.Root.Children[1];

        Assert.True(moved.HasPosition);
        Assert.True(group.HasPosition);
        Assert.Equal(home.Left + 40f, moved.X!.Value, 1);

        // The group's place is the nearest corner of what it holds, and its drawing is written
        // against it — so the one that was not moved is drawn exactly where it was.
        Assert.Equal(badge.Left, group.X!.Value, 1);
        Assert.Equal(0f, group.Drawings.Single().X!.Value, 1);

        Assert.Equal(badge.Left, Area(Drawn(panel)[1]).Left, 1);

        // Written through, as every other arrangement edit is.
        Assert.Contains("<drawing name=\"home\"", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Contains("y=\"", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task The_Drawing_Being_Looked_At_Survives_A_Rebuild()
    {
        // Every rebuild of a group's tab used to empty the Parameters and Element tabs. A settings
        // edit did it; a drawing moved on the board would do it on every drop.
        var window = await Host(Own(Declaring, Declaring));
        var panel = await Group(window, 0);

        Pick(window, panel, 1);

        var picked = Declarations(panel).Parameters!.Single().Name;

        Assert.Equal("tint", picked);

        // A rebuild, by the route a saved setting takes.
        panel.Refresh();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("tint", Declarations(panel).Parameters!.Single().Name);

        // And the line above the canvas still names the row rather than asking for one.
        Assert.DoesNotContain(
            panel.GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text is { } said && said.StartsWith("Click a drawing", StringComparison.Ordinal));
    }

    /// <summary>
    /// A drop leaves the view exactly where it was, and every picture on the board alone.
    /// </summary>
    /// <remarks>
    /// Zooming in on one icon to line it up with another is the reason a board can be arranged at
    /// all, and until now the drop threw the zoom away: the tab re-read every drawing from text and
    /// handed the canvas what reads as a new board. A drop writes x and y and nothing else, so
    /// there is nothing to read again.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Drop_Leaves_The_View_And_The_Pictures_Alone()
    {
        var window = await Host(Write("icons.svgstudio", Board()));
        var panel = Panel(window, "Project");

        var canvas = Canvas(panel);
        var home = Area(Drawn(panel)[0]);

        // About the middle of what is being dragged, so the point it is grabbed by does not move.
        var at = Over(canvas, home.MidX, home.MidY);

        canvas.ZoomTo(canvas.Scale * 2d, at);

        var scale = canvas.Scale;
        var offsetX = canvas.OffsetX;
        var offsetY = canvas.OffsetY;
        var pictures = Drawn(panel).Select(placed => placed.Svg).ToList();

        Drag(window, canvas, at, at + new Point(40d, 0d));

        Assert.Equal(scale, canvas.Scale, 6);
        Assert.Equal(offsetX, canvas.OffsetX, 6);
        Assert.Equal(offsetY, canvas.OffsetY, 6);

        // The same drawings, not merely drawings that look the same: a rebuild is what cost the
        // zoom, so the pictures being the very ones is the evidence none happened.
        var again = Drawn(panel).Select(placed => placed.Svg).ToList();

        Assert.Equal(pictures.Count, again.Count);
        Assert.All(pictures.Zip(again), pair => Assert.Same(pair.First, pair.Second));

        // And it did move: the board is what was rearranged.
        Assert.Equal(40d / scale, ((ProjectDrawing)window.Workspace!.Document.Root.Children[0]).X!.Value, 1);
    }

    /// <summary>
    /// An edit to one drawing rebuilds that one and leaves its neighbours as they were.
    /// </summary>
    /// <remarks>
    /// The other half of the same rule: the build reads a drawing's text and the size it is asked
    /// for, so a drawing whose text changed is rebuilt and one whose text did not is not. Both are
    /// asserted here, because a tab that reused everything would pass the first assertion of the
    /// test above while showing a stale picture.
    /// </remarks>
    [AvaloniaFact]
    public async Task An_Edit_Rebuilds_The_Drawing_It_Changed_And_No_Other()
    {
        var window = await Host(Write("icons.svgstudio", Board()));
        var panel = Panel(window, "Project");

        var before = Drawn(panel).Select(placed => placed.Svg).ToList();

        var edited = (ProjectDrawing)window.Workspace!.Document.Root.Children[0];

        edited.SetText(edited.Text.Replace("#00ff00", "#0000ff", StringComparison.Ordinal));

        window.Workspace.Save();
        Dispatcher.UIThread.RunJobs();

        var after = Drawn(panel).Select(placed => placed.Svg).ToList();

        Assert.NotSame(before[0], after[0]);
        Assert.Same(before[1], after[1]);
        Assert.Same(before[2], after[2]);
    }

    /// <summary>A setting that changes the size a drawing is built at rebuilds it.</summary>
    [AvaloniaFact]
    public async Task A_Size_A_Drawing_Inherits_Rebuilds_It()
    {
        var window = await Host(Write("icons.svgstudio", Board()));
        var panel = Panel(window, "Project");

        var before = Drawn(panel).Select(placed => placed.Svg).ToList();
        var group = (ProjectGroup)window.Workspace!.Document.Root.Children[2];

        group.Scale = 4f;

        window.Workspace.Save();
        Dispatcher.UIThread.RunJobs();

        var after = Drawn(panel).Select(placed => placed.Svg).ToList();

        // The one inside the group, and nothing outside it.
        Assert.Same(before[0], after[0]);
        Assert.Same(before[1], after[1]);
        Assert.NotSame(before[2], after[2]);
    }

    /// <summary>
    /// A drop keeps the ring, the row it came from and the Element tab, on the drawing that moved.
    /// </summary>
    /// <remarks>
    /// A rebuild used to clear all three, so every drop emptied the panes beside the canvas and took
    /// the ring off whatever was being worked on — which on a board is constant, since dragging is
    /// how a board is arranged at all.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Drop_Keeps_The_Ring_And_The_Element_Tab()
    {
        var window = await Host(Write("icons.svgstudio", Board()));
        var panel = Panel(window, "Project");
        var canvas = Canvas(panel);

        Pick(window, panel, 0);

        Assert.NotNull(canvas.Highlight);
        Assert.NotNull(Elements(panel).SelectedNode);
        Assert.Equal("#00ff00", Assert.IsType<SvgViewerElementPanel>(Element(panel)).Shown("fill"));

        var home = Area(Drawn(panel)[0]);

        Drag(
            window,
            canvas,
            Over(canvas, home.MidX, home.MidY),
            Over(canvas, home.MidX + 40f, home.MidY));

        var ring = canvas.Highlight;

        Assert.NotNull(ring);
        Assert.NotNull(Elements(panel).SelectedNode);
        Assert.Equal("#00ff00", Assert.IsType<SvgViewerElementPanel>(Element(panel)).Shown("fill"));

        // And it went with the drawing rather than staying behind on the board.
        var moved = Area(Drawn(panel)[0]);

        Assert.Equal(home.Left + 40f, moved.Left, 1);
        Assert.True(moved.Contains(ring!.Bounds), $"{ring.Bounds} is not inside {moved}");
    }

    /// <summary>
    /// A row put into another group keeps the place it had, read for the board it arrives on.
    /// </summary>
    /// <remarks>
    /// Dropping a row somewhere in the tree says which group holds it. It says nothing about where
    /// it should sit, so it must not move one — and it used to jump to the queue beside the
    /// arrangement, because the place was thrown away rather than read again.
    ///
    /// Its corner, not its size: a place is deliberately no part of what owns a size, so the row is
    /// built at the scale of the group it has joined. Here that is ×2, and the drawing doubles where
    /// it stands.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Row_Put_In_Another_Group_Keeps_Its_Place()
    {
        var window = await Host(Write("icons.svgstudio", Board()));
        var panel = Panel(window, "Project");

        var moved = (ProjectDrawing)window.Workspace!.Document.Root.Children[1];
        var into = (ProjectGroup)window.Workspace.Document.Root.Children[2];

        var was = Area(Shown(panel, moved));

        Assert.True(window.Move(moved, into, ProjectDrop.Inside));
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(moved, into.Children);

        var now = Area(Shown(panel, moved));

        Assert.Equal(was.Left, now.Left, 1);
        Assert.Equal(was.Top, now.Top, 1);

        // Written against the board it is on now, which is what makes that true: the group sits at
        // y 80, so a row that was at y 0 on the project's board is at y -80 on the group's.
        Assert.Equal(100f, moved.X!.Value, 1);
        Assert.Equal(-80f, moved.Y!.Value, 1);

        // And it is the group's drawing now, built the way the group builds one.
        Assert.Equal(was.Width * 2f, now.Width, 1);
    }

    /// <summary>
    /// Which placement on the board was built from a given row.
    /// </summary>
    /// <remarks>
    /// By its caption, which begins with the row's name. Not by index: the board is laid out with
    /// the placed rows first and the ones with no place queued after them, which is not the order
    /// the tree holds them in.
    /// </remarks>
    private static SvgViewerPlacement Shown(GroupPanel panel, ProjectDrawing drawing)
        => Drawn(panel).Single(placed =>
            placed.Label is { } caption && caption.StartsWith(drawing.Name + "\n", StringComparison.Ordinal));

    [AvaloniaFact]
    public async Task A_Group_Is_Carried_By_Its_Frame()
    {
        var path = Write("icons.svgstudio", Board());
        var window = await Host(path);
        var panel = Panel(window, "Project");

        var canvas = Canvas(panel);
        var framed = Assert.Single(Canvas(panel).Frames);
        var group = (ProjectGroup)window.Workspace!.Document.Root.Children[2];

        // The frame's own margin: inside it, and outside the drawing it holds.
        var margin = new SKPoint(framed.Bounds.Left + 1f, framed.Bounds.MidY);

        Drag(window, canvas, Over(canvas, margin.X, margin.Y), Over(canvas, margin.X + 30f, margin.Y));

        // One attribute, and what it holds follows without being written to.
        Assert.Equal(30f, group.X!.Value, 1);
        Assert.Equal(80f, group.Y!.Value, 1);
        Assert.Equal(0f, group.Drawings.Single().X!.Value, 1);

        Assert.Equal(30f, Area(Drawn(Panel(window, "Project"))[2]).Left, 1);
    }

    [AvaloniaFact]
    public async Task A_Board_Puts_Its_Items_Where_They_Say()
    {
        var window = await Host(Write("icons.svgstudio", Board()));
        var panel = Panel(window, "Project");
        var drawn = Drawn(panel);

        Assert.Equal(3, drawn.Count);

        // Where the file says, and not where a grid would have put them.
        Assert.Equal(0f, Area(drawn[0]).Left, 3);
        Assert.Equal(100f, Area(drawn[1]).Left, 3);

        // The one in the group is written against the group, which sits at 0,80 — so it lands
        // there, and the group's own scale still reaches it.
        Assert.Equal(0f, Area(drawn[2]).Left, 3);
        Assert.Equal(80f, Area(drawn[2]).Top, 3);
        Assert.Equal(48f, Picture(drawn[2])!.CullRect.Width);
    }

    [AvaloniaFact]
    public async Task A_Group_With_A_Place_Is_Drawn_As_A_Frame_Round_What_It_Holds()
    {
        var window = await Host(Write("icons.svgstudio", Board()));
        var panel = Panel(window, "Project");

        var framed = Assert.Single(Canvas(panel).Frames);

        Assert.Equal("Large", framed.Label);
        Assert.True(
            framed.Bounds.Contains(Area(Drawn(panel)[2])),
            $"{framed.Bounds} is not round the drawing it holds");

        // And round that one alone: the two on the board outside it are not in it.
        Assert.False(framed.Bounds.IntersectsWith(Area(Drawn(panel)[0])));
    }

    [AvaloniaFact]
    public async Task A_Drawing_With_No_Place_Waits_Beside_The_Ones_That_Have_One()
    {
        // The state a paste or an add leaves behind, and a hand-written file can too: nothing is
        // written at layout time, so it waits where it can be seen rather than under something.
        var window = await Host(Write("icons.svgstudio", Board(Holding("queued"))));
        var panel = Panel(window, "Project");
        var drawn = Drawn(panel);

        // In the order the walk places them: what the board says, then what it does not.
        var placed = Area(drawn[2]);
        var queued = Area(drawn[3]);

        Assert.True(queued.Left >= placed.Right, $"{queued} is not beside {placed}");
    }

    [AvaloniaFact]
    public async Task A_Drawing_On_A_Board_Is_Picked_Where_It_Sits()
    {
        // The same claim as the grid's, against places: the pairing from a click back to a node is
        // reference identity on a placement, and a board builds those a second way.
        var window = await Host(Write("icons.svgstudio", Board()));
        var panel = Panel(window, "Project");

        var canvas = Canvas(panel);
        var drawn = Drawn(panel);
        var second = Area(drawn[1]);

        Click(window, canvas, Over(canvas, second.MidX, second.MidY));

        var ring = canvas.Highlight;

        Assert.NotNull(ring);
        Assert.True(second.Contains(ring!.Bounds), $"{ring.Bounds} is not inside {second}");
        Assert.False(Area(drawn[0]).IntersectsWith(ring.Bounds), "the ring landed on the other drawing too");
    }

    /// <summary>A group that builds the one file twice, which is what a project usually does.</summary>
    private const string Twice = """
        <studio namespace="Demo.Icons">
          <group name="Large" namespace="Demo.Icons.Large" scale="2">
            <drawing name="badge" class="BadgeLarge">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
                <rect width="24" height="24" fill="#00ff00" />
              </svg>
            </drawing>
            <group name="Huge" class="BadgeHuge">
              <drawing name="badge-huge" scale="4">
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
                  <rect width="24" height="24" fill="#00ff00" />
                </svg>
              </drawing>
            </group>
          </group>
        </studio>
        """;

    [AvaloniaFact]
    public async Task Each_Build_Of_The_Same_File_Can_Be_Picked_In_Turn()
    {
        // Reported against Demo.Icons.Large: nothing in BadgeLarge could be picked once anything in
        // BadgeHuge had been. Both are built from badge.svg, so their elements carry the same
        // addresses, and asking a tree keyed by address for a row it already has selected is
        // indistinguishable from asking it for nothing.
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgstudio", Twice));

        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.ShowAsync((ProjectNode)((TreeViewItem)root.Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var panel = (GroupPanel)((TabItem)Tabs(window).SelectedItem!).Content!;

        window.Measure(new Size(900, 600));
        window.Arrange(new Rect(0, 0, 900, 600));
        Dispatcher.UIThread.RunJobs();

        var canvas = Canvas(panel);
        var placements = Drawn(panel);

        Assert.Equal(2, placements.Count);

        // Back and forth, because the failure only showed on the second of any two.
        for (var round = 0; round < 2; round++)
        {
            foreach (var placed in placements)
            {
                var area = Area(placed);

                Click(window, canvas, Over(canvas, area.MidX, area.MidY));

                Assert.NotNull(canvas.Highlight);
                Assert.True(
                    area.Contains(canvas.Highlight!.Bounds),
                    $"round {round}: the ring is at {canvas.Highlight.Bounds}, not on the drawing at {area}");
            }
        }
    }


    // ---- the tabs follow the selected drawing ---------------------------------------------------

    /// <summary>A drawing that declares a colour and paints a literal one, for an edit to bind.</summary>
    private const string Unbound = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs><e:code><e:param name="tint" type="color" default="#00ff00" /></e:code></defs>
          <rect width="24" height="24" fill="#00ff00" />
        </svg>
        """;

    /// <summary>A drawing that declares its own parameters.</summary>
    private const string Declaring = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs><e:code><e:param name="tint" type="color" default="#00ff00" /></e:code></defs>
          <rect width="24" height="24" fill="{{ tint }}" />
        </svg>
        """;

    [AvaloniaFact]
    public async Task A_Groups_Drawings_Are_Drawn_On_Its_Tab()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgstudio", Project));

        // The project row opens as a tab of its own, and it is a group like any other.
        var drawn = Drawn(Panel(window, "Project"));

        Assert.Equal(2, drawn.Count);
        Assert.All(drawn, placed => Assert.NotNull(Picture(placed)));

        // In the order the project builds them, and not one on top of another.
        Assert.StartsWith("home", drawn[0].Label);
        Assert.StartsWith("badge", drawn[1].Label);
        Assert.NotEqual(drawn[0].At, drawn[1].At);
    }

    [AvaloniaFact]
    public async Task Picking_A_Tab_Opens_The_Tree_Down_To_What_It_Shows()
    {

        var window = await Host(Write("icons.svgstudio", Project));

        var root = (TreeViewItem)Tree(window).Items[0]!;
        var group = (TreeViewItem)root.Items[1]!;
        var badge = (TreeViewItem)group.Items[0]!;

        await window.ShowAsync((ProjectNode)badge.Tag!);
        await window.ShowAsync((ProjectNode)((TreeViewItem)root.Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        // Folded away with a tab from inside it still open, which is what makes the tree stop
        // saying anything about where that tab is.
        group.IsExpanded = false;
        Dispatcher.UIThread.RunJobs();

        Tabs(window).SelectedItem = Tabs(window).Items
            .OfType<TabItem>()
            .Single(item => ReferenceEquals(item.Tag, badge.Tag));

        Dispatcher.UIThread.RunJobs();

        // Picking the tab is the answer to "where is this?", so the row comes back into sight.
        Assert.True(group.IsExpanded);
        Assert.Same(badge, Tree(window).SelectedItem);
    }

    [AvaloniaFact]
    public async Task A_Drawing_Exported_Is_Written_As_A_File_Of_Its_Own()
    {
        // Export rather than Save As, which points a tab at the file it wrote and so has nothing to
        // do for a drawing the project holds.
        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);

        await window.ShowAsync((ProjectNode)((TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        await Settle(window, "home");

        var copy = Path.Combine(_directory, "copied.svg");

        Assert.True(await window.ExportAsync(copy));

        // Without the indentation it was written at inside the project: what comes out is a drawing
        // rather than a slice of somebody else's file.
        Assert.Equal(Drawing, File.ReadAllText(copy));

        // And the project is left exactly as it was: an export is a copy, not a move.
        Assert.Equal(Project, File.ReadAllText(path));
    }

    [AvaloniaFact]
    public async Task Save_Is_Offered_Only_While_Something_Is_Unsaved()
    {

        var window = await Host(Write("icons.svgstudio", Pair));
        var root = (TreeViewItem)Tree(window).Items[0]!;
        var group = (ProjectGroup)(ProjectNode)((TreeViewItem)root.Items[0]!).Tag!;

        await window.ShowAsync(group.Drawings.First());
        Dispatcher.UIThread.RunJobs();

        var viewer = (SvgViewer)((TabItem)Tabs(window).SelectedItem!).Content!;
        var save = Menu(window, "Save");

        Assert.False(save.IsEnabled);

        // The menu is drawn from the same answer the tab's own dot is, so it follows without being
        // told separately.
        Assert.True(viewer.Resize(new SvgSizeRequest(48f, null, null)));
        Dispatcher.UIThread.RunJobs();

        Assert.True(save.IsEnabled);

        await window.SaveAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.False(save.IsEnabled);
    }

    /// <summary>The declaration panel on a group's Parameters tab, which the tab must be on to hold.</summary>
    private static SvgViewerDeclarationPanel Declarations(GroupPanel panel)
    {
        var tabs = panel.GetVisualDescendants().OfType<TabControl>().First();

        tabs.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();

        return panel.GetVisualDescendants().OfType<SvgViewerDeclarationPanel>().Single();
    }

    private static async Task<GroupPanel> Group(MainWindow window, int child)
    {
        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.ShowAsync((ProjectNode)((TreeViewItem)root.Items[child]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var panel = (GroupPanel)((TabItem)Tabs(window).SelectedItem!).Content!;

        window.Measure(new Size(900, 600));
        window.Arrange(new Rect(0, 0, 900, 600));
        Dispatcher.UIThread.RunJobs();

        return panel;
    }

    /// <summary>Picks the drawing at <paramref name="index"/> by clicking the middle of it.</summary>
    private static void Pick(MainWindow window, GroupPanel panel, int index)
    {
        var canvas = Canvas(panel);
        var area = Area(Drawn(panel)[index]);

        Click(window, canvas, Over(canvas, area.MidX, area.MidY));
    }

    [AvaloniaFact]
    public async Task Nothing_Picked_Says_To_Pick_Something()
    {

        var window = await Host(Write("icons.svgstudio", Pair));
        var panel = await Group(window, 0);

        // The tabs are about a drawing, and a group builds several: it cannot guess which.
        Assert.Null(Declarations(panel).Parameters);

        var note = panel.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(block => block.Text is { } said && said.Contains("Pick a drawing"));

        Assert.NotNull(note);
    }

    [AvaloniaFact]
    public async Task The_Parameters_Are_The_Picked_Drawings()
    {
        var window = await Host(Own(Declaring, Declaring));
        var panel = await Group(window, 0);

        Pick(window, panel, 0);

        // Read off the drawing as built, which is what a viewer would show for it.
        Assert.Equal(new[] { "tint" }, Declarations(panel).Parameters!.Select(row => row.Name).ToArray());
    }

    /// <summary>A drawing declaring a parameter the host is expected to supply, as a real one does.</summary>
    private const string OpenEnded = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs>
            <e:code>
              <e:param name="accent" type="color" default="#00a7ff" />
              <e:param name="whiteColor" type="color" />
              <e:param name="on" type="boolean" default="true" />
              <e:let name="colour">on ? accent : whiteColor</e:let>
            </e:code>
          </defs>
          <rect width="24" height="24" fill="{{ colour }}" />
        </svg>
        """;

    [AvaloniaFact]
    public async Task A_Parameter_With_No_Default_Does_Not_Grey_The_Group()
    {
        // Reported against a real project: every icon came up grey, which is what a drawing renders
        // when its expressions are left at placeholders. Binding the declared defaults refuses the
        // whole set the moment one parameter has none -- which a drawing is entitled to declare --
        // so nothing was bound at all. A viewer never hit it, because it binds the rows its panel
        // seeds rather than the defaults.
        var window = await Host(Own(OpenEnded, OpenEnded));
        var panel = await Group(window, 0);

        foreach (var placed in Drawn(panel))
        {
            using var bitmap = new SKBitmap(8, 8);

            using (var surface = new SKCanvas(bitmap))
            {
                surface.Clear(SKColors.White);
                surface.Scale(8f / 24f);
                placed.Svg.Draw(surface);
            }

            var painted = bitmap.GetPixel(4, 4);

            // The accent the drawing names, not the placeholder grey it fell back to.
            Assert.True(
                painted.Blue > 200 && painted.Red < 100,
                $"{painted} is not the accent colour: the drawing is still on its placeholders");
        }
    }

    /// <summary>What <see cref="Declaring"/> declares, seeded differently — the same parameter.</summary>
    private const string DeclaringSeededDifferently = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs><e:code><e:param name="tint" type="color" default="#0000ff" /></e:code></defs>
          <circle cx="12" cy="12" r="10" fill="{{ tint }}" />
        </svg>
        """;

    /// <summary>Another parameter altogether, sharing nothing with it but the type.</summary>
    private const string DeclaringAnother = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs><e:code><e:param name="shade" type="color" default="#00ff00" /></e:code></defs>
          <path d="M12 2 L22 22 L2 22 Z" fill="{{ shade }}" />
        </svg>
        """;

    /// <summary>The same name and type, on a slider with different ends.</summary>
    private const string DeclaringBounded = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs><e:code><e:param name="hue" type="number" default="120" min="0" max="360" /></e:code></defs>
          <rect width="24" height="24" fill="{{ hsl(hue, 100%, 50%) }}" />
        </svg>
        """;

    private const string DeclaringBoundedDifferently = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs><e:code><e:param name="hue" type="number" default="120" min="0" max="180" /></e:code></defs>
          <circle cx="12" cy="12" r="10" fill="{{ hsl(hue, 100%, 50%) }}" />
        </svg>
        """;

    /// <summary>A project holding the drawings given, in one group, named after their order.</summary>
    private string Own(params string[] drawings)
    {
        var names = new[] { "one", "two", "three" };
        var classes = new[] { "One", "Two", "Three" };

        var rows = drawings.Select((svg, index) =>
            $"""    <drawing name="{names[index]}" class="{classes[index]}">\n      """
            + svg.Replace("\n", "\n      ", StringComparison.Ordinal)
            + "\n    </drawing>");

        return Write("icons.svgstudio", $"""
            <studio namespace="Demo.Icons">
              <group name="Own" namespace="Demo.Icons.Own">
            {string.Join("\n", rows)}
              </group>
            </studio>

            """);
    }

    [AvaloniaFact]
    public async Task A_Value_Reaches_Every_Drawing_Declaring_The_Same_Thing()
    {
        // Not the same file either -- a rect and a circle, seeded at two
        // different colours. What pairs them is what they declare: a default is where a drawing
        // starts rather than what it takes, so a family caught at two points is still one family.
        var window = await Host(Own(Declaring, DeclaringSeededDifferently, DeclaringAnother));
        var panel = await Group(window, 0);

        var placements = Drawn(panel);

        Assert.Equal(3, placements.Count);

        Pick(window, panel, 0);

        var before = placements.Select(placed => placed.Svg.Picture).ToArray();

        ((SvgViewerColorParameter)Declarations(panel).Parameters!.Single()).Color = Colors.Red;
        Dispatcher.UIThread.RunJobs();

        Assert.NotSame(before[0], placements[0].Svg.Picture);
        Assert.NotSame(before[1], placements[1].Svg.Picture);

        // Another name, so another parameter, and it moves alone.
        Assert.Same(before[2], placements[2].Svg.Picture);
    }

    [AvaloniaFact]
    public async Task A_Parameter_On_A_Different_Slider_Is_A_Different_Parameter()
    {
        // The bounds are advice to a host rather than a constraint on the value, so the value would
        // have gone in either way -- but they are what the control offering it looks like, and two
        // parameters somebody would be given different sliders for are not the one parameter.
        var window = await Host(Own(DeclaringBounded, DeclaringBoundedDifferently));
        var panel = await Group(window, 0);

        var placements = Drawn(panel);

        Assert.Equal(2, placements.Count);

        Pick(window, panel, 0);

        var before = placements.Select(placed => placed.Svg.Picture).ToArray();

        ((SvgViewerNumberParameter)Declarations(panel).Parameters!.Single()).Value = 300d;
        Dispatcher.UIThread.RunJobs();

        Assert.NotSame(before[0], placements[0].Svg.Picture);
        Assert.Same(before[1], placements[1].Svg.Picture);
    }

    [AvaloniaFact]
    public async Task Picking_A_Drawing_That_Shares_Keeps_The_Value_On_Show()
    {
        // The value was bound into both, so the panel showing the next one its declared default
        // would have it disagreeing with the picture beside it.
        var window = await Host(Own(Declaring, DeclaringSeededDifferently, DeclaringAnother));
        var panel = await Group(window, 0);

        Pick(window, panel, 0);

        ((SvgViewerColorParameter)Declarations(panel).Parameters!.Single()).Color = Colors.Red;
        Dispatcher.UIThread.RunJobs();

        // The other drawing declaring the same thing.
        Pick(window, panel, 1);

        Assert.Equal(Colors.Red, ((SvgViewerColorParameter)Declarations(panel).Parameters!.Single()).Color);

        // The one that shares nothing shows what it declares.
        Pick(window, panel, 2);

        Assert.Equal(new[] { "shade" }, Declarations(panel).Parameters!.Select(row => row.Name).ToArray());
    }

    [AvaloniaFact]
    public async Task A_Drawing_Open_In_No_Tab_Is_Written_Into_The_Project()
    {
        // The last resort, and the one that has no buffer behind it: the edit is written and saved
        // at once, because there is nothing else holding the drawing.
        var path = Own(Declaring, Declaring);
        var window = await Host(path);
        var panel = await Group(window, 0);

        Pick(window, panel, 0);

        Assert.Equal(new[] { "tint" }, Declarations(panel).Parameters!.Select(row => row.Name).ToArray());

        panel.ParameterDialogService = new StubParameterDialogService(
            new SvgExpressionParameter("sweep", ExprType.Number, "4", null, null, null));

        Assert.True(await panel.AddParameterAsync());
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("sweep", File.ReadAllText(path));
    }

    [AvaloniaFact]
    public async Task A_Drawing_Open_In_A_Tab_Is_Edited_Through_That_Tab()
    {
        // So the edit can be taken back and is saved when somebody asks.
        var path = Write("icons.svgstudio", Pair);
        var window = await Host(path);

        var root = (TreeViewItem)Tree(window).Items[0]!;
        var group = (ProjectGroup)(ProjectNode)((TreeViewItem)root.Items[0]!).Tag!;

        // Open the drawing in a tab of its own first.
        await window.ShowAsync(group.Drawings.First());
        Dispatcher.UIThread.RunJobs();

        var viewer = (SvgViewer)((TabItem)Tabs(window).SelectedItem!).Content!;

        var panel = await Group(window, 0);

        Pick(window, panel, 0);

        panel.ParameterDialogService = new StubParameterDialogService(
            new SvgExpressionParameter("sweep", ExprType.Number, "4", null, null, null));

        Assert.True(await panel.AddParameterAsync());
        Dispatcher.UIThread.RunJobs();

        // In the open tab, and not on disk: it is unsaved work like any other.
        Assert.Contains("sweep", viewer.Source);
        Assert.True(viewer.IsSourceModified);
        Assert.DoesNotContain("sweep", File.ReadAllText(path));
    }

    /// <summary>The Element tab's content, whatever it currently is.</summary>
    private static object? Element(GroupPanel panel)
    {
        var tabs = panel.GetVisualDescendants().OfType<TabControl>().First();

        tabs.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();

        return ((TabItem)tabs.Items[2]!).Content is ContentControl host ? host.Content : null;
    }

    [AvaloniaFact]
    public async Task Picking_A_Shape_Shows_What_It_Is_Written_With()
    {

        var window = await Host(Write("icons.svgstudio", Pair));
        var panel = await Group(window, 0);

        Pick(window, panel, 0);

        var element = Assert.IsType<SvgViewerElementPanel>(Element(panel));

        // The rect the fixture draws, in the file's own words.
        Assert.Equal("#00ff00", element.Shown("fill"));
        Assert.Contains("width", element.Attributes);
    }

    [AvaloniaFact]
    public async Task An_Element_Edited_From_A_Group_Lands_In_The_Drawing()
    {
        var path = Own(Unbound, Declaring);
        var window = await Host(path);
        var panel = await Group(window, 0);

        Pick(window, panel, 0);

        var element = Assert.IsType<SvgViewerElementPanel>(Element(panel));

        Assert.True(element.Set("fill", "{{ tint }}"));
        Dispatcher.UIThread.RunJobs();

        // Into the drawing, which is where an element's attribute lives — and so into the project,
        // which is where the drawing lives.
        Assert.Contains("fill=\"{{ tint }}\"", File.ReadAllText(path));
    }

    private sealed class StubParameterDialogService : ISvgViewerParameterDialogService
    {
        private readonly SvgExpressionParameter? _answer;

        public StubParameterDialogService(SvgExpressionParameter? answer) => _answer = answer;

        public Task<SvgExpressionParameter?> AskAsync(TopLevel? owner, IReadOnlyCollection<string> taken)
            => Task.FromResult(_answer);

        public Task<SvgExpressionParameter?> EditAsync(
            TopLevel? owner,
            IReadOnlyCollection<string> taken,
            SvgExpressionParameter existing)
            => Task.FromResult(_answer);
    }

    private static SvgViewerElementTree Elements(GroupPanel panel)
        => panel.GetVisualDescendants().OfType<SvgViewerElementTree>().Single();

    [AvaloniaFact]
    public async Task The_Tree_Follows_Whichever_Drawing_Was_Clicked()
    {
        // One drawing at a time: the tree keys its rows by a path unique only inside one document,
        // so a group's several would collide. Clicking the other one swaps what is on show.

        var window = await Host(Write("icons.svgstudio", Pair));

        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.ShowAsync((ProjectNode)((TreeViewItem)root.Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var panel = (GroupPanel)((TabItem)Tabs(window).SelectedItem!).Content!;

        window.Measure(new Size(900, 600));
        window.Arrange(new Rect(0, 0, 900, 600));
        Dispatcher.UIThread.RunJobs();

        var canvas = Canvas(panel);
        var placements = Drawn(panel);
        var tree = Elements(panel);

        // Nothing is on show until something is picked: a group builds several and the pane cannot
        // guess which of them is meant.
        Assert.Null(tree.Root);

        var first = Area(placements[0]);

        Click(window, canvas, Over(canvas, first.MidX, first.MidY));

        Assert.NotNull(tree.Root);
        Assert.Equal("rect", tree.SelectedNode!.Label);

        var showing = tree.Root!.Element;

        // The other drawing is a different document, so the tree is rebuilt rather than reselected.
        var second = Area(placements[1]);

        Click(window, canvas, Over(canvas, second.MidX, second.MidY));

        Assert.NotSame(showing, tree.Root!.Element);
        Assert.Equal("rect", tree.SelectedNode!.Label);
    }


    private static SKPicture? Picture(SvgViewerPlacement placed) => placed.Svg.Picture;

    [AvaloniaFact]
    public async Task Clicking_A_Second_Group_Opens_It_Without_Folding_Either()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        // Two groups, because the fold only showed up on the second one: with one group open in a
        // tab, choosing another folded it instead of opening it.
        var window = await Host(Write("icons.svgstudio", $"""
            <studio>
              <group name="Large" namespace="Large" scale="2">
            {Holding("badge", " class=\"BadgeLarge\"")}
              </group>
              <group name="Small" namespace="Small" scale="0.5">
            {Holding("home", " class=\"HomeSmall\"")}
              </group>
            </studio>
            """));

        var root = (TreeViewItem)Tree(window).Items[0]!;
        var large = (TreeViewItem)root.Items[0]!;
        var small = (TreeViewItem)root.Items[1]!;

        // Opened first, because the tree now arrives folded and what this guards is a tap *closing*
        // a group. Folded rows would pass the assertions below for the wrong reason.
        large.IsExpanded = true;
        small.IsExpanded = true;

        Dispatcher.UIThread.RunJobs();

        Click(window, large);
        Assert.Same(large.Tag, Assert.IsType<GroupPanel>(((TabItem)Tabs(window).SelectedItem!).Content).Node);

        Click(window, small);

        // Driven through the pointer rather than by calling the handler, because the bug this
        // guards was entirely in the routing: TreeViewItem folds a node on a double tap of its
        // header, and the header is below the row in the route, so it went first.
        Assert.True(small.IsExpanded, "clicking the second group folded it away");
        Assert.True(large.IsExpanded, "the first group folded away");

        Assert.Same(small.Tag, Assert.IsType<GroupPanel>(((TabItem)Tabs(window).SelectedItem!).Content).Node);
    }

    /// <summary>A press and a release on <paramref name="target"/>, which is one tap.</summary>
    private static void Click(MainWindow window, Visual target)
    {
        var at = target.TranslatePoint(new Point(target.Bounds.Width / 2d, 8d), window)
                 ?? throw new InvalidOperationException("The row is not in the window's visual tree.");

        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Closing_The_Project_Takes_Its_Tabs_With_It()
    {

        var window = await Host(Write("icons.svgstudio", Project));

        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.ShowAsync((ProjectNode)root.Tag!);
        await window.ShowAsync((ProjectNode)((TreeViewItem)root.Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, Tabs(window).Items.Count);

        Assert.True(await window.CloseProjectAsync());
        Dispatcher.UIThread.RunJobs();

        Assert.Null(window.Workspace);
        Assert.Empty(Tree(window).Items);
        Assert.False(window.FindControl<Border>("ProjectPaneHost")!.IsVisible);

        // Every tab it had was the project's, so the window is back to empty.
        Assert.Empty(Tabs(window).Items);
    }

    [AvaloniaFact]
    public async Task Opening_A_Drawing_Leaves_The_Window_As_It_Was()
    {
        var window = await Host(Write("home.svg", Drawing));

        // No project, so nothing of the pane shows and the tabs are the whole window.
        Assert.False(window.FindControl<Border>("ProjectPaneHost")!.IsVisible);
        Assert.False(window.FindControl<GridSplitter>("ProjectSplitter")!.IsVisible);
        Assert.Empty(Tree(window).Items);

        var viewer = Assert.IsType<SvgViewer>(((TabItem)Tabs(window).SelectedItem!).Content);
        Assert.Equal("home.svg", Path.GetFileName(viewer.DocumentPath));
    }

    [AvaloniaFact]
    public async Task A_Drawing_Chosen_In_The_Tree_Is_Built_At_The_Size_Its_Group_Asks_For()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgstudio", Project));

        var root = (TreeViewItem)Tree(window).Items[0]!;
        var group = (TreeViewItem)root.Items[1]!;

        await window.ShowAsync((ProjectNode)((TreeViewItem)group.Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var viewer = await Settle(window, "badge");

        // scale="2" on the group, applied to the picture and not to the file.
        Assert.Equal(48f, viewer.Document!.Svg.Picture!.CullRect.Width);
        Assert.Equal(Drawing, File.ReadAllText(Path.Combine(_directory, "badge.svg")));

        // home.svg is outside the group, so it keeps the size it was written with.
        await window.ShowAsync((ProjectNode)((TreeViewItem)root.Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(24f, (await Settle(window, "home")).Document!.Svg.Picture!.CullRect.Width);
    }

    [AvaloniaFact]
    public async Task Editing_A_Group_Rebuilds_What_Is_Open_And_Saves_The_Project()
    {
        Write("badge.svg", Drawing);

        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);

        var root = (TreeViewItem)Tree(window).Items[0]!;
        var group = (TreeViewItem)root.Items[1]!;

        await window.ShowAsync((ProjectNode)((TreeViewItem)group.Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var viewer = await Settle(window, "badge");
        Assert.Equal(48f, viewer.Document!.Svg.Picture!.CullRect.Width);

        var workspace = window.Workspace!;

        ((ProjectGroup)workspace.Document.Root.Children[1]).Scale = 4f;
        workspace.Save();

        // Saved as XML, with the comment and the layout the author wrote still there.
        var saved = File.ReadAllText(path);
        Assert.Contains("scale=\"4\"", saved);
        Assert.Contains("<!-- kept, so an edit is proven not to reformat the file -->", saved);
        Assert.Equal(Project.Replace("scale=\"2\"", "scale=\"4\""), saved);

        // And the project still describes the same build to the generator.
        Assert.Equal(4f, window.Workspace!.Document.Flatten().Items[1].Scale);
    }

    [AvaloniaFact]
    public async Task A_Tab_Saves_What_Was_Typed_In_It_And_Nothing_Else()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var path = Write("icons.svgstudio", $"""
            <studio>
              <group name="Large" namespace="Large" scale="2">
            {Holding("badge", " class=\"BadgeLarge\"")}
              </group>
              <group name="Small" namespace="Small" scale="0.5">
            {Holding("home", " class=\"HomeSmall\"")}
              </group>
            </studio>
            """);

        var window = await Host(path);
        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.ShowAsync((ProjectNode)((TreeViewItem)root.Items[0]!).Tag!);
        await window.ShowAsync((ProjectNode)((TreeViewItem)root.Items[1]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var large = Panel(window, "Large");
        var small = Panel(window, "Small");

        Assert.False(large.IsModified);
        Assert.False(small.IsModified);

        // Typed in one tab each, so each carries its own.
        large.Edit("scale", "4");
        small.Edit("scale", "8");

        Assert.True(large.IsModified);
        Assert.True(small.IsModified);

        large.Save();

        // Only the tab that saved is clean, and only its edit reached the file.
        Assert.False(large.IsModified);
        Assert.True(small.IsModified);

        var saved = File.ReadAllText(path);
        Assert.Contains("scale=\"4\"", saved);
        Assert.Contains("scale=\"0.5\"", saved);
        Assert.DoesNotContain("scale=\"8\"", saved);

        small.Save();
        Assert.Contains("scale=\"8\"", File.ReadAllText(path));
    }

    [AvaloniaFact]
    public async Task An_Unsaved_Tab_Is_Marked_In_Its_Header()
    {

        var window = await Host(Write("icons.svgstudio", Project));
        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.ShowAsync((ProjectNode)((TreeViewItem)root.Items[1]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var header = (StackPanel)((TabItem)Tabs(window).SelectedItem!).Header!;
        var marker = (TextBlock)header.Children[0];
        var title = (TextBlock)header.Children[1];

        Assert.Equal("Large", title.Text);

        // The rendered mark, not the class it was handed: a class nothing styles would pass the
        // one and show nothing for the other.
        Assert.Equal("●", marker.Text);
        Assert.Equal(0d, marker.Opacity);

        Panel(window, "Large").Edit("scale", "4");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1d, marker.Opacity);

        Panel(window, "Large").Save();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0d, marker.Opacity);

        // The mark is its own element, so the name never changed and the tab never resized.
        Assert.Equal("Large", title.Text);
    }

    /// <summary>
    /// A group renamed is renamed on its tab as well. The header was written when the tab was, so
    /// the row in the tree read the new name while the tab open on that very row read the old one.
    /// </summary>
    [AvaloniaFact]
    public async Task A_Renamed_Group_Is_Renamed_On_Its_Tab()
    {

        var window = await Host(Write("icons.svgstudio", Project));
        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.ShowAsync((ProjectNode)((TreeViewItem)root.Items[1]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var item = (TabItem)Tabs(window).SelectedItem!;
        var title = (TextBlock)((StackPanel)item.Header!).Children[1];

        Assert.Equal("Large", title.Text);

        var panel = Panel(window, "Large");

        Assert.True(panel.Edit("name", "Huge"));

        panel.Save();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Huge", title.Text);

        // The tree said the new name all along; the window title is the same label again and read
        // from the same tab, so it cannot be left saying the old one either.
        Assert.Contains("Huge", Rows((TreeViewItem)Tree(window).Items[0]!));
        Assert.StartsWith("Huge — ", window.Title);
    }

    [AvaloniaFact]
    public async Task An_Unsaved_Drawing_Is_Marked_Too()
    {
        var window = await Host(Write("home.svg", Drawing));

        var item = (TabItem)Tabs(window).SelectedItem!;
        var header = (StackPanel)item.Header!;
        var marker = (TextBlock)header.Children[0];
        var title = (TextBlock)header.Children[1];

        Assert.Equal("home.svg", title.Text);
        Assert.DoesNotContain("unsaved", marker.Classes);

        var viewer = (SvgViewer)item.Content!;

        window.GetVisualDescendants().OfType<SvgViewer>().First().SetSource(Drawing + "<!-- edited -->");
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewer.IsSourceModified, "the drawing was not made unsaved");
        Assert.Contains("unsaved", marker.Classes);

        // The name is untouched, so the mark cannot widen the tab or be trimmed away with it.
        Assert.Equal("home.svg", title.Text);
    }

    [AvaloniaFact]
    public async Task A_Tab_Without_A_Mark_Does_Not_Ask_On_The_Way_Out()
    {
        Write("home.svg", Drawing);

        var window = await Host(Write("icons.svgstudio", $"""
            <studio namespace="Demo.Icons" singleFile="Icons.cs">
            {Holding("home", " class=\"Home\"")}
            </studio>
            """));

        // The project's own settings, which are the ones the panel could not read back — so an edit
        // to one stayed pending after it was saved, leaving the tab unmarked and the close button
        // still asking about it.
        await window.ShowAsync(window.Workspace!.Document.Root);
        Dispatcher.UIThread.RunJobs();

        var panel = Panel(window, "Project");

        Assert.Equal("Icons.cs", panel.Shown("singleFile"));

        panel.Edit("singleFile", "Other.cs");
        Assert.True(panel.IsModified);

        panel.Save();

        // Saved, said to be saved, and nothing left behind to be asked about.
        Assert.False(panel.IsModified);
        Assert.Equal("Other.cs", panel.Shown("singleFile"));
        Assert.Contains("singleFile=\"Other.cs\"", File.ReadAllText(Path.Combine(_directory, "icons.svgstudio")));

        var asked = new List<string>();
        window.ConfirmDiscard = message => { asked.Add(message); return Task.FromResult(true); };

        var item = Tabs(window).Items.OfType<TabItem>().Single(tab => ReferenceEquals(tab.Content, panel));
        ((Button)((StackPanel)item.Header!).Children.OfType<Button>().Single()).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(asked);
    }

    [AvaloniaFact]
    public async Task A_Save_Takes_The_Box_Being_Typed_In_And_The_Mark_Agrees()
    {
        Write("home.svg", Drawing);

        var window = await Host(Write("icons.svgstudio", $"""
            <studio namespace="Demo.Icons" singleFile="Icons.cs">
            {Holding("home", " class=\"Home\"")}
            </studio>
            """));

        await window.ShowAsync(window.Workspace!.Document.Root);
        Dispatcher.UIThread.RunJobs();

        var item = Tabs(window).Items.OfType<TabItem>().Single(tab => tab.Content is GroupPanel);
        var panel = (GroupPanel)item.Content!;
        var marker = (TextBlock)((StackPanel)item.Header!).Children[0];

        panel.Edit("singleFile", "Other.cs");
        Dispatcher.UIThread.RunJobs();

        // A second setting left in a box with the caret still in it. Saving takes that too, so
        // nothing is left pending behind a tab that has just reported itself saved — which is what
        // used to leave a tab with no mark and an unsaved warning waiting at the close button.
        var box = panel.GetVisualDescendants().OfType<TextBox>().Single(candidate => Equals(candidate.Tag, "namespace"));

        box.Focus();
        Dispatcher.UIThread.RunJobs();
        box.Text = "Typed.Icons";
        Dispatcher.UIThread.RunJobs();

        panel.Save();
        Dispatcher.UIThread.RunJobs();

        Assert.False(panel.IsModified);
        Assert.Equal(0d, marker.Opacity);

        var saved = File.ReadAllText(Path.Combine(_directory, "icons.svgstudio"));

        Assert.Contains("singleFile=\"Other.cs\"", saved);
        Assert.Contains("namespace=\"Typed.Icons\"", saved);
    }

    [AvaloniaFact]
    public async Task The_Mark_Appears_At_The_First_Keystroke()
    {

        var window = await Host(Write("icons.svgstudio", Project));

        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.ShowAsync((ProjectNode)((TreeViewItem)root.Items[1]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var item = Tabs(window).Items.OfType<TabItem>()
            .Single(tab => tab.Content is GroupPanel group && ProjectWorkspace.Label(group.Node) == "Large");
        var panel = (GroupPanel)item.Content!;
        var marker = (TextBlock)((StackPanel)item.Header!).Children[0];

        var box = panel.GetVisualDescendants().OfType<TextBox>().Single(candidate => Equals(candidate.Tag, "scale"));

        box.Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0d, marker.Opacity);

        // Typed into and the caret left where it is. The mark is the only thing saying there is
        // anything to save, and it used to wait for the caret to leave before saying so.
        box.Text = "6";
        Dispatcher.UIThread.RunJobs();

        Assert.True(panel.IsModified);
        Assert.Equal(1d, marker.Opacity);

        // And typed back to what the file says, still without leaving, is nothing to save again.
        box.Text = "2";
        Dispatcher.UIThread.RunJobs();

        Assert.False(panel.IsModified);
        Assert.Equal(0d, marker.Opacity);
    }

    [AvaloniaFact]
    public async Task Saving_While_Still_Typing_Saves_What_Is_Being_Typed()
    {

        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);

        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.ShowAsync((ProjectNode)((TreeViewItem)root.Items[1]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var panel = Panel(window, "Large");

        // Typed into, and the caret left where it is. An edit is recorded when the box loses focus,
        // so a save used to find nothing pending and write nothing at all.
        var box = panel.GetVisualDescendants().OfType<TextBox>().Single(candidate => Equals(candidate.Tag, "scale"));

        box.Focus();
        Dispatcher.UIThread.RunJobs();
        box.Text = "6";
        box.CaretIndex = 1;
        Dispatcher.UIThread.RunJobs();

        panel.Save();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("scale=\"6\"", File.ReadAllText(path));
        Assert.False(panel.IsModified);

        // And the caret is still in the box it was in, not thrown out by the rows being rebuilt.
        var resumed = panel.GetVisualDescendants().OfType<TextBox>().Single(candidate => Equals(candidate.Tag, "scale"));

        Assert.True(resumed.IsFocused);
        Assert.Equal(1, resumed.CaretIndex);
    }

    [AvaloniaFact]
    public async Task Exporting_A_Drawing_Gives_It_The_Size_The_Project_Builds_It_At()
    {
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgstudio", Project));

        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.ShowAsync((ProjectNode)((TreeViewItem)((TreeViewItem)root.Items[1]!).Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var viewer = await Settle(window, "badge");

        Assert.Equal(48f, viewer.Document!.Svg.Picture!.CullRect.Width);

        // What the screen shows is what the project builds, so an export that handed back the file
        // as written gave something the viewer never showed.
        var target = Path.Combine(_directory, "exported.svg");

        Assert.True(await window.ExportAsync(target));

        var exported = File.ReadAllText(target);

        Assert.Contains("width=\"48\"", exported);
        Assert.Contains("height=\"48\"", exported);

        // And the file it came from is untouched, since a project's size is not the drawing's.
        Assert.Equal(Drawing, File.ReadAllText(Path.Combine(_directory, "badge.svg")));
    }

    [AvaloniaFact]
    public async Task Export_Is_Offered_Only_While_A_Drawing_Is_Open()
    {
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgstudio", Project));

        var export = Menu(window, "Export…");
        var root = (TreeViewItem)Tree(window).Items[0]!;

        // Nothing is open, so there is nothing to export.
        Assert.False(export.IsEnabled);

        await window.ShowAsync((ProjectNode)((TreeViewItem)root.Items[1]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        // A group holds no drawing. The item used to stay live and do nothing at all when picked.
        Assert.False(export.IsEnabled);

        await window.ShowAsync((ProjectNode)((TreeViewItem)((TreeViewItem)root.Items[1]!).Items[0]!).Tag!);
        await Settle(window, "badge");

        Assert.True(export.IsEnabled);
    }

    /// <summary>The menu item headed <paramref name="header"/>, wherever it lives.</summary>
    private static NativeMenuItem Menu(MainWindow window, string header)
        => NativeMenu.GetMenu(window)!.Items
            .OfType<NativeMenuItem>()
            .SelectMany(item => item.Menu?.Items.OfType<NativeMenuItem>() ?? Enumerable.Empty<NativeMenuItem>())
            .Single(item => item.Header == header);

    [AvaloniaFact]
    public async Task Building_Several_Files_Names_Every_One_Of_Them()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgstudio", $"""
            <studio namespace="Demo.Icons">
            {Holding("home", " class=\"Home\" output=\"Home.cs\"")}
            {Holding("badge", " class=\"Badge\" output=\"Badge.cs\"")}
            </studio>
            """));

        var said = new List<string>();
        window.Announce = (_, message) => { said.Add(message); return Task.CompletedTask; };

        Assert.True(await window.BuildAsync());

        var message = Assert.Single(said);

        // A per-item output is shown nowhere else in the window, so this is the only place it is
        // ever said where one of these went.
        Assert.Contains("Wrote 2 files:", message);
        Assert.Contains(Path.Combine(_directory, "Home.cs"), message);
        Assert.Contains(Path.Combine(_directory, "Badge.cs"), message);

        Assert.True(Path.IsPathRooted(message.Split(Environment.NewLine)[1]));
    }

    [AvaloniaFact]
    public async Task Building_Is_Offered_Only_While_A_Project_Is_Open()
    {
        Write("home.svg", Drawing);

        var window = await Host(Write("home.svg", Drawing));

        // A drawing on its own is not a project, and Build did nothing at all when picked.
        Assert.False(Menu(window, "Build").IsEnabled);
        Assert.False(Menu(window, "Close").IsEnabled);


        var viewer = (SvgViewer)((TabItem)Tabs(window).SelectedItem!).Content!;

        Assert.True(await viewer.OpenAsync(new[] { Write("icons.svgstudio", Project) }));
        Dispatcher.UIThread.RunJobs();

        Assert.True(Menu(window, "Build").IsEnabled);
        Assert.True(Menu(window, "Close").IsEnabled);

        Assert.True(await window.CloseProjectAsync());
        Dispatcher.UIThread.RunJobs();

        Assert.False(Menu(window, "Build").IsEnabled);
    }

    [AvaloniaFact]
    public async Task Building_Writes_What_Svgc_Would_Write()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var path = Write("icons.svgstudio", $"""
            <studio namespace="Demo.Icons" singleFile="Icons.cs">
            {Holding("home", " class=\"Home\"")}
              <group name="Large" namespace="Demo.Icons.Large" scale="2">
            {Holding("badge", " class=\"BadgeLarge\"")}
              </group>
            </studio>
            """);

        var window = await Host(path);

        var said = new List<string>();
        window.Announce = (_, message) => { said.Add(message); return Task.CompletedTask; };

        Assert.True(await window.BuildAsync());

        // In full: a project decides where its own output goes, and the name alone said nothing
        // about where that was.
        Assert.Contains($"Wrote {Path.Combine(_directory, "Icons.cs")}", said);

        var generated = File.ReadAllText(Path.Combine(_directory, "Icons.cs"));

        // The same build svgc runs, so the groups decide the namespaces and the sizes exactly as
        // they do on the command line.
        Assert.Contains("namespace Demo.Icons", generated);
        Assert.Contains("class Home", generated);
        Assert.Contains("namespace Demo.Icons.Large", generated);
        Assert.Contains("class BadgeLarge", generated);

        // 24 as written, and 48 under a group asking for twice the size.
        Assert.Contains("24f, 24f", generated);
        Assert.Contains("48f, 48f", generated);
    }

    /// <summary>The open group tab whose node is labelled <paramref name="label"/>.</summary>
    [AvaloniaFact]
    public async Task A_Drawing_Added_Reaches_The_Tree_And_The_File()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);

        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.AddDrawingAsync((ProjectNode)root.Tag!, Write("extra.svg", Drawing));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            new[] { "Project", "home", "Large", "badge", "extra" },
            Rows((TreeViewItem)Tree(window).Items[0]!));

        // The drawing itself, on lines of its own rather than sharing the closing tag's — and the
        // rest of the file exactly as it was.
        var written = File.ReadAllText(path);

        Assert.Contains("\n  <drawing name=\"extra\">\n    <svg", written, StringComparison.Ordinal);
        Assert.Contains("<rect width=\"24\" height=\"24\" fill=\"#00ff00\" />", written, StringComparison.Ordinal);
        Assert.StartsWith(Project[..Project.IndexOf("</studio>", StringComparison.Ordinal)], written, StringComparison.Ordinal);

        // And it opens, at the size the project it just joined builds it at.
        Assert.Equal("extra", Named(Tab(window, "extra")));
    }

    [AvaloniaFact]
    public async Task A_Group_Added_Opens_So_It_Can_Be_Named()
    {
        Write("badge.svg", Drawing);

        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);

        var group = (TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[1]!;

        await window.AddGroupAsync((ProjectNode)group.Tag!);
        Dispatcher.UIThread.RunJobs();

        // Inside the group it was asked from, indented a level in from it.
        Assert.Contains("    </drawing>\n    <group name=\"group\" />", File.ReadAllText(path), StringComparison.Ordinal);

        // Named by neither of its settings until one is typed, which is what the tab is for.
        Assert.Equal("group", ProjectWorkspace.Label(Panel(window, "group").Node));
    }

    /// <summary>
    /// The tree arrives folded, down to the one row that is always there.
    /// </summary>
    /// <remarks>
    /// A project is usually a handful of rows and opening all of it cost nothing, until an imported
    /// PaintCode document turned out to be ten groups holding 1014 drawings and buried the ten rows
    /// anybody would start from.
    /// </remarks>
    [AvaloniaFact]
    public async Task The_Tree_Opens_Folded_Below_Its_Root()
    {

        var window = await Host(Write("icons.svgstudio", Project));

        var root = (TreeViewItem)Tree(window).Items[0]!;

        Assert.True(root.IsExpanded, "the root row folded, leaving the pane showing one word");
        Assert.False(((TreeViewItem)root.Items[1]!).IsExpanded);
    }

    /// <summary>
    /// A group the reader opened is still open after an edit rebuilds the rows.
    /// </summary>
    /// <remarks>
    /// Every edit builds the rows again from the document, which used to lose nothing because they
    /// were all open anyway. Folded by default, the same rebuild would shut the group somebody had
    /// just opened to find the place to add to — so what is open is held by node and put back.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Group_Left_Open_Is_Still_Open_After_An_Edit()
    {

        var window = await Host(Write("icons.svgstudio", Project));

        var root = (TreeViewItem)Tree(window).Items[0]!;
        var drawing = (TreeViewItem)root.Items[0]!;
        var group = (TreeViewItem)root.Items[1]!;

        group.IsExpanded = true;
        Dispatcher.UIThread.RunJobs();

        // Added beside the drawing at the top rather than inside the group, so the group is not
        // opened again on the way to showing the new row -- which would prove the reveal rather
        // than the memory.
        await window.AddGroupAsync((ProjectNode)drawing.Tag!);
        Dispatcher.UIThread.RunJobs();

        var rebuilt = (TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[2]!;

        Assert.True(rebuilt.IsExpanded, "the edit folded a group that was open");
    }

    [AvaloniaFact]
    public async Task Removing_A_Group_Takes_Its_Tabs_With_It()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);

        window.ConfirmRemove = _ => Task.FromResult(true);

        var root = (TreeViewItem)Tree(window).Items[0]!;
        var group = (TreeViewItem)root.Items[1]!;

        await window.ShowAsync((ProjectNode)group.Tag!);
        await window.ShowAsync((ProjectNode)((TreeViewItem)group.Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, Tabs(window).Items.Count);

        Assert.True(await window.RemoveAsync((ProjectNode)group.Tag!));
        Dispatcher.UIThread.RunJobs();

        // The group's tab and the drawing's under it both go: left open, either would go on editing
        // an element the document no longer holds and report itself saved. The project's own tab is
        // not under the group and stays.
        var kept = Assert.IsType<TabItem>(Assert.Single(Tabs(window).Items));

        Assert.Same(window.Workspace!.Document.Root, Assert.IsType<GroupPanel>(kept.Content).Node);
        Assert.Equal(new[] { "Project", "home" }, Rows((TreeViewItem)Tree(window).Items[0]!));

        // The comment stays. It is a sibling of the group, not part of it.
        var written = File.ReadAllText(path);

        Assert.Contains("<!-- kept, so an edit is proven not to reformat the file -->", written, StringComparison.Ordinal);
        Assert.DoesNotContain("<group", written, StringComparison.Ordinal);
        Assert.Contains("<drawing name=\"home\" class=\"Home\">", written, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_Branch_Refused_At_The_Question_Stays()
    {

        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);

        var asked = new List<string>();

        window.ConfirmRemove = message => { asked.Add(message); return Task.FromResult(false); };

        var group = (ProjectNode)((TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[1]!).Tag!;

        Assert.False(await window.RemoveAsync(group));

        // Asked because it takes a row with it, and the file is untouched.
        Assert.Contains("holds 1 row", Assert.Single(asked));
        Assert.Equal(Project, File.ReadAllText(path));
    }

    [AvaloniaFact]
    public async Task Unsaved_Work_Under_A_Removed_Group_Is_Asked_About_And_Can_Keep_It()
    {

        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);

        window.ConfirmRemove = _ => Task.FromResult(true);
        window.ConfirmDiscard = _ => Task.FromResult(false);

        var group = (ProjectNode)((TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[1]!).Tag!;

        await window.ShowAsync(group);
        Dispatcher.UIThread.RunJobs();

        Panel(window, "Large").Edit("class", "Renamed");

        // The tabs are closed first, so work typed into one still gets its question — and refusing
        // it stops the removal rather than losing the edit to it.
        Assert.False(await window.RemoveAsync(group));

        // The group's tab is still open with the edit in it, beside the project's own.
        Assert.Equal(2, Tabs(window).Items.Count);
        Assert.Equal(Project, File.ReadAllText(path));
    }

    [AvaloniaFact]
    public async Task A_Row_Dropped_On_A_Group_Goes_Into_It()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);

        var root = (TreeViewItem)Tree(window).Items[0]!;
        var home = (ProjectNode)((TreeViewItem)root.Items[0]!).Tag!;
        var group = (ProjectNode)((TreeViewItem)root.Items[1]!).Tag!;

        Assert.True(window.Move(home, group, ProjectDrop.Inside));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            new[] { "Project", "Large", "badge", "home" },
            Rows((TreeViewItem)Tree(window).Items[0]!));

        // Reparented rather than copied, so it now builds at the group's size.
        Assert.Equal(2f, home.EffectiveScale);

        // And the row moved with everything it holds, written at its new depth.
        var written = File.ReadAllText(path);

        Assert.Contains("    <drawing name=\"home\" class=\"Home\">\n      <svg", written, StringComparison.Ordinal);
        Assert.DoesNotContain("\n  <drawing name=\"home\"", written, StringComparison.Ordinal);

        // And back out again, above the group it came from.
        Assert.True(window.Move(home, group, ProjectDrop.Before));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            new[] { "Project", "home", "Large", "badge" },
            Rows((TreeViewItem)Tree(window).Items[0]!));

        Assert.Null(home.EffectiveScale);
    }

    [AvaloniaFact]
    public async Task A_Drop_That_Has_Nowhere_To_Land_Changes_Nothing()
    {

        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);

        var root = (TreeViewItem)Tree(window).Items[0]!;
        var home = (ProjectNode)((TreeViewItem)root.Items[0]!).Tag!;
        var group = (TreeViewItem)root.Items[1]!;
        var badge = (ProjectNode)((TreeViewItem)group.Items[0]!).Tag!;

        // Into a drawing, which holds nothing.
        Assert.False(window.Move(home, badge, ProjectDrop.Inside));

        // Into its own child, which would take the branch out of the document and leave it holding
        // itself.
        Assert.False(window.Move((ProjectNode)group.Tag!, badge, ProjectDrop.After));

        // Beside the project, which has nothing to sit beside.
        Assert.False(window.Move(home, (ProjectNode)root.Tag!, ProjectDrop.After));

        Assert.Equal(Project, File.ReadAllText(path));
    }

    [AvaloniaFact]
    public async Task A_Place_Is_Typed_As_A_Pair_And_Inherited_From_Nobody()
    {
        var path = Write("icons.svgstudio", $"""
            <studio namespace="Demo.Icons">
              <group name="Large" x="40" y="8">
            {Holding("badge")}
              </group>
            </studio>
            """);

        var window = await Host(path);
        var panel = await Group(window, 0);

        // The group's own place, read back off the file rather than left empty — a row that shows
        // nothing can never be typed back to what the file says, and stays pending for ever.
        Assert.Equal("40", panel.Shown("x"));
        Assert.Equal("8", panel.Shown("y"));

        var drawing = ((ProjectGroup)panel.Node).Drawings.Single();

        await window.ShowAsync(drawing);
        Dispatcher.UIThread.RunJobs();

        var settings = (GroupPanel)Assert.Single(((SvgViewer)((TabItem)Tabs(window).SelectedItem!).Content!).SidePanels).Content;
        var box = settings.GetVisualDescendants().OfType<TextBox>().Single(candidate => Equals(candidate.Tag, "x"));

        // Nothing behind it: a group's place is in its parent's coordinates, so offering it as this
        // drawing's inherited x would be a number about somewhere else.
        Assert.Null(box.Text);
        Assert.Null(box.PlaceholderText);

        // Typing one places it on the board's origin on the other axis, since half a point is not
        // a place — and the drawing goes on building at the size its group asks for.
        Assert.True(settings.Edit("x", "12"));

        settings.Save();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(12f, drawing.X);
        Assert.Equal(0f, drawing.Y);
        Assert.Contains("x=\"12\" y=\"0\"", File.ReadAllText(path), StringComparison.Ordinal);

        // Cleared, the place goes with it rather than leaving half of one.
        Assert.True(settings.Edit("y", null));

        settings.Save();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(drawing.X);
        Assert.Null(drawing.Y);
    }

    [AvaloniaFact]
    public async Task A_Drawing_Is_Given_A_Class_Of_Its_Own_In_Its_Tab()
    {
        Write("home.svg", Drawing);

        var path = Write("icons.svgstudio", $"""
            <studio namespace="Demo.Icons">
              <group name="Shared" class="Shared">
            {Holding("shared")}
            {Holding("second")}
              </group>
            </studio>
            """);

        var window = await Host(path);

        var group = (TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[0]!;

        await window.ShowAsync((ProjectNode)((TreeViewItem)group.Items[1]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var tab = Tabs(window).Items.OfType<TabItem>().Single(item => item.Tag is ProjectDrawing);
        var viewer = (SvgViewer)tab.Content!;

        // Beside the drawing's own parameters, in the pane a group keeps its settings in.
        var panel = Assert.IsType<GroupPanel>(Assert.Single(viewer.SidePanels).Content);
        var panes = viewer.GetVisualDescendants().OfType<TabControl>().Single(control => control.Classes.Contains("panes"));

        // First of the two, and so the one shown: a drawing opened from the tree is being looked at
        // as part of a project, so what the project says about it is what to open on.
        Assert.Equal(new[] { "Project", "Parameters", "Element" }, panes.Items.OfType<TabItem>().Select(item => (string)item.Header!));
        Assert.Equal(0, panes.SelectedIndex);

        var box = panel.GetVisualDescendants().OfType<TextBox>().Single(candidate => Equals(candidate.Tag, "class"));

        // Empty, with what the group hands down behind it: both rows become Shared until one of
        // them says otherwise, and generating two classes of one name is what that comes to.
        Assert.Null(box.Text);
        Assert.Equal("Shared — from Shared", box.PlaceholderText);

        panel.Edit("class", "Second");
        Dispatcher.UIThread.RunJobs();

        // Held until the tab is saved, as a group's are, and the tab says so meanwhile.
        Assert.Equal(1d, Marker(tab).Opacity);
        Assert.DoesNotContain("Second", File.ReadAllText(path));

        await window.SaveAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0d, Marker(tab).Opacity);
        Assert.Contains("<drawing name=\"second\" class=\"Second\">", File.ReadAllText(path), StringComparison.Ordinal);

        Assert.Equal(
            new[] { "Project", "Shared", "shared", "second" },
            Rows((TreeViewItem)Tree(window).Items[0]!));
    }

    [AvaloniaFact]
    public async Task A_Drawing_Tab_Answers_For_Its_Settings_As_Well_As_Its_Text()
    {
        Write("home.svg", Drawing);

        var window = await Host(Write("icons.svgstudio", Project));

        var asked = new List<string>();

        window.ConfirmDiscard = message => { asked.Add(message); return Task.FromResult(false); };

        var home = (ProjectNode)((TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[0]!).Tag!;

        await window.ShowAsync(home);
        Dispatcher.UIThread.RunJobs();

        var tab = Tabs(window).Items.OfType<TabItem>().Single(item => item.Tag is ProjectDrawing);

        ((GroupPanel)Assert.Single(((SvgViewer)tab.Content!).SidePanels).Content).Edit("output", "Home.cs");
        Dispatcher.UIThread.RunJobs();

        // The drawing's text is untouched; what is unsaved is the project's say over it, and the
        // close has to ask about that just the same.
        Assert.False(((SvgViewer)tab.Content!).IsSourceModified);

        var close = (Button)((StackPanel)tab.Header!).Children[2];

        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("home has changes that have not been saved.", asked);
        Assert.Contains(tab, Tabs(window).Items);
    }

    private static TextBlock Marker(TabItem item) => (TextBlock)((StackPanel)item.Header!).Children[0];

    private static GroupPanel Panel(MainWindow window, string label)
        => Tabs(window).Items.OfType<TabItem>()
            .Select(item => item.Content)
            .OfType<GroupPanel>()
            .Single(panel => ProjectWorkspace.Label(panel.Node) == label);

    /// <summary>Drops <paramref name="path"/> on the middle of the window, as a file manager would.</summary>
    /// <remarks>
    /// A real drop rather than a call to the handler: what was broken is hit testing — where the
    /// event is raised when there is nothing painted under the pointer — and calling the handler
    /// asserts none of that.
    /// </remarks>
    private static async Task Drop(MainWindow window, string path)
    {
        var file = await window.StorageProvider.TryGetFileFromPathAsync(path);

        Assert.NotNull(file);

        var carried = new DataTransfer();

        carried.Add(DataTransferItem.CreateFile(file!));

        var at = new Point(window.Width / 2d, window.Height / 2d);

        window.DragDrop(at, RawDragEventType.DragEnter, carried, DragDropEffects.Copy, RawInputModifiers.None);
        window.DragDrop(at, RawDragEventType.Drop, carried, DragDropEffects.Copy, RawInputModifiers.None);

        for (var attempt = 0; attempt < 200 && Tabs(window).Items.Count == 0; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The row for a label, found wherever it sits — the tree is rebuilt after every edit.</summary>
    private static TreeViewItem Row(MainWindow window, string label)
        => Descend((TreeViewItem)Tree(window).Items[0]!)
            .First(item => (string)item.Header! == label);

    private static IEnumerable<TreeViewItem> Descend(TreeViewItem item)
        => new[] { item }.Concat(item.Items.OfType<TreeViewItem>().SelectMany(Descend));

    /// <summary>What the row's own menu offers, as a right click on it would show.</summary>
    private static string[] Offers(TreeViewItem row)
        => row.ContextMenu!.Items.OfType<MenuItem>().Select(item => (string)item.Header!).ToArray();

    /// <summary>Picks a command off a row's menu.</summary>
    private static void Pick(MainWindow window, string label, string header)
    {
        var item = Row(window, label).ContextMenu!.Items
            .OfType<MenuItem>()
            .Single(item => (string)item.Header! == header);

        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The name this platform gives the command, which is the name the menu has to use.</summary>
    private static string Revealing => OperatingSystem.IsMacOS()
        ? "Reveal in Finder"
        : OperatingSystem.IsWindows()
            ? "Reveal in File Explorer"
            : "Open Containing Folder";

    [AvaloniaFact]
    public async Task A_Copied_Row_Is_Pasted_Where_An_Add_Would_Go()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);

        Pick(window, "home", "Copy");
        Pick(window, "Large", "Paste");

        // Into the group, at its end — and the row it was copied from stays where it was.
        Assert.Equal(
            new[] { "Project", "home", "Large", "badge", "home" },
            Rows((TreeViewItem)Tree(window).Items[0]!));

        Assert.Contains("<drawing name=\"home\" class=\"Home\">", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_Cut_Row_Moves_Rather_Than_Doubling()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);

        Pick(window, "badge", "Cut");
        Pick(window, "home", "Paste");

        Assert.Equal(
            new[] { "Project", "home", "badge", "Large" },
            Rows((TreeViewItem)Tree(window).Items[0]!));

        // Once in the file, not twice: what was cut is not still where it was.
        Assert.Single(window.Workspace!.Document.Root.Drawings, drawing => drawing.Name == "badge");

        // And nothing is left held: a second paste finds no row waiting and falls through to the
        // clipboard, which in a test has nothing on it, so the tree stays as it is.
        window.Announce = (_, _) => Task.CompletedTask;

        Pick(window, "Large", "Paste");

        Assert.Equal(
            new[] { "Project", "home", "badge", "Large" },
            Rows((TreeViewItem)Tree(window).Items[0]!));
    }

    /// <summary>The refusal a drag already makes, made the same way by a paste.</summary>
    [AvaloniaFact]
    public async Task A_Group_Cut_Cannot_Be_Pasted_Into_Its_Own_Branch()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);

        Pick(window, "Large", "Cut");
        Pick(window, "badge", "Paste");

        Assert.Equal(
            new[] { "Project", "home", "Large", "badge" },
            Rows((TreeViewItem)Tree(window).Items[0]!));

        Assert.Equal(Project, File.ReadAllText(path));
    }

    /// <summary>
    /// Unlike Cut and Copy, which are about the row they are on and can be refused by looking at it.
    /// </summary>
    /// <remarks>
    /// What a paste would land is on the system clipboard as often as it is a row held here, and
    /// there is no looking there to find out: a clipboard read wants a paste behind it. So the
    /// command is offered either way and says for itself when it found nothing.
    /// </remarks>
    [AvaloniaFact]
    public async Task Paste_Is_Offered_Whether_Or_Not_A_Row_Is_Held()
    {
        Write("home.svg", Drawing);

        var window = await Host(Write("icons.svgstudio", Project));

        Assert.Contains("Paste", Offers(Row(window, "Large")));

        Pick(window, "home", "Copy");

        Assert.Contains("Paste", Offers(Row(window, "Large")));
    }

    // ---- pasting a drawing off the system clipboard ------------------------------------------

    /// <summary>An icon copied in a drawing program, which has no file to name.</summary>
    [AvaloniaFact]
    public async Task An_Svg_On_The_Clipboard_Is_Written_Beside_The_Project_And_Added()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);

        await Copy(window, DataTransferItem.CreateText(Drawing));

        Pick(window, "Large", "Paste");
        await Settle(() => Rows((TreeViewItem)Tree(window).Items[0]!).Contains("drawing"));

        Assert.Equal(
            new[] { "Project", "home", "Large", "badge", "drawing" },
            Rows((TreeViewItem)Tree(window).Items[0]!));

        // In the project and nowhere else: nothing is written beside it.
        Assert.Contains("<drawing name=\"drawing\">", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_directory, "drawing.svg")));
    }

    [AvaloniaFact]
    public async Task A_Second_Paste_Is_A_Second_Drawing()
    {
        var window = await Host(Write("icons.svgstudio", Project));

        await Copy(window, DataTransferItem.CreateText(Drawing));

        Pick(window, "Large", "Paste");
        await Settle(() => Rows((TreeViewItem)Tree(window).Items[0]!).Contains("drawing"));

        Pick(window, "Large", "Paste");
        await Settle(() => window.Workspace!.Document.Root.Drawings.Count(drawing => drawing.Name == "drawing") == 2);

        // Two rows reading the same thing: a name is a label rather than an identifier, and which
        // of them is which is settled by renaming one.
        Assert.Equal(2, window.Workspace!.Document.Root.Drawings.Count(drawing => drawing.Name == "drawing"));
    }

    /// <summary>The format Illustrator has also written the same art as.</summary>
    [AvaloniaFact]
    public async Task A_Format_Naming_Itself_Svg_Is_Read_When_The_Text_Is_Not_One()
    {

        var window = await Host(Write("icons.svgstudio", Project));
        var carried = new DataTransfer();

        carried.Add(DataTransferItem.CreateText("Adobe Illustrator artwork"));
        carried.Add(DataTransferItem.Create(DataFormat.CreateStringPlatformFormat("image/svg+xml"), Drawing));

        await window.Clipboard!.SetDataAsync(carried);

        Pick(window, "Large", "Paste");
        await Settle(() => Rows((TreeViewItem)Tree(window).Items[0]!).Contains("drawing"));

        Assert.Contains(
            "<rect width=\"24\" height=\"24\" fill=\"#00ff00\" />",
            window.Workspace!.Document.Root.Drawings.Last().Text,
            StringComparison.Ordinal);
    }

    /// <summary>A drawing already on disk is a row where it is, not a copy of it.</summary>
    [AvaloniaFact]
    public async Task An_Svg_File_On_The_Clipboard_Is_Added_Where_It_Already_Is()
    {

        var window = await Host(Write("icons.svgstudio", Project));
        var copied = Write("mark.svg", Drawing);
        var file = await window.StorageProvider.TryGetFileFromPathAsync(copied);

        await Copy(window, DataTransferItem.CreateFile(file!));

        Pick(window, "Large", "Paste");
        await Settle(() => Rows((TreeViewItem)Tree(window).Items[0]!).Contains("mark.svg"));

        Assert.False(File.Exists(Path.Combine(_directory, "drawing")));
    }

    /// <summary>
    /// A clipboard holding none of it says what it held instead.
    /// </summary>
    /// <remarks>
    /// Which is the whole of what somebody can act on: a drawing program puts several pictures of
    /// the same art on the clipboard, and the paste works or does not by which of them arrived.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Clipboard_With_No_Drawing_On_It_Says_What_It_Had()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgstudio", Project));
        var said = new List<string>();

        window.Announce = (_, message) => { said.Add(message); return Task.CompletedTask; };

        await Copy(window, DataTransferItem.CreateText("just some words"));

        Pick(window, "Large", "Paste");
        await Settle(() => said.Count > 0);

        Assert.Contains("none of it is a drawing", said.Single());
        Assert.False(File.Exists(Path.Combine(_directory, "drawing")));

        Assert.Equal(
            new[] { "Project", "home", "Large", "badge" },
            Rows((TreeViewItem)Tree(window).Items[0]!));
    }

    /// <summary>A row held here is the more particular answer, and the one nothing else knows.</summary>
    [AvaloniaFact]
    public async Task A_Held_Row_Is_Pasted_Rather_Than_What_The_Clipboard_Holds()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgstudio", Project));

        await Copy(window, DataTransferItem.CreateText(Drawing));

        Pick(window, "home", "Copy");
        Pick(window, "Large", "Paste");

        Assert.Equal(
            new[] { "Project", "home", "Large", "badge", "home" },
            Rows((TreeViewItem)Tree(window).Items[0]!));

        Assert.False(File.Exists(Path.Combine(_directory, "drawing")));
    }

    /// <summary>
    /// A hold is spent by the paste that takes it, and the clipboard is heard again after.
    /// </summary>
    /// <remarks>
    /// The alternative was a copy that outlives its paste, which reads well until somebody copies a
    /// row in the morning: every paste that day answers with it, and an icon copied in a drawing
    /// program cannot be pasted at all.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Copied_Row_Is_Pasted_Once_And_Then_The_Clipboard_Is_Heard()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgstudio", Project));

        await Copy(window, DataTransferItem.CreateText(Drawing));

        Pick(window, "home", "Copy");
        Pick(window, "Large", "Paste");

        // The row, and then what was on the clipboard all along.
        Pick(window, "Large", "Paste");
        await Settle(() => Rows((TreeViewItem)Tree(window).Items[0]!).Contains("drawing"));

        Assert.Equal(
            new[]
            {
                "Project", "home", "Large", "badge",
                "home", "drawing"
            },
            Rows((TreeViewItem)Tree(window).Items[0]!));
    }

    /// <summary>Puts one thing on the clipboard, as another program would have left it there.</summary>
    private static async Task Copy(MainWindow window, DataTransferItem item)
    {
        var carried = new DataTransfer();

        carried.Add(item);

        await window.Clipboard!.SetDataAsync(carried);
    }

    /// <summary>Runs the loop until something is true, as a paste is not finished when it returns.</summary>
    private static async Task Settle(Func<bool> until)
    {
        for (var attempt = 0; attempt < 200 && !until(); attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// The keys the menu shows, taken where only the tree hears them.
    /// </summary>
    /// <remarks>
    /// On the tree rather than on the window: the window's own handler tunnels, so a copy taken
    /// there would be the tree's answer to every copy in the app, the search box included.
    /// </remarks>
    [AvaloniaFact]
    public async Task The_Keys_Copy_And_Paste_The_Selected_Row()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgstudio", Project));
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        Press(window, "home", Key.C, command);
        Press(window, "Large", Key.V, command);

        Assert.Equal(
            new[] { "Project", "home", "Large", "badge", "home" },
            Rows((TreeViewItem)Tree(window).Items[0]!));
    }

    /// <summary>Selects a row and presses a key on the tree, as typing into it would.</summary>
    private static void Press(MainWindow window, string label, Key key, KeyModifiers modifiers)
    {
        Tree(window).SelectedItem = Row(window, label);

        Tree(window).RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers
        });

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The project row is the file: there is nowhere to put it and nothing to take it from.</summary>
    [AvaloniaFact]
    public async Task The_Project_Row_Is_Neither_Cut_Nor_Copied()
    {

        var window = await Host(Write("icons.svgstudio", Project));

        var offers = Offers(Row(window, "Project"));

        Assert.DoesNotContain("Cut", offers);
        Assert.DoesNotContain("Copy", offers);
        Assert.DoesNotContain("Remove", offers);
    }

    [AvaloniaFact]
    public async Task Revealing_A_Drawing_Shows_The_Project_Holding_It()
    {
        // A drawing is in the project rather than beside it, so there is no other file to show.
        var project = Write("icons.svgstudio", Project);
        var window = await Host(project);
        var shown = new List<string>();

        window.ShowOnDisk = path => shown.Add(path);

        Pick(window, "home", Revealing);

        Assert.Equal(new[] { project }, shown);
    }

    /// <summary>A group is not a file, so what it shows is the project it is written in.</summary>
    [AvaloniaFact]
    public async Task Revealing_A_Group_Shows_The_Project()
    {

        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);
        var shown = new List<string>();

        window.ShowOnDisk = shown.Add;

        Pick(window, "Large", Revealing);
        Pick(window, "Project", Revealing);

        Assert.Equal(new[] { path, path }, shown);
    }

    /// <summary>The row's own height, without the branch under it — as the window measures it.</summary>
    private static double RowHeight(TreeViewItem item)
        => item.IsExpanded
            && item.Items.Count > 0
            && item.Items[0] is Visual first
            && first.TranslatePoint(new Point(0, 0), item) is { Y: > 0 } at
                ? at.Y
                : item.Bounds.Height;

    /// <summary>Drops files a fraction of the way down a row, as a file manager would.</summary>
    /// <remarks>
    /// Where down the row decides what the drop means — into a group, or before or after a row — so
    /// the point is measured against the row's own height rather than its bounds, which cover
    /// everything under it as well.
    ///
    /// The drag is walked through in full: only the move over the row works out where the drop
    /// would land, and a drop that never moved lands nowhere.
    /// </remarks>
    private static async Task Drop(MainWindow window, TreeViewItem row, double down, params string[] paths)
    {
        var carried = new DataTransfer();

        foreach (var path in paths)
        {
            var file = await window.StorageProvider.TryGetFileFromPathAsync(path);

            Assert.NotNull(file);

            carried.Add(DataTransferItem.CreateFile(file!));
        }

        var at = row.TranslatePoint(new Point(12, RowHeight(row) * down), window);

        Assert.NotNull(at);

        foreach (var stage in new[] { RawDragEventType.DragEnter, RawDragEventType.DragOver, RawDragEventType.Drop })
        {
            window.DragDrop(at!.Value, stage, carried, DragDropEffects.Copy, RawInputModifiers.None);
        }

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task A_Drawing_Dropped_On_A_Group_Goes_Into_It()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);
        var root = (TreeViewItem)Tree(window).Items[0]!;

        await Drop(window, (TreeViewItem)root.Items[1]!, 0.5d, Write("extra.svg", Drawing));

        Assert.Equal(
            new[] { "Project", "home", "Large", "badge", "extra" },
            Rows((TreeViewItem)Tree(window).Items[0]!));

        Assert.Contains("<drawing name=\"badge\" class=\"BadgeLarge\">", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Contains("<drawing name=\"extra\">", File.ReadAllText(path), StringComparison.Ordinal);

        Assert.Equal("extra", Named(Tab(window, "extra")));
    }

    /// <summary>
    /// The order dragged, after the row dropped on.
    /// </summary>
    /// <remarks>
    /// The one landing where asking twice gives the wrong answer: the row dropped after does not
    /// move as drawings are put behind it, so a second file worked out from scratch would land
    /// between it and the first.
    /// </remarks>
    [AvaloniaFact]
    public async Task Drawings_Dropped_After_A_Row_Keep_The_Order_They_Came_In()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgstudio", Project));
        var root = (TreeViewItem)Tree(window).Items[0]!;

        var before = Tabs(window).Items.Count;

        await Drop(window, (TreeViewItem)root.Items[0]!, 0.9d, Write("one.svg", Drawing), Write("two.svg", Drawing));

        Assert.Equal(
            new[] { "Project", "home", "one", "two", "Large", "badge" },
            Rows((TreeViewItem)Tree(window).Items[0]!));

        // One tab for the run, on the last of them: a folder of drawings is one act.
        Assert.Equal("two", Named(Tab(window, "two")));
        Assert.Equal(before + 1, Tabs(window).Items.Count);
    }

    /// <summary>The tree's background is the project itself, so a drop with no row under it lands there.</summary>
    [AvaloniaFact]
    public async Task A_Drawing_Dropped_Below_The_Rows_Goes_To_The_Project()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgstudio", Project));
        var root = (TreeViewItem)Tree(window).Items[0]!;

        // Under everything the project has, which is still inside the tree.
        await Drop(window, root, (root.Bounds.Height + 40d) / RowHeight(root), Write("extra.svg", Drawing));

        Assert.Equal(
            new[] { "Project", "home", "Large", "badge", "extra" },
            Rows((TreeViewItem)Tree(window).Items[0]!));
    }

    /// <summary>
    /// Only drawings are the tree's. Everything else is still the window's, and still opens.
    /// </summary>
    [AvaloniaFact]
    public async Task A_Project_Dropped_On_The_Tree_Opens_As_A_Project()
    {

        var window = await Host(Write("icons.svgstudio", Project));
        var root = (TreeViewItem)Tree(window).Items[0]!;

        var second = Write("other.svgstudio", Project.Replace("Demo.Icons", "Demo.Other", StringComparison.Ordinal));

        await Drop(window, root, 0.5d, second);

        for (var attempt = 0; attempt < 200 && window.Workspace?.Name != "other.svgstudio"; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.Equal("other.svgstudio", window.Workspace!.Name);
    }

    [AvaloniaFact]
    public async Task A_Drawing_Dropped_On_An_Empty_Window_Opens()
    {
        var drawing = Write("home.svg", Drawing);

        var window = new MainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Nothing is open, so nothing paints where a tab's drawing would be. The tab that used to
        // always be there was the drop target, and taking it away took the target with it.
        Assert.Empty(Tabs(window).Items);

        await Drop(window, drawing);

        Assert.Equal("home.svg", Named(Tab(window, "home.svg")));
    }

    [AvaloniaFact]
    public async Task A_Drawing_Dropped_On_One_That_Is_Open_Opens_Once()
    {
        Write("home.svg", Drawing);

        var badge = Write("badge.svg", Drawing.Replace("#00ff00", "#ff0000", StringComparison.Ordinal));

        var window = new MainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        await window.OpenAsync(new[] { Path.Combine(_directory, "home.svg") });
        Dispatcher.UIThread.RunJobs();

        Assert.Single(Tabs(window).Items);

        await Drop(window, badge);

        // Twice would be the ordinary outcome: the viewer takes a drop on itself and the window
        // takes everything else, and the same drop reaches both unless the viewer says it is done.
        for (var attempt = 0; attempt < 200 && Tabs(window).Items.Count < 2; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.Equal(2, Tabs(window).Items.Count);
    }

    /// <summary>A window with nothing open, which is what New… is picked from.</summary>
    private static MainWindow Empty()
    {
        var window = new MainWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        return window;
    }

    /// <summary>
    /// New: the project Open cannot reach, because it is not written yet.
    /// </summary>
    [AvaloniaFact]
    public async Task A_New_Project_Is_Written_And_Opened()
    {
        var path = Path.Combine(_directory, "fresh.svgstudio");
        var window = Empty();

        await window.NewProjectAsync(path);

        Dispatcher.UIThread.RunJobs();

        Assert.True(File.Exists(path));
        Assert.Equal("fresh.svgstudio", window.Workspace!.Name);
        Assert.Empty(window.Workspace.Document.Root.Drawings);

        // The tree is the project row and nothing under it: a new project holds no drawings.
        Assert.Equal(new[] { "Project" }, Rows((TreeViewItem)Tree(window).Items[0]!));

        // Written, opened, and open on its own settings — which for a project with nothing in it
        // yet is the only thing there is to be on.
        var tab = Assert.IsType<TabItem>(Assert.Single(Tabs(window).Items));

        Assert.Same(window.Workspace.Document.Root, Assert.IsType<GroupPanel>(tab.Content).Node);
    }

    /// <summary>
    /// The empty root is somewhere to write into: the first drawing lands indented under it rather
    /// than level with it, which is what an element written with no sibling to copy risks.
    /// </summary>
    [AvaloniaFact]
    public async Task A_Drawing_Added_To_A_New_Project_Is_Written_Under_It()
    {
        var path = Path.Combine(_directory, "fresh.svgstudio");
        var window = Empty();

        await window.NewProjectAsync(path);

        Dispatcher.UIThread.RunJobs();

        await Drop(window, (TreeViewItem)Tree(window).Items[0]!, 0.5d, Write("dropped.svg", Drawing));

        for (var attempt = 0; attempt < 200 && !window.Workspace!.Document.Root.Drawings.Any(); attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.Single(window.Workspace!.Document.Root.Drawings);
        Assert.Contains("\n  <drawing name=\"dropped\">\n    <svg ", File.ReadAllText(path), StringComparison.Ordinal);
    }

    /// <summary>
    /// New over a project that is already there opens it. The save panel asked about replacing the
    /// file; emptying somebody's project is not what answering yes to that meant.
    /// </summary>
    [AvaloniaFact]
    public async Task A_New_Project_Over_One_That_Exists_Opens_It()
    {

        var path = Write("icons.svgstudio", Project);
        var window = Empty();

        await window.NewProjectAsync(path);

        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Project, File.ReadAllText(path));
        Assert.Equal("Demo.Icons", window.Workspace!.Document.Root.Namespace);
    }

    /// <summary>Waits for the tab holding <paramref name="name"/> to have finished loading.</summary>
    /// <summary>The tab showing <paramref name="name"/>, which must be open.</summary>
    private static TabItem Tab(MainWindow window, string name)
        => Tabs(window).Items.OfType<TabItem>().Single(item => Named(item) == name);

    /// <summary>What a tab is showing: the row's name where the project holds it, else the file's.</summary>
    private static string? Named(TabItem item) => item.Tag is ProjectDrawing drawing
        ? drawing.Name
        : (item.Content as SvgViewer)?.DocumentPath is { } path
            ? Path.GetFileName(path)
            : null;

    private static async Task<SvgViewer> Settle(MainWindow window, string name)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            Dispatcher.UIThread.RunJobs();

            var viewer = Tabs(window).Items
                .OfType<TabItem>()
                .Where(item => Named(item) == name)
                .Select(item => item.Content)
                .OfType<SvgViewer>()
                .FirstOrDefault();

            if (viewer?.Document is { })
            {
                return viewer;
            }

            await Task.Delay(10);
        }

        throw new InvalidOperationException($"'{name}' was never opened.");
    }
}
