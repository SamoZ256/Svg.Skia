using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.AI;
using Svg.Skia.TypefaceProviders;
using Svg.Viewer.Skia.Avalonia;
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

    private string Home => Path.Combine(File, "home.svg");

    [AvaloniaFact]
    public async Task The_Panel_Is_Arranged_For_A_Saved_Project_Whenever_There_Is_A_Git()
    {
        Put(Project);

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
    public async Task Creating_A_Repository_Commits_The_Project()
    {
        Put(Project);
        System.IO.File.WriteAllText(Path.Combine(Work, "notes.txt"), "not part of it");

        var window = await Host();

        await window.Changes.CreateRepository();
        Assert.NotNull(window.Changes.Git);
        Assert.Equal(ProjectChangeKind.Added, Assert.Single(window.Changes.Changes).Kind);

        Configure(Work);
        await window.Changes.Commit("Add the icons");

        Assert.Equal("icons.svgstudio", Git(Work, "ls-tree", "--name-only", "HEAD").Trim());
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

        Assert.Contains("\r\n", (await window.Changes.Git!.Files("HEAD"))!["home.svg"], StringComparison.Ordinal);
        Assert.DoesNotContain("\r\n", System.IO.File.ReadAllText(Home), StringComparison.Ordinal);
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

    /// <summary>The project's fonts into its package, as a file manager would put them there.</summary>
    private void Hold(string font, string name)
    {
        Directory.CreateDirectory(Path.Combine(File, "fonts"));
        System.IO.File.Copy(ProjectFontsTests.Font(font), Path.Combine(File, "fonts", name), overwrite: true);
    }

    /// <summary>An added font is a change of the project's, until the commit that takes it.</summary>
    [AvaloniaFact]
    public async Task An_Added_Font_Is_A_Change_Until_It_Is_Committed()
    {
        Committed();

        var window = await Host();

        Assert.True(await window.AddFontsAsync(new[] { ProjectFontsTests.Font("NotoSans-Regular.ttf") }));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(ProjectChangeKind.Settings, Assert.Single(window.Changes.Changes).Kind);

        await window.SaveAsync();
        await window.Changes.Commit("Add a font");

        Assert.Contains("icons.svgstudio/fonts/NotoSans-Regular.ttf", Git(Work, "ls-tree", "-r", "--name-only", "HEAD"), StringComparison.Ordinal);
        Assert.Empty(window.Changes.Changes);

        window.Close();
    }

    /// <summary>A branch that differs only in a font is still another project to switch to.</summary>
    [AvaloniaFact]
    public async Task Switching_To_A_Branch_That_Changes_Only_A_Font_Reopens_The_Project()
    {
        Committed();
        Git(Work, "switch", "-c", "other");
        Hold("NotoSans-Regular.ttf", "NotoSans-Regular.ttf");
        Git(Work, "add", "-A");
        Git(Work, "commit", "-m", "Add a font");
        Git(Work, "switch", "main");

        var window = await Host();

        Assert.Empty(window.Workspace!.Document.Fonts);

        await window.Changes.Switch("other");

        Assert.Equal(new[] { "fonts/NotoSans-Regular.ttf" }, window.Workspace!.Document.Fonts.Keys);

        window.Close();
    }

    /// <summary>
    /// A font is not merged here: Studio says how to keep one side with git, before anything is
    /// taken, so the merge is still whole to finish there.
    /// </summary>
    [AvaloniaFact]
    public async Task A_Font_Both_Sides_Changed_Is_Left_To_Git_By_Name()
    {
        Committed();
        Hold("NotoSans-Regular.ttf", "Face.ttf");
        Git(Work, "add", "-A");
        Git(Work, "commit", "-m", "A font");
        Git(Work, "switch", "-c", "other");
        Hold("NotoSans-Bold.ttf", "Face.ttf");
        Git(Work, "commit", "-am", "Theirs");
        Git(Work, "switch", "main");
        Hold("NotoSans-Italic.ttf", "Face.ttf");
        Git(Work, "commit", "-am", "Ours");

        var window = await Host();
        var said = new List<string>();
        var asked = Asked(window);

        window.Announce = (_, message) =>
        {
            said.Add(message);

            return Task.CompletedTask;
        };

        Assert.Contains("CONFLICT", MergeInATerminal(), StringComparison.Ordinal);

        await window.Changes.Resolve();

        Assert.Contains(said, message => message.Contains("fonts/Face.ttf is a font both sides changed", StringComparison.Ordinal)
                                         && message.Contains("git checkout --ours -- \"icons.svgstudio/fonts/Face.ttf\"", StringComparison.Ordinal));
        Assert.Equal(0, asked());
        Assert.True(await window.Changes.Git!.InMerge());

        window.Close();
    }

    /// <summary>A conflict's sides are lone files of no project, drawn with the fonts the project committed.</summary>
    [AvaloniaFact]
    public async Task A_Merge_Draws_Both_Sides_With_The_Projects_Fonts()
    {
        Committed();
        Hold("NotoSans-Regular.ttf", "NotoSans-Regular.ttf");
        Git(Work, "add", "-A");
        Git(Work, "commit", "-m", "A font");
        Git(Work, "switch", "-c", "other");
        System.IO.File.WriteAllText(Home, System.IO.File.ReadAllText(Home).Replace("#00ff00", "#0000ff", StringComparison.Ordinal));
        Git(Work, "commit", "-am", "Theirs");
        Git(Work, "switch", "main");
        System.IO.File.WriteAllText(Home, System.IO.File.ReadAllText(Home).Replace("#00ff00", "#ff0000", StringComparison.Ordinal));
        Git(Work, "commit", "-am", "Ours");

        var window = await Host();
        ProjectMerge? offered = null;

        window.ResolveConflicts = merge =>
        {
            offered = merge;

            return Task.FromResult<bool?>(null);
        };

        Assert.Contains("CONFLICT", MergeInATerminal(), StringComparison.Ordinal);

        await window.Changes.Resolve();

        Assert.NotNull(offered);
        Assert.Equal("Noto Sans", Assert.IsType<CustomTypefaceProvider>(offered!.Typefaces![0]).FamilyName);

        window.Close();
    }

    /// <summary>What get_changes says of a font, which the project's settings row stands for.</summary>
    [AvaloniaFact]
    public async Task The_Assistant_Is_Told_Which_Font_Was_Added()
    {
        Committed();

        var window = await Host();

        Assert.True(await window.AddFontsAsync(new[] { ProjectFontsTests.Font("NotoSans-Regular.ttf") }));
        Dispatcher.UIThread.RunJobs();

        var changes = await Call(window, "get_changes", new());

        Assert.Contains("adds the font fonts/NotoSans-Regular.ttf", changes, StringComparison.Ordinal);
        Assert.DoesNotContain("templates", changes, StringComparison.Ordinal);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Switching_Branch_Reopens_The_Project_As_That_Branch_Has_It()
    {
        Committed();
        Git(Work, "switch", "-c", "other");
        Put(Added(Project, "star"));
        Git(Work, "add", "-A");
        Git(Work, "commit", "-m", "Add a star");
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
    public async Task Two_Drawings_Added_On_Two_Branches_Merge_Without_Asking()
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

        // Git's own merge is the whole of it: a drawing each is a file each, so nothing is left to settle.
        Assert.Equal(0, asked);
        Assert.Equal(new[] { "home", "moon", "star" }, window.Workspace!.Document.Root.Drawings.Select(drawing => drawing.Name));
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
    public async Task A_Drawing_Both_Sides_Added_Under_One_Name_Can_Be_Kept_Twice()
    {
        Diverged(Added(Project, "star"), Added(Project, "star").Replace("height=\"24\" />", "height=\"48\" />", StringComparison.Ordinal));

        var window = await Host();
        window.ResolveConflicts = merge =>
        {
            var conflict = merge.Conflicts.Single();

            Assert.True(conflict.IsAddition);
            conflict.Choice = ProjectSide.Both;

            return Task.FromResult<bool?>(true);
        };

        await window.Changes.Merge("other");

        Assert.True(System.IO.File.Exists(Path.Combine(File, "star~2.svg")));
        Assert.Equal(new[] { "home", "star", "star" }, window.Workspace!.Document.Root.Drawings.Select(drawing => drawing.Name));

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
        Assert.Contains("<<<<<<<", System.IO.File.ReadAllText(Home), StringComparison.Ordinal);

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
    public async Task A_Tool_Neither_Saves_Nor_Commits_Mid_Merge()
    {
        Diverged(Recoloured("#ff0000"), Recoloured("#0000ff"));

        var window = await Host();

        // Where the panel does not see it, so only git asked now knows.
        Assert.Contains("CONFLICT", MergeInATerminal(), StringComparison.Ordinal);

        var committing = await Call(window, "git_commit", new() { ["message"] = "Merge other" });

        Assert.Contains("in conflict", committing, StringComparison.Ordinal);
        Assert.DoesNotContain("<<<<<<<", Git(Work, "show", "HEAD:icons.svgstudio/home.svg"), StringComparison.Ordinal);
        Assert.True(await window.Changes.Git!.InMerge());

        // Unedited, so nothing would be written, and the window is still not what the file holds.
        var saving = await Call(window, "save", new());

        Assert.Contains("in conflict", saving, StringComparison.Ordinal);
        Assert.Contains("<<<<<<<", System.IO.File.ReadAllText(Home), StringComparison.Ordinal);
        Assert.Empty(_announced);

        window.Close();
    }

    [AvaloniaFact]
    public async Task The_Context_Says_A_Merge_Part_Way_Through_Holds_Saving_And_Committing_Back()
    {
        Diverged(Recoloured("#ff0000"), Recoloured("#0000ff"));

        var window = await Host();
        var before = await new AssistantTools(window).ContextAsync();

        // Where the panel does not see it, as a client's call arrives while Studio is in the background.
        Assert.Contains("CONFLICT", MergeInATerminal(), StringComparison.Ordinal);

        var during = await new AssistantTools(window).ContextAsync();

        Assert.Contains("\nGit: main", before, StringComparison.Ordinal);
        Assert.DoesNotContain("merge", before, StringComparison.Ordinal);
        Assert.Contains("\nGit: main; the project is part way through a merge or a rebase, so save and git_commit refuse until it is finished or aborted.", during, StringComparison.Ordinal);
        Assert.Empty(_announced);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Commit_From_The_Panel_Announces_Why_It_Was_Not_Made()
    {
        Committed();

        var window = await Host();

        // Git's refusal, here of nothing to commit, and then the project's, of a drawing it will not take.
        Assert.False(await window.Changes.Commit("Again"));

        await window.ShowAsync(window.Workspace!.Document.Root.Children[0]);
        Dispatcher.UIThread.RunJobs();
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<SvgViewer>().First().SetSource("<div/>");

        Assert.False(await window.Changes.Commit("Broken"));
        Assert.Equal(new[] { "The commit failed", "The drawing couldn't be saved" }, _announced);
        Assert.Equal("1", Git(Work, "rev-list", "--count", "HEAD").Trim());

        window.ConfirmDiscard = _ => Task.FromResult(true);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Tool_Commit_Is_Refused_Without_A_Message_Or_While_Git_Is_Running()
    {
        Committed();

        // Fetching for ever, until the time limit stops it: the # makes a comment of the arguments.
        Git(Work, "config", "core.sshCommand", "sleep 60 #");
        Git(Work, "remote", "add", "origin", "ssh://example.invalid/icons.git");
        Git(Work, "config", "branch.main.remote", "origin");
        Git(Work, "config", "branch.main.merge", "refs/heads/main");

        var window = await Host();
        var blank = await Call(window, "git_commit", new() { ["message"] = "  " });
        var timeout = ProjectGit.RemoteTimeout;

        ProjectGit.RemoteTimeout = TimeSpan.FromSeconds(2);

        try
        {
            // Busy from the call itself, before anything is awaited.
            var pulling = window.Changes.Pull();
            var running = await Call(window, "git_commit", new() { ["message"] = "During the pull" });

            Assert.Contains("still running", running, StringComparison.Ordinal);
            Assert.Empty(_announced);

            await pulling;
        }
        finally
        {
            ProjectGit.RemoteTimeout = timeout;
        }

        Assert.Contains("needs a message", blank, StringComparison.Ordinal);
        Assert.Equal("1", Git(Work, "rev-list", "--count", "HEAD").Trim());

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
        Assert.Contains("<<<<<<<", System.IO.File.ReadAllText(Home), StringComparison.Ordinal);

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

    /// <remarks>The merge window is a modal the client cannot answer, so the tool is told instead, and the window offers it to the person.</remarks>
    [AvaloniaFact]
    public async Task A_Tool_Opening_A_Project_Git_Stopped_A_Merge_In_Says_So_And_Opens_No_Merge_Window()
    {
        Diverged(Recoloured("#ff0000"), Recoloured("#0000ff"));
        Assert.Contains("CONFLICT", MergeInATerminal(), StringComparison.Ordinal);

        var window = Empty();
        var asked = Asked(window);

        var opened = await Call(window, "open_file", new() { ["path"] = File });

        Assert.Equal(0, asked());
        Assert.Equal("icons.svgstudio is part way through a merge or a rebase, and can't be opened until it is finished or aborted: open it in Studio, which offers to resolve a merge, or finish it with git.", opened);
        Assert.Null(window.Workspace);
        Assert.Contains("<<<<<<<", System.IO.File.ReadAllText(Home), StringComparison.Ordinal);
        Assert.Empty(_announced);

        window.Close();
    }

    /// <remarks>Git still says the file is conflicted once a clean copy is written over it and not yet added.</remarks>
    [AvaloniaFact]
    public async Task A_Project_A_Tool_Opens_Mid_Merge_Is_Said_To_Be_And_The_Merge_Is_Offered_When_The_Person_Comes_Back()
    {
        Diverged(Recoloured("#ff0000"), Recoloured("#0000ff"));
        Assert.Contains("CONFLICT", MergeInATerminal(), StringComparison.Ordinal);
        System.IO.File.WriteAllText(Home, TestPackage.From(Recoloured("#ff0000"))["home.svg"]);

        var window = Empty();
        var asked = Asked(window);

        var opened = await Call(window, "open_file", new() { ["path"] = File });

        Assert.Equal(0, asked());
        Assert.Equal($"Opened the project icons.svgstudio at {File}; get_project lists it.\nicons.svgstudio is part way through a merge: resolve it from Studio's Changes panel, or with git; save and git_commit refuse until then.", opened);
        Assert.Equal(File, window.Workspace!.Document.Path);

        // As the window's return to the front refreshes.
        await window.Changes.Refresh();

        Assert.Equal(1, asked());
        Assert.Empty(_announced);

        window.Close();
    }

    /// <remarks>Before the file is read: what it holds is the conflict markers, which would be refused as a project that no longer reads.</remarks>
    [AvaloniaFact]
    public async Task Reload_Mid_Merge_Is_Refused_As_A_Merge_And_Keeps_The_Project_Open()
    {
        Diverged(Recoloured("#ff0000"), Recoloured("#0000ff"));

        var window = await Host();
        var asked = Asked(window);
        var workspace = window.Workspace;

        Assert.Contains("CONFLICT", MergeInATerminal(), StringComparison.Ordinal);

        var reloaded = await Call(window, "reload", new());

        Assert.Equal(0, asked());
        Assert.Equal("The project is part way through a merge or a rebase; finish it, or abort it, then reload.", reloaded);
        Assert.Same(workspace, window.Workspace);
        Assert.Empty(_announced);

        window.Close();
    }

    /// <remarks>Written over a file git still holds unmerged, the project is tracked there, and the refresh that finds the conflict offered the merge window.</remarks>
    [AvaloniaFact]
    public async Task A_Project_A_Tool_Saves_Over_A_File_Git_Stopped_A_Merge_In_Is_Said_To_Be_Mid_Merge_And_Opens_No_Merge_Window()
    {
        Diverged(Recoloured("#ff0000"), Recoloured("#0000ff"));
        Assert.Contains("CONFLICT", MergeInATerminal(), StringComparison.Ordinal);

        var window = Empty();
        var asked = Asked(window);

        Assert.True(await window.NewProjectAsync());
        Dispatcher.UIThread.RunJobs();

        var saved = await Call(window, "save", new() { ["path"] = File });

        Assert.Equal(0, asked());
        Assert.Equal($"Saved as {File}, replacing the project there.\nicons.svgstudio is part way through a merge: resolve it from Studio's Changes panel, or with git; save and git_commit refuse until then.", saved);
        Assert.Equal(File, window.Workspace!.Document.Path);

        await window.Changes.Refresh();

        Assert.Equal(1, asked());
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
        TestPackage.Write(Path.Combine(other, "icons.svgstudio"), Added(Project, "star"));
        Git(other, "add", "-A");
        Git(other, "commit", "-m", "Add a star");
        Git(other, "push");

        Put(Added(Project, "moon"));
        Git(Work, "add", "-A");
        Git(Work, "commit", "-m", "Add a moon");

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
        Assert.False(await window.Changes.Git!.InMerge());
        Assert.Empty(_announced);

        window.Close();
    }

    [AvaloniaFact]
    public void Compare_And_Merge_Draw_Both_Sides()
    {
        var before = TestPackage.Parse(Recoloured("#ff0000"), "icons.svgstudio");
        var after = TestPackage.Parse(Recoloured("#0000ff"), "icons.svgstudio");

        var compare = new ProjectCompareWindow(Assert.Single(ProjectChanges.Compare(before, after)));
        compare.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, Pictures(compare).Count(picture => picture.Picture is { }));
        compare.Close();

        var merging = new ProjectMergeWindow(new ProjectMerge(new[]
        {
            new ProjectConflict(
                "home.svg",
                TestPackage.From(Recoloured("#ff0000"))["home.svg"],
                TestPackage.From(Recoloured("#0000ff"))["home.svg"],
                added: false)
        }));
        merging.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, Pictures(merging).Count(picture => picture.Picture is { }));
        merging.Close();
    }

    [AvaloniaFact]
    public async Task The_Title_Says_The_Project_Branch_Apart_From_Studio_Build()
    {
        var build = MainWindow.Build;
        MainWindow.Build = "master @ c48512f55";

        try
        {
            var window = Empty();
            Assert.Equal("SVG Studio (dev: master @ c48512f55)", window.Title);

            var drawing = Path.Combine(_directory, "star.svg");
            System.IO.File.WriteAllText(drawing, """<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" />""");
            await window.OpenAsync(new[] { drawing });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("star.svg — SVG Studio (dev: master @ c48512f55)", window.Title);
            window.Close();

            Put(Project);

            window = await Host();
            Assert.Equal("Project — icons.svgstudio — SVG Studio (dev: master @ c48512f55)", window.Title);
            window.Close();

            Committed();
            window = await Host();
            Assert.Equal("Project — icons.svgstudio [main] — SVG Studio (dev: master @ c48512f55)", window.Title);

            // A release is built without the info; a refresh is what redraws the title.
            MainWindow.Build = null;
            await window.Changes.Refresh();
            Assert.Equal("Project — icons.svgstudio [main] — SVG Studio", window.Title);

            var remote = Path.Combine(_directory, "remote.git");
            Git(_directory, "init", "--bare", "-b", "main", remote);
            Git(Work, "remote", "add", "origin", remote);
            Git(Work, "push", "-u", "origin", "main");

            var other = Path.Combine(_directory, "other");
            Git(_directory, "clone", "--config", "core.autocrlf=false", remote, other);
            Configure(other);
            TestPackage.Write(Path.Combine(other, "icons.svgstudio"), Added(Project, "star"));
            Git(other, "add", "-A");
            Git(other, "commit", "-m", "Add a star");
            Git(other, "push");

            System.IO.File.WriteAllText(Path.Combine(Work, "notes.txt"), "ahead");
            Git(Work, "add", "notes.txt");
            Git(Work, "commit", "-m", "Add notes");
            Git(Work, "fetch");
            await window.Changes.Refresh();
            Assert.Equal("Project — icons.svgstudio [main ↑1 ↓1] — SVG Studio", window.Title);

            Git(Work, "reset", "--hard", "HEAD~1");
            await window.Changes.Refresh();
            Assert.Equal("Project — icons.svgstudio [main ↓1] — SVG Studio", window.Title);

            Git(Work, "checkout", "--detach");
            await window.Changes.Refresh();
            Assert.Equal("Project — icons.svgstudio [detached] — SVG Studio", window.Title);

            window.Close();
        }
        finally
        {
            MainWindow.Build = build;
        }
    }

    [AvaloniaFact]
    public async Task The_Title_Says_A_Merge_Is_Under_Way()
    {
        Diverged(Recoloured("#ff0000"), Recoloured("#0000ff"));

        var window = await Host();
        window.ResolveConflicts = _ => Task.FromResult<bool?>(true);
        await window.Changes.Merge("other");

        Assert.EndsWith(" — icons.svgstudio [main, merging] — " + Studio(), window.Title, StringComparison.Ordinal);

        await window.Changes.Commit("Merge other");

        Assert.EndsWith(" — icons.svgstudio [main] — " + Studio(), window.Title, StringComparison.Ordinal);

        window.Close();
    }

    [AvaloniaFact]
    public async Task The_Title_Says_Which_Branch_A_Rebase_Is_Moving()
    {
        Committed();
        System.IO.File.WriteAllText(Path.Combine(Work, "notes.txt"), "rebased");
        Git(Work, "add", "notes.txt");
        Git(Work, "commit", "-m", "Add notes");

        // Stopped without a conflict, which would leave a project that cannot be opened.
        Assert.NotEqual(0, Exit(Work, "rebase", "--exec", "false", "HEAD~1"));

        var window = await Host();

        Assert.EndsWith(" — icons.svgstudio [main, rebasing] — " + Studio(), window.Title, StringComparison.Ordinal);

        window.Close();
    }

    /// <summary>What the title ends with for the Studio these tests were built as.</summary>
    private static string Studio() => MainWindow.Build is { } build ? $"SVG Studio (dev: {build})" : "SVG Studio";

    private static IEnumerable<Avalonia.Controls.Skia.SKPictureControl> Pictures(Window window)
        => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.Skia.SKPictureControl>();

    /// <summary>A repository on <c>main</c> with the project committed.</summary>
    private void Committed()
    {
        Directory.CreateDirectory(Work);
        Git(Work, "init", "-b", "main");
        Configure(Work);
        Put(Project);
        Git(Work, "add", "icons.svgstudio");
        Git(Work, "commit", "-m", "Add the icons");
    }

    /// <summary><c>main</c> checked out with <paramref name="ours"/> committed, and <c>other</c> with <paramref name="theirs"/>.</summary>
    private void Diverged(string ours, string theirs)
    {
        Committed();
        Git(Work, "switch", "-c", "other");
        Put(theirs);
        Git(Work, "add", "-A");
        Git(Work, "commit", "-m", "Theirs");
        Git(Work, "switch", "main");
        Put(ours);
        Git(Work, "add", "-A");
        Git(Work, "commit", "-m", "Ours");
    }

    /// <summary>The project folder as <paramref name="xml"/> describes it, with nothing left over from before.</summary>
    private void Put(string xml)
    {
        if (Directory.Exists(File))
        {
            Scratch.Delete(File);
        }

        TestPackage.Write(File, xml);
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

    /// <summary>A tool called as a client calls it: no button is pressed, and Studio answers its own question yes.</summary>
    private static async Task<string> Call(MainWindow window, string tool, AIFunctionArguments arguments)
    {
        var function = new AssistantTools(window) { Confirm = _ => Task.FromResult(true) }
            .For(small: false, docs: false)
            .OfType<AIFunction>()
            .Single(one => one.Name == tool);

        return (await function.InvokeAsync(arguments).AsTask().WaitAsync(TimeSpan.FromSeconds(30)))?.ToString() ?? string.Empty;
    }

    /// <summary>Counts the merge windows offered, each left as it is, as a person closing one unanswered would.</summary>
    private static Func<int> Asked(MainWindow window)
    {
        var asked = 0;

        window.ResolveConflicts = _ =>
        {
            asked++;
            return Task.FromResult<bool?>(null);
        };

        return () => asked;
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
