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
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using Svg.CodeGen.Skia.Projects;
using SkiaSharp;
using Svg.Expressions;
using Svg.Expressions.Recipes;
using Svg.Viewer.Skia.Avalonia;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// Opening an svgc project: the tree beside the tabs, and a drawing shown at the size the project
/// builds it at rather than the one it was written with.
/// </summary>
public class MainWindowProjectTests : IDisposable
{
    private const string Drawing = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <rect width="24" height="24" fill="#00ff00" />
        </svg>
        """;

    private const string Project = """
        <svgc>
          <namespace>Demo.Icons</namespace>

          <svg input="home.svg" class="Home" />

          <!-- kept, so an edit is proven not to reformat the file -->
          <group namespace="Demo.Icons.Large" scale="2">
            <svg input="badge.svg" class="BadgeLarge" />
          </group>
        </svgc>
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
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Nothing is open until this: a window starts empty, with no tab standing in for a file.
        await window.OpenAsync(new[] { path });

        Dispatcher.UIThread.RunJobs();

        return window;
    }

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

        var window = await Host(Write("icons.svgcproj", Project));

        Assert.Equal("icons.svgcproj", window.Workspace!.Name);

        // The pane and one tab: the project's own settings. A window with a project open and
        // nothing on it showed the tree and an empty strip, and the row to click to see anything
        // was the one row of the tree that is always there.
        var tab = Assert.IsType<TabItem>(Assert.Single(Tabs(window).Items));

        Assert.Same(window.Workspace.Document.Root, Assert.IsType<GroupPanel>(tab.Content).Node);
        Assert.Same(tab, Tabs(window).SelectedItem);

        Assert.True(window.FindControl<Border>("ProjectPaneHost")!.IsVisible);

        var root = Assert.IsType<TreeViewItem>(Assert.Single(Tree(window).Items));

        // A drawing carries what it becomes, since a project usually builds one file more than once.
        Assert.Equal(
            new[] { "Demo.Icons", "home.svg - Home", "Demo.Icons.Large", "badge.svg - BadgeLarge" },
            Rows(root));
    }

    [AvaloniaFact]
    public async Task A_Group_Is_Named_By_Whichever_Of_Namespace_And_Class_It_Sets()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", """
            <svgc>
              <group namespace="Nav" class="Both"><svg input="home.svg" /></group>
              <group namespace="OnlySpace"><svg input="home.svg" /></group>
              <group class="OnlyClass"><svg input="home.svg" /></group>
              <group scale="2"><svg input="badge.svg" /></group>
            </svgc>
            """));

        var root = Assert.IsType<TreeViewItem>(Assert.Single(Tree(window).Items));

        // Neither is a name, but they are all the format has to tell one group from another. A row
        // reading "group" for every one of them told them apart no better than nothing.
        Assert.Equal(
            new[]
            {
                "Project",
                "Nav - Both", "home.svg - Both",
                "OnlySpace", "home.svg",
                "OnlyClass", "home.svg - OnlyClass",
                "group", "badge.svg"
            },
            Rows(root));
    }

    [AvaloniaFact]
    public async Task One_File_Built_Several_Times_Gives_A_Row_Each()
    {
        Write("badge.svg", Drawing);

        // The shape a project is for: one drawing, built at several sizes under several names.
        var window = await Host(Write("icons.svgcproj", """
            <svgc>
              <svg input="badge.svg" class="Badge" />
              <group class="BadgeLarge" scale="2">
                <svg input="badge.svg" />
                <group><svg input="badge.svg" scale="4" /></group>
              </group>
              <svg input="badge.svg" />
            </svgc>
            """));

        var root = Assert.IsType<TreeViewItem>(Assert.Single(Tree(window).Items));

        // The class each entry ends up with, inherited or its own. Named by the file alone every
        // one of these read "badge.svg"; the last has no class anywhere and still does.
        Assert.Equal(
            new[]
            {
                "Project",
                "badge.svg - Badge",
                "BadgeLarge",
                "badge.svg - BadgeLarge",
                "group", "badge.svg - BadgeLarge",
                "badge.svg"
            },
            Rows(root));
    }

    [AvaloniaFact]
    public async Task A_Group_Opens_In_A_Tab_Showing_What_It_Builds()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Project));

        var root = (TreeViewItem)Tree(window).Items[0]!;
        var group = (SvgcProjectNode)((TreeViewItem)root.Items[1]!).Tag!;

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

    /// <summary>A recipe that paints the fixture something it is not, so a pixel can tell.</summary>
    /// <remarks>
    /// hue 0 rather than the 120 <see cref="Recipe"/> uses: 120 is hsl for the #00ff00 the drawing
    /// already is, which would pass whether the recipe reached the canvas or not.
    /// </remarks>
    private const string RedRecipe = """
        <recipe xmlns="https://svg.skia/expr/1.0">
          <code>
            <param name="hue" type="number" default="0" />
            <let name="tint">hsl(hue, 100%, 50%)</let>
          </code>
          <replace color="#00ff00">tint</replace>
        </recipe>
        """;

    [AvaloniaFact]
    public async Task A_Groups_Drawings_Are_Drawn_On_Its_Tab()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Project));

        // The project row opens as a tab of its own, and it is a group like any other.
        var drawn = Drawn(Panel(window, "Demo.Icons"));

        Assert.Equal(2, drawn.Count);
        Assert.All(drawn, placed => Assert.NotNull(Picture(placed)));

        // In the order the project builds them, and not one on top of another.
        Assert.StartsWith("home.svg", drawn[0].Label);
        Assert.StartsWith("badge.svg", drawn[1].Label);
        Assert.NotEqual(drawn[0].At, drawn[1].At);
    }

    [AvaloniaFact]
    public async Task A_Drawing_Keeps_The_Line_The_List_Showed_As_Its_Caption()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Project));

        Assert.Equal(
            new[]
            {
                "home.svg\nDemo.Icons.Home   as written",
                "badge.svg\nDemo.Icons.Large.BadgeLarge   ×2"
            },
            Drawn(Panel(window, "Demo.Icons")).Select(placed => placed.Label).ToArray());
    }

    /// <summary>The whole branch, which is what the group builds.</summary>
    [AvaloniaFact]
    public async Task A_Group_Draws_The_Drawings_Of_The_Groups_Under_It()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Project));

        var root = (TreeViewItem)Tree(window).Items[0]!;

        // The project row shows both; the group under it shows the one it holds. Read before the
        // tab is switched, since a panel that is not the one being looked at holds no pictures.
        Assert.Equal(2, Drawn(Panel(window, "Demo.Icons")).Count);

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)root.Items[1]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(Drawn(Panel(window, "Demo.Icons.Large")));
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
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Project));
        var drawn = Drawn(Panel(window, "Demo.Icons"));

        // The fixture is 24 square; the group builds it at ×2. Nothing is fitted to a tile: the
        // canvas is placed in drawing units and its own zoom is what makes the spread fit.
        Assert.Equal(24f, Picture(drawn[0])!.CullRect.Width);
        Assert.Equal(48f, Picture(drawn[1])!.CullRect.Width);
    }

    [AvaloniaFact]
    public async Task A_Group_With_No_Drawings_Still_Says_So()
    {
        var window = await Host(Write("icons.svgcproj", """
            <svgc>
              <group namespace="Empty" />
            </svgc>
            """));

        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)root.Items[0]!).Tag!);
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
        Write("home.svg", Drawing);
        Write("badge.svg", "not a drawing at all");

        var window = await Host(Write("icons.svgcproj", Project));
        var panel = Panel(window, "Demo.Icons");

        // The one that reads is still drawn, and the one that does not is said out loud.
        Assert.Single(Drawn(panel));
        Assert.StartsWith("home.svg", Drawn(panel)[0].Label);

        Assert.Contains(
            panel.GetVisualDescendants().OfType<TextBlock>(),
            block => block.IsVisible && block.Text is { } said && said.StartsWith("badge.svg could not be read"));
    }

    /// <summary>
    /// The pictures live exactly as long as the tab is the one being looked at.
    /// </summary>
    /// <remarks>
    /// Written down because it is what keeps the canvas cheap: every save refreshes every open
    /// panel, and a group tab nobody is looking at would otherwise re-read every drawing under it.
    /// </remarks>
    [AvaloniaFact]
    public async Task Drawings_Are_Let_Go_While_The_Tab_Is_Not_Looked_At()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Project));
        var panel = Panel(window, "Demo.Icons");

        Assert.Equal(2, Drawn(panel).Count);

        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)root.Items[1]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(Drawn(panel));

        Tabs(window).SelectedItem = Tabs(window).Items.OfType<TabItem>()
            .Single(item => ReferenceEquals(item.Content, panel));
        Dispatcher.UIThread.RunJobs();

        Assert.All(Drawn(panel), placed => Assert.NotNull(Picture(placed)));
    }

    /// <summary>
    /// A group's canvas is painted the way its drawings' own tabs are painted.
    /// </summary>
    /// <remarks>
    /// Through the open buffer rather than the recipe file, which the second half is what proves: a
    /// rule typed and not saved moves the icons, as it already moves the drawing on its own tab.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Drawing_Under_A_Recipe_Is_Drawn_As_The_Project_Builds_It()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var recipe = Write("icons.recipe", RedRecipe);
        var window = await Host(Write("icons.svgcproj", RecipeProject));
        var panel = Panel(window, "Demo.Icons");
        var drawn = Drawn(panel);

        // home.svg is outside the group the recipe is on, so nothing rewrites it.
        Assert.Equal(SKColors.Lime, Centre(Picture(drawn[0])!));
        Assert.Equal(SKColors.Red, Centre(Picture(drawn[1])!));

        window.ShowRecipe(recipe).Workspace.Document.Text = RedRecipe.Replace("\"0\"", "\"240\"");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(RedRecipe, File.ReadAllText(recipe));

        Tabs(window).SelectedItem = Tabs(window).Items.OfType<TabItem>()
            .Single(item => ReferenceEquals(item.Content, panel));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(SKColors.Blue, Centre(Picture(Drawn(panel)[1])!));
    }

    /// <summary>The colour in the middle of a drawing, which for the fixture is all of it.</summary>
    private static SKColor Centre(SKPicture picture)
    {
        var bounds = picture.CullRect;

        using var bitmap = new SKBitmap((int)bounds.Width, (int)bounds.Height);
        using var canvas = new SKCanvas(bitmap);

        canvas.Clear(SKColors.Transparent);
        canvas.DrawPicture(picture);

        return bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
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
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Project));
        var panel = Panel(window, "Demo.Icons");
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
        <svgc>
          <namespace>Demo.Icons</namespace>

          <group namespace="Demo.Icons.Both">
            <svg input="home.svg" class="Home" />
            <svg input="badge.svg" class="Badge" />
          </group>
        </svgc>
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
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Pair));

        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)root.Items[0]!).Tag!);
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

    /// <summary>A group that builds the one file twice, which is what a project usually does.</summary>
    private const string Twice = """
        <svgc>
          <namespace>Demo.Icons</namespace>
          <group namespace="Demo.Icons.Large" scale="2">
            <svg input="badge.svg" class="BadgeLarge" />
            <group class="BadgeHuge">
              <svg input="badge.svg" scale="4" />
            </group>
          </group>
        </svgc>
        """;

    [AvaloniaFact]
    public async Task Each_Build_Of_The_Same_File_Can_Be_Picked_In_Turn()
    {
        // Reported against Demo.Icons.Large: nothing in BadgeLarge could be picked once anything in
        // BadgeHuge had been. Both are built from badge.svg, so their elements carry the same
        // addresses, and asking a tree keyed by address for a row it already has selected is
        // indistinguishable from asking it for nothing.
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Twice));

        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)root.Items[0]!).Tag!);
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

    /// <summary>A drawing that declares its own parameters, so it needs no recipe.</summary>
    private const string Declaring = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs><e:code><e:param name="tint" type="color" default="#00ff00" /></e:code></defs>
          <rect width="24" height="24" fill="{{ tint }}" />
        </svg>
        """;

    /// <summary>A group with a recipe on it and two drawings built through it.</summary>
    private const string SharedRecipeProject = """
        <svgc>
          <namespace>Demo.Icons</namespace>
          <group namespace="Demo.Icons.Both" recipe="icons.recipe">
            <svg input="home.svg" class="Home" />
            <svg input="badge.svg" class="Badge" />
          </group>
        </svgc>
        """;

    // ---- writing a recipe into the drawings ------------------------------------------------------

    /// <summary>Two groups, each with a recipe of its own, under one that names none.</summary>
    private const string NestedRecipeProject = """
        <svgc>
          <namespace>Demo.Icons</namespace>
          <group namespace="Demo.Icons.All">
            <group namespace="Demo.Icons.One" recipe="icons.recipe">
              <svg input="home.svg" class="Home" />
            </group>
            <group namespace="Demo.Icons.Two" recipe="other.recipe">
              <svg input="badge.svg" class="Badge" />
            </group>
          </group>
        </svgc>
        """;

    private const string OtherRecipe = """
        <recipe xmlns="https://svg.skia/expr/1.0">
          <code>
            <param name="shade" type="color" default="#0000ff" />
          </code>
          <replace color="#00ff00">shade</replace>
        </recipe>
        """;

    /// <summary>The project node a tree row stands for, found by the label the row shows.</summary>
    private static SvgcProjectNode Node(MainWindow window, string label)
        => (SvgcProjectNode)Row(window, label).Tag!;

    /// <summary>A window whose dialogs answer themselves, since a modal cannot be driven.</summary>
    private static List<string> Willing(MainWindow window)
    {
        var said = new List<string>();

        window.ConfirmApply = _ => Task.FromResult(true);
        window.Announce = (_, message) => { said.Add(message); return Task.CompletedTask; };

        return said;
    }

    [AvaloniaFact]
    public async Task Applying_A_Recipe_Writes_It_Into_The_Drawings()
    {
        var home = Write("home.svg", Drawing);
        var badge = Write("badge.svg", Drawing);
        Write("icons.recipe", Recipe);

        var window = await Host(Write("icons.svgcproj", SharedRecipeProject));

        Willing(window);

        Assert.True(await window.ApplyRecipeAsync(Node(window, "Demo.Icons.Both")));
        Dispatcher.UIThread.RunJobs();

        // Both files hold the declarations and the expression now, and neither is the recipe's.
        foreach (var path in new[] { home, badge })
        {
            var written = File.ReadAllText(path);

            Assert.Contains("<e:code>", written);
            Assert.Contains("name=\"hue\"", written);
            Assert.Contains("fill=\"{{ tint }}\"", written);
            Assert.DoesNotContain("#00ff00", written);
        }
    }

    [AvaloniaFact]
    public async Task Applying_A_Recipe_Stops_The_Node_Naming_It()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);
        Write("icons.recipe", Recipe);

        var path = Write("icons.svgcproj", SharedRecipeProject);
        var window = await Host(path);

        Willing(window);

        Assert.True(await window.ApplyRecipeAsync(Node(window, "Demo.Icons.Both")));
        Dispatcher.UIThread.RunJobs();

        // The drawings declare their own parameters now, so the project has no reason to name it —
        // and naming it would put a build back through a conversion with nothing left to do.
        Assert.DoesNotContain("recipe=", File.ReadAllText(path));
    }

    /// <summary>Each drawing takes the recipe nearest to it, not the one the command was pressed on.</summary>
    [AvaloniaFact]
    public async Task Applying_A_Recipe_Reaches_The_Recipes_Of_Groups_Below()
    {
        var home = Write("home.svg", Drawing);
        var badge = Write("badge.svg", Drawing);
        Write("icons.recipe", Recipe);
        Write("other.recipe", OtherRecipe);

        var path = Write("icons.svgcproj", NestedRecipeProject);
        var window = await Host(path);

        Willing(window);

        // The node pressed names no recipe at all; both of the ones below it are applied.
        Assert.True(await window.ApplyRecipeAsync(Node(window, "Demo.Icons.All")));
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("fill=\"{{ tint }}\"", File.ReadAllText(home));
        Assert.Contains("fill=\"{{ shade }}\"", File.ReadAllText(badge));

        // Both settings are gone, since both nodes that named one sit under what was applied.
        Assert.DoesNotContain("recipe=", File.ReadAllText(path));
    }

    /// <summary>The recipe is on the project, above the group the command is pressed on.</summary>
    private const string InheritedRecipeProject = """
        <svgc>
          <namespace>Demo.Icons</namespace>
          <recipe>icons.recipe</recipe>
          <group namespace="Demo.Icons.Both">
            <svg input="home.svg" class="Home" />
            <svg input="badge.svg" class="Badge" />
          </group>
        </svgc>
        """;

    /// <summary>
    /// A recipe inherited from above the node is applied, and left named.
    /// </summary>
    /// <remarks>
    /// It covers drawings this did not touch, so taking it away would take it from them too. Safe
    /// to leave only because applying it again to a drawing that already holds its declarations
    /// now does nothing — which is what the second half of this checks.
    /// </remarks>
    [AvaloniaFact]
    public async Task Applying_An_Inherited_Recipe_Leaves_It_Named_And_Is_Idempotent()
    {
        var home = Write("home.svg", Drawing);
        Write("badge.svg", Drawing);
        Write("icons.recipe", Recipe);

        var path = Write("icons.svgcproj", InheritedRecipeProject);
        var window = await Host(path);

        var said = Willing(window);

        Assert.True(await window.ApplyRecipeAsync(Node(window, "Demo.Icons.Both")));
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("fill=\"{{ tint }}\"", File.ReadAllText(home));

        // The project still names it, because it is the project's and not this group's.
        Assert.Contains("icons.recipe", File.ReadAllText(path));

        var was = File.ReadAllText(home);

        said.Clear();

        // So the command is still offered, and pressing it again goes all the way through the
        // rewriter, finds every declaration already made and every value already an expression,
        // and writes nothing.
        Assert.False(await window.ApplyRecipeAsync(Node(window, "Demo.Icons.Both")));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(was, File.ReadAllText(home));
        Assert.Contains(said, message => message.Contains("already in the expression format", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Applying_A_Recipe_Named_Twice_Over_Writes_The_File_Once()
    {
        // A project builds one drawing more than once. Converting it per row would convert it and
        // then meet its own output on the second pass.
        Write("home.svg", Drawing);
        Write("icons.recipe", Recipe);

        var window = await Host(Write("icons.svgcproj", """
            <svgc>
              <namespace>Demo.Icons</namespace>
              <group namespace="Demo.Icons.Both" recipe="icons.recipe">
                <svg input="home.svg" class="Home" />
                <svg input="home.svg" class="HomeAgain" />
              </group>
            </svgc>
            """));

        var said = Willing(window);

        Assert.True(await window.ApplyRecipeAsync(Node(window, "Demo.Icons.Both")));
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(said, message => message.Contains("1 of 1", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Applying_A_Recipe_Is_Refused_While_Anything_Is_Unsaved()
    {
        var home = Write("home.svg", Drawing);
        Write("badge.svg", Drawing);
        var recipe = Write("icons.recipe", Recipe);

        var window = await Host(Write("icons.svgcproj", SharedRecipeProject));

        var said = Willing(window);
        var was = File.ReadAllText(home);

        // A rule typed into the recipe and not saved. Reading the file would bake something else
        // than what is on screen; reading the buffer would bake what no file says.
        window.ShowRecipe(recipe).Workspace.Document.Insert(0, " ");
        Dispatcher.UIThread.RunJobs();

        Assert.False(await window.ApplyRecipeAsync(Node(window, "Demo.Icons.Both")));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(was, File.ReadAllText(home));
        Assert.Contains(said, message => message.Contains("icons.recipe", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Applying_A_Recipe_Offers_Itself_Where_A_Recipe_Reaches()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);
        Write("icons.recipe", Recipe);
        Write("other.recipe", OtherRecipe);

        var window = await Host(Write("icons.svgcproj", NestedRecipeProject));

        // The outer group names nothing itself, and still offers it: the groups under it do.
        Assert.Contains("Apply…", RecipeButtons(await Group(window, 0)));
    }

    [AvaloniaFact]
    public async Task A_Group_Under_No_Recipe_Does_Not_Offer_To_Apply_One()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Pair));

        Assert.DoesNotContain("Apply…", RecipeButtons(await Group(window, 0)));
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

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)root.Items[child]!).Tag!);
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
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);
        Write("icons.recipe", Recipe);

        var window = await Host(Write("icons.svgcproj", SharedRecipeProject));
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
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);
        Write("icons.recipe", Recipe);

        var window = await Host(Write("icons.svgcproj", SharedRecipeProject));
        var panel = await Group(window, 0);

        Pick(window, panel, 0);

        // Read off the drawing as built, which is where a recipe's declarations end up.
        Assert.Equal(new[] { "hue" }, Declarations(panel).Parameters!.Select(row => row.Name).ToArray());
    }

    /// <summary>A group holding two drawings under one recipe and one that declares its own.</summary>
    private const string MixedProject = """
        <svgc>
          <namespace>Demo.Icons</namespace>
          <group namespace="Demo.Icons.Mixed">
            <group recipe="icons.recipe">
              <svg input="home.svg" class="One" />
              <svg input="badge.svg" class="Two" />
            </group>
            <svg input="plain.svg" class="Plain" />
          </group>
        </svgc>
        """;

    /// <summary>A recipe declaring a parameter the host is expected to supply, as a real one does.</summary>
    private const string OpenEndedRecipe = """
        <recipe xmlns="https://svg.skia/expr/1.0">
          <code>
            <param name="accent" type="color" default="#00a7ff" />
            <param name="whiteColor" type="color" />
            <param name="on" type="boolean" default="true" />
            <let name="colour">on ? accent : whiteColor</let>
          </code>
          <replace color="#00ff00">colour</replace>
        </recipe>
        """;

    [AvaloniaFact]
    public async Task A_Parameter_With_No_Default_Does_Not_Grey_The_Group()
    {
        // Reported against a real project: every icon came up grey, which is what a drawing renders
        // when its expressions are left at placeholders. Binding the declared defaults refuses the
        // whole set the moment one parameter has none -- and a recipe is entitled to declare one --
        // so nothing was bound at all. A viewer never hit it, because it binds the rows its panel
        // seeds rather than the defaults.
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);
        Write("icons.recipe", OpenEndedRecipe);

        var window = await Host(Write("icons.svgcproj", SharedRecipeProject));
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

            // The accent the recipe names, not the placeholder grey it fell back to.
            Assert.True(
                painted.Blue > 200 && painted.Red < 100,
                $"{painted} is not the accent colour: the drawing is still on its placeholders");
        }
    }

    [AvaloniaFact]
    public async Task A_Value_Reaches_Every_Drawing_Sharing_The_Declaration()
    {
        // A value belongs where its declaration does. Under a recipe that is every drawing built
        // through it, so moving one slider moves the family -- which is the whole reason to look at
        // them side by side. A drawing declaring its own shares with nothing.
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);
        Write("plain.svg", Declaring);
        Write("icons.recipe", Recipe);

        var window = await Host(Write("icons.svgcproj", MixedProject));
        var panel = await Group(window, 0);

        var placements = Drawn(panel);

        Assert.Equal(3, placements.Count);

        Pick(window, panel, 0);

        var before = placements.Select(placed => placed.Svg.Picture).ToArray();

        ((SvgViewerNumberParameter)Declarations(panel).Parameters!.Single()).Value = 0d;
        Dispatcher.UIThread.RunJobs();

        Assert.NotSame(before[0], placements[0].Svg.Picture);
        Assert.NotSame(before[1], placements[1].Svg.Picture);

        // Its own declarations, so nothing was shared with it.
        Assert.Same(before[2], placements[2].Svg.Picture);
    }

    [AvaloniaFact]
    public async Task Picking_A_Drawing_That_Shares_Keeps_The_Value_On_Show()
    {
        // The value was bound into both, so the panel showing the next one its declared default
        // would have it disagreeing with the picture beside it.
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);
        Write("plain.svg", Declaring);
        Write("icons.recipe", Recipe);

        var window = await Host(Write("icons.svgcproj", MixedProject));
        var panel = await Group(window, 0);

        Pick(window, panel, 0);

        ((SvgViewerNumberParameter)Declarations(panel).Parameters!.Single()).Value = 0d;
        Dispatcher.UIThread.RunJobs();

        // The other drawing under the same recipe.
        Pick(window, panel, 1);

        Assert.Equal(0d, ((SvgViewerNumberParameter)Declarations(panel).Parameters!.Single()).Value);

        // The one that shares nothing shows what it declares.
        Pick(window, panel, 2);

        Assert.Equal(new[] { "tint" }, Declarations(panel).Parameters!.Select(row => row.Name).ToArray());
    }

    [AvaloniaFact]
    public async Task Adding_A_Parameter_Writes_Into_The_Drawings_Recipe()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);
        var recipe = Write("icons.recipe", Recipe);

        var window = await Host(Write("icons.svgcproj", SharedRecipeProject));
        var panel = await Group(window, 0);

        Pick(window, panel, 0);

        panel.ParameterDialogService = new StubParameterDialogService(
            new SvgExpressionParameter("sweep", ExprType.Number, "4", null, null, null));

        Assert.True(await panel.AddParameterAsync());
        Dispatcher.UIThread.RunJobs();

        // Into the recipe's shared buffer, which is where a drawing under one keeps its parameters.
        Assert.Contains("sweep", window.ShowRecipe(recipe).Workspace.Document.Text);
    }

    [AvaloniaFact]
    public async Task A_Drawing_With_No_Recipe_Is_Written_To_Its_File()
    {
        // The last resort, and the one that has no buffer behind it: the edit is written and saved
        // at once, because there is nothing else holding the drawing.
        var home = Write("home.svg", Declaring);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Pair));
        var panel = await Group(window, 0);

        Pick(window, panel, 0);

        Assert.Equal(new[] { "tint" }, Declarations(panel).Parameters!.Select(row => row.Name).ToArray());

        panel.ParameterDialogService = new StubParameterDialogService(
            new SvgExpressionParameter("sweep", ExprType.Number, "4", null, null, null));

        Assert.True(await panel.AddParameterAsync());
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("sweep", File.ReadAllText(home));
    }

    [AvaloniaFact]
    public async Task A_Drawing_Open_In_A_Tab_Is_Edited_Through_That_Tab()
    {
        // So the edit can be taken back and is saved when somebody asks, and so two tabs on one
        // file cannot end up disagreeing about it.
        var home = Write("home.svg", Declaring);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Pair));

        var root = (TreeViewItem)Tree(window).Items[0]!;
        var group = (SvgcProjectGroup)(SvgcProjectNode)((TreeViewItem)root.Items[0]!).Tag!;

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
        Assert.DoesNotContain("sweep", File.ReadAllText(home));
    }

    /// <summary>The Replacements tab's content, whatever it currently is.</summary>
    private static object? Replacements(GroupPanel panel)
    {
        var tabs = panel.GetVisualDescendants().OfType<TabControl>().First();

        tabs.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();

        return ((TabItem)tabs.Items[2]!).Content is ContentControl host ? host.Content : null;
    }

    [AvaloniaFact]
    public async Task The_Colours_Are_The_Picked_Drawings()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);
        Write("icons.recipe", Recipe);

        var window = await Host(Write("icons.svgcproj", SharedRecipeProject));
        var panel = await Group(window, 0);

        Pick(window, panel, 0);

        var colours = Assert.IsType<ReplacementsPanel>(Replacements(panel));

        // The fixture paints one colour, and the recipe has a rule for it.
        Assert.Equal(new[] { "#00ff00" }, colours.Values.Select(value => value.Text).ToArray());
        Assert.Equal("tint", colours.Expression("color", "#00ff00"));
    }

    /// <summary>
    /// A rule written from a group repaints the group, straight away.
    /// </summary>
    /// <remarks>
    /// The panel writes into the recipe's buffer, which is what a drawing's own tab is rebuilt from.
    /// A group was not: <c>Rebuild</c> only looked at tabs holding a viewer, so the drawings on
    /// screen went on showing what the recipe used to say until the tab was left and come back to.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Rule_Written_From_A_Group_Repaints_It()
    {
        Write("home.svg", Fading);
        Write("badge.svg", Fading);
        Write("icons.recipe", Recipe);

        var window = await Host(Write("icons.svgcproj", SharedRecipeProject));
        var panel = await Group(window, 0);

        Pick(window, panel, 0);

        var placements = Drawn(panel);
        var before = placements.Select(placed => placed.Svg.Picture).ToArray();

        // A value the recipe says nothing about yet, so this is an added rule rather than an edited
        // one -- which is what changes the document the drawings are built from.
        Assert.True(Assert.IsType<ReplacementsPanel>(Replacements(panel)).Bind("opacity", "0.5", "hue / 240"));

        // The buffer settles a fifth of a second after the last keystroke, so this waits it out the
        // way the drawing-tab rebuild test does.
        for (var attempt = 0; attempt < 200 && ReferenceEquals(before[0], Drawn(panel)[0].Svg.Picture); attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.NotSame(before[0], Drawn(panel)[0].Svg.Picture);

        // And the one beside it, since both are built through the recipe that just changed.
        Assert.NotSame(before[1], Drawn(panel)[1].Svg.Picture);
    }

    /// <summary>
    /// A group tab answers for the recipe it wrote into: it takes the mark, and ⌘S saves it.
    /// </summary>
    /// <remarks>
    /// With no drawing tab open there was nothing else holding the edit, and nothing said so —
    /// the modified fan-out knew about viewer and recipe tabs only, and Save's group branch wrote
    /// the project file and returned. The work was unsaved with no dot and no way to save it.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Recipe_Edited_From_A_Group_Is_Marked_And_Saved_On_That_Tab()
    {
        Write("home.svg", Fading);
        Write("badge.svg", Fading);
        var recipe = Write("icons.recipe", Recipe);

        var window = await Host(Write("icons.svgcproj", SharedRecipeProject));
        var panel = await Group(window, 0);

        Pick(window, panel, 0);

        var tab = Tabs(window).Items.OfType<TabItem>().Single(item => ReferenceEquals(item.Content, panel));

        Assert.DoesNotContain("unsaved", Marker(tab).Classes);

        Assert.True(Assert.IsType<ReplacementsPanel>(Replacements(panel)).Bind("opacity", "0.5", "hue / 240"));
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("unsaved", Marker(tab).Classes);
        Assert.DoesNotContain("hue / 240", File.ReadAllText(recipe));

        await window.SaveAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("hue / 240", File.ReadAllText(recipe));
        Assert.DoesNotContain("unsaved", Marker(tab).Classes);
    }

    [AvaloniaFact]
    public async Task A_Drawing_Under_No_Recipe_Has_Nothing_To_Recolour()
    {
        // The same rule a drawing's own tab follows: the colours are a recipe's rules, and without
        // one there are none.
        Write("home.svg", Declaring);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Pair));
        var panel = await Group(window, 0);

        Pick(window, panel, 0);

        var said = Assert.IsType<TextBlock>(Replacements(panel));

        Assert.Contains("not built through a recipe", said.Text);
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
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Pair));

        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)root.Items[0]!).Tag!);
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
        var window = await Host(Write("icons.svgcproj", """
            <svgc>
              <group namespace="Large" scale="2">
                <svg input="badge.svg" class="BadgeLarge" />
              </group>
              <group namespace="Small" scale="0.5">
                <svg input="home.svg" class="HomeSmall" />
              </group>
            </svgc>
            """));

        var root = (TreeViewItem)Tree(window).Items[0]!;
        var large = (TreeViewItem)root.Items[0]!;
        var small = (TreeViewItem)root.Items[1]!;

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
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Project));

        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.ShowAsync((SvgcProjectNode)root.Tag!);
        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)root.Items[0]!).Tag!);
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

        var window = await Host(Write("icons.svgcproj", Project));

        var root = (TreeViewItem)Tree(window).Items[0]!;
        var group = (TreeViewItem)root.Items[1]!;

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)group.Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var viewer = await Settle(window, "badge.svg");

        // scale="2" on the group, applied to the picture and not to the file.
        Assert.Equal(48f, viewer.Document!.Svg.Picture!.CullRect.Width);
        Assert.Equal(Drawing, File.ReadAllText(Path.Combine(_directory, "badge.svg")));

        // home.svg is outside the group, so it keeps the size it was written with.
        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)root.Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(24f, (await Settle(window, "home.svg")).Document!.Svg.Picture!.CullRect.Width);
    }

    [AvaloniaFact]
    public async Task Editing_A_Group_Rebuilds_What_Is_Open_And_Saves_The_Project()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var path = Write("icons.svgcproj", Project);
        var window = await Host(path);

        var root = (TreeViewItem)Tree(window).Items[0]!;
        var group = (TreeViewItem)root.Items[1]!;

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)group.Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var viewer = await Settle(window, "badge.svg");
        Assert.Equal(48f, viewer.Document!.Svg.Picture!.CullRect.Width);

        var workspace = window.Workspace!;

        ((SvgcProjectGroup)workspace.Document.Root.Children[1]).Scale = 4f;
        workspace.Save();

        // Saved as XML, with the comment and the layout the author wrote still there.
        var saved = File.ReadAllText(path);
        Assert.Contains("scale=\"4\"", saved);
        Assert.Contains("<!-- kept, so an edit is proven not to reformat the file -->", saved);
        Assert.Equal(Project.Replace("scale=\"2\"", "scale=\"4\""), saved);

        // And the project still describes the same build to the generator.
        Assert.Equal(4f, SvgcProject.Load(path).Items[1].Scale);
    }

    [AvaloniaFact]
    public async Task A_Tab_Saves_What_Was_Typed_In_It_And_Nothing_Else()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var path = Write("icons.svgcproj", """
            <svgc>
              <group namespace="Large" scale="2">
                <svg input="badge.svg" class="BadgeLarge" />
              </group>
              <group namespace="Small" scale="0.5">
                <svg input="home.svg" class="HomeSmall" />
              </group>
            </svgc>
            """);

        var window = await Host(path);
        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)root.Items[0]!).Tag!);
        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)root.Items[1]!).Tag!);
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
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Project));
        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)root.Items[1]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var header = (StackPanel)((TabItem)Tabs(window).SelectedItem!).Header!;
        var marker = (TextBlock)header.Children[0];
        var title = (TextBlock)header.Children[1];

        Assert.Equal("Demo.Icons.Large", title.Text);

        // The rendered mark, not the class it was handed: a class nothing styles would pass the
        // one and show nothing for the other.
        Assert.Equal("●", marker.Text);
        Assert.Equal(0d, marker.Opacity);

        Panel(window, "Demo.Icons.Large").Edit("scale", "4");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1d, marker.Opacity);

        Panel(window, "Demo.Icons.Large").Save();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0d, marker.Opacity);

        // The mark is its own element, so the name never changed and the tab never resized.
        Assert.Equal("Demo.Icons.Large", title.Text);
    }

    /// <summary>
    /// A group renamed is renamed on its tab as well. The header was written when the tab was, so
    /// the row in the tree read the new name while the tab open on that very row read the old one.
    /// </summary>
    [AvaloniaFact]
    public async Task A_Renamed_Group_Is_Renamed_On_Its_Tab()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Project));
        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)root.Items[1]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var item = (TabItem)Tabs(window).SelectedItem!;
        var title = (TextBlock)((StackPanel)item.Header!).Children[1];

        Assert.Equal("Demo.Icons.Large", title.Text);

        var panel = Panel(window, "Demo.Icons.Large");

        Assert.True(panel.Edit("namespace", "Demo.Icons.Huge"));

        panel.Save();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Demo.Icons.Huge", title.Text);

        // The tree said the new name all along; the window title is the same label again and read
        // from the same tab, so it cannot be left saying the old one either.
        Assert.Contains("Demo.Icons.Huge", Rows((TreeViewItem)Tree(window).Items[0]!));
        Assert.StartsWith("Demo.Icons.Huge — ", window.Title);
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
        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        window.GetVisualDescendants().OfType<TextEditor>().First().Document.Text = Drawing + "<!-- edited -->";
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
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", """
            <svgc>
              <namespace>Demo.Icons</namespace>
              <singleFile>Icons.cs</singleFile>
              <svg input="home.svg" class="Home" />
            </svgc>
            """));

        // The project's own settings, which are elements rather than attributes and were the ones
        // the panel could not read back — so an edit to one stayed pending after it was saved,
        // leaving the tab unmarked and the close button still asking about it.
        await window.ShowAsync(window.Workspace!.Document.Root);
        Dispatcher.UIThread.RunJobs();

        var panel = Panel(window, "Demo.Icons");

        Assert.Equal("Icons.cs", panel.Shown("singleFile"));

        panel.Edit("singleFile", "Other.cs");
        Assert.True(panel.IsModified);

        panel.Save();

        // Saved, said to be saved, and nothing left behind to be asked about.
        Assert.False(panel.IsModified);
        Assert.Equal("Other.cs", panel.Shown("singleFile"));
        Assert.Contains("<singleFile>Other.cs</singleFile>", File.ReadAllText(Path.Combine(_directory, "icons.svgcproj")));

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

        var window = await Host(Write("icons.svgcproj", """
            <svgc>
              <namespace>Demo.Icons</namespace>
              <singleFile>Icons.cs</singleFile>
              <svg input="home.svg" class="Home" />
            </svgc>
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

        var saved = File.ReadAllText(Path.Combine(_directory, "icons.svgcproj"));

        Assert.Contains("<singleFile>Other.cs</singleFile>", saved);
        Assert.Contains("<namespace>Typed.Icons</namespace>", saved);
    }

    [AvaloniaFact]
    public async Task The_Mark_Appears_At_The_First_Keystroke()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Project));

        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)root.Items[1]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var item = Tabs(window).Items.OfType<TabItem>()
            .Single(tab => tab.Content is GroupPanel group && ProjectWorkspace.Label(group.Node) == "Demo.Icons.Large");
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
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var path = Write("icons.svgcproj", Project);
        var window = await Host(path);

        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)root.Items[1]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var panel = Panel(window, "Demo.Icons.Large");

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
    public async Task Saving_One_Tab_Reaches_The_Other_Tabs_On_The_Same_File()
    {
        Write("badge.svg", Drawing);

        // One file, built twice, which is what a project is for — and so two tabs on one file.
        var window = await Host(Write("icons.svgcproj", """
            <svgc>
              <svg input="badge.svg" class="Badge" />
              <group class="BadgeLarge" scale="2"><svg input="badge.svg" /></group>
            </svgc>
            """));

        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)root.Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var first = Tabs(window).Items.OfType<TabItem>()
            .Single(tab => tab.Content is SvgViewer { DocumentPath: { } } viewer
                           && Path.GetFileName(viewer.DocumentPath!) == "badge.svg");

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)((TreeViewItem)root.Items[1]!).Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var second = (TabItem)Tabs(window).SelectedItem!;
        var edited = (SvgViewer)second.Content!;

        Assert.NotSame(first, second);

        // Typed into and saved, in the tab that is on screen.
        edited.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        window.GetVisualDescendants().OfType<TextEditor>().First().Document.Text =
            Drawing.Replace("#00ff00", "#0000ff", StringComparison.Ordinal);
        Dispatcher.UIThread.RunJobs();

        await window.SaveAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("#0000ff", File.ReadAllText(Path.Combine(_directory, "badge.svg")));

        // The other tab held its own copy and went on showing what the file used to say.
        Tabs(window).SelectedItem = first;

        var stale = (SvgViewer)first.Content!;

        for (var attempt = 0; attempt < 200 && stale.Source.Contains("#00ff00"); attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        Assert.Contains("#0000ff", stale.Source);
    }

    [AvaloniaFact]
    public async Task Exporting_A_Drawing_Gives_It_The_Size_The_Project_Builds_It_At()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Project));

        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)((TreeViewItem)root.Items[1]!).Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var viewer = await Settle(window, "badge.svg");

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
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Project));

        var export = Menu(window, "Export…");
        var root = (TreeViewItem)Tree(window).Items[0]!;

        // Nothing is open, so there is nothing to export.
        Assert.False(export.IsEnabled);

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)root.Items[1]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        // A group holds no drawing. The item used to stay live and do nothing at all when picked.
        Assert.False(export.IsEnabled);

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)((TreeViewItem)root.Items[1]!).Items[0]!).Tag!);
        await Settle(window, "badge.svg");

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

        var window = await Host(Write("icons.svgcproj", """
            <svgc>
              <namespace>Demo.Icons</namespace>
              <svg input="home.svg" class="Home" output="Home.cs" />
              <svg input="badge.svg" class="Badge" output="Badge.cs" />
            </svgc>
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

        Write("badge.svg", Drawing);

        var viewer = (SvgViewer)((TabItem)Tabs(window).SelectedItem!).Content!;

        Assert.True(await viewer.OpenAsync(new[] { Write("icons.svgcproj", Project) }));
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

        var path = Write("icons.svgcproj", """
            <svgc>
              <namespace>Demo.Icons</namespace>
              <singleFile>Icons.cs</singleFile>
              <svg input="home.svg" class="Home" />
              <group namespace="Demo.Icons.Large" scale="2">
                <svg input="badge.svg" class="BadgeLarge" />
              </group>
            </svgc>
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

        var path = Write("icons.svgcproj", Project);
        var window = await Host(path);

        var root = (TreeViewItem)Tree(window).Items[0]!;

        await window.AddDrawingAsync((SvgcProjectNode)root.Tag!, Write("extra.svg", Drawing));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            new[] { "Demo.Icons", "home.svg - Home", "Demo.Icons.Large", "badge.svg - BadgeLarge", "extra.svg" },
            Rows((TreeViewItem)Tree(window).Items[0]!));

        // Relative, so the project still builds on a machine that is not this one, and on its own
        // line rather than sharing the closing tag's.
        Assert.Equal(
            Project.Replace("</group>\n</svgc>", "</group>\n  <svg input=\"extra.svg\" />\n</svgc>"),
            File.ReadAllText(path));

        // And it opens, at the size the project it just joined builds it at.
        Assert.Equal("extra.svg", Path.GetFileName((await Settle(window, "extra.svg")).DocumentPath));
    }

    [AvaloniaFact]
    public async Task A_Group_Added_Opens_So_It_Can_Be_Named()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var path = Write("icons.svgcproj", Project);
        var window = await Host(path);

        var group = (TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[1]!;

        await window.AddGroupAsync((SvgcProjectNode)group.Tag!);
        Dispatcher.UIThread.RunJobs();

        // Inside the group it was asked from, indented a level in from it.
        Assert.Contains("    <svg input=\"badge.svg\" class=\"BadgeLarge\" />\n    <group />", File.ReadAllText(path));

        // Named by neither of its settings until one is typed, which is what the tab is for.
        Assert.Equal("group", ProjectWorkspace.Label(Panel(window, "group").Node));
    }

    [AvaloniaFact]
    public async Task Removing_A_Group_Takes_Its_Tabs_With_It()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var path = Write("icons.svgcproj", Project);
        var window = await Host(path);

        window.ConfirmRemove = _ => Task.FromResult(true);

        var root = (TreeViewItem)Tree(window).Items[0]!;
        var group = (TreeViewItem)root.Items[1]!;

        await window.ShowAsync((SvgcProjectNode)group.Tag!);
        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)group.Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, Tabs(window).Items.Count);

        Assert.True(await window.RemoveAsync((SvgcProjectNode)group.Tag!));
        Dispatcher.UIThread.RunJobs();

        // The group's tab and the drawing's under it both go: left open, either would go on editing
        // an element the document no longer holds and report itself saved. The project's own tab is
        // not under the group and stays.
        var kept = Assert.IsType<TabItem>(Assert.Single(Tabs(window).Items));

        Assert.Same(window.Workspace!.Document.Root, Assert.IsType<GroupPanel>(kept.Content).Node);
        Assert.Equal(new[] { "Demo.Icons", "home.svg - Home" }, Rows((TreeViewItem)Tree(window).Items[0]!));

        // The comment stays. It is a sibling of the group, not part of it.
        Assert.Equal(
            Project.Replace("\n  <group namespace=\"Demo.Icons.Large\" scale=\"2\">\n    <svg input=\"badge.svg\" class=\"BadgeLarge\" />\n  </group>", ""),
            File.ReadAllText(path));
    }

    [AvaloniaFact]
    public async Task A_Branch_Refused_At_The_Question_Stays()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var path = Write("icons.svgcproj", Project);
        var window = await Host(path);

        var asked = new List<string>();

        window.ConfirmRemove = message => { asked.Add(message); return Task.FromResult(false); };

        var group = (SvgcProjectNode)((TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[1]!).Tag!;

        Assert.False(await window.RemoveAsync(group));

        // Asked because it takes a row with it, and the file is untouched.
        Assert.Contains("holds 1 row", Assert.Single(asked));
        Assert.Equal(Project, File.ReadAllText(path));
    }

    [AvaloniaFact]
    public async Task Unsaved_Work_Under_A_Removed_Group_Is_Asked_About_And_Can_Keep_It()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var path = Write("icons.svgcproj", Project);
        var window = await Host(path);

        window.ConfirmRemove = _ => Task.FromResult(true);
        window.ConfirmDiscard = _ => Task.FromResult(false);

        var group = (SvgcProjectNode)((TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[1]!).Tag!;

        await window.ShowAsync(group);
        Dispatcher.UIThread.RunJobs();

        Panel(window, "Demo.Icons.Large").Edit("class", "Renamed");

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

        var path = Write("icons.svgcproj", Project);
        var window = await Host(path);

        var root = (TreeViewItem)Tree(window).Items[0]!;
        var home = (SvgcProjectNode)((TreeViewItem)root.Items[0]!).Tag!;
        var group = (SvgcProjectNode)((TreeViewItem)root.Items[1]!).Tag!;

        Assert.True(window.Move(home, group, ProjectDrop.Inside));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            new[] { "Demo.Icons", "Demo.Icons.Large", "badge.svg - BadgeLarge", "home.svg - Home" },
            Rows((TreeViewItem)Tree(window).Items[0]!));

        // Reparented rather than copied, so it now builds at the group's size.
        Assert.Equal(2f, home.EffectiveScale);
        Assert.Contains("<svg input=\"badge.svg\" class=\"BadgeLarge\" />\n    <svg input=\"home.svg\" class=\"Home\" />", File.ReadAllText(path));

        // And back out again, above the group it came from.
        Assert.True(window.Move(home, group, ProjectDrop.Before));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            new[] { "Demo.Icons", "home.svg - Home", "Demo.Icons.Large", "badge.svg - BadgeLarge" },
            Rows((TreeViewItem)Tree(window).Items[0]!));

        Assert.Null(home.EffectiveScale);
    }

    [AvaloniaFact]
    public async Task A_Drop_That_Has_Nowhere_To_Land_Changes_Nothing()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var path = Write("icons.svgcproj", Project);
        var window = await Host(path);

        var root = (TreeViewItem)Tree(window).Items[0]!;
        var home = (SvgcProjectNode)((TreeViewItem)root.Items[0]!).Tag!;
        var group = (TreeViewItem)root.Items[1]!;
        var badge = (SvgcProjectNode)((TreeViewItem)group.Items[0]!).Tag!;

        // Into a drawing, which holds nothing.
        Assert.False(window.Move(home, badge, ProjectDrop.Inside));

        // Into its own child, which would take the branch out of the document and leave it holding
        // itself.
        Assert.False(window.Move((SvgcProjectNode)group.Tag!, badge, ProjectDrop.After));

        // Beside the project, which has nothing to sit beside.
        Assert.False(window.Move(home, (SvgcProjectNode)root.Tag!, ProjectDrop.After));

        Assert.Equal(Project, File.ReadAllText(path));
    }

    [AvaloniaFact]
    public async Task One_File_Built_Twice_Is_Given_A_Class_Each_In_Its_Tab()
    {
        Write("home.svg", Drawing);

        var path = Write("icons.svgcproj", """
            <svgc>
              <namespace>Demo.Icons</namespace>
              <group class="Shared">
                <svg input="home.svg" />
                <svg input="home.svg" />
              </group>
            </svgc>
            """);

        var window = await Host(path);

        var group = (TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[0]!;

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)group.Items[1]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var tab = Tabs(window).Items.OfType<TabItem>().Single(item => item.Tag is SvgcProjectDrawing);
        var viewer = (SvgViewer)tab.Content!;

        // Beside the drawing's own parameters, in the pane a group keeps its settings in.
        var panel = Assert.IsType<GroupPanel>(Assert.Single(viewer.SidePanels).Content);
        var panes = viewer.GetVisualDescendants().OfType<TabControl>().Single(control => control.Classes.Contains("panes"));

        // First of the two, and so the one shown: a drawing opened from the tree is being looked at
        // as part of a project, so what the project says about it is what to open on.
        Assert.Equal(new[] { "Project", "Parameters" }, panes.Items.OfType<TabItem>().Select(item => (string)item.Header!));
        Assert.Equal(0, panes.SelectedIndex);

        var box = panel.GetVisualDescendants().OfType<TextBox>().Single(candidate => Equals(candidate.Tag, "class"));

        // Empty, with what the group hands down behind it — which is the whole trouble: both rows
        // are the same file and both become Shared until one of them says otherwise.
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
        Assert.Contains("<svg input=\"home.svg\" class=\"Second\" />", File.ReadAllText(path));

        Assert.Equal(
            new[] { "Demo.Icons", "Shared", "home.svg - Shared", "home.svg - Second" },
            Rows((TreeViewItem)Tree(window).Items[0]!));
    }

    [AvaloniaFact]
    public async Task A_Drawing_Tab_Answers_For_Its_Settings_As_Well_As_Its_Text()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Project));

        var asked = new List<string>();

        window.ConfirmDiscard = message => { asked.Add(message); return Task.FromResult(false); };

        var home = (SvgcProjectNode)((TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[0]!).Tag!;

        await window.ShowAsync(home);
        Dispatcher.UIThread.RunJobs();

        var tab = Tabs(window).Items.OfType<TabItem>().Single(item => item.Tag is SvgcProjectDrawing);

        ((GroupPanel)Assert.Single(((SvgViewer)tab.Content!).SidePanels).Content).Edit("output", "Home.cs");
        Dispatcher.UIThread.RunJobs();

        // The drawing's text is untouched; what is unsaved is the project's say over it, and the
        // close has to ask about that just the same.
        Assert.False(((SvgViewer)tab.Content!).IsSourceModified);

        var close = (Button)((StackPanel)tab.Header!).Children[2];

        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("home.svg has changes that have not been saved.", asked);
        Assert.Contains(tab, Tabs(window).Items);
    }

    [AvaloniaFact]
    public async Task A_Drawing_Pointed_At_Another_File_Takes_Its_Tab_With_It()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var path = Write("icons.svgcproj", Project);
        var window = await Host(path);

        var home = (SvgcProjectDrawing)((TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[0]!).Tag!;

        await window.ShowAsync(home);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("home.svg", Path.GetFileName((await Settle(window, "home.svg")).DocumentPath));

        home.Input = "badge.svg";
        window.Workspace!.Save();

        // Left alone, the tab goes on showing a file its row no longer names.
        Assert.Equal("badge.svg", Path.GetFileName((await Settle(window, "badge.svg")).DocumentPath));
    }

    private static TextBlock Marker(TabItem item) => (TextBlock)((StackPanel)item.Header!).Children[0];

    private static GroupPanel Panel(MainWindow window, string label)
        => Tabs(window).Items.OfType<TabItem>()
            .Select(item => item.Content)
            .OfType<GroupPanel>()
            .Single(panel => ProjectWorkspace.Label(panel.Node) == label);

    private const string Recipe = """
        <recipe xmlns="https://svg.skia/expr/1.0">
          <code>
            <param name="hue" type="number" default="120" />
            <let name="tint">hsl(hue, 100%, 50%)</let>
          </code>
          <replace color="#00ff00">tint</replace>
        </recipe>
        """;

    /// <summary>The sample project with a recipe on the group, which only badge.svg is under.</summary>
    private const string RecipeProject = """
        <svgc>
          <namespace>Demo.Icons</namespace>

          <svg input="home.svg" class="Home" />

          <group namespace="Demo.Icons.Large" scale="2" recipe="icons.recipe">
            <svg input="badge.svg" class="BadgeLarge" />
          </group>
        </svgc>
        """;

    [AvaloniaFact]
    public async Task A_Drawing_Under_A_Recipe_Is_Shown_As_The_Project_Builds_It()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);
        Write("icons.recipe", Recipe);

        var window = await Host(Write("icons.svgcproj", RecipeProject));

        var root = (TreeViewItem)Tree(window).Items[0]!;
        var group = (TreeViewItem)root.Items[1]!;

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)group.Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var viewer = await Settle(window, "badge.svg");

        // The recipe declares the parameter, so the drawing on screen has one to drive even though
        // its own file declares nothing.
        Assert.Equal(new[] { "hue" }, viewer.Parameters.Select(row => row.Name).ToArray());
        Assert.Equal(new[] { "tint" }, viewer.Document!.Declarations.Lets.Select(let => let.Name).ToArray());

        // What is edited and saved is still the file: the rewrite is only what gets drawn.
        Assert.Equal(Drawing, viewer.Source);
        Assert.Equal(Drawing, File.ReadAllText(Path.Combine(_directory, "badge.svg")));
        Assert.False(viewer.IsSourceModified);

        // home.svg is outside the group, so nothing rewrites it.
        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)root.Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty((await Settle(window, "home.svg")).Parameters);
    }

    [AvaloniaFact]
    public async Task The_Panel_Declares_Into_The_Recipe_And_Never_Into_The_Drawing()
    {
        var (window, viewer) = await Painting();
        var recipe = Path.Combine(_directory, "icons.recipe");
        var badge = Path.Combine(_directory, "badge.svg");

        // The panel is offered, not locked: the parameters it shows are the recipe's, so this is
        // where they are edited. Written into the drawing they would be a declaration block, and a
        // recipe refuses a document that already has one.
        Assert.True(viewer.CommitLet(new SvgViewerLet(null) { Name = "deep", Expression = "hsl(hue + 5, 71%, 40%)" }));
        Dispatcher.UIThread.RunJobs();

        var buffer = Replacements(viewer).Recipe;

        Assert.Contains("""<let name="deep">hsl(hue + 5, 71%, 40%)</let>""", buffer.Text);
        Assert.True(buffer.IsModified);

        // Neither file has been written, and the drawing has not been touched at all.
        Assert.Equal(Drawing, viewer.Source);
        Assert.Equal(Drawing, File.ReadAllText(badge));
        Assert.DoesNotContain("deep", File.ReadAllText(recipe));
        Assert.False(viewer.IsSourceModified);

        // And the drawing shows what it now declares, once the recipe settles.
        for (var attempt = 0; attempt < 200 && viewer.Lets.Count < 2; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.Contains("deep", viewer.Lets.Select(let => let.Name));

        // A drawing with no recipe over it writes into itself, as it always did.
        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var plain = await Settle(window, "home.svg");

        Assert.Null(plain.DeclarationTarget);
        Assert.True(plain.CommitLet(new SvgViewerLet(null) { Name = "deep", Expression = "1" }));
        Assert.Contains("deep", plain.Source);
    }

    [AvaloniaFact]
    public async Task A_Recipe_That_Will_Not_Apply_Is_Said_Over_The_Drawing_It_Was_For()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        // A rule with no expression: the kind of thing a recipe is left in halfway through writing.
        Write("icons.recipe", """
            <recipe xmlns="https://svg.skia/expr/1.0">
              <replace color="#00ff00"></replace>
            </recipe>
            """);

        var window = await Host(Write("icons.svgcproj", RecipeProject));

        var group = (TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[1]!;

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)group.Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var viewer = await Settle(window, "badge.svg");

        // The drawing still opens. Refusing to show it would leave nothing to read the reason on.
        Assert.NotNull(viewer.Document);
        Assert.Empty(viewer.Parameters);

        Assert.Contains("icons.recipe was not applied", viewer.Notice);
        Assert.Contains("no expression", viewer.Notice);
    }

    [AvaloniaFact]
    public async Task Taking_The_Recipe_Off_A_Group_Reads_Its_Drawings_Again()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);
        Write("icons.recipe", Recipe);

        var window = await Host(Write("icons.svgcproj", RecipeProject));

        var group = (TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[1]!;

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)group.Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var viewer = await Settle(window, "badge.svg");
        Assert.Single(viewer.Parameters);

        var workspace = window.Workspace!;

        ((SvgcProjectGroup)workspace.Document.Root.Children[1]).Recipe = null;
        workspace.Save();

        for (var attempt = 0; attempt < 200 && viewer.Parameters.Count > 0; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        // The size did not change, so only the recipe could have asked for this reload.
        Assert.Empty(viewer.Parameters);
        Assert.Null(viewer.Rewrite);

        // And what the panel writes goes back to the drawing, since nothing else declares for it.
        Assert.Null(viewer.DeclarationTarget);

        // The colours went with the recipe: there is nothing left to bind them to.
        Assert.Empty(viewer.SidePanels.Select(pane => pane.Content).OfType<ReplacementsPanel>());
    }

    /// <summary>The buttons on a panel's recipe row, by what they are labelled.</summary>
    /// <summary>
    /// What the settings pane offers about the recipe.
    /// </summary>
    /// <remarks>
    /// Scoped to the pane, which is the scrolled half: the canvas beside it has buttons of its own,
    /// and taking every button in the panel would be reading the zoom controls as recipe commands.
    /// </remarks>
    private static string[] RecipeButtons(GroupPanel panel)
        => panel.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.GetVisualAncestors().OfType<Control>().Any(above => above.Name == "Settings"))
            .Select(button => button.Content as string)
            .Where(content => content is { })
            .ToArray()!;

    [AvaloniaFact]
    public async Task A_Recipe_Is_Named_And_Dropped_With_Buttons()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var recipe = Write("icons.recipe", Recipe);
        var path = Write("icons.svgcproj", Project);
        var window = await Host(path);

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[1]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var panel = Panel(window, "Demo.Icons.Large");

        // Nothing named, so there is nothing to type into — only the two ways to name one.
        Assert.DoesNotContain(
            panel.GetVisualDescendants().OfType<TextBox>(),
            box => Equals(box.Tag, "recipe"));
        Assert.Equal(new[] { "Add…", "New…" }, RecipeButtons(panel));

        panel.SetRecipe(recipe);
        Dispatcher.UIThread.RunJobs();

        // Carried relative, so the project still builds anywhere it is cloned to.
        Assert.Equal("icons.recipe", panel.Shown("recipe"));
        Assert.True(panel.IsModified);

        // Named, so the two ways to name one give way to writing it into the drawings and to
        // dropping it again.
        Assert.Equal(new[] { "Apply…", "✕" }, RecipeButtons(panel));

        panel.Save();

        Assert.Contains("recipe=\"icons.recipe\"", File.ReadAllText(path));

        // And the drawing under it is read again through it, without anything else asking.
        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)((TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[1]!).Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("hue", Assert.Single((await Settle(window, "badge.svg")).Parameters).Name);

        panel.RemoveRecipe();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(panel.Shown("recipe"));
        Assert.Equal(new[] { "Add…", "New…" }, RecipeButtons(panel));

        panel.Save();

        Assert.DoesNotContain("recipe=", File.ReadAllText(path));

        // The file is the project's to name, not the project's to own.
        Assert.True(File.Exists(recipe));
    }

    /// <summary>
    /// A new recipe says nothing. It was written with a parameter, a let and the colours its
    /// drawings paint listed under them as commented-out rules — an opening line to read and delete
    /// before the file said what its author meant.
    /// </summary>
    [AvaloniaFact]
    public async Task A_New_Recipe_Is_Empty()
    {
        Write("badge.svg", Drawing);
        Write("mark.svg", Drawing.Replace("#00ff00", "#ff8800", StringComparison.Ordinal));

        var window = await Host(Write("icons.svgcproj", """
            <svgc>
              <namespace>Demo.Icons</namespace>

              <group namespace="Demo.Icons.Large" scale="2">
                <svg input="badge.svg" class="BadgeLarge" />
                <svg input="mark.svg" class="MarkLarge" />
              </group>
            </svgc>
            """));

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var written = Path.Combine(_directory, "large.recipe");

        Panel(window, "Demo.Icons.Large").CreateRecipe(written);

        var text = File.ReadAllText(written);

        Assert.Equal("""
            <?xml version="1.0" encoding="utf-8"?>
            <recipe xmlns="https://svg.skia/expr/1.0">
            </recipe>

            """, text);

        // Empty and still a recipe: it parses, declares nothing and repaints nothing, so the file
        // applies as it stands rather than having to be finished before the drawing will load.
        var recipe = SvgRecipe.Parse(text);

        Assert.Empty(recipe.Rules);
        Assert.Equal(0, SvgRecipeRewriter.Apply(Drawing, recipe).TotalReplacements);
    }

    [AvaloniaFact]
    public async Task A_New_Recipe_Is_Written_Where_It_Is_Asked_For_And_Applies_As_It_Stands()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var path = Write("icons.svgcproj", Project);
        var window = await Host(path);

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[1]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var panel = Panel(window, "Demo.Icons.Large");
        var written = Path.Combine(_directory, "large.recipe");

        panel.CreateRecipe(written);
        panel.Save();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("large.recipe", panel.Shown("recipe"));

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)((TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[1]!).Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var viewer = await Settle(window, "badge.svg");

        // Empty applies as it stands too: the drawing opens through the recipe with nothing to say
        // about it, rather than failing to load until the file has been written.
        Assert.Null(viewer.Notice);
        Assert.Empty(viewer.Parameters);
    }

    [AvaloniaFact]
    public async Task A_Recipe_That_Is_Already_There_Is_Named_Rather_Than_Written_Over()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var recipe = Write("icons.recipe", Recipe);

        var window = await Host(Write("icons.svgcproj", Project));

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[1]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        Panel(window, "Demo.Icons.Large").CreateRecipe(recipe);

        Assert.Equal(Recipe, File.ReadAllText(recipe));
    }

    private static RecipePanel? Editor(MainWindow window)
        => Tabs(window).Items.OfType<TabItem>().Select(item => item.Content).OfType<RecipePanel>().SingleOrDefault();

    /// <summary>A window on the sample project with the recipe named by its group.</summary>
    private async Task<MainWindow> Recipes(string? drawing = null, string? recipe = null)
    {
        Write("home.svg", Drawing);
        Write("badge.svg", drawing ?? Drawing);
        Write("icons.recipe", recipe ?? Recipe);

        var window = await Host(Write("icons.svgcproj", RecipeProject));

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[1]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        return window;
    }

    [AvaloniaFact]
    public async Task Double_Clicking_A_Recipe_Opens_It_In_A_Tab()
    {
        var window = await Recipes();

        Assert.Null(Editor(window));

        var name = Panel(window, "Demo.Icons.Large")
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .Single(block => block.Text == "icons.recipe");

        Click(window, name);
        Click(window, name);

        var editor = Editor(window);

        Assert.NotNull(editor);
        Assert.Equal(Recipe, editor!.Text);
        Assert.Null(editor.Fault);

        // The tab it opened is the one being looked at, and it is the recipe's own.
        Assert.Same(editor, ((TabItem)Tabs(window).SelectedItem!).Content);

        // And there is something to look at. AvaloniaEdit's control theme is included by the viewer
        // in its own styles, which do not reach a tab beside it — without it at the application the
        // editor templated to nothing and the tab opened empty.
        Assert.NotEmpty(editor.GetVisualDescendants().OfType<TextArea>());
    }

    [AvaloniaFact]
    public async Task Editing_A_Recipe_Marks_Its_Tab_And_Saving_Reads_The_Drawings_Again()
    {
        var window = await Recipes();

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)((TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[1]!).Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var viewer = await Settle(window, "badge.svg");
        Assert.Equal("hue", Assert.Single(viewer.Parameters).Name);

        var editor = window.ShowRecipe(Path.Combine(_directory, "icons.recipe"));
        Dispatcher.UIThread.RunJobs();

        var item = (TabItem)Tabs(window).SelectedItem!;

        Assert.False(editor.IsModified);
        Assert.DoesNotContain("unsaved", Marker(item).Classes);

        editor.Text = Recipe.Replace("hue", "tone", StringComparison.Ordinal);
        Dispatcher.UIThread.RunJobs();

        Assert.True(editor.IsModified);
        Assert.Contains("unsaved", Marker(item).Classes);

        await window.SaveAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.False(editor.IsModified);
        Assert.Contains("tone", File.ReadAllText(Path.Combine(_directory, "icons.recipe")));

        // Still what it was: the drawing is behind the recipe being typed in, and a tab out of
        // sight is marked to be read again rather than read where nobody is looking.
        Assert.Equal("hue", Assert.Single(viewer.Parameters).Name);

        Tabs(window).SelectedItem = Tabs(window).Items.OfType<TabItem>().Single(tab => ReferenceEquals(tab.Content, viewer));
        Dispatcher.UIThread.RunJobs();

        // The point of editing it here: what the drawing under it declares follows the save.
        for (var attempt = 0; attempt < 200 && viewer.Parameters.FirstOrDefault()?.Name != "tone"; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.Equal("tone", Assert.Single(viewer.Parameters).Name);
    }

    [AvaloniaFact]
    public async Task A_Recipe_That_Will_Not_Read_Is_Said_Under_It_And_Still_Typed_In()
    {
        var window = await Recipes();

        var editor = window.ShowRecipe(Path.Combine(_directory, "icons.recipe"));

        // Half a recipe is what one looks like while it is being written; taking the text back
        // between keystrokes would make it unwritable.
        editor.Text = """
            <recipe xmlns="https://svg.skia/expr/1.0">
              <replace color="#00ff00"></replace>
            </recipe>
            """;

        Dispatcher.UIThread.RunJobs();

        Assert.Contains("no expression", editor.Fault);
        Assert.Contains("<replace", editor.Text);

        editor.Text = Recipe;
        Dispatcher.UIThread.RunJobs();

        Assert.Null(editor.Fault);
    }

    [AvaloniaFact]
    public async Task One_Recipe_Opens_Once_And_Closes_With_The_Project()
    {
        var window = await Recipes();

        var path = Path.Combine(_directory, "icons.recipe");

        // Several groups name one recipe, and a tab per namer would be two editors over one file.
        Assert.Same(window.ShowRecipe(path), window.ShowRecipe(path));
        Assert.NotNull(Editor(window));

        Assert.True(await window.CloseProjectAsync());
        Dispatcher.UIThread.RunJobs();

        Assert.Null(Editor(window));
    }

    private static ReplacementsPanel Replacements(SvgViewer viewer)
        => viewer.SidePanels.Select(pane => pane.Content).OfType<ReplacementsPanel>().Single();

    /// <summary>A window with badge.svg open under the recipe, which is what the colours are for.</summary>
    private async Task<(MainWindow Window, SvgViewer Viewer)> Painting(string? drawing = null, string? recipe = null)
    {
        var window = await Recipes(drawing, recipe);

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)((TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[1]!).Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        return (window, await Settle(window, "badge.svg"));
    }

    [AvaloniaFact]
    public async Task The_Colours_Of_A_Drawing_Are_A_Pane_Of_Their_Own()
    {
        var (window, viewer) = await Painting();

        Assert.Equal(
            new[] { "Project", "Replacements", "Parameters" },
            viewer.GetVisualDescendants()
                .OfType<TabControl>()
                .Single(control => control.Classes.Contains("panes"))
                .Items.OfType<TabItem>()
                .Select(item => (string)item.Header!));

        var colours = Replacements(viewer);

        // The drawing's own colour, and what the recipe already says paints it.
        Assert.Equal(new[] { "#00ff00" }, colours.Values.Select(value => value.Text));
        Assert.Equal("tint", colours.Expression("color", "#00ff00"));

        // A drawing with no recipe over it has nothing to bind, so it has no pane either.
        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty((await Settle(window, "home.svg")).SidePanels.Select(pane => pane.Content).OfType<ReplacementsPanel>());
    }

    [AvaloniaFact]
    public async Task Binding_A_Colour_Changes_What_The_Drawing_Paints_Before_Any_Save()
    {
        // A drawing with a colour the recipe says nothing about yet.
        var (window, viewer) = await Painting(Drawing.Replace("#00ff00", "#ff0000", StringComparison.Ordinal));
        var colours = Replacements(viewer);
        var recipe = Path.Combine(_directory, "icons.recipe");

        Assert.Equal(new[] { "#ff0000" }, colours.Values.Select(value => value.Text));
        Assert.Null(colours.Expression("color", "#ff0000"));

        Assert.True(colours.Bind("color", "#ff0000", "hsl(hue, 100%, 50%)"));
        Dispatcher.UIThread.RunJobs();

        Assert.Null(colours.Fault);
        Assert.Equal("hsl(hue, 100%, 50%)", colours.Expression("color", "#ff0000"));

        // Written into the recipe's buffer and nowhere near the drawing.
        Assert.Contains("""<replace color="#ff0000">hsl(hue, 100%, 50%)</replace>""", colours.Recipe.Text);
        Assert.DoesNotContain("{{", File.ReadAllText(Path.Combine(_directory, "badge.svg")));
        Assert.DoesNotContain("#ff0000", File.ReadAllText(recipe));
        Assert.True(colours.Recipe.IsModified);

        // And the drawing follows it, unsaved, because the parameter it names is now bound to a
        // colour the drawing has.
        for (var attempt = 0; attempt < 200 && viewer.Parameters.Count == 0; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.Equal("hue", Assert.Single(viewer.Parameters).Name);

        Assert.True(colours.Unbind("color", "#ff0000"));
        Dispatcher.UIThread.RunJobs();

        Assert.Null(colours.Expression("color", "#ff0000"));
        Assert.DoesNotContain("#ff0000", colours.Recipe.Text);
    }

    [AvaloniaFact]
    public async Task Editing_A_Recipe_Leaves_The_Pane_Being_Looked_At_Where_It_Was()
    {
        var (_, viewer) = await Painting();

        var panes = viewer.GetVisualDescendants().OfType<TabControl>().Single(control => control.Classes.Contains("panes"));

        panes.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Replacements", (string)((TabItem)panes.SelectedItem!).Header!);

        Assert.True(Replacements(viewer).Bind("color", "#00ff00", "hsl(hue, 50%, 50%)"));

        // The drawings under a recipe are read again when it settles, and rebuilding the strip over
        // somebody typing in it took them back to the first tab on every keystroke.
        for (var attempt = 0; attempt < 60; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        var strip = viewer.GetVisualDescendants().OfType<TabControl>().Single(control => control.Classes.Contains("panes"));

        Assert.Equal("Replacements", (string)((TabItem)strip.SelectedItem!).Header!);
    }

    /// <summary>
    /// Clicking an element of a drawing built through a recipe still shows it in the text.
    /// </summary>
    /// <remarks>
    /// The tree is built from the document the recipe makes, and the pane shows the file. A recipe
    /// puts <c>&lt;defs&gt;&lt;e:code&gt;</c> at the front of the root, so every child after it is
    /// one index further along than the file has it — and an address is a path of child indices.
    /// Every row of every drawing under a recipe looked up nothing and moved the caret nowhere.
    /// </remarks>
    [AvaloniaFact]
    public async Task An_Element_Of_A_Drawing_Under_A_Recipe_Is_Found_In_Its_Own_Text()
    {
        var (_, viewer) = await Painting();

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var editor = viewer.GetVisualDescendants().OfType<TextEditor>().Single(control => control.Name == "SourceEditor");

        // The drawing is one rect. In the built document it is the second child, because the recipe
        // injected a defs in front of it; in the file it is the first.
        var rect = viewer.Elements.Root!.Children.Single(child => child.Element is SvgRectangle);

        Assert.Equal("1", rect.AddressKey);

        Assert.True(viewer.RevealInSource(rect));
        Assert.Contains("<rect", editor.SelectedText);
    }

    [AvaloniaFact]
    public async Task Editing_A_Recipe_Does_Not_Read_The_Drawing_Off_The_Disk_Again()
    {
        var (_, viewer) = await Painting();

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var editor = viewer.GetVisualDescendants().OfType<TextEditor>().Single(control => control.Name == "SourceEditor");

        var buffer = editor.Document;
        var built = viewer.Document;

        Assert.True(Replacements(viewer).Bind("color", "#00ff00", "hsl(hue, 50%, 50%)"));

        for (var attempt = 0; attempt < 200 && ReferenceEquals(viewer.Document, built); attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        // The drawing was built again — a recipe decides what its colours come to.
        Assert.NotSame(built, viewer.Document);

        // From the text the pane was already holding. Reading the file for it dropped this buffer,
        // and with it the caret, the scroll and anything typed into the pane, on every keystroke
        // somebody made in the recipe.
        Assert.Same(buffer, editor.Document);
    }

    [AvaloniaFact]
    public async Task A_Recipe_Edited_From_A_Drawing_Is_Marked_And_Saved_On_That_Tab()
    {
        var (window, viewer) = await Painting();
        var recipe = Path.Combine(_directory, "icons.recipe");

        var tab = Tabs(window).Items.OfType<TabItem>().Single(item => ReferenceEquals(item.Content, viewer));

        Assert.DoesNotContain("unsaved", Marker(tab).Classes);

        // No recipe tab is open, and nothing makes you open one: this is the only thing holding the
        // work, so it is the thing that has to say so.
        Assert.True(Replacements(viewer).Bind("color", "#00ff00", "hsl(hue, 50%, 50%)"));
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("unsaved", Marker(tab).Classes);
        Assert.DoesNotContain("hsl(hue, 50%, 50%)", File.ReadAllText(recipe));

        await window.SaveAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("hsl(hue, 50%, 50%)", File.ReadAllText(recipe));
        Assert.DoesNotContain("unsaved", Marker(tab).Classes);

        // The drawing itself was never the thing being edited, and is not written either way.
        Assert.Equal(Drawing, File.ReadAllText(Path.Combine(_directory, "badge.svg")));
    }

    [AvaloniaFact]
    public async Task Closing_A_Project_Asks_About_A_Recipe_No_Tab_Is_Left_On()
    {
        var (window, viewer) = await Painting();

        Assert.True(Replacements(viewer).Bind("color", "#00ff00", "hsl(hue, 50%, 50%)"));
        Dispatcher.UIThread.RunJobs();

        // The tabs it was edited from go, and the buffer is left with nothing to speak for it.
        window.ConfirmDiscard = _ => Task.FromResult(true);

        foreach (var item in Tabs(window).Items.OfType<TabItem>().Where(item => item.Tag is SvgcProjectNode).ToList())
        {
            ((StackPanel)item.Header!).Children.OfType<Button>().Single()
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            for (var attempt = 0; attempt < 50 && Tabs(window).Items.Contains(item); attempt++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }
        }

        var asked = new List<string>();

        window.ConfirmDiscard = message =>
        {
            asked.Add(message);

            return Task.FromResult(false);
        };

        Assert.False(await window.CloseProjectAsync());

        // Refused, so the project is still open and the work is still there.
        Assert.Contains("icons.recipe", Assert.Single(asked));
        Assert.NotNull(window.Workspace);
    }

    [AvaloniaFact]
    public async Task Undo_On_A_Drawing_Tab_Reaches_The_Recipe_Behind_It()
    {
        var (window, viewer) = await Painting();
        var colours = Replacements(viewer);
        var was = colours.Recipe.Text;

        Assert.True(colours.Bind("color", "#00ff00", "hsl(hue, 50%, 50%)"));
        Dispatcher.UIThread.RunJobs();

        // A menu item's gesture belongs to the window, so this is the only route to any stack — and
        // a drawing tab under a recipe used to match none of the ones it tried.
        Assert.True(window.Undo());

        Assert.Equal(was, colours.Recipe.Text);
        Assert.False(colours.Recipe.IsModified);

        Assert.True(window.Redo());
        Assert.Contains("hsl(hue, 50%, 50%)", colours.Recipe.Text);
    }

    [AvaloniaFact]
    public async Task Undo_Takes_The_Drawings_Own_Text_Back_First()
    {
        var (window, viewer) = await Painting();
        var colours = Replacements(viewer);

        Assert.True(colours.Bind("color", "#00ff00", "hsl(hue, 50%, 50%)"));
        Dispatcher.UIThread.RunJobs();

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var editor = viewer.GetVisualDescendants().OfType<TextEditor>().Single(control => control.Name == "SourceEditor");

        editor.Document.Insert(0, "<!-- typed -->");
        Dispatcher.UIThread.RunJobs();

        // The tab is named after the drawing, so the drawing goes first.
        Assert.True(window.Undo());

        Assert.DoesNotContain("typed", viewer.Source);
        Assert.Contains("hsl(hue, 50%, 50%)", colours.Recipe.Text);

        // And the recipe once the drawing has run out.
        Assert.True(window.Undo());

        Assert.DoesNotContain("hsl(hue, 50%, 50%)", colours.Recipe.Text);
    }

    /// <summary>The box, the readout beside it and the trouble under it, for one value.</summary>
    private static (TextBox Box, TextBlock Readout, TextBlock Trouble) Painted(ReplacementsPanel colours, string value, string name = "color")
    {
        var box = colours.GetVisualDescendants().OfType<TextBox>().Single(candidate => Equals(candidate.Tag, (name, value)));
        var row = (StackPanel)box.FindAncestorOfType<Grid>()!.Parent!;
        var blocks = row.GetLogicalDescendants().OfType<TextBlock>().ToList();

        return (box, blocks[blocks.Count - 2], blocks[blocks.Count - 1]);
    }

    private static ReplacementsPanel Showing(SvgViewer viewer)
    {
        viewer.GetVisualDescendants()
            .OfType<TabControl>()
            .Single(control => control.Classes.Contains("panes"))
            .SelectedIndex = 1;

        Dispatcher.UIThread.RunJobs();

        return Replacements(viewer);
    }

    [AvaloniaFact]
    public async Task A_Colour_Expression_Is_Checked_Where_It_Is_Typed()
    {
        var (_, viewer) = await Painting();
        var colours = Showing(viewer);
        var was = colours.Recipe.Text;

        var (box, _, trouble) = Painted(colours, "#00ff00");

        box.Text = "hsl(hu, 100%, 50%)";
        Dispatcher.UIThread.RunJobs();

        // Said under the box it was typed in. It used to reach the drawing and be reported on the
        // drawing's status line, a long way from here.
        Assert.True(trouble.IsVisible);
        Assert.Contains("hu", trouble.Text);

        // And nothing is written while it will not check.
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(was, colours.Recipe.Text);
        Assert.Equal("hsl(hu, 100%, 50%)", box.Text);
    }

    [AvaloniaFact]
    public async Task A_Colour_Expression_Of_The_Wrong_Type_Is_Refused()
    {
        var (_, viewer) = await Painting();
        var colours = Showing(viewer);

        var (box, _, trouble) = Painted(colours, "#00ff00");

        // Well formed and wrong: a rule's body lands in the attribute it names, and this one names
        // colours. Nothing caught this before it reached the drawing.
        box.Text = "hue + 1";
        Dispatcher.UIThread.RunJobs();

        Assert.True(trouble.IsVisible);
        Assert.Contains("colour", trouble.Text);
    }

    /// <summary>A drawing with an opacity in it, which a rule names by that attribute.</summary>
    private const string Fading = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <rect width="24" height="24" fill="#00ff00" opacity="0.5" />
        </svg>
        """;

    [AvaloniaFact]
    public async Task A_Value_That_Is_Not_A_Colour_Is_Offered_And_Bound()
    {
        var (_, viewer) = await Painting(Fading);
        var colours = Showing(viewer);

        // Both kinds, each named as its own rule: the colour across every colour attribute, the
        // opacity under the attribute it sits on.
        Assert.Equal(
            new[] { ("color", "#00ff00"), ("opacity", "0.5") },
            colours.Values.Select(value => (value.Name, value.Text)).ToArray());

        Assert.True(colours.Bind("opacity", "0.5", "hue / 240"));

        Assert.Contains("""<replace opacity="0.5">hue / 240</replace>""", colours.Recipe.Text);
        Assert.Equal("hue / 240", colours.Expression("opacity", "0.5"));

        // And the colour rule beside it is untouched, which is the whole point of naming both.
        Assert.Equal("tint", colours.Expression("color", "#00ff00"));
    }

    [AvaloniaFact]
    public async Task An_Expression_Is_Checked_As_What_The_Attribute_Holds()
    {
        var (_, viewer) = await Painting(Fading);
        var colours = Showing(viewer);

        var (box, _, trouble) = Painted(colours, "0.5", "opacity");

        // A colour here is as wrong as a number was on the row above: an opacity scales an alpha.
        box.Text = "tint";
        Dispatcher.UIThread.RunJobs();

        Assert.True(trouble.IsVisible);
        Assert.Contains("number", trouble.Text);

        // Nothing written while it will not check.
        var was = colours.Recipe.Text;

        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(was, colours.Recipe.Text);

        // And the number that does check is taken.
        box.Text = "hue / 240";
        Dispatcher.UIThread.RunJobs();

        Assert.False(trouble.IsVisible);
    }

    [AvaloniaFact]
    public async Task A_Colour_Reads_Out_What_It_Comes_To_And_Follows_The_Parameters()
    {
        var (_, viewer) = await Painting();
        var colours = Showing(viewer);

        var (box, readout, trouble) = Painted(colours, "#00ff00");

        Assert.False(trouble.IsVisible);
        Assert.True(readout.IsVisible);

        // The recipe paints it hsl(hue, 100%, 50%) with hue at 120.
        Assert.Contains("colour", readout.Text);

        var before = readout.Text;

        Assert.True(viewer.TrySetParameterValue("hue", ExprValue.Number(240f)));
        Dispatcher.UIThread.RunJobs();

        // A readout is what the rule paints now, so it moves with the slider.
        Assert.NotEqual(before, readout.Text);
        Assert.Equal("tint", box.Text);
    }

    [AvaloniaFact]
    public async Task Picking_A_Tab_Opens_The_Tree_Down_To_What_It_Shows()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Project));

        var root = (TreeViewItem)Tree(window).Items[0]!;
        var group = (TreeViewItem)root.Items[1]!;
        var badge = (TreeViewItem)group.Items[0]!;

        await window.ShowAsync((SvgcProjectNode)badge.Tag!);
        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)root.Items[0]!).Tag!);
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
    public async Task Picking_A_Tab_With_No_Row_Leaves_The_Tree_Alone()
    {
        var (window, viewer) = await Painting();

        var root = (TreeViewItem)Tree(window).Items[0]!;
        var group = (TreeViewItem)root.Items[1]!;

        // A recipe's tab is a file, not a node of the project, so there is nothing to open down to.
        window.ShowRecipe(Path.Combine(_directory, "icons.recipe"));
        Dispatcher.UIThread.RunJobs();

        group.IsExpanded = false;

        Tabs(window).SelectedItem = Tabs(window).Items
            .OfType<TabItem>()
            .Single(item => item.Content is RecipePanel);

        Dispatcher.UIThread.RunJobs();

        Assert.False(group.IsExpanded);
    }

    [AvaloniaFact]
    public async Task Saving_A_Drawing_As_Another_File_Points_Its_Tab_At_The_New_One()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Project));

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var viewer = await Settle(window, "home.svg");
        var copy = Path.Combine(_directory, "copied.svg");

        Assert.True(await window.SaveAsAsync(copy));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Drawing, File.ReadAllText(copy));

        // The one it came from is left as it was: this is a save under another name, not a move.
        Assert.Equal(Drawing, File.ReadAllText(Path.Combine(_directory, "home.svg")));

        // And the tab is the new file's now, so saving again writes there.
        await Settle(window, "copied.svg");

        Assert.Equal(copy, viewer.DocumentPath);
        Assert.False(viewer.IsSourceModified);
    }

    [AvaloniaFact]
    public async Task Save_Is_Offered_Only_While_Something_Is_Unsaved()
    {
        var (window, viewer) = await Painting();

        var save = Menu(window, "Save");

        Assert.False(save.IsEnabled);

        // The recipe behind the drawing, which is the case the tab's dot was widened for — and the
        // menu is drawn from the same answer, so it follows without being told separately.
        Assert.True(Replacements(viewer).Bind("color", "#00ff00", "hsl(hue, 50%, 50%)"));
        Dispatcher.UIThread.RunJobs();

        Assert.True(save.IsEnabled);

        await window.SaveAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.False(save.IsEnabled);
    }

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

        var path = Write("icons.svgcproj", Project);
        var window = await Host(path);

        Pick(window, "home.svg - Home", "Copy");
        Pick(window, "Demo.Icons.Large", "Paste");

        // Into the group, at its end — and the row it was copied from stays where it was.
        Assert.Equal(
            new[] { "Demo.Icons", "home.svg - Home", "Demo.Icons.Large", "badge.svg - BadgeLarge", "home.svg - Home" },
            Rows((TreeViewItem)Tree(window).Items[0]!));

        Assert.Contains("<svg input=\"home.svg\" class=\"Home\" />\n  </group>", File.ReadAllText(path));
    }

    [AvaloniaFact]
    public async Task A_Cut_Row_Moves_Rather_Than_Doubling()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var path = Write("icons.svgcproj", Project);
        var window = await Host(path);

        Pick(window, "badge.svg - BadgeLarge", "Cut");
        Pick(window, "home.svg - Home", "Paste");

        Assert.Equal(
            new[] { "Demo.Icons", "home.svg - Home", "badge.svg - BadgeLarge", "Demo.Icons.Large" },
            Rows((TreeViewItem)Tree(window).Items[0]!));

        // Once in the file, not twice: what was cut is not still where it was.
        Assert.Single(window.Workspace!.Document.Root.Drawings, drawing => drawing.Input == "badge.svg");

        // And nothing is left held: a second paste finds no row waiting and falls through to the
        // clipboard, which in a test has nothing on it, so the tree stays as it is.
        window.Announce = (_, _) => Task.CompletedTask;

        Pick(window, "Demo.Icons.Large", "Paste");

        Assert.Equal(
            new[] { "Demo.Icons", "home.svg - Home", "badge.svg - BadgeLarge", "Demo.Icons.Large" },
            Rows((TreeViewItem)Tree(window).Items[0]!));
    }

    /// <summary>The refusal a drag already makes, made the same way by a paste.</summary>
    [AvaloniaFact]
    public async Task A_Group_Cut_Cannot_Be_Pasted_Into_Its_Own_Branch()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var path = Write("icons.svgcproj", Project);
        var window = await Host(path);

        Pick(window, "Demo.Icons.Large", "Cut");
        Pick(window, "badge.svg - BadgeLarge", "Paste");

        Assert.Equal(
            new[] { "Demo.Icons", "home.svg - Home", "Demo.Icons.Large", "badge.svg - BadgeLarge" },
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
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Project));

        Assert.Contains("Paste", Offers(Row(window, "Demo.Icons.Large")));

        Pick(window, "home.svg - Home", "Copy");

        Assert.Contains("Paste", Offers(Row(window, "Demo.Icons.Large")));
    }

    // ---- pasting a drawing off the system clipboard ------------------------------------------

    /// <summary>An icon copied in a drawing program, which has no file to name.</summary>
    [AvaloniaFact]
    public async Task An_Svg_On_The_Clipboard_Is_Written_Beside_The_Project_And_Added()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var path = Write("icons.svgcproj", Project);
        var window = await Host(path);

        await Copy(window, DataTransferItem.CreateText(Drawing));

        Pick(window, "Demo.Icons.Large", "Paste");
        await Settle(() => Rows((TreeViewItem)Tree(window).Items[0]!).Contains("drawing.svg"));

        Assert.Equal(
            new[] { "Demo.Icons", "home.svg - Home", "Demo.Icons.Large", "badge.svg - BadgeLarge", "drawing.svg" },
            Rows((TreeViewItem)Tree(window).Items[0]!));

        // A file of its own, and the project naming it the way it names the drawings it was written
        // with — relative to itself.
        Assert.Equal(Drawing, File.ReadAllText(Path.Combine(_directory, "drawing.svg")));
        Assert.Contains("<svg input=\"drawing.svg\" />", File.ReadAllText(path));
    }

    [AvaloniaFact]
    public async Task A_Second_Paste_Does_Not_Write_Over_The_First()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Project));

        await Copy(window, DataTransferItem.CreateText(Drawing));

        Pick(window, "Demo.Icons.Large", "Paste");
        await Settle(() => Rows((TreeViewItem)Tree(window).Items[0]!).Contains("drawing.svg"));

        Pick(window, "Demo.Icons.Large", "Paste");
        await Settle(() => Rows((TreeViewItem)Tree(window).Items[0]!).Contains("drawing-2.svg"));

        Assert.True(File.Exists(Path.Combine(_directory, "drawing.svg")));
        Assert.True(File.Exists(Path.Combine(_directory, "drawing-2.svg")));
    }

    /// <summary>The format Illustrator has also written the same art as.</summary>
    [AvaloniaFact]
    public async Task A_Format_Naming_Itself_Svg_Is_Read_When_The_Text_Is_Not_One()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Project));
        var carried = new DataTransfer();

        carried.Add(DataTransferItem.CreateText("Adobe Illustrator artwork"));
        carried.Add(DataTransferItem.Create(DataFormat.CreateStringPlatformFormat("image/svg+xml"), Drawing));

        await window.Clipboard!.SetDataAsync(carried);

        Pick(window, "Demo.Icons.Large", "Paste");
        await Settle(() => Rows((TreeViewItem)Tree(window).Items[0]!).Contains("drawing.svg"));

        Assert.Equal(Drawing, File.ReadAllText(Path.Combine(_directory, "drawing.svg")));
    }

    /// <summary>A drawing already on disk is a row where it is, not a copy of it.</summary>
    [AvaloniaFact]
    public async Task An_Svg_File_On_The_Clipboard_Is_Added_Where_It_Already_Is()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Project));
        var copied = Write("mark.svg", Drawing);
        var file = await window.StorageProvider.TryGetFileFromPathAsync(copied);

        await Copy(window, DataTransferItem.CreateFile(file!));

        Pick(window, "Demo.Icons.Large", "Paste");
        await Settle(() => Rows((TreeViewItem)Tree(window).Items[0]!).Contains("mark.svg"));

        Assert.False(File.Exists(Path.Combine(_directory, "drawing.svg")));
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

        var window = await Host(Write("icons.svgcproj", Project));
        var said = new List<string>();

        window.Announce = (_, message) => { said.Add(message); return Task.CompletedTask; };

        await Copy(window, DataTransferItem.CreateText("just some words"));

        Pick(window, "Demo.Icons.Large", "Paste");
        await Settle(() => said.Count > 0);

        Assert.Contains("none of it is a drawing", said.Single());
        Assert.False(File.Exists(Path.Combine(_directory, "drawing.svg")));

        Assert.Equal(
            new[] { "Demo.Icons", "home.svg - Home", "Demo.Icons.Large", "badge.svg - BadgeLarge" },
            Rows((TreeViewItem)Tree(window).Items[0]!));
    }

    /// <summary>A row held here is the more particular answer, and the one nothing else knows.</summary>
    [AvaloniaFact]
    public async Task A_Held_Row_Is_Pasted_Rather_Than_What_The_Clipboard_Holds()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Project));

        await Copy(window, DataTransferItem.CreateText(Drawing));

        Pick(window, "home.svg - Home", "Copy");
        Pick(window, "Demo.Icons.Large", "Paste");

        Assert.Equal(
            new[] { "Demo.Icons", "home.svg - Home", "Demo.Icons.Large", "badge.svg - BadgeLarge", "home.svg - Home" },
            Rows((TreeViewItem)Tree(window).Items[0]!));

        Assert.False(File.Exists(Path.Combine(_directory, "drawing.svg")));
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

        var window = await Host(Write("icons.svgcproj", Project));

        await Copy(window, DataTransferItem.CreateText(Drawing));

        Pick(window, "home.svg - Home", "Copy");
        Pick(window, "Demo.Icons.Large", "Paste");

        // The row, and then what was on the clipboard all along.
        Pick(window, "Demo.Icons.Large", "Paste");
        await Settle(() => Rows((TreeViewItem)Tree(window).Items[0]!).Contains("drawing.svg"));

        Assert.Equal(
            new[]
            {
                "Demo.Icons", "home.svg - Home", "Demo.Icons.Large", "badge.svg - BadgeLarge",
                "home.svg - Home", "drawing.svg"
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

        var window = await Host(Write("icons.svgcproj", Project));
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        Press(window, "home.svg - Home", Key.C, command);
        Press(window, "Demo.Icons.Large", Key.V, command);

        Assert.Equal(
            new[] { "Demo.Icons", "home.svg - Home", "Demo.Icons.Large", "badge.svg - BadgeLarge", "home.svg - Home" },
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
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Project));

        var offers = Offers(Row(window, "Demo.Icons"));

        Assert.DoesNotContain("Cut", offers);
        Assert.DoesNotContain("Copy", offers);
        Assert.DoesNotContain("Remove", offers);
    }

    [AvaloniaFact]
    public async Task Revealing_A_Drawing_Shows_The_File_It_Names()
    {
        var drawing = Write("home.svg", Drawing);

        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Project));
        var shown = new List<string>();

        window.ShowOnDisk = path => shown.Add(path);

        Pick(window, "home.svg - Home", Revealing);

        Assert.Equal(new[] { drawing }, shown);
    }

    /// <summary>A group is not a file, so what it shows is the project it is written in.</summary>
    [AvaloniaFact]
    public async Task Revealing_A_Group_Shows_The_Project()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var path = Write("icons.svgcproj", Project);
        var window = await Host(path);
        var shown = new List<string>();

        window.ShowOnDisk = shown.Add;

        Pick(window, "Demo.Icons.Large", Revealing);
        Pick(window, "Demo.Icons", Revealing);

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

        var path = Write("icons.svgcproj", Project);
        var window = await Host(path);
        var root = (TreeViewItem)Tree(window).Items[0]!;

        await Drop(window, (TreeViewItem)root.Items[1]!, 0.5d, Write("extra.svg", Drawing));

        Assert.Equal(
            new[] { "Demo.Icons", "home.svg - Home", "Demo.Icons.Large", "badge.svg - BadgeLarge", "extra.svg" },
            Rows((TreeViewItem)Tree(window).Items[0]!));

        Assert.Contains("<svg input=\"badge.svg\" class=\"BadgeLarge\" />", File.ReadAllText(path));
        Assert.Contains("<svg input=\"extra.svg\" />", File.ReadAllText(path));

        Assert.Equal("extra.svg", Path.GetFileName((await Settle(window, "extra.svg")).DocumentPath));
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

        var window = await Host(Write("icons.svgcproj", Project));
        var root = (TreeViewItem)Tree(window).Items[0]!;

        var before = Tabs(window).Items.Count;

        await Drop(window, (TreeViewItem)root.Items[0]!, 0.9d, Write("one.svg", Drawing), Write("two.svg", Drawing));

        Assert.Equal(
            new[] { "Demo.Icons", "home.svg - Home", "one.svg", "two.svg", "Demo.Icons.Large", "badge.svg - BadgeLarge" },
            Rows((TreeViewItem)Tree(window).Items[0]!));

        // One tab for the run, on the last of them: a folder of drawings is one act.
        Assert.Equal("two.svg", Path.GetFileName((await Settle(window, "two.svg")).DocumentPath));
        Assert.Equal(before + 1, Tabs(window).Items.Count);
    }

    /// <summary>The tree's background is the project itself, so a drop with no row under it lands there.</summary>
    [AvaloniaFact]
    public async Task A_Drawing_Dropped_Below_The_Rows_Goes_To_The_Project()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Project));
        var root = (TreeViewItem)Tree(window).Items[0]!;

        // Under everything the project has, which is still inside the tree.
        await Drop(window, root, (root.Bounds.Height + 40d) / RowHeight(root), Write("extra.svg", Drawing));

        Assert.Equal(
            new[] { "Demo.Icons", "home.svg - Home", "Demo.Icons.Large", "badge.svg - BadgeLarge", "extra.svg" },
            Rows((TreeViewItem)Tree(window).Items[0]!));
    }

    /// <summary>
    /// Only drawings are the tree's. Everything else is still the window's, and still opens.
    /// </summary>
    [AvaloniaFact]
    public async Task A_Project_Dropped_On_The_Tree_Opens_As_A_Project()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Project));
        var root = (TreeViewItem)Tree(window).Items[0]!;

        var second = Write("other.svgcproj", Project.Replace("Demo.Icons", "Demo.Other", StringComparison.Ordinal));

        await Drop(window, root, 0.5d, second);

        for (var attempt = 0; attempt < 200 && window.Workspace?.Name != "other.svgcproj"; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.Equal("other.svgcproj", window.Workspace!.Name);
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

        Assert.Equal("home.svg", Path.GetFileName((await Settle(window, "home.svg")).DocumentPath));
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

    [AvaloniaFact]
    public async Task A_Rule_That_This_Drawing_Has_No_Colour_For_Is_Still_Shown()
    {
        var (_, viewer) = await Painting(Drawing.Replace("#00ff00", "#ff0000", StringComparison.Ordinal));
        var colours = Replacements(viewer);

        // The recipe's rule is for #00ff00, which this drawing does not paint. One recipe covers a
        // family, so that is ordinary — but a rule that appeared to have vanished would not be.
        Assert.Equal(new[] { "#ff0000" }, colours.Values.Select(value => value.Text));
        Assert.Equal("tint", colours.Expression("color", "#00ff00"));

        // On screen, not just in the model: the pane's content is out of the tree until its tab is
        // the one being looked at.
        var panes = viewer.GetVisualDescendants().OfType<TabControl>().Single(control => control.Classes.Contains("panes"));

        panes.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(
            "Not in this drawing",
            colours.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text));
    }

    [AvaloniaFact]
    public async Task A_Colour_Is_Bound_Through_The_Rule_The_Recipe_Already_Writes_For_It()
    {
        // The recipe names the colour one way and the drawing another. Two spellings of one colour
        // must not become two rules, which the recipe then refuses to read at all.
        var (_, viewer) = await Painting(recipe: Recipe.Replace("#00ff00", "rgb(0, 255, 0)", StringComparison.Ordinal));
        var colours = Replacements(viewer);

        Assert.Equal(new[] { "#00ff00" }, colours.Values.Select(value => value.Text));
        Assert.True(colours.Bind("color", "#00ff00", "deep"));

        Assert.Contains("""color="rgb(0, 255, 0)">deep<""", colours.Recipe.Text);
        Assert.Null(colours.Recipe.Fault);
    }

    [AvaloniaFact]
    public async Task A_Drawing_Follows_Its_Recipe_Before_The_Recipe_Is_Saved()
    {
        var window = await Recipes();

        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)((TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[1]!).Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        var viewer = await Settle(window, "badge.svg");
        Assert.Equal("hue", Assert.Single(viewer.Parameters).Name);

        var recipe = Path.Combine(_directory, "icons.recipe");
        var editor = window.ShowRecipe(recipe);

        editor.Text = Recipe.Replace("hue", "tone", StringComparison.Ordinal);
        Dispatcher.UIThread.RunJobs();

        // Nothing is written. What the drawing is built through is the buffer, not the file.
        Assert.Contains("hue", File.ReadAllText(recipe));
        Assert.True(editor.IsModified);

        var tab = Tabs(window).Items.OfType<TabItem>().Single(item => ReferenceEquals(item.Content, viewer));

        for (var attempt = 0; attempt < 200 && viewer.Parameters.FirstOrDefault()?.Name != "tone"; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);

            // Selected on every pass, since the tab is only read again once it is looked at.
            Tabs(window).SelectedItem = tab;
        }

        Assert.Equal("tone", Assert.Single(viewer.Parameters).Name);
        Assert.Contains("hue", File.ReadAllText(recipe));
    }

    [AvaloniaFact]
    public async Task A_Recipe_Is_Undone_And_Redone_Through_The_Window()
    {
        var window = await Recipes();

        var editor = window.ShowRecipe(Path.Combine(_directory, "icons.recipe"));
        Dispatcher.UIThread.RunJobs();

        editor.Text = "<recipe xmlns=\"https://svg.skia/expr/1.0\" />";
        Dispatcher.UIThread.RunJobs();

        Assert.True(editor.IsModified);

        // Through the window, because a menu item's gesture is the window's: the keystroke is taken
        // before AvaloniaEdit can see it, and used to reach a viewer the tab has none of.
        Assert.True(window.Undo());
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Recipe, editor.Text);
        Assert.False(editor.IsModified);

        Assert.True(window.Redo());
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain("<code>", editor.Text);
        Assert.True(editor.IsModified);
    }

    [AvaloniaFact]
    public async Task A_Recipe_Is_Painted_For_The_Theme_Of_The_Window_It_Is_In()
    {
        var window = await Recipes();

        var panel = window.ShowRecipe(Path.Combine(_directory, "icons.recipe"));
        Dispatcher.UIThread.RunJobs();

        // Built before it is in any tree, so what it painted itself in the constructor was the
        // light palette whatever window it went into.
        window.RequestedThemeVariant = ThemeVariant.Dark;
        Dispatcher.UIThread.RunJobs();

        Assert.True(panel.TryFindResource("SvgViewerSourceTextBrush", ThemeVariant.Dark, out var resource));

        var expected = ((ISolidColorBrush)resource!).Color;
        var editor = panel.GetVisualDescendants().OfType<TextEditor>().Single();
        var area = panel.GetVisualDescendants().OfType<TextArea>().Single();

        Assert.Equal(expected, ((ISolidColorBrush)editor.Foreground!).Color);

        // With none of its own the caret is drawn by inverting what is behind it, which came out as
        // a caret nobody could see.
        Assert.Equal(expected, ((ISolidColorBrush)area.CaretBrush!).Color);
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
        var path = Path.Combine(_directory, "fresh.svgcproj");
        var window = Empty();

        await window.NewProjectAsync(path);

        Dispatcher.UIThread.RunJobs();

        Assert.True(File.Exists(path));
        Assert.Equal("fresh.svgcproj", window.Workspace!.Name);
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
        var drawing = Write("home.svg", Drawing);
        var path = Path.Combine(_directory, "fresh.svgcproj");
        var window = Empty();

        await window.NewProjectAsync(path);

        Dispatcher.UIThread.RunJobs();

        await Drop(window, (TreeViewItem)Tree(window).Items[0]!, 0.5d, drawing);

        for (var attempt = 0; attempt < 200 && !window.Workspace!.Document.Root.Drawings.Any(); attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.Single(window.Workspace!.Document.Root.Drawings);
        Assert.Contains("\n  <svg ", File.ReadAllText(path), StringComparison.Ordinal);
    }

    /// <summary>
    /// New over a project that is already there opens it. The save panel asked about replacing the
    /// file; emptying somebody's project is not what answering yes to that meant.
    /// </summary>
    [AvaloniaFact]
    public async Task A_New_Project_Over_One_That_Exists_Opens_It()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var path = Write("icons.svgcproj", Project);
        var window = Empty();

        await window.NewProjectAsync(path);

        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Project, File.ReadAllText(path));
        Assert.Equal("Demo.Icons", window.Workspace!.Document.Root.Namespace);
    }

    /// <summary>Waits for the tab holding <paramref name="name"/> to have finished loading.</summary>
    private static async Task<SvgViewer> Settle(MainWindow window, string name)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            Dispatcher.UIThread.RunJobs();

            var viewer = Tabs(window).Items
                .OfType<TabItem>()
                .Select(item => item.Content)
                .OfType<SvgViewer>()
                .FirstOrDefault(open => open.DocumentPath is { } path && Path.GetFileName(path) == name);

            if (viewer?.Document is { })
            {
                return viewer;
            }

            await Task.Delay(10);
        }

        throw new InvalidOperationException($"'{name}' was never opened.");
    }
}
