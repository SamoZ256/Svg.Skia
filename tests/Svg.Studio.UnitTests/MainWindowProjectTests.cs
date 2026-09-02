using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using Svg.CodeGen.Skia.Projects;
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
    public async Task A_Project_Opens_Into_The_Pane_And_Takes_No_Tab_Of_Its_Own()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        var window = await Host(Write("icons.svgcproj", Project));

        Assert.Equal("icons.svgcproj", window.Workspace!.Name);

        // The project is what the window works on, not one of the things it shows: opening it adds
        // no tab at all, and the window had none to begin with.
        Assert.Empty(Tabs(window).Items);

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
        Assert.Single(Tabs(window).Items);
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

        Assert.Equal(2, Tabs(window).Items.Count);

        Assert.True(await window.RemoveAsync((SvgcProjectNode)group.Tag!));
        Dispatcher.UIThread.RunJobs();

        // The group's tab and the drawing's under it both go: left open, either would go on editing
        // an element the document no longer holds and report itself saved.
        Assert.Empty(Tabs(window).Items);
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

        Assert.Single(Tabs(window).Items);
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

        var buffer = Colours(viewer).Recipe;

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
        Assert.Empty(viewer.SidePanels.Select(pane => pane.Content).OfType<ColourPanel>());
    }

    /// <summary>The buttons on a panel's recipe row, by what they are labelled.</summary>
    private static string[] RecipeButtons(GroupPanel panel)
        => panel.GetVisualDescendants()
            .OfType<Button>()
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
        Assert.Equal(new[] { "✕" }, RecipeButtons(panel));

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

    [AvaloniaFact]
    public async Task A_New_Recipe_Names_The_Colours_Its_Drawings_Paint()
    {
        // Two drawings under the group, painting a colour each.
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

        // The colours a recipe is for are the ones its drawings actually paint. Reading them out of
        // the files yourself was most of the work of starting one.
        Assert.Contains("""<!-- <replace color="#00ff00">accent</replace> -->""", text);
        Assert.Contains("""<!-- <replace color="#ff8800">accent</replace> -->""", text);
        Assert.Contains("The 2 colours these drawings paint", text);

        // Commented, every one: the file it writes has to apply as it stands, and binding them all
        // to the one let above would repaint the whole set the moment it was made.
        var recipe = SvgRecipe.Parse(text);

        Assert.Empty(recipe.ColorRules);
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

        // What it is written with applies as it stands: the drawing has a slider to drag before a
        // single line of the file has been edited, which is the point of writing one at all.
        Assert.Null(viewer.Notice);
        Assert.Equal("hue", Assert.Single(viewer.Parameters).Name);
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

    private static ColourPanel Colours(SvgViewer viewer)
        => viewer.SidePanels.Select(pane => pane.Content).OfType<ColourPanel>().Single();

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
            new[] { "Project", "Colours", "Parameters" },
            viewer.GetVisualDescendants()
                .OfType<TabControl>()
                .Single(control => control.Classes.Contains("panes"))
                .Items.OfType<TabItem>()
                .Select(item => (string)item.Header!));

        var colours = Colours(viewer);

        // The drawing's own colour, and what the recipe already says paints it.
        Assert.Equal(new[] { "#00ff00" }, colours.Colours);
        Assert.Equal("tint", colours.Expression("#00ff00"));

        // A drawing with no recipe over it has nothing to bind, so it has no pane either.
        await window.ShowAsync((SvgcProjectNode)((TreeViewItem)((TreeViewItem)Tree(window).Items[0]!).Items[0]!).Tag!);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty((await Settle(window, "home.svg")).SidePanels.Select(pane => pane.Content).OfType<ColourPanel>());
    }

    [AvaloniaFact]
    public async Task Binding_A_Colour_Changes_What_The_Drawing_Paints_Before_Any_Save()
    {
        // A drawing with a colour the recipe says nothing about yet.
        var (window, viewer) = await Painting(Drawing.Replace("#00ff00", "#ff0000", StringComparison.Ordinal));
        var colours = Colours(viewer);
        var recipe = Path.Combine(_directory, "icons.recipe");

        Assert.Equal(new[] { "#ff0000" }, colours.Colours);
        Assert.Null(colours.Expression("#ff0000"));

        Assert.True(colours.Bind("#ff0000", "hsl(hue, 100%, 50%)"));
        Dispatcher.UIThread.RunJobs();

        Assert.Null(colours.Fault);
        Assert.Equal("hsl(hue, 100%, 50%)", colours.Expression("#ff0000"));

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

        Assert.True(colours.Unbind("#ff0000"));
        Dispatcher.UIThread.RunJobs();

        Assert.Null(colours.Expression("#ff0000"));
        Assert.DoesNotContain("#ff0000", colours.Recipe.Text);
    }

    [AvaloniaFact]
    public async Task Editing_A_Recipe_Leaves_The_Pane_Being_Looked_At_Where_It_Was()
    {
        var (_, viewer) = await Painting();

        var panes = viewer.GetVisualDescendants().OfType<TabControl>().Single(control => control.Classes.Contains("panes"));

        panes.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Colours", (string)((TabItem)panes.SelectedItem!).Header!);

        Assert.True(Colours(viewer).Bind("#00ff00", "hsl(hue, 50%, 50%)"));

        // The drawings under a recipe are read again when it settles, and rebuilding the strip over
        // somebody typing in it took them back to the first tab on every keystroke.
        for (var attempt = 0; attempt < 60; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        var strip = viewer.GetVisualDescendants().OfType<TabControl>().Single(control => control.Classes.Contains("panes"));

        Assert.Equal("Colours", (string)((TabItem)strip.SelectedItem!).Header!);
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

        Assert.True(Colours(viewer).Bind("#00ff00", "hsl(hue, 50%, 50%)"));

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
        Assert.True(Colours(viewer).Bind("#00ff00", "hsl(hue, 50%, 50%)"));
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

        Assert.True(Colours(viewer).Bind("#00ff00", "hsl(hue, 50%, 50%)"));
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
        var colours = Colours(viewer);
        var was = colours.Recipe.Text;

        Assert.True(colours.Bind("#00ff00", "hsl(hue, 50%, 50%)"));
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
        var colours = Colours(viewer);

        Assert.True(colours.Bind("#00ff00", "hsl(hue, 50%, 50%)"));
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

    /// <summary>The box, the readout beside it and the trouble under it, for one colour.</summary>
    private static (TextBox Box, TextBlock Readout, TextBlock Trouble) Painted(ColourPanel colours, string colour)
    {
        var box = colours.GetVisualDescendants().OfType<TextBox>().Single(candidate => Equals(candidate.Tag, colour));
        var row = (StackPanel)box.FindAncestorOfType<Grid>()!.Parent!;
        var blocks = row.GetLogicalDescendants().OfType<TextBlock>().ToList();

        return (box, blocks[blocks.Count - 2], blocks[blocks.Count - 1]);
    }

    private static ColourPanel Showing(SvgViewer viewer)
    {
        viewer.GetVisualDescendants()
            .OfType<TabControl>()
            .Single(control => control.Classes.Contains("panes"))
            .SelectedIndex = 1;

        Dispatcher.UIThread.RunJobs();

        return Colours(viewer);
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

        // Well formed and wrong: a rule's body lands in fill, stroke and stop-color, which are all
        // colour slots. Nothing caught this before it reached the drawing.
        box.Text = "hue + 1";
        Dispatcher.UIThread.RunJobs();

        Assert.True(trouble.IsVisible);
        Assert.Contains("colour", trouble.Text);
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
        Assert.True(Colours(viewer).Bind("#00ff00", "hsl(hue, 50%, 50%)"));
        Dispatcher.UIThread.RunJobs();

        Assert.True(save.IsEnabled);

        await window.SaveAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.False(save.IsEnabled);
    }

    [AvaloniaFact]
    public async Task A_Rule_That_This_Drawing_Has_No_Colour_For_Is_Still_Shown()
    {
        var (_, viewer) = await Painting(Drawing.Replace("#00ff00", "#ff0000", StringComparison.Ordinal));
        var colours = Colours(viewer);

        // The recipe's rule is for #00ff00, which this drawing does not paint. One recipe covers a
        // family, so that is ordinary — but a rule that appeared to have vanished would not be.
        Assert.Equal(new[] { "#ff0000" }, colours.Colours);
        Assert.Equal("tint", colours.Expression("#00ff00"));

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
        var colours = Colours(viewer);

        Assert.Equal(new[] { "#00ff00" }, colours.Colours);
        Assert.True(colours.Bind("#00ff00", "deep"));

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
