using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// A project file in a repository, with a bare repository beside it standing in for the remote, so
/// fetch, pull and push are exercised without a network.
/// </summary>
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
    private readonly string _settings = StudioSettings.Store;
    private readonly string _store = ProjectRecovery.Store;

    public ProjectGitTests()
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

    [Fact]
    public async Task A_File_Outside_A_Repository_Has_None()
    {
        Directory.CreateDirectory(Work);
        System.IO.File.WriteAllText(File, Project);

        Assert.Null(await ProjectGit.For(File));
    }

    [Fact]
    public async Task A_New_File_Is_Untracked_Until_It_Is_Committed_And_Then_In_Its_History()
    {
        var git = await Repository();

        Assert.Equal(ProjectGitState.Untracked, (await git.Status()).State);
        Assert.Empty(await git.History(10));

        await git.Commit("Add the icons");

        var status = await git.Status();
        Assert.Equal(ProjectGitState.Committed, status.State);
        Assert.Equal("main", status.Branch);
        Assert.Equal("Add the icons", Assert.Single(await git.History(10)).Subject);

        System.IO.File.AppendAllText(File, "\n");
        Assert.Equal(ProjectGitState.Modified, (await git.Status()).State);
    }

    [Fact]
    public async Task A_Commit_Takes_The_Project_And_Leaves_What_Else_Was_Staged()
    {
        var git = await Repository();
        System.IO.File.WriteAllText(Path.Combine(Work, "other.txt"), "other");
        Git(Work, "add", "other.txt");

        await git.Commit("Add the icons");

        Assert.Equal("icons.svgstudio", Git(Work, "show", "--name-only", "--format=", "HEAD").Trim());
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
        System.IO.File.AppendAllText(Path.Combine(other, "icons.svgstudio"), "\n");
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
    public async Task Init_Makes_A_Repository_That_Diffs_By_Drawing_And_Refuses_A_Second_Time()
    {
        Directory.CreateDirectory(Work);
        System.IO.File.WriteAllText(File, Project);

        var git = await ProjectGit.Init(File);

        Assert.NotNull(await ProjectGit.For(File));
        Assert.Equal("*.svgstudio diff=svgstudio\n", System.IO.File.ReadAllText(Path.Combine(Work, ".gitattributes")));
        Assert.Equal(
            "^[ \t]*(<(drawing|group)[ \t].*)$",
            Git(Work, "config", "--local", "diff.svgstudio.xfuncname").TrimEnd('\n'));
        Assert.Equal(ProjectGitState.Untracked, (await git.Status()).State);

        await Assert.ThrowsAsync<ProjectGitException>(() => ProjectGit.Init(File));
    }

    [Fact]
    public async Task Init_Adds_To_An_Existing_Attributes_File_Only_What_It_Lacks()
    {
        Directory.CreateDirectory(Work);
        System.IO.File.WriteAllText(File, Project);
        var attributes = Path.Combine(Work, ".gitattributes");
        System.IO.File.WriteAllText(attributes, "*.png binary");

        await ProjectGit.Init(File);
        Assert.Equal("*.png binary\n*.svgstudio diff=svgstudio\n", System.IO.File.ReadAllText(attributes));

        Scratch.Delete(Path.Combine(Work, ".git"));
        await ProjectGit.Init(File);
        Assert.Equal("*.png binary\n*.svgstudio diff=svgstudio\n", System.IO.File.ReadAllText(attributes));
    }

    [Fact]
    public async Task Head_Is_Null_Until_The_First_Commit_And_At_Reads_The_File_Back_From_It()
    {
        var git = await Repository();

        Assert.Null(await git.Head());
        Assert.Null(await git.At("HEAD"));

        // A byte order mark is part of the text, as it is to ProjectDocument.Load.
        System.IO.File.WriteAllText(File, Project, new System.Text.UTF8Encoding(true));
        await git.Commit("Add the icons");

        Assert.Matches("^[0-9a-f]{40}$", await git.Head());
        Assert.Equal("\uFEFF" + Project, await git.At("HEAD"));
        Assert.Null(await git.At("no-such-revision"));
        Assert.Equal<(string?, string?, string?)>((null, null, null), await git.Stages());
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
        System.IO.File.AppendAllText(File, "\n");
        await git.Commit("Change the icons");
        await git.Switch("main");

        var refusal = await Assert.ThrowsAsync<ProjectGitException>(() => git.DeleteBranch("topic"));
        Assert.Contains("not fully merged", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Merge_Of_Two_Drawings_Added_At_The_End_Conflicts_And_Can_Be_Aborted()
    {
        var git = await Diverged();
        var ours = System.IO.File.ReadAllText(File);

        Assert.False(await git.InMerge());
        Assert.True(await git.Merge("theirs"));

        Assert.Equal(ProjectGitState.Conflicted, (await git.Status()).State);
        Assert.True(await git.InMerge());
        Assert.Null(await git.Rebasing());
        Assert.Equal((Project, ours, Added(Project, "star")), await git.Stages());

        await git.AbortMerge();

        Assert.False(await git.InMerge());
        Assert.Equal(ours, System.IO.File.ReadAllText(File));
        Assert.Equal(ProjectGitState.Committed, (await git.Status()).State);
    }

    [Fact]
    public async Task A_Merge_Resolved_By_Writing_The_File_Commits_With_Both_Parents()
    {
        var git = await Diverged();
        System.IO.File.WriteAllText(Path.Combine(Work, "other.txt"), "other");

        Assert.True(await git.Merge("theirs"));
        System.IO.File.WriteAllText(File, Added(Added(Project, "moon"), "star"));
        await git.Commit("Merge the icons");

        Assert.False(await git.InMerge());
        Assert.Equal(ProjectGitState.Committed, (await git.Status()).State);
        Assert.Equal(3, Git(Work, "rev-list", "--parents", "-n1", "HEAD").Trim().Split(' ').Length);
        Assert.Equal(Added(Added(Project, "moon"), "star"), await git.At("HEAD"));
        Assert.Equal("?? other.txt", Git(Work, "status", "--porcelain").Trim());
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
        System.IO.File.WriteAllText(Path.Combine(Work, "notes.txt"), "notes\n");
        Git(Work, "add", "notes.txt");
        await git.Commit("Add the icons");
        Git(Work, "commit", "-m", "Add the notes");
        await git.Push(await git.Status());

        var other = Path.Combine(_directory, "other");
        Git(_directory, "clone", "--config", "core.autocrlf=false", remote, other);
        Configure(other);
        System.IO.File.AppendAllText(Path.Combine(other, "notes.txt"), "more\n");
        Git(other, "commit", "-am", "Change the notes");
        Git(other, "push");

        System.IO.File.WriteAllText(File, Added(Project, "moon"));
        await git.Commit("Add a moon");
        await git.Fetch();

        await Assert.ThrowsAsync<ProjectGitException>(() => git.Pull());
        Assert.False(await git.PullMerge());
        Assert.Equal((2, 0), ((await git.Status()).Ahead, (await git.Status()).Behind));

        System.IO.File.WriteAllText(Path.Combine(other, "icons.svgstudio"), Added(Project, "star"));
        Git(other, "commit", "-am", "Add a star");
        Git(other, "push");
        await git.Fetch();

        Assert.True(await git.PullMerge());
        Assert.Equal(ProjectGitState.Conflicted, (await git.Status()).State);
    }

    /// <summary>
    /// A committed project, with <c>theirs</c> adding a star at the end and the checked out branch a
    /// moon at the same place, the case git cannot merge by lines.
    /// </summary>
    private async Task<ProjectGit> Diverged()
    {
        var git = await Repository();
        await git.Commit("Add the icons");

        await git.CreateBranch("theirs", switchTo: true);
        System.IO.File.WriteAllText(File, Added(Project, "star"));
        await git.Commit("Add a star");

        await git.Switch("main");
        System.IO.File.WriteAllText(File, Added(Project, "moon"));
        await git.Commit("Add a moon");

        return git;
    }

    [Fact]
    public async Task A_Cherry_Pick_Stopped_On_The_Project_Commits_Whole_Once_Resolved()
    {
        var git = await Repository();
        await git.Commit("Add the icons");
        Git(Work, "switch", "-c", "other");
        System.IO.File.WriteAllText(File, Project.Replace("#00ff00", "#0000ff", StringComparison.Ordinal));
        Git(Work, "commit", "-am", "Theirs");
        Git(Work, "switch", "main");
        System.IO.File.WriteAllText(File, Project.Replace("#00ff00", "#ff0000", StringComparison.Ordinal));
        Git(Work, "commit", "-am", "Ours");

        Assert.NotEqual(0, Exit(Work, "cherry-pick", "other"));
        Assert.Equal(ProjectGitState.Conflicted, (await git.Status()).State);
        Assert.False(await git.InMerge());

        System.IO.File.WriteAllText(File, Project);
        await git.Commit("Picked");

        Assert.Equal("Picked", (await git.History(1)).Single().Subject);
        Assert.Equal(ProjectGitState.Committed, (await git.Status()).State);
    }

    [Fact]
    public async Task An_Ignored_Project_Says_So_Rather_Than_Reading_As_Committed()
    {
        var git = await Repository();
        System.IO.File.WriteAllText(Path.Combine(Work, ".gitignore"), "*.svgstudio\n");

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
    public async Task The_Committed_Text_Reads_As_Checkout_Writes_It()
    {
        var git = await Repository();
        await git.Commit("Add the icons");
        Git(Work, "config", "core.autocrlf", "true");
        System.IO.File.Delete(File);
        Git(Work, "checkout", "--", "icons.svgstudio");

        Assert.Contains("\r\n", System.IO.File.ReadAllText(File), StringComparison.Ordinal);
        Assert.Equal(System.IO.File.ReadAllText(File), await git.At("HEAD"));
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
        Assert.True(System.IO.File.Exists(File));
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

    private async Task<ProjectGit> Repository()
    {
        Directory.CreateDirectory(Work);
        Git(Work, "init", "-b", "main");
        Configure(Work);
        System.IO.File.WriteAllText(File, Project);

        return Assert.IsType<ProjectGit>(await ProjectGit.For(File));
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
