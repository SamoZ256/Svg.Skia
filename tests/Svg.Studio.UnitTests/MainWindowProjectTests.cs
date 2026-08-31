using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Svg.CodeGen.Skia.Projects;
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

    /// <summary>A window with <paramref name="path"/> opened through the request a drop also raises.</summary>
    private static async Task<MainWindow> Host(string path)
    {
        var window = new MainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var tabs = window.FindControl<TabControl>("Tabs")!;
        var first = (SvgViewer)((TabItem)tabs.Items[0]!).Content!;

        // The window starts loading its bundled sample with nothing to await it by, so a project
        // opened before it lands would reuse the tab the sample is about to fill.
        for (var attempt = 0; attempt < 200 && first.Document is null; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.True(await first.OpenAsync(new[] { path }));

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
    public async Task A_Project_Opens_Into_The_Pane_And_Takes_No_Tab_Of_Its_Own()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Project));

        Assert.Equal("icons.svgcproj", window.Workspace!.Name);

        // The project is what the window works on, not one of the things it shows: opening it adds
        // no tab, and the sample the window started on is still the only one.
        Assert.Single(Tabs(window).Items);
        Assert.IsType<SvgViewer>(((TabItem)Tabs(window).Items[0]!).Content);

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

        // Chosen twice is the same tab, not a second one.
        await window.ShowAsync(group);
        Assert.Equal(2, Tabs(window).Items.Count);
    }

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

        Assert.Equal(3, Tabs(window).Items.Count);

        Assert.True(await window.CloseProjectAsync());
        Dispatcher.UIThread.RunJobs();

        Assert.Null(window.Workspace);
        Assert.Empty(Tree(window).Items);
        Assert.False(window.FindControl<Border>("ProjectPaneHost")!.IsVisible);

        // The sample the window started on is not the project's, so it stays.
        Assert.Single(Tabs(window).Items);
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

        var item = Tabs(window).Items.OfType<TabItem>().Single(tab => tab.Content is GroupPanel);
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

        // The sample the window starts on is a drawing, so it is there to export.
        Assert.True(export.IsEnabled);

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
        // an element the document no longer holds and report itself saved.
        Assert.Single(Tabs(window).Items);
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
    public async Task One_File_Built_Twice_Is_Given_A_Class_Each_In_The_Pane()
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
        var settings = window.FindControl<Border>("ProjectSettings")!;

        // A group has a tab for its settings, so the pane stays out of its way.
        Tree(window).SelectedItem = group;
        Dispatcher.UIThread.RunJobs();

        Assert.False(settings.IsVisible);

        Tree(window).SelectedItem = group.Items[1];
        Dispatcher.UIThread.RunJobs();

        Assert.True(settings.IsVisible);

        var panel = Assert.IsType<GroupPanel>(settings.Child);
        var box = panel.GetVisualDescendants().OfType<TextBox>().Single(candidate => Equals(candidate.Tag, "class"));

        // Empty, with what the group hands down behind it — which is the whole trouble: both rows
        // are the same file and both become Shared until one of them says otherwise.
        Assert.Null(box.Text);
        Assert.Equal("Shared — from Shared", box.PlaceholderText);

        box.Focus();
        Dispatcher.UIThread.RunJobs();
        box.Text = "Second";
        Dispatcher.UIThread.RunJobs();

        // The caret leaving is the save: the pane has no tab to hold an edit, and the panel goes
        // the moment another row is picked.
        panel.GetVisualDescendants().OfType<TextBox>().First(candidate => Equals(candidate.Tag, "output")).Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("<svg input=\"home.svg\" class=\"Second\" />", File.ReadAllText(path));

        Assert.Equal(
            new[] { "Demo.Icons", "Shared", "home.svg - Shared", "home.svg - Second" },
            Rows((TreeViewItem)Tree(window).Items[0]!));
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

    private static GroupPanel Panel(MainWindow window, string label)
        => Tabs(window).Items.OfType<TabItem>()
            .Select(item => item.Content)
            .OfType<GroupPanel>()
            .Single(panel => ProjectWorkspace.Label(panel.Node) == label);

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
