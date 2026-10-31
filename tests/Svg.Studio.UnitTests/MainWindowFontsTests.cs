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
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.AI;
using Svg.Skia.TypefaceProviders;
using Svg.Viewer.Skia.Avalonia;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// A project's fonts in the window: dropped on it, listed and taken out in the project's
/// Properties, and what a tab and an export say of a face they lack.
/// </summary>
[Collection("settings")]
public class MainWindowFontsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    private readonly string _settings = StudioSettings.Store;

    private readonly string _recovery = ProjectRecovery.Store;

    public MainWindowFontsTests()
    {
        StudioSettings.Store = Path.Combine(_directory, "settings");
        ProjectRecovery.Store = Path.Combine(_directory, "recovery");
    }

    public void Dispose()
    {
        StudioSettings.Store = _settings;
        ProjectRecovery.Store = _recovery;
        Scratch.Delete(_directory);
    }

    private static string Font(string name) => ProjectFontsTests.Font(name);

    /// <summary>A project of one drawing whose text asks for Noto Sans at <paramref name="weight"/>.</summary>
    private string Project(string weight = "normal")
        => TestPackage.Write(Path.Combine(_directory, "icons.svgstudio"), $"""
            <studio namespace="Demo.Icons">
              <drawing name="badge" class="Badge">
            {ProjectFontsTests.Text("AB12", $"font-family=\"Noto Sans\" font-weight=\"{weight}\"")}
              </drawing>
            </studio>
            """);

    /// <summary>A copy of one of the test fonts under another name, where a test needs one.</summary>
    private string Copied(string font, string name)
    {
        var path = Path.Combine(_directory, name);

        File.Copy(Font(font), path);

        return path;
    }

    private static async Task<MainWindow> Host(string? project, List<(string Title, string Message)>? said = null)
    {
        var window = new MainWindow();

        window.Announce = (title, message) =>
        {
            said?.Add((title, message));

            return Task.CompletedTask;
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        if (project is { })
        {
            await window.OpenAsync(new[] { project });
            Dispatcher.UIThread.RunJobs();
        }

        return window;
    }

    /// <summary>The drawing's tab, opened and in front.</summary>
    private static async Task<SvgViewer> Tab(MainWindow window)
    {
        await window.ShowAsync(window.Workspace!.Document.Root.Children.OfType<ProjectDrawing>().Single());
        Dispatcher.UIThread.RunJobs();

        return (SvgViewer)((TabItem)window.FindControl<TabControl>("Tabs")!.SelectedItem!).Content!;
    }

    /// <summary>The project's own board, with nothing on it picked, whose Properties are the project's.</summary>
    private static async Task<GroupPanel> Board(MainWindow window)
    {
        await window.ShowAsync(window.Workspace!.Document.Root);
        Dispatcher.UIThread.RunJobs();

        var panel = (GroupPanel)((TabItem)window.FindControl<TabControl>("Tabs")!.SelectedItem!).Content!;

        window.Measure(new Size(900, 600));
        window.Arrange(new Rect(0, 0, 900, 600));
        Dispatcher.UIThread.RunJobs();

        return panel;
    }

    private static StackPanel Properties(MainWindow window)
        => window.GetVisualDescendants().OfType<StackPanel>().Single(found => found.Name == "Properties");

    private static string? Note(SvgViewer viewer)
        => viewer.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Name == "NoteText") is { IsVisible: true } note ? note.Text : null;

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Dropped on the middle of the window, as a file manager would.</summary>
    private static async Task Drop(MainWindow window, string path)
    {
        var file = await window.StorageProvider.TryGetFileFromPathAsync(path);
        var carried = new DataTransfer();

        carried.Add(DataTransferItem.CreateFile(file!));

        var at = new Point(window.Width / 2d, window.Height / 2d);

        window.DragDrop(at, RawDragEventType.DragEnter, carried, DragDropEffects.Copy, RawInputModifiers.None);
        window.DragDrop(at, RawDragEventType.Drop, carried, DragDropEffects.Copy, RawInputModifiers.None);

        await Gestures.Until(() => window.Workspace!.Document.Fonts.Count > 0);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Joined as an edit, the way a drawing is: undone and done again, written by the save, and
    /// never a tab of its own.
    /// </summary>
    [AvaloniaFact]
    public async Task A_Font_Dropped_On_The_Window_Joins_The_Project_As_An_Edit()
    {
        var folder = Project();
        var window = await Host(folder);
        var viewer = await Tab(window);
        var workspace = window.Workspace!;
        var tabs = window.FindControl<TabControl>("Tabs")!.Items.Count;

        Assert.IsType<FontManagerTypefaceProvider>(viewer.Svg!.Settings.TypefaceProviders![0]);

        await Drop(window, Font("NotoSans-Regular.ttf"));

        Assert.Equal(new[] { "fonts/NotoSans-Regular.ttf" }, workspace.Document.Fonts.Keys);
        Assert.True(workspace.IsEdited);
        Assert.Equal(tabs, window.FindControl<TabControl>("Tabs")!.Items.Count);
        Assert.Equal("Noto Sans", Assert.IsType<CustomTypefaceProvider>(viewer.Svg!.Settings.TypefaceProviders![0]).FamilyName);
        Assert.Null(Note(viewer));

        workspace.Undo();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(workspace.Document.Fonts);
        Assert.IsType<FontManagerTypefaceProvider>(viewer.Svg!.Settings.TypefaceProviders![0]);

        workspace.Redo();
        Dispatcher.UIThread.RunJobs();

        Assert.IsType<CustomTypefaceProvider>(viewer.Svg!.Settings.TypefaceProviders![0]);

        await window.SaveAsync();

        Assert.Equal(File.ReadAllBytes(Font("NotoSans-Regular.ttf")), File.ReadAllBytes(Path.Combine(folder, "fonts", "NotoSans-Regular.ttf")));
    }

    /// <summary>Held in the window until the project has a folder, and measured with all the same.</summary>
    [AvaloniaFact]
    public async Task Fonts_Join_A_Project_Never_Saved()
    {
        var window = await Host(null);

        Assert.True(await window.NewProjectAsync());

        var drawing = Path.Combine(_directory, "badge.svg");

        File.WriteAllText(drawing, ProjectFontsTests.Text("AB12", "font-family=\"Noto Sans\""));
        await window.AddDrawingsAsync(window.Workspace!.Document.Root, new[] { drawing });

        Assert.True(await window.AddFontsAsync(new[] { Font("NotoSans-Regular.ttf") }));

        var document = window.Workspace!.Document;
        var log = new List<string>();

        Assert.Null(document.Path);

        document.Export(Path.Combine(_directory, "Icons.g.cs"), SvgTextLayout.Baked, log.Add);

        Assert.DoesNotContain(log, line => line.StartsWith("warning:", StringComparison.Ordinal));

        var target = Path.Combine(_directory, "new.svgstudio");

        window.AskWhereToSave = _ => Task.FromResult<string?>(target);

        await window.SaveAsync();

        Assert.True(File.Exists(Path.Combine(target, "fonts", "NotoSans-Regular.ttf")));
    }

    [AvaloniaFact]
    public async Task A_Font_With_No_Project_Open_Says_Where_It_Belongs()
    {
        var said = new List<(string, string)>();
        var window = await Host(null, said);

        Assert.False(await window.OpenAsync(new[] { Font("NotoSans-Regular.ttf") }));

        Assert.Equal("Fonts belong to a project", Assert.Single(said).Item1);
        Assert.Empty(window.FindControl<TabControl>("Tabs")!.Items);
    }

    /// <summary>
    /// What is not a font, and a face the project has already, which it could never measure with
    /// since the first that matches is the one used: refused, and the rest added in one step.
    /// </summary>
    [AvaloniaFact]
    public async Task What_Cannot_Be_Used_Is_Refused_And_The_Rest_Added_In_One_Step()
    {
        var said = new List<(string Title, string Message)>();
        var window = await Host(Project(), said);
        var workspace = window.Workspace!;
        var pointer = Path.Combine(_directory, "Pointer.ttf");

        File.WriteAllText(pointer, "version https://git-lfs.github.com/spec/v1\n");

        Assert.False(await window.AddFontsAsync(new[] { pointer, Font("NotoSans-Regular.ttf"), Font("NotoSans-Bold.ttf"), Copied("NotoSans-Regular.ttf", "Again.ttf"), Copied("NotoSans-Italic.ttf", "Collection.ttc") }));

        Assert.Equal(new[] { "fonts/NotoSans-Bold.ttf", "fonts/NotoSans-Regular.ttf" }, workspace.Document.Fonts.Keys);
        Assert.Equal("Some fonts couldn't be added", said.Single().Title);
        Assert.Contains("Pointer.ttf is not a font Skia can read", said.Single().Message, StringComparison.Ordinal);
        Assert.Contains("Again.ttf is Noto Sans 400, which the project has already as fonts/NotoSans-Regular.ttf", said.Single().Message, StringComparison.Ordinal);
        Assert.Contains("Collection.ttc is not an .otf or a .ttf file.", said.Single().Message, StringComparison.Ordinal);

        workspace.Undo();

        Assert.Empty(workspace.Document.Fonts);
    }

    /// <summary>Another face under a name the project's fonts already have, ignoring case as macOS and Windows do.</summary>
    [AvaloniaFact]
    public async Task A_Font_Named_As_One_There_Is_Is_Named_Apart()
    {
        var window = await Host(Project());

        Assert.True(await window.AddFontsAsync(new[] { Font("NotoSans-Regular.ttf") }));
        Assert.True(await window.AddFontsAsync(new[] { Copied("NotoSans-Bold.ttf", "notosans-regular.ttf") }));

        Assert.Equal(new[] { "fonts/NotoSans-Regular.ttf", "fonts/notosans-regular~2.ttf" }, window.Workspace!.Document.Fonts.Keys);
    }

    /// <summary>A font inside the open project's own folder is not a way of opening the project again.</summary>
    [AvaloniaFact]
    public async Task A_Font_The_Package_Holds_Dropped_Again_Leaves_The_Project_Open()
    {
        var folder = Project();

        Directory.CreateDirectory(Path.Combine(folder, "fonts"));
        File.Copy(Font("NotoSans-Regular.ttf"), Path.Combine(folder, "fonts", "NotoSans-Regular.ttf"));

        var window = await Host(folder);
        var workspace = window.Workspace;

        Assert.False(await window.OpenAsync(new[] { Path.Combine(folder, "fonts", "NotoSans-Regular.ttf") }));

        Assert.Same(workspace, window.Workspace);
        Assert.Single(workspace!.Document.Fonts);
    }

    /// <summary>Listed as face and file in the project's Properties, and taken out from there; the save deletes the file.</summary>
    [AvaloniaFact]
    public async Task The_Projects_Properties_List_Its_Fonts_And_Take_One_Out()
    {
        var folder = Project();
        var window = await Host(folder);

        Assert.True(await window.AddFontsAsync(new[] { Font("NotoSans-Regular.ttf") }));
        await window.SaveAsync();

        await Board(window);

        var properties = Properties(window);

        Assert.Contains(properties.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Noto Sans 400 · NotoSans-Regular.ttf");

        Click(properties.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Tag, "fonts/NotoSans-Regular.ttf")));

        Assert.Empty(window.Workspace!.Document.Fonts);
        Assert.DoesNotContain(Properties(window).GetVisualDescendants().OfType<TextBlock>(), block => block.Text?.Contains("NotoSans-Regular.ttf", StringComparison.Ordinal) == true);

        await window.SaveAsync();

        Assert.False(Directory.Exists(Path.Combine(folder, "fonts")));
    }

    [AvaloniaFact]
    public async Task Add_Fonts_Asks_For_Files_And_Adds_Them()
    {
        var window = await Host(Project());

        window.AskForFonts = () => Task.FromResult<IReadOnlyList<string>>(new[] { Font("NotoSans-Bold.ttf") });

        await Board(window);

        Click(Properties(window).GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Add fonts…")));
        await Gestures.Until(() => window.Workspace!.Document.Fonts.Count > 0);

        Assert.Equal(new[] { "fonts/NotoSans-Bold.ttf" }, window.Workspace!.Document.Fonts.Keys);
    }

    /// <summary>Beside the project, and spelled so another machine finds it too.</summary>
    [AvaloniaFact]
    public async Task The_Fonts_Folder_Is_One_Beside_The_Project()
    {
        var window = await Host(Project());
        var panel = await Board(window);

        Assert.False(panel.Edit("fontsFolder", "/Users/somebody/fonts"));
        Assert.Contains("relative to the project's folder", panel.Fault, StringComparison.Ordinal);

        Assert.False(panel.Edit("fontsFolder", "fonts"));
        Assert.Contains("beside the project", panel.Fault, StringComparison.Ordinal);

        Assert.True(panel.Edit("fontsFolder", "../fonts"));
    }

    [AvaloniaFact]
    public async Task An_Unsaved_Project_Has_No_Folder_To_Find_A_Fonts_Folder_From()
    {
        var window = await Host(null);

        Assert.True(await window.NewProjectAsync());

        var panel = await Board(window);

        Assert.False(panel.Edit("fontsFolder", "../fonts"));
        Assert.StartsWith("Save the project first", panel.Fault, StringComparison.Ordinal);
    }

    /// <summary>A tab says which face its project's fonts lack, and stops saying it once they hold it.</summary>
    [AvaloniaFact]
    public async Task A_Tab_Names_The_Face_Its_Projects_Fonts_Lack()
    {
        var window = await Host(Project("bold"));

        Assert.True(await window.AddFontsAsync(new[] { Font("NotoSans-Regular.ttf") }));

        var viewer = await Tab(window);

        Assert.Equal(
            "'Noto Sans' 700 is not among the project's fonts (Noto Sans 400), so each machine measures it with a font of its own.",
            Note(viewer));

        Assert.True(await window.AddFontsAsync(new[] { Font("NotoSans-Bold.ttf") }));
        Dispatcher.UIThread.RunJobs();

        Assert.Null(Note(viewer));
    }

    /// <summary>A tab holding edits of its own is drawn from them again in the project's new fonts, and keeps them.</summary>
    [AvaloniaFact]
    public async Task A_Tab_With_Edits_Follows_The_Fonts_And_Keeps_Them()
    {
        var window = await Host(Project());
        var viewer = await Tab(window);
        var edited = viewer.Source!.Replace("AB12", "AB34", StringComparison.Ordinal);

        viewer.SetSource(edited);
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewer.IsSourceModified);
        Assert.True(await window.AddFontsAsync(new[] { Font("NotoSans-Regular.ttf") }));
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewer.IsSourceModified);
        Assert.Equal(edited, viewer.Source);
        Assert.IsType<CustomTypefaceProvider>(viewer.Svg!.Settings.TypefaceProviders![0]);
    }

    /// <summary>The board draws what it holds again in the project's fonts, and again without them on an undo.</summary>
    [AvaloniaFact]
    public async Task The_Board_Draws_In_The_Projects_Fonts()
    {
        var window = await Host(Project());
        var board = await Board(window);

        ITypefaceProvider First() => Gestures.Canvas(board).Placements.Single().Svg.Settings.TypefaceProviders![0];

        Assert.IsType<FontManagerTypefaceProvider>(First());

        Assert.True(await window.AddFontsAsync(new[] { Font("NotoSans-Regular.ttf") }));
        await Gestures.Until(() => First() is CustomTypefaceProvider);

        window.Workspace!.Undo();
        await Gestures.Until(() => First() is FontManagerTypefaceProvider);
    }

    /// <summary>Fonts are the project's: a drawing's Properties offer neither the list's button nor the folder.</summary>
    [AvaloniaFact]
    public async Task A_Drawings_Properties_Hold_No_Fonts()
    {
        var window = await Host(Project());
        var viewer = await Tab(window);
        var panel = Assert.IsType<GroupPanel>(Assert.Single(viewer.SidePanels).Content);

        Assert.Contains(panel.GetLogicalDescendants().OfType<TextBox>(), box => Equals(box.Tag, "class"));
        Assert.DoesNotContain(panel.GetLogicalDescendants().OfType<TextBox>(), box => Equals(box.Tag, "fontsFolder"));
        Assert.DoesNotContain(panel.GetLogicalDescendants().OfType<Button>(), button => Equals(button.Content, "Add fonts…"));
    }

    /// <summary>What an export says, it says among the export's warnings.</summary>
    [AvaloniaFact]
    public async Task Exporting_Says_Which_Faces_The_Fonts_Lack()
    {
        var said = new List<(string Title, string Message)>();
        var window = await Host(Project("bold"), said);

        Assert.True(await window.AddFontsAsync(new[] { Font("NotoSans-Regular.ttf") }));

        window.AskExportSettings = _ => Task.FromResult<IReadOnlyDictionary<string, string?>?>(new Dictionary<string, string?>());

        Assert.True(await window.ExportAsync(Path.Combine(_directory, "Icons.cs")));

        Assert.Contains(said, one => one.Message.Contains("warning: badge: 'Noto Sans' 700 is not among the project's fonts", StringComparison.Ordinal));
    }

    /// <summary>A tool called as a client calls it.</summary>
    private static async Task<string> Call(MainWindow window, string tool, AIFunctionArguments arguments)
    {
        var function = new AssistantTools(window) { Confirm = _ => Task.FromResult(true) }
            .For(small: false, docs: false)
            .OfType<AIFunction>()
            .Single(one => one.Name == tool);

        return (await function.InvokeAsync(arguments).AsTask().WaitAsync(TimeSpan.FromSeconds(30)))?.ToString() ?? string.Empty;
    }

    /// <summary>So what the assistant writes in a font-family is a face the project's fonts hold.</summary>
    [AvaloniaFact]
    public async Task The_Assistant_Reads_The_Fonts_A_Project_Measures_With()
    {
        var window = await Host(Project());

        Assert.True(await window.AddFontsAsync(new[] { Font("NotoSans-Regular.ttf") }));

        Assert.Contains("\nMeasures text with: Noto Sans 400 (fonts/NotoSans-Regular.ttf)\n", await Call(window, "get_node", new() { ["node"] = "" }), StringComparison.Ordinal);
    }

    /// <summary>A font is not something to open; the assistant is told how one joins the project instead.</summary>
    [AvaloniaFact]
    public async Task The_Assistant_Is_Told_A_Font_Joins_The_Project_Rather_Than_Opening()
    {
        var window = await Host(Project());
        var workspace = window.Workspace;
        var tabs = window.FindControl<TabControl>("Tabs")!.Items.Count;

        Assert.Contains("is a font, which is not opened", await Call(window, "open_file", new() { ["path"] = Font("NotoSans-Regular.ttf") }), StringComparison.Ordinal);

        Assert.Same(workspace, window.Workspace);
        Assert.Empty(workspace!.Document.Fonts);
        Assert.Equal(tabs, window.FindControl<TabControl>("Tabs")!.Items.Count);
    }

    [AvaloniaFact]
    public async Task The_Assistant_Sets_The_Fonts_Folder_On_The_Project_Alone()
    {
        var window = await Host(Project());

        Assert.StartsWith("Set fontsFolder on", await Call(window, "set_node", new() { ["node"] = "", ["setting"] = "fontsFolder", ["value"] = "../fonts" }), StringComparison.Ordinal);
        Assert.Equal("../fonts", window.Workspace!.Document.Root.FontsFolder);

        Assert.Contains("has no setting called 'fontsFolder'", await Call(window, "set_node", new() { ["node"] = "0", ["setting"] = "fontsFolder", ["value"] = "../fonts" }), StringComparison.Ordinal);
        Assert.Contains("relative to the project's folder", await Call(window, "set_node", new() { ["node"] = "", ["setting"] = "fontsFolder", ["value"] = "/fonts" }), StringComparison.Ordinal);
    }
}
