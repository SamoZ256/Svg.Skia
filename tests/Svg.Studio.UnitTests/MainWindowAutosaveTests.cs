using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// The copy kept of unsaved work: where it goes, when it is written, and what it is offered back
/// for.
/// </summary>
/// <remarks>
/// Driven rather than waited on. <see cref="ProjectRecovery.Now"/> is the clock and
/// <see cref="ProjectRecovery.Pulse"/> is one turn of it, so a test that would otherwise sleep for
/// half a minute moves the clock and asks for the tick.
/// </remarks>
public class MainWindowAutosaveTests : IDisposable
{
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
    private readonly string _store = ProjectRecovery.Store;
    private readonly string _settings = StudioSettings.Store;
    private readonly Func<DateTime> _clock = ProjectRecovery.Now;
    private DateTime _now = new(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);

    public MainWindowAutosaveTests()
    {
        ProjectRecovery.Store = Path.Combine(_directory, "recovery");
        ProjectRecovery.Now = () => _now;
        StudioSettings.Store = Path.Combine(_directory, "settings");
    }

    public void Dispose()
    {
        ProjectRecovery.Store = _store;
        ProjectRecovery.Now = _clock;
        StudioSettings.Store = _settings;

        Directory.Delete(_directory, recursive: true);
    }

    [AvaloniaFact]
    public async Task An_Edited_Project_Is_Copied_And_Its_Own_File_Is_Not()
    {
        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);

        Move(window);

        _now = _now.AddSeconds(31d);

        Assert.True(Recovery(window).Pulse());

        // The copy holds the work; the project holds what it held when it was opened.
        Assert.Equal(window.Workspace!.Document.ToXml(), File.ReadAllText(ProjectRecovery.For(path)));
        Assert.Equal(Project, File.ReadAllText(path));
    }

    [AvaloniaFact]
    public async Task One_Edit_Waits_The_Whole_Cooldown()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var recovery = Recovery(window);

        Move(window);

        _now = _now.AddSeconds(29d);
        Assert.False(recovery.Pulse());

        _now = _now.AddSeconds(2d);
        Assert.True(recovery.Pulse());
    }

    [AvaloniaFact]
    public async Task Work_Piling_Up_Is_Copied_Sooner_And_All_At_Once()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var recovery = Recovery(window);
        var workspace = window.Workspace!;

        // A drag on a board raises one of these per gesture, and a run of them is what the floor is
        // for: ten is already worth the least this will wait.
        for (var edit = 0; edit < 40; edit++)
        {
            workspace.Edit();
        }

        _now = _now.AddSeconds(2d);
        Assert.False(recovery.Pulse());

        _now = _now.AddSeconds(2d);
        Assert.True(recovery.Pulse());

        // One copy for the burst, not forty: the second tick has nothing left behind it.
        Assert.False(recovery.Pulse());
    }

    [AvaloniaFact]
    public void The_Cooldown_Shortens_As_Work_Piles_Up()
    {
        Assert.Equal(TimeSpan.FromSeconds(30d), ProjectRecovery.Cooldown(1));
        Assert.Equal(TimeSpan.FromSeconds(15d), ProjectRecovery.Cooldown(2));
        Assert.Equal(TimeSpan.FromSeconds(3d), ProjectRecovery.Cooldown(10));
        Assert.Equal(TimeSpan.FromSeconds(3d), ProjectRecovery.Cooldown(400));
    }

    [AvaloniaFact]
    public async Task Saving_The_Project_Takes_The_Copy_With_It()
    {
        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);

        Move(window);

        _now = _now.AddSeconds(31d);
        Assert.True(Recovery(window).Pulse());
        Assert.True(File.Exists(ProjectRecovery.For(path)));

        await window.SaveAsync();

        // The work is somewhere better now, and a copy left behind would be offered back over it.
        Assert.False(File.Exists(ProjectRecovery.For(path)));
        Assert.False(Recovery(window).IsArmed);
    }

    [AvaloniaFact]
    public async Task Closing_The_Project_Takes_The_Copy_With_It()
    {
        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);

        window.ConfirmDiscard = _ => Task.FromResult(true);

        Move(window);

        _now = _now.AddSeconds(31d);
        Assert.True(Recovery(window).Pulse());

        Assert.True(await window.CloseProjectAsync());
        Assert.False(File.Exists(ProjectRecovery.For(path)));
    }

    [AvaloniaFact]
    public async Task A_Copy_That_Differs_Is_Offered_And_Restoring_Leaves_It_Unsaved()
    {
        var path = Write("icons.svgstudio", Project);

        Kept(path, Project.Replace("scale=\"2\"", "scale=\"6\"", StringComparison.Ordinal));

        var asked = new List<string>();
        var window = Empty();

        window.ConfirmDiscardRecovery = message =>
        {
            asked.Add(message);

            return Task.FromResult(false);
        };

        await window.OpenAsync(new[] { path });
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("icons.svgstudio", Assert.Single(asked), StringComparison.Ordinal);

        // Given back, and still nobody's but the window's: the file is what it was.
        Assert.Contains("scale=\"6\"", window.Workspace!.Document.ToXml(), StringComparison.Ordinal);
        Assert.True(window.Workspace.IsEdited);
        Assert.Equal(Project, File.ReadAllText(path));
        Assert.True(File.Exists(ProjectRecovery.For(path)));
    }

    [AvaloniaFact]
    public async Task A_Copy_That_Is_Discarded_Is_Gone_And_The_File_Opens()
    {
        var path = Write("icons.svgstudio", Project);

        Kept(path, Project.Replace("scale=\"2\"", "scale=\"6\"", StringComparison.Ordinal));

        var window = Empty();

        window.ConfirmDiscardRecovery = _ => Task.FromResult(true);

        await window.OpenAsync(new[] { path });
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain("scale=\"6\"", window.Workspace!.Document.ToXml(), StringComparison.Ordinal);
        Assert.False(window.Workspace.IsEdited);
        Assert.False(File.Exists(ProjectRecovery.For(path)));
    }

    [AvaloniaFact]
    public async Task A_Copy_Saying_The_Same_Thing_Is_Not_Offered()
    {
        var path = Write("icons.svgstudio", Project);

        Kept(path, Project);

        var asked = 0;
        var window = Empty();

        window.ConfirmDiscardRecovery = _ =>
        {
            asked++;

            return Task.FromResult(true);
        };

        await window.OpenAsync(new[] { path });
        Dispatcher.UIThread.RunJobs();

        // Nothing to say, so nothing is asked — and the copy is tidied away rather than left to ask
        // the same question at every open.
        Assert.Equal(0, asked);
        Assert.False(File.Exists(ProjectRecovery.For(path)));
    }

    [AvaloniaFact]
    public async Task A_Copy_Older_Than_The_File_Is_Still_Offered()
    {
        var path = Write("icons.svgstudio", Project);

        Kept(path, Project.Replace("scale=\"2\"", "scale=\"6\"", StringComparison.Ordinal));

        File.SetLastWriteTimeUtc(ProjectRecovery.For(path), DateTime.UtcNow.AddHours(-2d));

        var asked = new List<string>();
        var window = Empty();

        window.ConfirmDiscardRecovery = message =>
        {
            asked.Add(message);

            return Task.FromResult(false);
        };

        await window.OpenAsync(new[] { path });
        Dispatcher.UIThread.RunJobs();

        // Something else wrote the file after the crash, and the copy still holds work it never saw.
        Assert.Contains("something else has written it since", Assert.Single(asked), StringComparison.Ordinal);
        Assert.Contains("scale=\"6\"", window.Workspace!.Document.ToXml(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A project with no file is not covered, and is covered the moment it has one. The copy is
    /// keyed by the project's path, and a project nobody has named has nothing to be looked up
    /// under again — a copy that cannot be found is worse than none.
    /// </summary>
    [AvaloniaFact]
    public async Task A_Project_Is_Copied_Once_It_Has_A_File()
    {
        var target = Path.Combine(_directory, "fresh.svgstudio");
        var window = Empty();

        window.AskWhereToSave = _ => Task.FromResult<string?>(target);

        await window.NewProjectAsync();
        Dispatcher.UIThread.RunJobs();

        window.Workspace!.Edit();

        _now = _now.AddSeconds(31d);

        Assert.Null(window.Recovery);

        await window.SaveAsync();

        window.Workspace.Edit();

        _now = _now.AddSeconds(31d);

        Assert.True(Recovery(window).Pulse());
        Assert.True(File.Exists(ProjectRecovery.For(target)));
    }

    [AvaloniaFact]
    public async Task Switched_Off_It_Copies_Nothing_And_Clears_What_It_Kept()
    {
        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);

        Move(window);

        _now = _now.AddSeconds(31d);
        Assert.True(Recovery(window).Pulse());

        Toggle(window);

        Assert.False(StudioSettings.Autosave);
        Assert.False(File.Exists(ProjectRecovery.For(path)));

        window.Workspace!.Edit();

        _now = _now.AddSeconds(120d);

        Assert.False(Recovery(window).Pulse());
        Assert.False(File.Exists(ProjectRecovery.For(path)));
    }

    [AvaloniaFact]
    public async Task The_Setting_Survives_A_Restart()
    {
        var window = await Host(Write("icons.svgstudio", Project));

        Toggle(window);

        var second = Empty();

        Assert.False(Item(second).IsChecked);
        Assert.False(StudioSettings.Autosave);
    }

    [AvaloniaFact]
    public async Task A_Copy_That_Cannot_Be_Written_Speaks_Once_It_Is_Not_A_Fluke()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var recovery = Recovery(window);
        var said = new List<string>();

        recovery.Trouble = (title, _) => said.Add(title);

        // A file where the directory should be: every write fails the way a full or locked disk does.
        ProjectRecovery.Store = Path.Combine(_directory, "wall");
        File.WriteAllText(ProjectRecovery.Store, string.Empty);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            window.Workspace!.Edit();

            _now = _now.AddSeconds(31d);

            Assert.False(recovery.Pulse());
        }

        // Quiet while it might have been the once, and then said — what this protects is somebody's
        // belief that their work is covered.
        Assert.Equal("Work is not being copied", Assert.Single(said));
    }

    [AvaloniaFact]
    public async Task The_Clock_Runs_Only_While_There_Is_Something_To_Copy()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var recovery = Recovery(window);

        Assert.False(recovery.IsArmed);

        Move(window);

        Assert.True(recovery.IsArmed);

        _now = _now.AddSeconds(31d);

        Assert.True(recovery.Pulse());
        Assert.False(recovery.IsArmed);
    }

    /// <summary>A copy of <paramref name="text"/> left as a crash would have left it.</summary>
    private static void Kept(string path, string text)
    {
        Directory.CreateDirectory(ProjectRecovery.Store);
        File.WriteAllText(ProjectRecovery.For(path), text);
    }

    /// <summary>One gesture: a row into the group beside it, which no tab is holding.</summary>
    private static void Move(MainWindow window)
    {
        var root = window.Workspace!.Document.Root;

        if (root.Children.OfType<ProjectGroup>().FirstOrDefault() is { } group)
        {
            Assert.True(window.Move(root.Children[0], group, ProjectDrop.Inside));
        }
        else
        {
            window.Workspace.Edit();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static ProjectRecovery Recovery(MainWindow window)
        => window.Recovery ?? throw new InvalidOperationException("The window is keeping no copy.");

    private static NativeMenuItem Item(MainWindow window)
        => NativeMenu.GetMenu(window)!.Items
            .OfType<NativeMenuItem>()
            .SelectMany(item => item.Menu?.Items.OfType<NativeMenuItem>() ?? Enumerable.Empty<NativeMenuItem>())
            .Single(item => item.Header == "Autosave Recovery");

    private static void Toggle(MainWindow window)
    {
        // The way the platform's menu bar picks one: the item's own Click is raised through the
        // seam the exporter uses, since a NativeMenuItem is not a control to be clicked.
        ((INativeMenuItemExporterEventsImplBridge)Item(window)).RaiseClicked();

        Dispatcher.UIThread.RunJobs();
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_directory, name);

        File.WriteAllText(path, text);

        return path;
    }

    private static MainWindow Empty()
    {
        var window = new MainWindow();

        window.Announce = (_, _) => Task.CompletedTask;
        window.Show();

        Dispatcher.UIThread.RunJobs();

        return window;
    }

    private static async Task<MainWindow> Host(string path)
    {
        var window = Empty();

        await window.OpenAsync(new[] { path });

        Dispatcher.UIThread.RunJobs();

        return window;
    }
}
