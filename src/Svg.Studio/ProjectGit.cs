// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Svg.Studio;

/// <summary>A git command that did not succeed, carrying what git said about it.</summary>
public sealed class ProjectGitException : Exception
{
    public ProjectGitException(string message) : base(message)
    {
    }
}

/// <summary>What git says about the project file and the branch it sits on.</summary>
/// <param name="Upstream">The branch it pulls from and pushes to, or null when it has none.</param>
/// <param name="State">The file against the last commit: tracked and clean, modified, untracked, or conflicted.</param>
public sealed record ProjectGitStatus(
    string? Branch,
    string? Upstream,
    int Ahead,
    int Behind,
    ProjectGitState State);

public enum ProjectGitState
{
    Committed,
    Modified,
    Untracked,
    Conflicted,

    /// <summary>Matched by a <c>.gitignore</c>, so git will not take it into a commit.</summary>
    Ignored
}

public sealed record ProjectGitCommit(string Hash, string Author, DateTimeOffset When, string Subject);

/// <summary>A branch that can be switched to.</summary>
/// <param name="Name">The local name, which is what <see cref="ProjectGit.Switch"/> takes.</param>
/// <param name="Remote">The remote-tracking branch, such as <c>origin/feature</c>, when there is no local branch yet.</param>
public sealed record ProjectGitBranch(string Name, bool Current, string? Remote);

/// <summary>The repository a project file sits in, driven through the <c>git</c> the user has installed.</summary>
/// <remarks>
/// The CLI rather than libgit2: LibGit2Sharp ships no SSH transport and ignores credential helpers,
/// so a remote would have meant Studio asking for and keeping passwords itself. Every command runs
/// in the project's directory with the file named by its bare name, so a pathspec is never built
/// from a path git may have resolved through a symlink (<c>/tmp</c> is <c>/private/tmp</c> on macOS).
/// </remarks>
public sealed class ProjectGit
{
    private ProjectGit(string executable, string directory, string name)
    {
        Executable = executable;
        Directory = directory;
        Name = name;
    }

    public string Executable { get; }

    public string Directory { get; }

    /// <summary>The project's file name, which is its pathspec from <see cref="Directory"/>.</summary>
    public string Name { get; }

    /// <summary>How long a command that talks to a remote may take. Settable so a test need not wait.</summary>
    public static TimeSpan RemoteTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The <c>git</c> found on this machine, or null. Settable so a test can point it elsewhere.</summary>
    public static string? Installed { get; set; } = Find();

    /// <summary>The repository holding <paramref name="path"/>, or null when there is none or no git.</summary>
    public static async Task<ProjectGit?> For(string path)
    {
        if (Installed is not { } executable || Path.GetDirectoryName(Path.GetFullPath(path)) is not { } directory
            || !System.IO.Directory.Exists(directory))
        {
            return null;
        }

        var git = new ProjectGit(executable, directory, Path.GetFileName(path));

        // Fails outside a work tree, and inside a .git directory, which is where a project should not be.
        var (code, output, _) = await git.Run(new[] { "rev-parse", "--is-inside-work-tree" })
            .ConfigureAwait(false);

        return code == 0 && output.Trim() == "true" ? git : null;
    }

    /// <summary>Makes a repository in the directory of <paramref name="path"/>, set up to diff projects by drawing.</summary>
    /// <remarks>
    /// Only a repository Studio creates is configured; an existing one's config is never touched,
    /// which is why this refuses inside a work tree rather than configuring it.
    /// </remarks>
    public static async Task<ProjectGit> Init(string path)
    {
        if (Installed is not { } executable || Path.GetDirectoryName(Path.GetFullPath(path)) is not { } directory
            || !System.IO.Directory.Exists(directory))
        {
            throw new ProjectGitException("There is no git on this machine, or no directory for the project.");
        }

        var git = new ProjectGit(executable, directory, Path.GetFileName(path));

        if (await For(path).ConfigureAwait(false) is { })
        {
            throw new ProjectGitException("The project is already in a repository.");
        }

        await git.Checked(new[] { "init" }).ConfigureAwait(false);

        const string Attribute = "*.svgstudio diff=svgstudio";
        var attributes = Path.Combine(directory, ".gitattributes");
        var existing = File.Exists(attributes) ? await File.ReadAllTextAsync(attributes).ConfigureAwait(false) : "";

        if (!existing.Split('\n').Any(line => line.Trim() == Attribute))
        {
            var separator = existing.Length == 0 || existing.EndsWith('\n') ? "" : "\n";
            await File.AppendAllTextAsync(attributes, separator + Attribute + "\n").ConfigureAwait(false);
        }

        // Every hunk header then names the drawing or group the hunk is in.
        await git.Checked(
            new[] { "config", "diff.svgstudio.xfuncname", "^[ \t]*(<(drawing|group)[ \t].*)$" }).ConfigureAwait(false);

        // Staged so the first commit carries it; Commit takes what is staged while there is no HEAD.
        await git.Checked(new[] { "add", "--", ".gitattributes" }).ConfigureAwait(false);

        return git;
    }

    /// <summary>The commit checked out, or null before the first commit.</summary>
    public async Task<string?> Head()
    {
        var (code, output, _) = await Run(new[] { "rev-parse", "-q", "--verify", "HEAD" })
            .ConfigureAwait(false);

        return code == 0 ? output.Trim() : null;
    }

    /// <summary>The project file's text at <paramref name="revision"/>, or null when it is not there or the revision does not exist.</summary>
    public Task<string?> At(string revision)
        => Blob($"{revision}:./{Name}");

    /// <summary>The common ancestor, our and their versions of a conflicted file; each is null when that side has none.</summary>
    public async Task<(string? Base, string? Ours, string? Theirs)> Stages()
        => (await Blob($":1:./{Name}").ConfigureAwait(false),
            await Blob($":2:./{Name}").ConfigureAwait(false),
            await Blob($":3:./{Name}").ConfigureAwait(false));

    /// <remarks>
    /// <c>cat-file</c> rather than <c>show</c>, which may run a textconv filter over the blob, and with
    /// <c>--filters</c> so it reads as checkout writes it: a bare blob has LF where autocrlf put CRLF on
    /// disk, and every drawing would compare as changed.
    /// </remarks>
    private async Task<string?> Blob(string name)
    {
        var (code, output, _) = await Run(new[] { "cat-file", "--filters", name }).ConfigureAwait(false);

        return code == 0 ? output : null;
    }

    /// <summary>The local branches, and the remote-tracking ones no local branch of the same name has taken.</summary>
    public async Task<IReadOnlyList<ProjectGitBranch>> Branches()
    {
        var output = await Checked(
            new[]
            {
                "for-each-ref", "--format=%(refname)%1f%(refname:short)%1f%(refname:lstrip=3)%1f%(HEAD)",
                "refs/heads", "refs/remotes"
            }).ConfigureAwait(false);

        var branches = new List<ProjectGitBranch>();

        foreach (var fields in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('\x1f')))
        {
            if (fields.Length != 4)
            {
                continue;
            }

            if (fields[0].StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                branches.Add(new ProjectGitBranch(fields[1], fields[3] == "*", null));
            }
            else if (!fields[0].EndsWith("/HEAD", StringComparison.Ordinal)
                     && branches.All(branch => branch.Name != fields[2]))
            {
                // refs/heads sorts before refs/remotes, so every local branch is already listed.
                branches.Add(new ProjectGitBranch(fields[2], false, fields[1]));
            }
        }

        return branches;
    }

    public Task CreateBranch(string name, bool switchTo)
        => Checked(switchTo ? new[] { "switch", "-c", name } : new[] { "branch", name });

    /// <summary>Checks out <paramref name="name"/>, making a local branch tracking the remote one when there is only that.</summary>
    /// <remarks>
    /// Refused when the branch lacks a project that is committed here, since the switch would delete
    /// the file out from under the window that has it open.
    /// </remarks>
    public async Task Switch(string name)
    {
        var target = await Has(name + "^{commit}").ConfigureAwait(false)
            ? name
            : (await Branches().ConfigureAwait(false)).FirstOrDefault(branch => branch.Name == name)?.Remote;

        if (target is { } && await Has($"HEAD:./{Name}").ConfigureAwait(false)
                          && !await Has($"{target}:./{Name}").ConfigureAwait(false))
        {
            throw new ProjectGitException($"{name} has no {Name}, so the project can't stay open on it. Switch with git to work there.");
        }

        await Checked(new[] { "switch", name }).ConfigureAwait(false);
    }

    /// <summary>Deletes a merged branch; git refuses one with unmerged commits, and its refusal is the message.</summary>
    public Task DeleteBranch(string name)
        => Checked(new[] { "branch", "-d", name });

    /// <summary>Merges <paramref name="branch"/> into the current one, returning true when it stopped with the project conflicted.</summary>
    public Task<bool> Merge(string branch)
        => Merging(new[] { "merge", "--no-edit", branch });

    /// <summary>Pulls with a merge when the branches have diverged, returning true when it stopped with the project conflicted.</summary>
    public Task<bool> PullMerge()
        => Merging(new[] { "pull", "--no-rebase", "--no-edit" }, remote: true);

    private async Task<bool> Merging(IEnumerable<string> arguments, bool remote = false)
    {
        var (code, output, error) = await Run(arguments, remote).ConfigureAwait(false);

        if (code == 0)
        {
            return false;
        }

        if ((await Status().ConfigureAwait(false)).State == ProjectGitState.Conflicted)
        {
            return true;
        }

        throw new ProjectGitException(error.Trim() is { Length: > 0 } said ? said : output.Trim());
    }

    public Task<bool> InMerge() => Has("MERGE_HEAD");

    /// <summary>Whether <paramref name="revision"/> names an object, such as a ref or <c>HEAD:./file</c>.</summary>
    private async Task<bool> Has(string revision)
        => (await Run(new[] { "rev-parse", "-q", "--verify", revision }).ConfigureAwait(false)).Code == 0;

    /// <summary>The branch a rebase stopped part way is rebasing, or null when none is; Studio leaves it to git to finish.</summary>
    /// <remarks>Status reads the HEAD a rebase moves as detached, so only here is the branch said.</remarks>
    public async Task<string?> Rebasing()
    {
        foreach (var name in new[] { "rebase-merge", "rebase-apply" })
        {
            var path = Path.Combine(Directory, (await Checked(new[] { "rev-parse", "--git-path", name }).ConfigureAwait(false))
                .Trim());

            if (System.IO.Directory.Exists(path))
            {
                // "detached HEAD" when what was rebased was not a branch.
                var head = Path.Combine(path, "head-name");
                var branch = File.Exists(head) ? File.ReadAllText(head).Trim() : string.Empty;

                return branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch["refs/heads/".Length..] : "detached";
            }
        }

        return null;
    }

    public Task AbortMerge()
        => Checked(new[] { "merge", "--abort" });

    /// <summary>Marks the project file resolved, or stages it for the next commit.</summary>
    public Task Stage()
        => Checked(new[] { "add", "--", Name });

    public async Task<ProjectGitStatus> Status()
    {
        var output = await Checked(new[] { "status", "--porcelain=v2", "--branch", "--ignored=matching", "--", Name })
            .ConfigureAwait(false);

        string? branch = null, upstream = null;
        int ahead = 0, behind = 0;
        var state = ProjectGitState.Committed;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                // "(detached)" is what a checked out commit reads as.
                branch = line[14..] == "(detached)" ? null : line[14..];
            }
            else if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                upstream = line[18..];
            }
            else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var counts = line[12..].Split(' ');
                ahead = int.Parse(counts[0].TrimStart('+'), CultureInfo.InvariantCulture);
                behind = int.Parse(counts[1].TrimStart('-'), CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("u ", StringComparison.Ordinal))
            {
                state = ProjectGitState.Conflicted;
            }
            else if (line.StartsWith("! ", StringComparison.Ordinal))
            {
                state = ProjectGitState.Ignored;
            }
            else if (line.StartsWith("? ", StringComparison.Ordinal))
            {
                state = ProjectGitState.Untracked;
            }
            else if (line.StartsWith("1 ", StringComparison.Ordinal) || line.StartsWith("2 ", StringComparison.Ordinal))
            {
                state = ProjectGitState.Modified;
            }
        }

        return new ProjectGitStatus(branch, upstream, ahead, behind, state);
    }

    /// <summary>The commits that touched the project file, newest first.</summary>
    public async Task<IReadOnlyList<ProjectGitCommit>> History(int count)
    {
        // A repository with no commit yet has no HEAD for log to start from, which is not a failure.
        var (code, output, _) = await Run(
            new[] { "log", $"-n{count}", "--format=%H%x1f%an%x1f%at%x1f%s", "--", Name })
            .ConfigureAwait(false);

        if (code != 0)
        {
            return Array.Empty<ProjectGitCommit>();
        }

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\x1f'))
            .Where(fields => fields.Length == 4)
            .Select(fields => new ProjectGitCommit(
                fields[0],
                fields[1],
                DateTimeOffset.FromUnixTimeSeconds(long.Parse(fields[2], CultureInfo.InvariantCulture)),
                fields[3]))
            .ToList();
    }

    /// <summary>Commits the project file as it is on disk, and nothing else.</summary>
    /// <remarks>
    /// Named on the commit as well as the add, so whatever else somebody had staged in the same
    /// repository stays staged and out of this commit. Except during a merge, cherry-pick or revert,
    /// which git refuses to commit partially, so that commit takes everything staged. The first commit also takes
    /// the attributes <see cref="Init"/> staged beside the file.
    /// </remarks>
    public async Task Commit(string message)
    {
        await Stage().ConfigureAwait(false);

        if (await InMerge().ConfigureAwait(false) || await Has("CHERRY_PICK_HEAD").ConfigureAwait(false)
            || await Has("REVERT_HEAD").ConfigureAwait(false))
        {
            await Checked(new[] { "commit", "-m", message }).ConfigureAwait(false);
            return;
        }

        var first = await Head().ConfigureAwait(false) is null
                    && (await Checked(new[] { "ls-files", "--", ".gitattributes" }).ConfigureAwait(false)).Length > 0;

        await Checked(
            first
                ? new[] { "commit", "-m", message, "--", Name, ".gitattributes" }
                : new[] { "commit", "-m", message, "--", Name }).ConfigureAwait(false);
    }

    public Task Fetch()
        => Checked(new[] { "fetch" }, remote: true);

    /// <summary>Brings the branch up to its upstream, refusing rather than merging when the two have diverged.</summary>
    /// <remarks>
    /// Fast-forward only because a merge would write conflict markers into the project, which the
    /// window cannot then open.
    /// </remarks>
    public Task Pull()
        => Checked(new[] { "pull", "--ff-only" }, remote: true);

    /// <summary>Pushes the branch, giving it an upstream on the only remote when it has none yet.</summary>
    public async Task Push(ProjectGitStatus status)
    {
        if (status.Branch is null)
        {
            throw new ProjectGitException("No branch is checked out. Switch to a branch, or make one, before pushing.");
        }

        if (status.Upstream is { })
        {
            await Checked(new[] { "push" }, remote: true).ConfigureAwait(false);
            return;
        }

        var remotes = (await Checked(new[] { "remote" }).ConfigureAwait(false))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (remotes.Length != 1)
        {
            throw new ProjectGitException(remotes.Length == 0
                ? "This repository has no remote to push to."
                : $"The branch has no upstream, and there are {remotes.Length} remotes to choose from. "
                  + "Set one with git push -u.");
        }

        await Checked(new[] { "push", "-u", remotes[0], status.Branch }, remote: true).ConfigureAwait(false);
    }

    private async Task<string> Checked(IEnumerable<string> arguments, bool remote = false)
    {
        var (code, output, error) = await Run(arguments, remote).ConfigureAwait(false);

        if (code != 0)
        {
            throw new ProjectGitException(error.Trim() is { Length: > 0 } said ? said : output.Trim());
        }

        return output;
    }

    /// <param name="remote">Whether it talks to a remote, which gets a time limit and an ssh that cannot prompt.</param>
    private async Task<(int Code, string Output, string Error)> Run(IEnumerable<string> arguments, bool remote = false)
    {
        var start = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = Directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // Nobody can answer a prompt from a window, so a remote that wants a password fails with a
        // message instead of waiting forever for a terminal that is not there.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "never";
        start.Environment["GIT_LITERAL_PATHSPECS"] = "1";

        // GIT_TERMINAL_PROMPT does not reach ssh, which asks on the tty Studio was started from, if
        // any, and waits there. Only where the user has not chosen an ssh command of their own.
        if (remote && !start.Environment.ContainsKey("GIT_SSH_COMMAND") && !start.Environment.ContainsKey("GIT_SSH")
            && (await Run(new[] { "config", "--get", "core.sshCommand" }).ConfigureAwait(false)).Code != 0)
        {
            start.Environment["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes";
        }

        using var limit = new CancellationTokenSource();

        if (remote)
        {
            limit.CancelAfter(RemoteTimeout);
        }

        using var process = Process.Start(start)
                            ?? throw new ProjectGitException($"{Executable} could not be started.");

        process.StandardInput.Close();

        // Decoded from the bytes rather than read as text, which strips a byte order mark from a
        // blob that has one; ProjectDocument.Load keeps it for the same reason.
        using var bytes = new MemoryStream();
        var output = process.StandardOutput.BaseStream.CopyToAsync(bytes, limit.Token);
        var error = process.StandardError.ReadToEndAsync(limit.Token);

        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);

            throw new ProjectGitException($"git gave no answer in {RemoteTimeout.TotalSeconds:0} seconds and was stopped.");
        }

        await output.ConfigureAwait(false);

        return (process.ExitCode, new UTF8Encoding(false).GetString(bytes.ToArray()), await error.ConfigureAwait(false));
    }

    /// <summary>The first real <c>git</c> in the usual places and then on PATH.</summary>
    /// <remarks>
    /// A GUI app on macOS gets launchd's PATH, which has no Homebrew in it, and <c>/usr/bin/git</c>
    /// there is a stub that opens an installer when the Command Line Tools are missing — so the
    /// stub is skipped and the tools it would have run are looked for directly.
    /// </remarks>
    private static string? Find()
    {
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        var known = OperatingSystem.IsMacOS()
            ? new[]
            {
                "/opt/homebrew/bin", "/usr/local/bin", "/Library/Developer/CommandLineTools/usr/bin",
                "/Applications/Xcode.app/Contents/Developer/usr/bin"
            }
            : OperatingSystem.IsWindows()
                ? new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd") }
                : Array.Empty<string>();

        var searched = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(directory => !(OperatingSystem.IsMacOS() && directory.TrimEnd('/') == "/usr/bin"));

        return known.Concat(searched)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
    }
}
