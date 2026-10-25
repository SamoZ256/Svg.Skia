using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// A project in a repository, with a bare repository beside it standing in for the remote, so
/// fetch, pull and push are exercised without a network.
/// </summary>
/// <remarks>
/// In the settings collection for <see cref="ProjectGit.RemoteTimeout"/>: one static for the run,
/// which one test here shortens while <see cref="ChangesPanelTests"/> runs real git.
/// </remarks>
[Collection("settings")]
public class ProjectGitTests : IDisposable
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

    public void Dispose() => Scratch.Delete(_directory);

    private string Work => Path.Combine(_directory, "work");

    private string Folder => Path.Combine(Work, "icons.svgstudio");

    private string Home => Path.Combine(Folder, "home.svg");

    [Fact]
    public async Task A_Project_Outside_A_Repository_Has_None()
    {
        Put(Project);

        Assert.Null(await ProjectGit.For(Folder));
    }

    [Fact]
    public async Task A_New_Project_Is_Untracked_Until_It_Is_Committed_And_Then_In_Its_History()
    {
        var git = await Repository();

        Assert.Equal(ProjectGitState.Untracked, (await git.Status()).State);
        Assert.Empty(await git.History(10));

        await git.Commit("Add the icons");

        var status = await git.Status();
        Assert.Equal(ProjectGitState.Committed, status.State);
        Assert.Equal("main", status.Branch);
        Assert.Equal("Add the icons", Assert.Single(await git.History(10)).Subject);

        File.AppendAllText(Home, "\n");
        Assert.Equal(ProjectGitState.Modified, (await git.Status()).State);
    }

    [Fact]
    public async Task A_Drawing_Added_Or_Deleted_Is_A_Change_And_The_Commit_Takes_It()
    {
        var git = await Repository();
        await git.Commit("Add the icons");

        Put(Added(Project, "star"));
        Assert.Equal(ProjectGitState.Modified, (await git.Status()).State);
        await git.Commit("Add a star");

        File.Delete(Home);
        Assert.Equal(ProjectGitState.Modified, (await git.Status()).State);
        await git.Commit("Take the home away");

        Assert.Equal(ProjectGitState.Committed, (await git.Status()).State);
        Assert.Equal(
            new[] { "star.svg", "studio.xml" },
            (await git.Files("HEAD"))!.Keys.OrderBy(path => path, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_Commit_Takes_The_Project_And_Leaves_What_Else_Was_Staged()
    {
        var git = await Repository();
        File.WriteAllText(Path.Combine(Work, "other.txt"), "other");
        Git(Work, "add", "other.txt");

        await git.Commit("Add the icons");

        Assert.Equal(
            new[] { "icons.svgstudio/home.svg", "icons.svgstudio/studio.xml" },
            Git(Work, "show", "--name-only", "--format=", "HEAD").Trim().Split('\n'));
        Assert.Equal("A  other.txt", Git(Work, "status", "--porcelain").Trim());
    }

    [Fact]
    public async Task The_First_Push_Sets_An_Upstream_And_A_Pull_Brings_Back_What_Another_Clone_Pushed()
    {
        var git = await Repository();
        var remote = Path.Combine(_directory, "remote.git");
        Git(_directory, "init", "--bare", "-b", "main", remote);
        Git(Work, "remote", "add", "origin", remote);

        await git.Commit("Add the icons");
        await git.Push(await git.Status());

        var pushed = await git.Status();
        Assert.Equal("origin/main", pushed.Upstream);
        Assert.Equal((0, 0), (pushed.Ahead, pushed.Behind));

        var other = Path.Combine(_directory, "other");
        Git(_directory, "clone", "--config", "core.autocrlf=false", remote, other);
        Configure(other);
        File.AppendAllText(Path.Combine(other, "icons.svgstudio", "home.svg"), "\n");
        Git(other, "commit", "-am", "Change the icons");
        Git(other, "push");

        await git.Fetch();
        Assert.Equal(1, (await git.Status()).Behind);

        await git.Pull();
        Assert.Equal("Change the icons", (await git.History(1)).Single().Subject);
    }

    [Fact]
    public async Task A_Push_With_No_Remote_Says_So()
    {
        var git = await Repository();
        await git.Commit("Add the icons");

        var refusal = await Assert.ThrowsAsync<ProjectGitException>(async () => await git.Push(await git.Status()));

        Assert.Contains("no remote", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Init_Makes_A_Repository_That_Needs_Nothing_Configured_And_Refuses_A_Second_Time()
    {
        Put(Project);

        var git = await ProjectGit.Init(Folder);

        Assert.NotNull(await ProjectGit.For(Folder));
        Assert.False(File.Exists(Path.Combine(Work, ".gitattributes")));
        Assert.Equal(ProjectGitState.Untracked, (await git.Status()).State);

        await Assert.ThrowsAsync<ProjectGitException>(() => ProjectGit.Init(Folder));
    }

    [Fact]
    public async Task Head_Is_Null_Until_The_First_Commit_And_Files_Read_The_Project_Back_From_It()
    {
        var git = await Repository();

        Assert.Null(await git.Head());
        Assert.Null(await git.Files("HEAD"));

        // A byte order mark is part of the text, as it is to ProjectDocument.Load.
        var drawing = File.ReadAllText(Home);
        File.WriteAllText(Home, drawing, new UTF8Encoding(true));
        await git.Commit("Add the icons");

        Assert.Matches("^[0-9a-f]{40}$", await git.Head());

        var files = (await git.Files("HEAD"))!;

        Assert.Equal("﻿" + drawing, files["home.svg"]);
        Assert.Equal(TestPackage.From(Project)["studio.xml"], files["studio.xml"]);
        Assert.Null(await git.Files("no-such-revision"));
        Assert.Empty(await git.Conflicts());
    }

    [Fact]
    public async Task Branches_Are_Created_Switched_Deleted_And_Listed_With_The_Remote_Ones_Not_Yet_Local()
    {
        var git = await Repository();
        var remote = Path.Combine(_directory, "remote.git");
        Git(_directory, "init", "--bare", "-b", "main", remote);
        Git(Work, "remote", "add", "origin", remote);
        await git.Commit("Add the icons");
        await git.Push(await git.Status());

        var other = Path.Combine(_directory, "other");
        Git(_directory, "clone", "--config", "core.autocrlf=false", remote, other);
        Git(other, "push", "origin", "main:shared");
        await git.Fetch();

        await git.CreateBranch("feature", switchTo: false);
        Assert.Equal("main", (await git.Status()).Branch);
        await git.CreateBranch("topic", switchTo: true);
        Assert.Equal("topic", (await git.Status()).Branch);

        Assert.Equal(
            new[]
            {
                new ProjectGitBranch("feature", false, null), new ProjectGitBranch("main", false, null),
                new ProjectGitBranch("topic", true, null), new ProjectGitBranch("shared", false, "origin/shared")
            },
            await git.Branches());

        await git.Switch("shared");
        var status = await git.Status();
        Assert.Equal(("shared", "origin/shared"), (status.Branch, status.Upstream));
        Assert.Single(await git.Branches(), branch => branch.Name == "shared");

        await git.DeleteBranch("feature");
        Assert.DoesNotContain(await git.Branches(), branch => branch.Name == "feature");

        await git.Switch("topic");
        File.AppendAllText(Home, "\n");
        await git.Commit("Change the icons");
        await git.Switch("main");

        var refusal = await Assert.ThrowsAsync<ProjectGitException>(() => git.DeleteBranch("topic"));
        Assert.Contains("not fully merged", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_Drawings_Added_On_Two_Branches_Merge_In_Plain_Git()
    {
        var git = await Diverged(theirs: Added(Project, "star"), ours: Added(Project, "moon"));

        // Git's own merge, with nothing of Studio's in it: each drawing is a file of its own.
        Git(Work, "merge", "--no-edit", "theirs");

        Assert.False(await git.InMerge());
        Assert.Equal(
            new[] { "home", "moon", "star" },
            ProjectDocument.Load(Folder).Root.Children.Select(child => child.Name));
    }

    [Fact]
    public async Task Edits_To_Different_Drawings_On_Two_Branches_Merge_Cleanly()
    {
        var both = Added(Project, "star");
        var git = await Repository(both);
        await git.Commit("Add the icons");

        await git.CreateBranch("theirs", switchTo: true);
        Put(both.Replace("viewBox=\"0 0 24 24\" width=\"24\" height=\"24\" />", "viewBox=\"0 0 24 24\" width=\"48\" height=\"48\" />", StringComparison.Ordinal));
        await git.Commit("Grow the star");

        await git.Switch("main");
        Put(both.Replace("#00ff00", "#0000ff", StringComparison.Ordinal));
        await git.Commit("Recolour the home");

        Assert.False(await git.Merge("theirs"));

        var merged = TestPackage.Dump(Folder);

        Assert.Contains("#0000ff", merged, StringComparison.Ordinal);
        Assert.Contains("width=\"48\"", merged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Drawing_Moved_On_The_Board_On_One_Branch_And_Resized_On_Another_Merges_Cleanly()
    {
        var git = await Repository();
        await git.Commit("Add the icons");

        await git.CreateBranch("theirs", switchTo: true);
        Put(Project.Replace("<drawing name=\"home\">", "<drawing name=\"home\" x=\"40\" y=\"80\">", StringComparison.Ordinal));
        await git.Commit("Move the home");

        await git.Switch("main");
        Put(Project.Replace("width=\"24\" height=\"24\">", "width=\"48\" height=\"48\">", StringComparison.Ordinal));
        await git.Commit("Grow the home");

        Assert.False(await git.Merge("theirs"));

        var home = (ProjectDrawing)ProjectDocument.Load(Folder).Root.Children.Single();

        Assert.Equal((40f, 80f), (home.X, home.Y));
        Assert.Contains("width=\"48\"", home.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Empty_Group_Survives_A_Commit_And_A_Fresh_Clone()
    {
        var git = await Repository(Project.Replace("</studio>", "  <group name=\"Large\" scale=\"2\" />\n</studio>", StringComparison.Ordinal));
        await git.Commit("Add the icons");

        var clone = Path.Combine(_directory, "clone");
        Git(_directory, "clone", Work, clone);

        var group = Assert.Single(ProjectDocument.Load(Path.Combine(clone, "icons.svgstudio")).Root.Children.OfType<ProjectGroup>());

        Assert.Equal(("Large", 2f), (group.Name, group.Scale));
    }

    [Fact]
    public async Task One_Drawing_Changed_On_Both_Sides_Conflicts_Alone_And_Can_Be_Aborted()
    {
        var git = await Diverged(theirs: Recoloured("#0000ff"), ours: Recoloured("#ff0000"));
        var ours = File.ReadAllText(Home);

        Assert.False(await git.InMerge());
        Assert.True(await git.Merge("theirs"));

        Assert.Equal(ProjectGitState.Conflicted, (await git.Status()).State);
        Assert.True(await git.InMerge());
        Assert.Null(await git.Rebasing());
        Assert.Equal("home.svg", Assert.Single(await git.Conflicts()));

        var (@base, mine, theirs) = await git.Sides("home.svg");

        Assert.Contains("#00ff00", @base, StringComparison.Ordinal);
        Assert.Equal(ours, mine);
        Assert.Contains("#0000ff", theirs, StringComparison.Ordinal);

        await git.AbortMerge();

        Assert.False(await git.InMerge());
        Assert.Equal(ours, File.ReadAllText(Home));
        Assert.Equal(ProjectGitState.Committed, (await git.Status()).State);
    }

    [Fact]
    public async Task A_Conflict_Settled_By_Taking_A_Side_Commits_With_Both_Parents()
    {
        var git = await Diverged(theirs: Recoloured("#0000ff"), ours: Recoloured("#ff0000"));
        File.WriteAllText(Path.Combine(Work, "other.txt"), "other");

        Assert.True(await git.Merge("theirs"));
        await git.Take("home.svg", ProjectSide.Theirs);
        await git.Commit("Merge the icons");

        Assert.False(await git.InMerge());
        Assert.Equal(ProjectGitState.Committed, (await git.Status()).State);
        Assert.Equal(3, Git(Work, "rev-list", "--parents", "-n1", "HEAD").Trim().Split(' ').Length);
        Assert.Contains("#0000ff", (await git.Files("HEAD"))!["home.svg"], StringComparison.Ordinal);
        Assert.Equal("?? other.txt", Git(Work, "status", "--porcelain").Trim());
    }

    [Fact]
    public async Task A_Side_That_Deleted_The_Drawing_Is_Taken_By_Deleting_It()
    {
        var git = await Repository();
        await git.Commit("Add the icons");
        await git.CreateBranch("theirs", switchTo: true);
        File.Delete(Home);
        await git.Commit("Take the home away");
        await git.Switch("main");
        Put(Recoloured("#ff0000"));
        await git.Commit("Recolour the home");

        Assert.True(await git.Merge("theirs"));
        await git.Take("home.svg", ProjectSide.Theirs);
        await git.Commit("Merge the icons");

        Assert.False(File.Exists(Home));
        Assert.False((await git.Files("HEAD"))!.ContainsKey("home.svg"));
    }

    [Fact]
    public async Task A_Merge_That_Cannot_Start_Throws_Rather_Than_Reporting_A_Conflict()
    {
        var git = await Repository();
        await git.Commit("Add the icons");

        await Assert.ThrowsAsync<ProjectGitException>(() => git.Merge("no-such-branch"));
    }

    [Fact]
    public async Task A_Pull_Of_Diverged_Clones_Merges_Cleanly_Or_Stops_Conflicted()
    {
        var git = await Repository();
        var remote = Path.Combine(_directory, "remote.git");
        Git(_directory, "init", "--bare", "-b", "main", remote);
        Git(Work, "remote", "add", "origin", remote);
        await git.Commit("Add the icons");
        await git.Push(await git.Status());

        var other = Path.Combine(_directory, "other");
        Git(_directory, "clone", "--config", "core.autocrlf=false", remote, other);
        Configure(other);
        TestPackage.Write(Path.Combine(other, "icons.svgstudio"), Added(Project, "star"));
        Git(other, "add", "-A");
        Git(other, "commit", "-m", "Add a star");
        Git(other, "push");

        Put(Added(Project, "moon"));
        await git.Commit("Add a moon");
        await git.Fetch();

        await Assert.ThrowsAsync<ProjectGitException>(() => git.Pull());
        Assert.False(await git.PullMerge());
        Assert.Equal((2, 0), ((await git.Status()).Ahead, (await git.Status()).Behind));

        File.WriteAllText(Path.Combine(other, "icons.svgstudio", "home.svg"), TestPackage.From(Recoloured("#0000ff"))["home.svg"]);
        Git(other, "commit", "-am", "Recolour the home");
        Git(other, "push");
        Put(Added(Recoloured("#ff0000"), "moon"));
        await git.Commit("Recolour the home too");
        await git.Fetch();

        Assert.True(await git.PullMerge());
        Assert.Equal(ProjectGitState.Conflicted, (await git.Status()).State);
    }

    [Fact]
    public async Task A_Cherry_Pick_Stopped_On_The_Project_Commits_Whole_Once_Resolved()
    {
        var git = await Repository();
        await git.Commit("Add the icons");
        Git(Work, "switch", "-c", "other");
        Put(Recoloured("#0000ff"));
        Git(Work, "commit", "-am", "Theirs");
        Git(Work, "switch", "main");
        Put(Recoloured("#ff0000"));
        Git(Work, "commit", "-am", "Ours");

        Assert.NotEqual(0, Exit(Work, "cherry-pick", "other"));
        Assert.Equal(ProjectGitState.Conflicted, (await git.Status()).State);
        Assert.False(await git.InMerge());

        Put(Project);
        await git.Commit("Picked");

        Assert.Equal("Picked", (await git.History(1)).Single().Subject);
        Assert.Equal(ProjectGitState.Committed, (await git.Status()).State);
    }

    [Fact]
    public async Task An_Ignored_Project_Says_So_Rather_Than_Reading_As_Committed()
    {
        var git = await Repository();
        File.WriteAllText(Path.Combine(Work, ".gitignore"), "*.svgstudio\n");

        Assert.Equal(ProjectGitState.Ignored, (await git.Status()).State);
    }

    [Fact]
    public async Task A_Detached_Head_Is_Not_Pushed()
    {
        var git = await Repository();
        await git.Commit("Add the icons");
        Git(Work, "remote", "add", "origin", Path.Combine(_directory, "remote.git"));
        Git(Work, "switch", "--detach");

        var refusal = await Assert.ThrowsAsync<ProjectGitException>(async () => await git.Push(await git.Status()));

        Assert.StartsWith("No branch is checked out", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Committed_Files_Read_As_Checkout_Writes_Them()
    {
        var git = await Repository();
        await git.Commit("Add the icons");
        Git(Work, "config", "core.autocrlf", "true");
        File.Delete(Home);
        Git(Work, "checkout", "--", "icons.svgstudio/home.svg");

        Assert.Contains("\r\n", File.ReadAllText(Home), StringComparison.Ordinal);
        Assert.Equal(File.ReadAllText(Home), (await git.Files("HEAD"))!["home.svg"]);
    }

    [Fact]
    public async Task A_Switch_To_A_Branch_Without_The_Project_Is_Refused()
    {
        var git = await Repository();
        await git.Commit("Add the icons");
        Git(Work, "switch", "--orphan", "bare");
        Git(Work, "commit", "--allow-empty", "-m", "Nothing");
        Git(Work, "switch", "main");

        await Assert.ThrowsAsync<ProjectGitException>(() => git.Switch("bare"));

        Assert.Equal("main", (await git.Status()).Branch);
        Assert.True(File.Exists(Home));
    }

    [Fact]
    public async Task A_Remote_That_Never_Answers_Is_Given_Up_On()
    {
        var git = await Repository();
        await git.Commit("Add the icons");

        // An ssh command of the user's own is left alone, and this one hangs the way a prompt would.
        Git(Work, "config", "core.sshCommand", "sleep 60");
        Git(Work, "remote", "add", "origin", "ssh://example.invalid/icons.git");

        var timeout = ProjectGit.RemoteTimeout;
        ProjectGit.RemoteTimeout = TimeSpan.FromSeconds(2);

        try
        {
            var clock = Stopwatch.StartNew();

            await Assert.ThrowsAsync<ProjectGitException>(() => git.Fetch());
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30));
        }
        finally
        {
            ProjectGit.RemoteTimeout = timeout;
        }
    }

    /// <summary>A committed project, with <c>theirs</c> holding one version of it and the checked out branch another.</summary>
    private async Task<ProjectGit> Diverged(string theirs, string ours)
    {
        var git = await Repository();
        await git.Commit("Add the icons");

        await git.CreateBranch("theirs", switchTo: true);
        Put(theirs);
        await git.Commit("Theirs");

        await git.Switch("main");
        Put(ours);
        await git.Commit("Ours");

        return git;
    }

    /// <summary>A git command expected to fail, which <see cref="Git"/> would count as a test failure.</summary>
    internal static int Exit(string directory, params string[] arguments) => Ran(directory, arguments).Code;

    internal static string Added(string project, string name)
        => project.Replace(
            "</studio>",
            $"""
              <drawing name="{name}">
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24" />
              </drawing>
            </studio>
            """,
            StringComparison.Ordinal);

    private static string Recoloured(string colour) => Project.Replace("#00ff00", colour, StringComparison.Ordinal);

    /// <summary>The project folder as <paramref name="xml"/> describes it, with nothing left over from before.</summary>
    private void Put(string xml)
    {
        if (Directory.Exists(Folder))
        {
            Scratch.Delete(Folder);
        }

        TestPackage.Write(Folder, xml);
    }

    private async Task<ProjectGit> Repository(string project = Project)
    {
        Directory.CreateDirectory(Work);
        Git(Work, "init", "-b", "main");
        Configure(Work);
        Put(project);

        return Assert.IsType<ProjectGit>(await ProjectGit.For(Folder));
    }

    /// <summary>An identity, no signing and no line ending conversion, whatever the machine's own configuration says.</summary>
    internal static void Configure(string repository)
    {
        Git(repository, "config", "user.name", "Studio Tests");
        Git(repository, "config", "user.email", "studio@example.com");
        Git(repository, "config", "commit.gpgsign", "false");
        Git(repository, "config", "core.autocrlf", "false");
    }

    internal static string Git(string directory, params string[] arguments)
    {
        var (code, output, error) = Ran(directory, arguments);

        Assert.True(code == 0, $"git {string.Join(' ', arguments)}: {error}");

        return output;
    }

    private static (int Code, string Output, string Error) Ran(string directory, string[] arguments)
    {
        var start = new ProcessStartInfo(ProjectGit.Installed!)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, output, error);
    }
}
