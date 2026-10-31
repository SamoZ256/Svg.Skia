// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
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

/// <summary>The repository a project sits in, driven through the <c>git</c> the user has installed.</summary>
/// <remarks>
/// The CLI rather than libgit2: LibGit2Sharp ships no SSH transport and ignores credential helpers,
/// so a remote would have meant Studio asking for and keeping passwords itself. Every command runs
/// in the folder holding the project with the project named by its bare name, so a pathspec is
/// never built from a path git may have resolved through a symlink (<c>/tmp</c> is
/// <c>/private/tmp</c> on macOS).
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

    /// <summary>The project's folder name, which is its pathspec from <see cref="Directory"/>.</summary>
    public string Name { get; }

    private string? _prefix;

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

    /// <summary>Makes a repository in the folder holding the project at <paramref name="path"/>.</summary>
    /// <remarks>Refused inside a work tree, where the project already has a repository.</remarks>
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

        return git;
    }

    /// <summary>The commit checked out, or null before the first commit.</summary>
    public async Task<string?> Head()
    {
        var (code, output, _) = await Run(new[] { "rev-parse", "-q", "--verify", "HEAD" })
            .ConfigureAwait(false);

        return code == 0 ? output.Trim() : null;
    }

    /// <summary>
    /// The project's files at <paramref name="revision"/>, by path inside the project, or null when
    /// it is not there or the revision does not exist.
    /// </summary>
    /// <remarks>
    /// <c>archive</c>, which writes each file as checkout would: a bare blob has LF where autocrlf put
    /// CRLF on disk, and every drawing would compare as changed. One process for the lot, since a
    /// project runs to a thousand files and a process each takes seconds. Not <c>cat-file --batch
    /// --filters</c>, whose header gives the size before the filters ran, so its output cannot be
    /// cut into files. The project's fonts go into <paramref name="fonts"/> as the bytes they are.
    /// </remarks>
    public async Task<Dictionary<string, string>?> Files(string revision, IDictionary<string, ReadOnlyMemory<byte>>? fonts = null)
    {
        var (code, output, _) = await Run(new[] { "archive", "--format=tar", revision, "--", Name }, null).ConfigureAwait(false);

        if (code != 0)
        {
            return null;
        }

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var decoder = new UTF8Encoding(false);
        var root = Name + "/";

        using var reader = new TarReader(new MemoryStream(output));

        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                || entry.DataStream is not { } data
                || !entry.Name.StartsWith(root, StringComparison.Ordinal)
                || entry.Name[root.Length..] is not { } relative
                || !(ProjectPackage.Holds(relative) || fonts is { } && ProjectPackage.HoldsFont(relative)))
            {
                continue;
            }

            using var bytes = new MemoryStream();

            data.CopyTo(bytes);

            if (ProjectPackage.HoldsFont(relative))
            {
                fonts![relative] = bytes.ToArray();
            }
            else
            {
                files[relative] = decoder.GetString(bytes.ToArray());
            }
        }

        return files.Count > 0 ? files : null;
    }

    /// <summary>
    /// The common ancestor, our and their versions of a conflicted file of the project, at
    /// <paramref name="path"/> inside it; each is null when that side has none.
    /// </summary>
    public async Task<(string? Base, string? Ours, string? Theirs)> Sides(string path)
        => (await Blob($":1:./{Name}/{path}").ConfigureAwait(false),
            await Blob($":2:./{Name}/{path}").ConfigureAwait(false),
            await Blob($":3:./{Name}/{path}").ConfigureAwait(false));

    /// <inheritdoc cref="Files" />
    private async Task<string?> Blob(string name)
    {
        var (code, output, _) = await Run(new[] { "cat-file", "--filters", name }).ConfigureAwait(false);

        return code == 0 ? output : null;
    }

    /// <summary>The paths inside the project that a merge left conflicted.</summary>
    public async Task<IReadOnlyList<string>> Conflicts()
    {
        var root = await Prefix().ConfigureAwait(false) + Name + "/";

        return Entries(await Checked(new[] { "status", "--porcelain=v2", "-z", "--", Name }).ConfigureAwait(false))
            .Where(entry => entry.Kind == 'u' && entry.Path.StartsWith(root, StringComparison.Ordinal))
            .Select(entry => entry.Path[root.Length..])
            .ToList();
    }

    /// <summary>Settles a conflicted file by taking one side's version of it, which may be none.</summary>
    public async Task Take(string path, ProjectSide side)
    {
        var name = Name + "/" + path;

        if (await Blob($":{(side == ProjectSide.Theirs ? 3 : 2)}:./{name}").ConfigureAwait(false) is null)
        {
            await Checked(new[] { "rm", "-q", "--", name }).ConfigureAwait(false);
            return;
        }

        await Checked(new[] { "checkout", side == ProjectSide.Theirs ? "--theirs" : "--ours", "--", name }).ConfigureAwait(false);
        await Checked(new[] { "add", "--", name }).ConfigureAwait(false);
    }

    /// <summary>Where the folder holding the project sits in the repository, ending in a slash, or empty at its root.</summary>
    /// <remarks>Paths git prints from the root, as status and ls-tree do, are made relative to the project with it.</remarks>
    private async Task<string> Prefix()
        => _prefix ??= (await Checked(new[] { "rev-parse", "--show-prefix" }).ConfigureAwait(false)).Trim();

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

    /// <summary>Stages the project, drawings deleted and added included, for the next commit.</summary>
    public Task Stage()
        => Checked(new[] { "add", "--", Name });

    /// <remarks>
    /// The project's state is the worst of its files': one conflicted file makes it conflicted, and
    /// one drawing added or changed makes it modified. Untracked and ignored say whether the project
    /// itself is, which is its <c>studio.xml</c>.
    /// </remarks>
    public async Task<ProjectGitStatus> Status()
    {
        var output = await Checked(new[] { "status", "--porcelain=v2", "-z", "--branch", "--ignored=matching", "--", Name })
            .ConfigureAwait(false);
        var manifest = await Prefix().ConfigureAwait(false) + Name + "/" + ProjectPackage.Manifest;

        string? branch = null, upstream = null;
        int ahead = 0, behind = 0;
        var conflicted = false;
        var modified = false;
        var untracked = false;
        var ignored = false;

        foreach (var (kind, line, path) in Entries(output))
        {
            // The project's own folder, where git sums a folder nothing of is tracked up as one entry.
            var whole = manifest.StartsWith(path, StringComparison.Ordinal) && path.EndsWith('/');

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
            else if (kind == 'u')
            {
                conflicted = true;
            }
            else if (kind == '!')
            {
                ignored |= whole || path == manifest;
            }
            else if (kind == '?')
            {
                untracked |= whole || path == manifest;
                modified = true;
            }
            else if (kind is '1' or '2')
            {
                modified = true;
            }
        }

        var state = conflicted ? ProjectGitState.Conflicted
            : ignored ? ProjectGitState.Ignored
            : untracked ? ProjectGitState.Untracked
            : modified ? ProjectGitState.Modified
            : ProjectGitState.Committed;

        return new ProjectGitStatus(branch, upstream, ahead, behind, state);
    }

    /// <summary>The entries of <c>status --porcelain=v2 -z</c>: what kind each is, its line, and its path.</summary>
    /// <remarks>
    /// <c>-z</c> so a path is written as it is rather than quoted, and a rename, which is the one
    /// entry followed by a second path, has that second path skipped.
    /// </remarks>
    private static IEnumerable<(char Kind, string Line, string Path)> Entries(string output)
    {
        var fields = output.Split('\0');

        for (var at = 0; at < fields.Length; at++)
        {
            var line = fields[at];

            if (line.Length < 2)
            {
                continue;
            }

            var kind = line[0];
            var path = kind switch
            {
                '?' or '!' => line[2..],
                '1' => Field(line, 8),
                '2' => Field(line, 9),
                'u' => Field(line, 10),
                _ => string.Empty
            };

            if (kind == '2')
            {
                at++;
            }

            yield return (kind, line, path);
        }

        // Everything after the space ending the first count fields, since a path may hold spaces.
        static string Field(string line, int count)
        {
            var at = 0;

            for (var field = 0; field < count && at >= 0; field++)
            {
                at = line.IndexOf(' ', at) + 1;
            }

            return at > 0 ? line[at..] : string.Empty;
        }
    }

    /// <summary>The commits that touched the project, newest first.</summary>
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

    /// <summary>Commits the project as it is on disk, and nothing else.</summary>
    /// <remarks>
    /// Named on the commit as well as the add, so whatever else somebody had staged in the same
    /// repository stays staged and out of this commit. Except during a merge, cherry-pick or revert,
    /// which git refuses to commit partially, so that commit takes everything staged.
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

        await Checked(new[] { "commit", "-m", message, "--", Name }).ConfigureAwait(false);
    }

    public Task Fetch()
        => Checked(new[] { "fetch" }, remote: true);

    /// <summary>Brings the branch up to its upstream, refusing rather than merging when the two have diverged.</summary>
    /// <remarks>
    /// Fast-forward only because a merge may write conflict markers into a drawing, which the window
    /// cannot then open until it is resolved.
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
        var (code, output, error) = await Run(arguments, null, remote).ConfigureAwait(false);

        // Decoded from the bytes rather than read as text, which strips a byte order mark from a
        // blob that has one; ProjectPackage.Read keeps it for the same reason.
        return (code, new UTF8Encoding(false).GetString(output), error);
    }

    /// <param name="input">What to write to its standard input, or null for nothing.</param>
    private async Task<(int Code, byte[] Output, string Error)> Run(IEnumerable<string> arguments, string? input, bool remote = false)
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

        using var bytes = new MemoryStream();
        var output = process.StandardOutput.BaseStream.CopyToAsync(bytes, limit.Token);
        var error = process.StandardError.ReadToEndAsync(limit.Token);

        // Written while the output is already being read, so neither side fills its pipe waiting on the other.
        if (input is { })
        {
            var written = new UTF8Encoding(false).GetBytes(input);

            await process.StandardInput.BaseStream.WriteAsync(written, limit.Token).ConfigureAwait(false);
        }

        process.StandardInput.Close();

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

        return (process.ExitCode, bytes.ToArray(), await error.ConfigureAwait(false));
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
