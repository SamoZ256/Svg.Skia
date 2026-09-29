using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using static Svg.Studio.UnitTests.ProjectGitTests;

namespace Svg.Studio.UnitTests;

/// <summary>
/// The changes panel driven through the window: making a repository, what has changed, branches,
/// and a merge that stops on the project.
/// </summary>
[Collection("settings")]
public class ChangesPanelTests : IDisposable
{
    private const string Project = """
        <studio namespace="Demo.Icons">
          <drawing name="home">
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect width="24" height="24" fill="#00ff00" />
            </svg>
          </drawing>
        </studio>
        """;

    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;
    private readonly string _settings = StudioSettings.Store;
    private readonly string _store = ProjectRecovery.Store;
    private readonly List<string> _announced = new();

    public ChangesPanelTests()
    {
        StudioSettings.Store = Path.Combine(_directory, "settings");
        ProjectRecovery.Store = Path.Combine(_directory, "recovery");
    }

    public void Dispose()
    {
        StudioSettings.Store = _settings;
        ProjectRecovery.Store = _store;

        Scratch.Delete(_directory);
    }

    private string Work => Path.Combine(_directory, "work");

    private string File => Path.Combine(Work, "icons.svgstudio");

    [AvaloniaFact]
    public async Task The_Panel_Is_Arranged_For_A_Saved_Project_Whenever_There_Is_A_Git()
    {
        Directory.CreateDirectory(Work);
        System.IO.File.WriteAllText(File, Project);

        var window = await Host();

        Assert.Contains("tree+changes/1.4/tree/open", window.Layout, StringComparison.Ordinal);
        Assert.Null(window.Changes.Git);

        await window.CloseProjectAsync();
        Assert.DoesNotContain("changes", window.Layout, StringComparison.Ordinal);

        var installed = ProjectGit.Installed;
        ProjectGit.Installed = null;

        try
        {
            await window.OpenAsync(new[] { File });
            Dispatcher.UIThread.RunJobs();

            Assert.DoesNotContain("changes", window.Layout, StringComparison.Ordinal);
        }
        finally
        {
            ProjectGit.Installed = installed;
        }

        window.Close();
    }

    [AvaloniaFact]
    public async Task Creating_A_Repository_Commits_The_Project_And_Its_Attributes()
    {
        Directory.CreateDirectory(Work);
        System.IO.File.WriteAllText(File, Project);
        System.IO.File.WriteAllText(Path.Combine(Work, "notes.txt"), "not part of it");

        var window = await Host();

        await window.Changes.CreateRepository();
        Assert.NotNull(window.Changes.Git);
        Assert.Equal(ProjectChangeKind.Added, Assert.Single(window.Changes.Changes).Kind);

        Configure(Work);
        await window.Changes.Commit("Add the icons");

        Assert.Equal(".gitattributes\nicons.svgstudio", Git(Work, "ls-tree", "--name-only", "HEAD").Trim());
        Assert.Empty(window.Changes.Changes);
        Assert.Empty(_announced);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Project_Committed_Where_Checkout_Writes_Crlf_Is_Unchanged()
    {
        Committed();
        Git(Work, "config", "core.autocrlf", "true");

        var window = await Host();

        Assert.Contains("\r\n", await window.Changes.Git!.At("HEAD"), StringComparison.Ordinal);
        Assert.DoesNotContain("\r\n", System.IO.File.ReadAllText(File), StringComparison.Ordinal);
        Assert.Empty(window.Changes.Changes);

        window.Close();
    }

    [AvaloniaFact]
    public async Task An_Unsaved_Edit_Is_A_Changed_Row_And_A_Dot_On_The_Tree()
    {
        Committed();

        var window = await Host();
        Assert.Empty(window.Changes.Changes);

        var workspace = window.Workspace!;
        var drawing = workspace.Document.Root.Drawings.Single();

        workspace.Do(
            "redraw",
            () => ProjectSnapshot.Text(drawing),
            () => drawing.SetText(drawing.Text.Replace("#00ff00", "#0000ff", StringComparison.Ordinal)));
        Dispatcher.UIThread.RunJobs();

        var change = Assert.Single(window.Changes.Changes);
        Assert.Equal(ProjectChangeKind.Changed, change.Kind);
        Assert.Same(drawing, change.After);

        // The header is still the label, which the search and every test reading a row go by.
        var row = Rows(window).Single(item => ReferenceEquals(item.Tag, drawing));
        Assert.Equal("home", row.Header);
        Assert.NotNull(row.HeaderTemplate);
        Assert.Null(Rows(window).Single(item => item.Tag is ProjectRoot).HeaderTemplate);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Switching_Branch_Reopens_The_Project_As_That_Branch_Has_It()
    {
        Committed();
        Git(Work, "switch", "-c", "other");
        System.IO.File.WriteAllText(File, Added(Project, "star"));
        Git(Work, "commit", "-am", "Add a star");
        Git(Work, "switch", "main");

        var window = await Host();
        Assert.DoesNotContain(window.Workspace!.Document.Root.Drawings, drawing => drawing.Name == "star");

        await window.Changes.Switch("other");

        Assert.Contains(window.Workspace!.Document.Root.Drawings, drawing => drawing.Name == "star");
        Assert.Equal("other", (await window.Changes.Git!.Status()).Branch);
        Assert.Empty(_announced);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Two_Drawings_Added_At_The_End_Merge_Without_Asking_And_Commit()
    {
        Diverged(Added(Project, "moon"), Added(Project, "star"));

        var window = await Host();
        var asked = 0;
        window.ResolveConflicts = _ =>
        {
            asked++;
            return Task.FromResult<bool?>(null);
        };

        await window.Changes.Merge("other");

        Assert.Equal(0, asked);
        Assert.Equal(new[] { "home", "moon", "star" }, window.Workspace!.Document.Root.Drawings.Select(drawing => drawing.Name));
        Assert.True(await window.Changes.Git!.InMerge());

        await window.Changes.Commit("Merge other");

        Assert.False(await window.Changes.Git!.InMerge());
        Assert.Equal(3, Git(Work, "rev-list", "--parents", "-n1", "HEAD").Trim().Split(' ').Length);
        Assert.Empty(_announced);

        window.Close();
    }

    [AvaloniaFact]
    public async Task One_Drawing_Changed_On_Both_Sides_Is_Asked_About_And_Theirs_Is_Kept()
    {
        Diverged(Recoloured("#ff0000"), Recoloured("#0000ff"));

        var window = await Host();
        var asked = 0;
        window.ResolveConflicts = merge =>
        {
            asked++;

            foreach (var conflict in merge.Conflicts)
            {
                conflict.Choice = ProjectSide.Theirs;
            }

            return Task.FromResult<bool?>(true);
        };

        await window.Changes.Merge("other");

        Assert.Equal(1, asked);
        Assert.Contains("#0000ff", ProjectDocument.Load(File).Root.Drawings.Single().Text, StringComparison.Ordinal);
        Assert.Contains("#0000ff", window.Workspace!.Document.Root.Drawings.Single().Text, StringComparison.Ordinal);

        await window.Changes.Commit("Merge other");

        Assert.Equal(3, Git(Work, "rev-list", "--parents", "-n1", "HEAD").Trim().Split(' ').Length);
        Assert.Empty(_announced);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Save_Is_Refused_While_Conflicted_And_Aborting_Puts_The_File_Back()
    {
        Diverged(Recoloured("#ff0000"), Recoloured("#0000ff"));

        var window = await Host();
        window.ResolveConflicts = _ => Task.FromResult<bool?>(null);

        await window.Changes.Merge("other");
        Assert.True(await window.Changes.Conflicted());

        window.Workspace!.Edit();
        await window.SaveAsync();

        Assert.Equal(new[] { "Resolve the merge first" }, _announced);
        Assert.Contains("<<<<<<<", System.IO.File.ReadAllText(File), StringComparison.Ordinal);

        // Resolving would write the file under the unsaved edit, which a save would then put back.
        var asked = 0;
        window.ResolveConflicts = _ =>
        {
            asked++;
            return Task.FromResult<bool?>(true);
        };
        await window.Changes.Resolve();

        Assert.Equal(0, asked);
        Assert.Equal(new[] { "Resolve the merge first", "Discard before resolving" }, _announced);
        Assert.True(await window.Changes.Conflicted());

        // An abort puts back our side, which is what the window holds, so the edit is kept.
        await window.Changes.AbortMerge();

        Assert.False(await window.Changes.Git!.InMerge());
        Assert.Contains("#ff0000", window.Workspace!.Document.Root.Drawings.Single().Text, StringComparison.Ordinal);
        Assert.Equal(2, _announced.Count);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_File_Left_Conflicted_By_A_Rebase_Is_Left_To_Git()
    {
        Diverged(Recoloured("#ff0000"), Recoloured("#0000ff"));
        Assert.NotEqual(0, Exit(Work, "rebase", "other"));

        var window = Empty();
        var asked = 0;
        window.ResolveConflicts = _ =>
        {
            asked++;
            return Task.FromResult<bool?>(true);
        };

        await window.OpenAsync(new[] { File });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, asked);
        Assert.Equal(new[] { "A rebase is in progress" }, _announced);
        Assert.Contains("<<<<<<<", System.IO.File.ReadAllText(File), StringComparison.Ordinal);

        window.Close();
    }

    [AvaloniaFact]
    public async Task An_Unsaved_Edit_Makes_A_Typed_Message_Committable()
    {
        Committed();

        var window = await Host();
        var controls = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window.Changes).ToList();
        var message = controls.OfType<TextBox>().Single(box => box.PlaceholderText == "Commit message");
        var commit = controls.OfType<Button>().Single(button => Equals(button.Content, "Commit"));

        message.Text = "Fix colour";
        Assert.False(commit.IsEnabled);

        var drawing = window.Workspace!.Document.Root.Drawings.Single();
        window.Workspace.Do(
            "redraw",
            () => ProjectSnapshot.Text(drawing),
            () => drawing.SetText(drawing.Text.Replace("#00ff00", "#0000ff", StringComparison.Ordinal)));
        Dispatcher.UIThread.RunJobs();

        Assert.True(commit.IsEnabled);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_File_Left_Conflicted_By_A_Merge_Elsewhere_Is_Resolved_On_Opening()
    {
        Diverged(Recoloured("#ff0000"), Recoloured("#0000ff"));
        Assert.Contains("CONFLICT", MergeInATerminal(), StringComparison.Ordinal);

        var window = Empty();
        var asked = 0;
        window.ResolveConflicts = merge =>
        {
            asked++;
            merge.Conflicts.Single().Choice = ProjectSide.Theirs;
            return Task.FromResult<bool?>(true);
        };

        await window.OpenAsync(new[] { File });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, asked);
        Assert.Contains("#0000ff", window.Workspace!.Document.Root.Drawings.Single().Text, StringComparison.Ordinal);
        Assert.Empty(_announced);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Pull_Of_Diverged_Branches_Offers_A_Merge()
    {
        Committed();
        var remote = Path.Combine(_directory, "remote.git");
        Git(_directory, "init", "--bare", "-b", "main", remote);
        Git(Work, "remote", "add", "origin", remote);
        Git(Work, "push", "-u", "origin", "main");

        var other = Path.Combine(_directory, "other");
        Git(_directory, "clone", "--config", "core.autocrlf=false", remote, other);
        Configure(other);
        System.IO.File.WriteAllText(Path.Combine(other, "icons.svgstudio"), Added(Project, "star"));
        Git(other, "commit", "-am", "Add a star");
        Git(other, "push");

        System.IO.File.WriteAllText(File, Added(Project, "moon"));
        Git(Work, "commit", "-am", "Add a moon");

        var window = await Host();
        var offered = new List<string>();
        window.Changes.Confirm = (title, _, _) =>
        {
            offered.Add(title);
            return Task.FromResult(true);
        };

        await window.Changes.Pull();

        Assert.Equal(new[] { "The branches have diverged" }, offered);
        Assert.Equal(new[] { "home", "moon", "star" }, window.Workspace!.Document.Root.Drawings.Select(drawing => drawing.Name));
        Assert.True(await window.Changes.Git!.InMerge());
        Assert.Empty(_announced);

        window.Close();
    }

    [AvaloniaFact]
    public void Compare_And_Merge_Draw_Both_Sides()
    {
        var before = ProjectDocument.Parse(Recoloured("#ff0000"), "icons.svgstudio");
        var after = ProjectDocument.Parse(Recoloured("#0000ff"), "icons.svgstudio");

        var compare = new ProjectCompareWindow(Assert.Single(ProjectChanges.Compare(before, after)));
        compare.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, Pictures(compare).Count(picture => picture.Picture is { }));
        compare.Close();

        var merging = new ProjectMergeWindow(ProjectMerge.Of(ProjectDocument.Parse(Project, "icons.svgstudio"), before, after));
        merging.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, Pictures(merging).Count(picture => picture.Picture is { }));
        merging.Close();
    }

    private static IEnumerable<Avalonia.Controls.Skia.SKPictureControl> Pictures(Window window)
        => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.Skia.SKPictureControl>();

    /// <summary>A repository on <c>main</c> with the project committed.</summary>
    private void Committed()
    {
        Directory.CreateDirectory(Work);
        Git(Work, "init", "-b", "main");
        Configure(Work);
        System.IO.File.WriteAllText(File, Project);
        Git(Work, "add", "icons.svgstudio");
        Git(Work, "commit", "-m", "Add the icons");
    }

    /// <summary><c>main</c> checked out with <paramref name="ours"/> committed, and <c>other</c> with <paramref name="theirs"/>.</summary>
    private void Diverged(string ours, string theirs)
    {
        Committed();
        Git(Work, "switch", "-c", "other");
        System.IO.File.WriteAllText(File, theirs);
        Git(Work, "commit", "-am", "Theirs");
        Git(Work, "switch", "main");
        System.IO.File.WriteAllText(File, ours);
        Git(Work, "commit", "-am", "Ours");
    }

    private static string Recoloured(string fill) => Project.Replace("#00ff00", fill, StringComparison.Ordinal);

    /// <summary>A merge that stops, which the test helper would count as a failure.</summary>
    private string MergeInATerminal()
    {
        var start = new System.Diagnostics.ProcessStartInfo(ProjectGit.Installed!)
        {
            WorkingDirectory = Work,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "merge", "--no-edit", "other" }
        };

        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();

        return output;
    }

    private MainWindow Empty()
    {
        var window = new MainWindow();

        window.Announce = (title, _) =>
        {
            _announced.Add(title);
            return Task.CompletedTask;
        };
        window.Show();

        Dispatcher.UIThread.RunJobs();

        return window;
    }

    private async Task<MainWindow> Host()
    {
        var window = Empty();

        await window.OpenAsync(new[] { File });
        Dispatcher.UIThread.RunJobs();

        return window;
    }

    private static IEnumerable<TreeViewItem> Rows(MainWindow window)
    {
        IEnumerable<TreeViewItem> Under(TreeViewItem item)
            => new[] { item }.Concat(item.Items.OfType<TreeViewItem>().SelectMany(Under));

        return window.FindControl<TreeView>("ProjectTree")!.Items.OfType<TreeViewItem>().SelectMany(Under);
    }
}
