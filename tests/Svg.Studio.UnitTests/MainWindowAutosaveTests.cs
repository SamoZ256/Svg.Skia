using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
[Collection("settings")]
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

        Scratch.Delete(_directory);
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
        Assert.Equal(TestPackage.Dump(window.Workspace!.Document), TestPackage.Dump(ProjectDocument.Read(ProjectRecovery.Read(ProjectRecovery.For(path))!, null)));
        Assert.Equal(TestPackage.Normal(Project), TestPackage.Read(path));
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

    /// <summary>
    /// A font added since the save is in the copy, and the fonts already saved are not copied again
    /// but taken from the folder: the copy is written every few seconds.
    /// </summary>
    [AvaloniaFact]
    public async Task A_Font_Added_Since_The_Save_Is_Copied_And_Restored()
    {
        var path = Write("icons.svgstudio", Project);

        Directory.CreateDirectory(Path.Combine(path, "fonts"));
        File.Copy(ProjectFontsTests.Font("NotoSans-Regular.ttf"), Path.Combine(path, "fonts", "NotoSans-Regular.ttf"));

        var window = await Host(path);

        Assert.True(await window.AddFontsAsync(new[] { ProjectFontsTests.Font("NotoSans-Bold.ttf") }));

        _now = _now.AddSeconds(31d);
        Assert.True(Recovery(window).Pulse());

        using (var zip = System.IO.Compression.ZipFile.OpenRead(ProjectRecovery.For(path)))
        {
            Assert.Equal(0, zip.GetEntry("fonts/NotoSans-Regular.ttf")!.Length);
            Assert.Equal(new FileInfo(ProjectFontsTests.Font("NotoSans-Bold.ttf")).Length, zip.GetEntry("fonts/NotoSans-Bold.ttf")!.Length);
        }

        // Opened again as after a crash, with the copy still there and nothing else changed.
        var asked = 0;
        var again = Empty();

        again.ConfirmDiscardRecovery = _ =>
        {
            asked++;

            return Task.FromResult(false);
        };

        await again.OpenAsync(new[] { path });
        Dispatcher.UIThread.RunJobs();

        var fonts = again.Workspace!.Document.Fonts;

        Assert.Equal(1, asked);
        Assert.True(again.Workspace.IsEdited);
        Assert.Equal(new[] { "fonts/NotoSans-Bold.ttf", "fonts/NotoSans-Regular.ttf" }, fonts.Keys);
        Assert.True(fonts["fonts/NotoSans-Bold.ttf"].Span.SequenceEqual(File.ReadAllBytes(ProjectFontsTests.Font("NotoSans-Bold.ttf"))));
        Assert.True(fonts["fonts/NotoSans-Regular.ttf"].Span.SequenceEqual(File.ReadAllBytes(ProjectFontsTests.Font("NotoSans-Regular.ttf"))));
    }

    /// <summary>A copy an older Studio kept has no fonts in it, which is not the same as the project having none.</summary>
    [AvaloniaFact]
    public async Task A_Copy_Without_Fonts_Keeps_The_Folders()
    {
        var path = Write("icons.svgstudio", Project);

        Directory.CreateDirectory(Path.Combine(path, "fonts"));
        File.Copy(ProjectFontsTests.Font("NotoSans-Regular.ttf"), Path.Combine(path, "fonts", "NotoSans-Regular.ttf"));
        Kept(path, Project.Replace("scale=\"2\"", "scale=\"6\"", StringComparison.Ordinal));

        var window = Empty();

        window.ConfirmDiscardRecovery = _ => Task.FromResult(false);

        await window.OpenAsync(new[] { path });
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("scale=\"6\"", TestPackage.Dump(window.Workspace!.Document), StringComparison.Ordinal);
        Assert.Equal(new[] { "fonts/NotoSans-Regular.ttf" }, window.Workspace.Document.Fonts.Keys);
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
        Assert.Contains("scale=\"6\"", TestPackage.Dump(window.Workspace!.Document), StringComparison.Ordinal);
        Assert.True(window.Workspace.IsEdited);
        Assert.Equal(TestPackage.Normal(Project), TestPackage.Read(path));
        Assert.True(File.Exists(ProjectRecovery.For(path)));
    }

    /// <remarks>Parsed from the copy, it had read nothing from the file, so a pull after the restore went unseen and the tools' save wrote over it.</remarks>
    [AvaloniaFact]
    public async Task A_Restored_Copy_Still_Sees_The_File_Change_Under_It()
    {
        var path = Write("icons.svgstudio", Project);

        Kept(path, Project.Replace("scale=\"2\"", "scale=\"6\"", StringComparison.Ordinal));

        var window = Empty();

        window.ConfirmDiscardRecovery = _ => Task.FromResult(false);

        await window.OpenAsync(new[] { path });
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.Workspace!.Document.ChangedOnDisk());

        TestPackage.Write(path, Project.Replace("#00ff00", "#0000ff", StringComparison.Ordinal));

        Assert.True(window.Workspace.Document.ChangedOnDisk());
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

        Assert.DoesNotContain("scale=\"6\"", TestPackage.Dump(window.Workspace!.Document), StringComparison.Ordinal);
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
        Assert.Contains("scale=\"6\"", TestPackage.Dump(window.Workspace!.Document), StringComparison.Ordinal);
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

        window.Workspace!.Edit();

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

        await Toggle(window);

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

        await Toggle(window);

        Assert.False(StudioSettings.Autosave);

        // A second window reads the file rather than anything the first one is holding.
        var settings = new SettingsWindow();

        settings.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.False(settings.Autosave.IsChecked);

        settings.Close();
    }

    /// <summary>
    /// Settings looked at and closed with nothing touched keeps the copies. What throws them away is
    /// the setting going off, not the window having been open.
    /// </summary>
    [AvaloniaFact]
    public async Task Settings_Closed_With_Nothing_Touched_Keeps_The_Copies()
    {
        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);

        Move(window);

        _now = _now.AddSeconds(31d);
        Assert.True(Recovery(window).Pulse());

        window.ShowSettings = () => Task.CompletedTask;

        await window.ShowSettingsAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.True(StudioSettings.Autosave);
        Assert.True(File.Exists(ProjectRecovery.For(path)));
    }

    /// <summary>
    /// The window writes the setting as the box is ticked, and the host asks it again afterwards —
    /// which is what makes switching the copies off throw away the ones already kept.
    /// </summary>
    [AvaloniaFact]
    public void The_Settings_Window_Writes_The_Setting_As_It_Is_Switched()
    {
        var window = new SettingsWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.Autosave.IsChecked);

        window.Autosave.IsChecked = false;
        Dispatcher.UIThread.RunJobs();

        Assert.False(StudioSettings.Autosave);

        window.Autosave.IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(StudioSettings.Autosave);

        window.Close();
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
    internal static void Kept(string path, string text)
    {
        Directory.CreateDirectory(ProjectRecovery.Store);

        using var zip = System.IO.Compression.ZipFile.Open(ProjectRecovery.For(path), System.IO.Compression.ZipArchiveMode.Create);

        foreach (var (relative, file) in TestPackage.From(text))
        {
            using var entry = new StreamWriter(zip.CreateEntry(relative).Open());

            entry.Write(file);
        }
    }

    /// <summary>One gesture: a row into the group beside it, which no tab is holding.</summary>
    private static void Move(MainWindow window)
    {
        var root = window.Workspace!.Document.Root;

        if (root.Children.OfType<ProjectGroup>().FirstOrDefault() is { } group)
        {
            Assert.True(window.Move(root.Children[0], group));
        }
        else
        {
            window.Workspace.Edit();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static ProjectRecovery Recovery(MainWindow window)
        => window.Recovery ?? throw new InvalidOperationException("The window is keeping no copy.");

    /// <summary>Switches the copies off the way somebody would: in the settings, and out again.</summary>
    /// <remarks>
    /// Through the window's own seam rather than a real settings window, which a headless run
    /// cannot show — what the window does to the setting is tested on the window itself.
    /// </remarks>
    private static async Task Toggle(MainWindow window)
    {
        window.ShowSettings = () =>
        {
            StudioSettings.Autosave = !StudioSettings.Autosave;

            return Task.CompletedTask;
        };

        await window.ShowSettingsAsync();

        Dispatcher.UIThread.RunJobs();
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_directory, name);

        if (name.EndsWith(".svgstudio", StringComparison.Ordinal))
        {
            return TestPackage.Write(path, text);
        }

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
