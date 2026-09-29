// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Svg.CodeGen.Skia.Projects;
using Svg.Expressions.Recipes;

namespace Svg.Studio;

/// <summary>The project file's place in its repository: the branch, what changed, the remote, the history.</summary>
/// <remarks>
/// It knows nothing of the window. Saving, reopening and reporting are handed in, since each of
/// them already has one way it is done there.
/// </remarks>
public sealed class ChangesPanel : UserControl
{
    /// <summary>Rows past this are counted rather than built: a first commit of an import is a thousand of them.</summary>
    private const int Listed = 200;

    private readonly TextBlock _branch = new() { FontSize = 12, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _branches;
    private readonly TextBlock _state = new() { FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _message = new() { FontSize = 12, PlaceholderText = "Commit message" };
    private readonly Button _create;
    private readonly Button _resolve;
    private readonly Button _abort;
    private readonly Button _commit;
    private readonly Button _fetch;
    private readonly Button _pull;
    private readonly Button _push;
    private readonly StackPanel _remote;
    private readonly DockPanel _committing = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _lists = new() { Spacing = 6 };
    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly StackPanel _history = new() { Spacing = 6 };

    private string? _path;
    private int _tracking;
    private ProjectGit? _git;
    private ProjectGitStatus? _status;
    private bool _merging;
    private string? _rebasing;
    private bool _busy;

    /// <summary>Whether this conflict has been offered for resolving already, so a refresh does not offer it again.</summary>
    private bool _offered;

    /// <summary>HEAD's version of the project, read once per commit.</summary>
    private (string? Hash, ProjectDocument? Document)? _head;

    private IReadOnlyList<ProjectChange> _changes = Array.Empty<ProjectChange>();

    public ChangesPanel()
    {
        _branches = new Button
        {
            Content = _branch,
            Padding = new Thickness(6, 2),
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            [ToolTip.TipProperty] = "Branches"
        };

        _create = Action("Create repository", CreateRepository);
        _resolve = Action("Resolve…", Resolve);
        _abort = Action("Abort merge", AbortMerge);
        _commit = Action("Commit", () => Commit(_message.Text?.Trim() ?? string.Empty));
        _fetch = Action("Fetch", () => Remote("Fetching…", "The fetch failed", git => git.Fetch()));
        _pull = Action("Pull", Pull);
        _push = Action("Push", () => Remote("Pushing…", "The push failed", git => git.Push(_status!)));

        _message.TextChanged += (_, _) => Enable();

        // Enter commits, as it finishes every other one-line box in the window.
        _message.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && _commit.IsEnabled)
            {
                e.Handled = true;
                await Commit(_message.Text?.Trim() ?? string.Empty);
            }
        };

        _remote = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _fetch, _pull, _push } };
        var merging = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _resolve, _abort } };
        DockPanel.SetDock(_commit, Dock.Right);
        _commit.Margin = new Thickness(6, 0, 0, 0);
        _committing.Children.Add(_commit);
        _committing.Children.Add(_message);

        var top = new StackPanel
        {
            Margin = new Thickness(10, 10, 10, 8),
            Spacing = 4,
            Children = { _branches, _state, _create, merging, _remote, _committing }
        };

        _lists.Children.Add(Heading("Changes"));
        _lists.Children.Add(_list);
        _lists.Children.Add(Heading("History"));
        _lists.Children.Add(_history);

        var body = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        body.Children.Add(top);
        body.Children.Add(new ScrollViewer { Content = _lists, Padding = new Thickness(10, 0, 10, 10) });

        Content = body;

        Enable();
    }

    /// <summary>The repository the open project is in, or null while it is in none.</summary>
    public ProjectGit? Git => _git;

    /// <summary>How the open project differs from its last commit, unsaved edits included.</summary>
    public IReadOnlyList<ProjectChange> Changes => _changes;

    /// <summary>The branch as the window title reads it, such as <c>main ↑1 ↓2, merging</c>, or null outside a repository.</summary>
    public string? Summary => _status is { } status
        ? (_rebasing ?? status.Branch ?? "detached")
          + (status.Ahead > 0 ? $" ↑{status.Ahead}" : string.Empty)
          + (status.Behind > 0 ? $" ↓{status.Behind}" : string.Empty)
          + (_rebasing is { } ? ", rebasing" : _merging || status.State == ProjectGitState.Conflicted ? ", merging" : string.Empty)
        : null;

    /// <summary>Raised when <see cref="Changes"/> has been worked out again, and after every refresh of what git says.</summary>
    public event EventHandler? Compared;

    /// <summary>Writes the project so what is committed is what the window shows; false is a refusal already said.</summary>
    public Func<Task<bool>> Saving { get; set; } = () => Task.FromResult(true);

    /// <summary>Whether the window holds anything not on disk, which a pull would be written under.</summary>
    public Func<bool> Unsaved { get; set; } = () => false;

    /// <summary>Opens the project at the path again from its file, after git has rewritten it.</summary>
    public Func<string, Task> Reopen { get; set; } = _ => Task.CompletedTask;

    /// <summary>The project the window holds, which is what the changes are of.</summary>
    public Func<ProjectDocument?> Document { get; set; } = () => null;

    /// <summary>Opens a node of the open project, for a change row that is clicked.</summary>
    public Func<ProjectNode, Task> Open { get; set; } = _ => Task.CompletedTask;

    public Func<string, string, Task> Announce { get; set; } = (_, _) => Task.CompletedTask;

    /// <summary>Asks a question with <c>accept</c> as the button that says yes.</summary>
    public Func<string, string, string, Task<bool>> Confirm { get; set; } = (_, _, _) => Task.FromResult(false);

    /// <summary>Asks for a name, returning null when nothing was given.</summary>
    public Func<string, string, Task<string?>> AskName { get; set; } = (_, _) => Task.FromResult<string?>(null);

    /// <summary>Settles the conflicts a merge left; see <see cref="MainWindow.ResolveConflicts"/>.</summary>
    public Func<ProjectMerge, Task<bool?>> ResolveConflicts { get; set; } = _ => Task.FromResult<bool?>(null);

    /// <summary>Points the panel at the project file <paramref name="path"/>, or at nothing.</summary>
    /// <returns>Whether there is anything to show: a saved project, and a git to ask about it.</returns>
    public async Task<bool> Track(string? path)
    {
        var tracking = ++_tracking;
        var shown = path is { } && ProjectGit.Installed is { } && File.Exists(path) ? path : null;
        var git = shown is { } ? await ProjectGit.For(shown).ConfigureAwait(true) : null;

        // A later call has pointed the panel somewhere else while git was being asked.
        if (tracking != _tracking)
        {
            return false;
        }

        _path = shown;
        _git = git;
        _status = null;
        _head = null;
        _offered = false;

        await Refresh().ConfigureAwait(true);

        return _path is { };
    }

    /// <summary>Asks git again, for a save, a return to the window, or a command of its own.</summary>
    public async Task Refresh()
    {
        if (_busy)
        {
            return;
        }

        if (_git is not { } git)
        {
            _status = null;
            _branch.Text = null;
            _state.Text = _path is { } ? "Not in a repository." : null;
            _list.Children.Clear();
            _history.Children.Clear();
            Recompare();
            return;
        }

        try
        {
            var status = await git.Status().ConfigureAwait(true);
            var hash = await git.Head().ConfigureAwait(true);
            var head = _head is { } known && known.Hash == hash
                ? known
                : (hash, hash is { } ? Parsed(await git.At("HEAD").ConfigureAwait(true), Path.Combine(git.Directory, git.Name)) : null);
            var merging = await git.InMerge().ConfigureAwait(true);
            var rebasing = await git.Rebasing().ConfigureAwait(true);
            var branches = await git.Branches().ConfigureAwait(true);
            var history = await git.History(50).ConfigureAwait(true);

            // A command, or a track, that ran while these were asked has said something newer, and
            // HEAD's parse is cached by hash alone, which another project in the repository shares.
            if (!ReferenceEquals(git, _git))
            {
                return;
            }

            _head = head;
            _merging = merging;
            _rebasing = rebasing;
            _status = status;
            Show(git, status, branches, history);
        }
        catch (ProjectGitException failure)
        {
            if (!ReferenceEquals(git, _git))
            {
                return;
            }

            _status = null;
            _state.Text = failure.Message;
        }

        Recompare();

        if (_status is not { State: ProjectGitState.Conflicted })
        {
            _offered = false;
        }
        else if (!_offered && _rebasing is null)
        {
            // Once per conflict: closing the merge window hands focus back to the window, whose
            // return refreshes, which would offer it again for ever.
            _offered = true;
            await Resolve(git, opening: false).ConfigureAwait(true);
        }
    }

    /// <summary>Compares HEAD's version with the window's again, which an edit changes and git does not.</summary>
    public void Recompare()
    {
        _changes = _git is { } && _head is { } head && Document() is { } now
            ? ProjectChanges.Compare(head.Document, now)
            : Array.Empty<ProjectChange>();

        _list.Children.Clear();

        foreach (var change in _changes.Take(Listed))
        {
            _list.Children.Add(Row(change));
        }

        if (_changes.Count > Listed)
        {
            _list.Children.Add(new TextBlock { Text = $"and {_changes.Count - Listed} more", FontSize = 11, Opacity = 0.6 });
        }
        else if (_changes.Count == 0 && _git is { })
        {
            _list.Children.Add(new TextBlock { Text = "Nothing changed since the last commit.", FontSize = 11, Opacity = 0.6 });
        }

        // An edit is what makes a clean project committable.
        Enable();
        Compared?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What a change is marked with, in the list and on the tree.</summary>
    public static string Mark(ProjectChangeKind kind) => kind switch
    {
        ProjectChangeKind.Added => "+",
        ProjectChangeKind.Removed => "−",
        ProjectChangeKind.Changed => "●",
        ProjectChangeKind.Moved => "↔",
        _ => "⚙"
    };

    public static IBrush Brush(ProjectChangeKind kind) => kind switch
    {
        ProjectChangeKind.Added => Brushes.SeaGreen,
        ProjectChangeKind.Removed => Brushes.IndianRed,
        ProjectChangeKind.Changed => Brushes.DarkOrange,
        ProjectChangeKind.Moved => Brushes.SteelBlue,
        _ => Brushes.MediumPurple
    };

    /// <summary>Makes a repository for a project that is in none, ready for its first commit.</summary>
    public async Task CreateRepository()
    {
        if (_path is not { } path || _git is { })
        {
            return;
        }

        try
        {
            await ProjectGit.Init(path).ConfigureAwait(true);
        }
        catch (ProjectGitException failure)
        {
            await Announce("The repository couldn't be created", failure.Message).ConfigureAwait(true);
            return;
        }

        _message.Text = $"Add {Path.GetFileName(path)}";

        await Track(path).ConfigureAwait(true);
    }

    /// <summary>Saves the project and commits it with <paramref name="message"/>.</summary>
    public async Task Commit(string message)
    {
        if (_git is not { } git || string.IsNullOrWhiteSpace(message) || !await Saving().ConfigureAwait(true))
        {
            return;
        }

        await Run("Committing…", "The commit failed", () => git.Commit(message.Trim())).ConfigureAwait(true);

        if (_status is { State: ProjectGitState.Committed } && !_merging)
        {
            _message.Text = null;
        }
    }

    public Task Pull() => Rewrite("pulling", "Pulling…", "The pull failed", async git =>
    {
        try
        {
            await git.Pull().ConfigureAwait(true);
            return false;
        }
        catch (ProjectGitException)
        {
            // The pull has fetched, so the counts are against what the remote has now.
            if (await git.Status().ConfigureAwait(true) is not { Ahead: > 0, Behind: > 0 } status)
            {
                throw;
            }

            if (!await Confirm(
                    "The branches have diverged",
                    $"This branch has {status.Ahead} commits the upstream has not, and it has {status.Behind} this one has not. "
                    + "Merging them makes a merge commit, and may stop on drawings changed on both.",
                    "Merge").ConfigureAwait(true))
            {
                return false;
            }

            return await git.PullMerge().ConfigureAwait(true);
        }
    });

    public Task Switch(string branch) => Rewrite("switching branches", "Switching…", "The switch failed", async git =>
    {
        await git.Switch(branch).ConfigureAwait(true);
        return false;
    });

    /// <summary>Merges <paramref name="branch"/> into the one checked out, resolving what it stops on.</summary>
    public Task Merge(string branch) => Rewrite("merging", "Merging…", "The merge failed", git => git.Merge(branch));

    /// <summary>Asks for a name, and makes and switches to a branch of it.</summary>
    public async Task CreateBranch()
    {
        if (_git is not { } git || await AskName("New branch", "What to call the branch. It starts from the commit checked out now, "
                                                                + "and the project stays as it is.").ConfigureAwait(true) is not { Length: > 0 } name)
        {
            return;
        }

        await Run("Creating…", "The branch couldn't be created", () => git.CreateBranch(name.Trim(), switchTo: true))
            .ConfigureAwait(true);
    }

    public Task DeleteBranch(string branch)
        => Remote("Deleting…", "The branch couldn't be deleted", git => git.DeleteBranch(branch));

    /// <summary>Offers the conflicts of the merge in progress again, for a merge window that was closed unanswered.</summary>
    public Task Resolve() => _git is { } git ? Resolve(git, opening: false) : Task.CompletedTask;

    /// <summary>Resolves the merge <paramref name="path"/> is stopped in, which is why it cannot be opened.</summary>
    /// <returns>Whether the file was in a merge, and has been dealt with or said about.</returns>
    public async Task<bool> Resolve(string path)
    {
        if (await ProjectGit.For(path).ConfigureAwait(true) is not { } git)
        {
            return false;
        }

        try
        {
            if ((await git.Status().ConfigureAwait(true)).State != ProjectGitState.Conflicted)
            {
                return false;
            }
        }
        catch (ProjectGitException)
        {
            return false;
        }

        await Resolve(git, opening: true).ConfigureAwait(true);

        return true;
    }

    /// <summary>Whether the tracked project is in conflict, asked of git now rather than of the last refresh.</summary>
    public async Task<bool> Conflicted()
    {
        try
        {
            return _git is { } git && (await git.Status().ConfigureAwait(true)).State == ProjectGitState.Conflicted;
        }
        catch (ProjectGitException)
        {
            return false;
        }
    }

    public async Task AbortMerge()
    {
        if (_git is not { } git)
        {
            return;
        }

        var tracking = _tracking;

        await Run("Aborting…", "The merge couldn't be aborted", () => git.AbortMerge()).ConfigureAwait(true);

        // Unsaved edits are left alone: the abort puts back our side, which is what the window was
        // opened from, since it could not open the conflicted file.
        if (tracking == _tracking && !Unsaved())
        {
            await Reopen(Path.Combine(git.Directory, git.Name)).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Merges the three versions git holds of the file by node, asking about what both sides
    /// changed, and writes the answer — or aborts the merge, or leaves it for later.
    /// </summary>
    /// <param name="opening">Whether the window is failing to open the file, which then cannot be left for later.</param>
    private async Task Resolve(ProjectGit git, bool opening)
    {
        var tracking = _tracking;
        var path = Path.Combine(git.Directory, git.Name);
        ProjectMerge merge;

        // What the window holds is older than the file resolving writes, and a save would put it back.
        if (Unsaved() && Holds(path))
        {
            await Announce("Discard before resolving", "The window holds changes that are not on disk, and resolving the "
                                                        + "merge would write the file under them. Close the project unsaved, "
                                                        + "and the merge is offered again when it opens.").ConfigureAwait(true);
            return;
        }

        try
        {
            // Stage 2 is then the upstream and stage 3 the user's own commit, so mine and theirs
            // would be offered the wrong way round, and there is no merge to abort.
            if (await git.Rebasing().ConfigureAwait(true) is { })
            {
                await Announce("A rebase is in progress", $"{git.Name} is in conflict part way through a rebase. "
                                                          + "Finish it, or abort it, with git.").ConfigureAwait(true);
                return;
            }

            var (@base, ours, theirs) = await git.Stages().ConfigureAwait(true);

            if (ours is null || theirs is null)
            {
                throw new SvgcProjectException($"{git.Name} was deleted on one side of the merge.");
            }

            merge = ProjectMerge.Of(
                @base is { } ? ProjectDocument.Parse(@base, path) : null,
                ProjectDocument.Parse(ours, path),
                ProjectDocument.Parse(theirs, path));
        }
        catch (Exception failure) when (failure is SvgcProjectException or SvgRecipeException or ProjectGitException)
        {
            await Announce("The merge can't be resolved here", failure.Message + " Abort the merge, or resolve it with git.")
                .ConfigureAwait(true);
            return;
        }

        var answer = merge.Conflicts.Count == 0 ? true : await ResolveConflicts(merge).ConfigureAwait(true);

        if (answer is null)
        {
            if (opening)
            {
                await Announce("The merge isn't resolved", $"{git.Name} is part way through a merge, and can't be opened "
                                                           + "until it is resolved or aborted.").ConfigureAwait(true);
            }

            return;
        }

        try
        {
            if (answer == true)
            {
                // Through the document, which writes the text as Save always does.
                ProjectDocument.Parse(merge.ToXml(), path).Save(path);
                await git.Stage().ConfigureAwait(true);
            }
            else
            {
                await git.AbortMerge().ConfigureAwait(true);
            }
        }
        catch (Exception failure) when (failure is ProjectGitException or SvgcProjectException or IOException or UnauthorizedAccessException)
        {
            await Announce("The merge couldn't be finished", failure.Message).ConfigureAwait(true);
            return;
        }

        if (answer == true && string.IsNullOrWhiteSpace(_message.Text))
        {
            _message.Text = "Merge";
        }

        await Reopened(path, tracking).ConfigureAwait(true);
    }

    /// <summary>Whether the window's project is the file at <paramref name="path"/>.</summary>
    private bool Holds(string path) => string.Equals(Document()?.Path, path, StringComparison.Ordinal);

    /// <summary>
    /// Opens the project again after git rewrote it, unless the panel was pointed at another project
    /// since <paramref name="tracking"/>, or the window was edited meanwhile.
    /// </summary>
    /// <remarks>
    /// Edited, reopening would ask to discard them, and a window kept as it is would save its older
    /// document over what git wrote, so it is said instead.
    /// </remarks>
    private async Task Reopened(string path, int tracking)
    {
        if (tracking != _tracking)
        {
            return;
        }

        if (Unsaved() && Holds(path))
        {
            await Announce("The file changed under unsaved edits", $"Git rewrote {Path.GetFileName(path)} while the window held "
                                                                   + "changes that are not on disk. Saving them would write over what "
                                                                   + "git wrote: close the project unsaved and open it again.")
                .ConfigureAwait(true);
            return;
        }

        await Reopen(path).ConfigureAwait(true);
    }

    private static ProjectDocument? Parsed(string? text, string path)
    {
        try
        {
            return text is { } ? ProjectDocument.Parse(text, path) : null;
        }
        catch (Exception failure) when (failure is SvgcProjectException or SvgRecipeException)
        {
            // Unreadable at HEAD, as a committed conflict would be: everything then reads as added.
            return null;
        }
    }

    private void Show(
        ProjectGit git,
        ProjectGitStatus status,
        IReadOnlyList<ProjectGitBranch> branches,
        IReadOnlyList<ProjectGitCommit> history)
    {
        _branch.Text = (status.Branch ?? "Detached HEAD")
                       + (status.Upstream is { } upstream ? $" → {upstream}" : string.Empty) + " ▾";
        _branches.Flyout = Menu(branches);

        var tracking = status.Upstream is null
            ? " No upstream yet."
            : status is { Ahead: 0, Behind: 0 }
                ? " Up to date with the upstream."
                : $" {status.Ahead} ahead, {status.Behind} behind.";

        _state.Text = _rebasing is { }
            ? "A rebase is in progress. Finish it, or abort it, with git."
            : status.State switch
            {
                ProjectGitState.Conflicted => $"{git.Name} is in conflict. Resolve it, or abort the merge.",
                _ when _merging => "Merged. Commit to finish the merge.",
                ProjectGitState.Untracked => $"{git.Name} has never been committed.",
                ProjectGitState.Ignored => $"{git.Name} is ignored by a .gitignore, so it can't be committed.",
                ProjectGitState.Modified => "Saved changes not yet committed.",
                _ => "Everything saved is committed."
            } + tracking;

        _history.Children.Clear();

        foreach (var commit in history)
        {
            _history.Children.Add(new StackPanel
            {
                [ToolTip.TipProperty] = commit.Hash,
                Children =
                {
                    new TextBlock { Text = commit.Subject, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock
                    {
                        Text = $"{commit.Author} · {commit.When.LocalDateTime.ToString("g", CultureInfo.CurrentCulture)}",
                        FontSize = 11,
                        Opacity = 0.6
                    }
                }
            });
        }
    }

    /// <summary>The branch drop-down: the branches to switch to, and what can be done with them.</summary>
    private MenuFlyout Menu(IReadOnlyList<ProjectGitBranch> branches)
    {
        var menu = new MenuFlyout();
        var others = branches.Where(branch => !branch.Current).ToList();

        foreach (var branch in branches)
        {
            menu.Items.Add(Item(
                branch.Remote is { } remote ? $"{branch.Name} ({remote})" : branch.Name,
                branch.Current ? null : () => Switch(branch.Name),
                branch.Current));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("New branch…", CreateBranch));

        var merging = Item("Merge into current", null);
        var deleting = Item("Delete", null);

        foreach (var branch in others)
        {
            merging.Items.Add(Item(branch.Remote ?? branch.Name, () => Merge(branch.Remote ?? branch.Name)));

            if (branch.Remote is null)
            {
                deleting.Items.Add(Item(branch.Name, () => DeleteBranch(branch.Name)));
            }
        }

        merging.IsEnabled = merging.Items.Count > 0;
        deleting.IsEnabled = deleting.Items.Count > 0;
        menu.Items.Add(merging);
        menu.Items.Add(deleting);

        return menu;
    }

    private static MenuItem Item(string header, Func<Task>? click, bool current = false)
    {
        var item = new MenuItem { Header = header };

        if (current)
        {
            item.ToggleType = MenuItemToggleType.CheckBox;
            item.IsChecked = true;
        }

        if (click is { })
        {
            item.Click += async (_, _) => await click();
        }

        return item;
    }

    private Control Row(ProjectChange change)
    {
        var node = (change.After ?? change.Before)!;
        var compare = Action("Compare", () =>
        {
            if (TopLevel.GetTopLevel(this) is Window owner)
            {
                new ProjectCompareWindow(change).Show(owner);
            }

            return Task.CompletedTask;
        });

        var open = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 1),
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            IsEnabled = change.After is { },
            [ToolTip.TipProperty] = change.Key.Length == 0 ? null : change.Key,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = Mark(change.Kind), Foreground = Brush(change.Kind), FontSize = 12, Width = 12 },
                    new TextBlock { Text = ProjectWorkspace.Label(node), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis }
                }
            }
        };

        open.Click += async (_, _) =>
        {
            if (change.After is { } after)
            {
                await Open(after);
            }
        };

        var row = new DockPanel();
        DockPanel.SetDock(compare, Dock.Right);
        row.Children.Add(compare);
        row.Children.Add(open);

        return row;
    }

    private void Enable()
    {
        var outside = _git is null && _path is { };
        var ready = _git is { } && _status is { } && !_busy;
        var conflicted = _status is { State: ProjectGitState.Conflicted };
        var settled = ready && !conflicted && _rebasing is null;

        _branches.IsVisible = _git is { };
        _remote.IsVisible = _committing.IsVisible = _lists.IsVisible = _git is { };
        _create.IsVisible = outside;
        _create.IsEnabled = outside && !_busy;
        _resolve.IsVisible = conflicted && _rebasing is null;

        // Only a merge can be aborted with merge --abort; a cherry-pick or a popped stash cannot.
        _abort.IsVisible = _resolve.IsVisible && _merging;
        _resolve.IsEnabled = _abort.IsEnabled = ready;

        _branches.IsEnabled = settled && !_merging;
        _commit.IsEnabled = settled && !string.IsNullOrWhiteSpace(_message.Text) && _status!.State != ProjectGitState.Ignored
                            && (_status.State != ProjectGitState.Committed || _merging || Unsaved());
        _fetch.IsEnabled = ready;
        _pull.IsEnabled = settled && !_merging && _status!.Upstream is { };
        _push.IsEnabled = settled && !_merging && _status!.Branch is { };
        _message.IsEnabled = ready;
    }

    /// <summary>
    /// Runs a command that may rewrite the project file, refused while the window holds unsaved
    /// work, and reopens the project when the bytes changed under it.
    /// </summary>
    /// <param name="command">True when it stopped conflicted, which the refresh after it resolves.</param>
    private async Task Rewrite(string doing, string running, string failed, Func<ProjectGit, Task<bool>> command)
    {
        if (_git is not { } git)
        {
            return;
        }

        // Refused rather than saved first: the file is about to be replaced, and what is typed into the
        // window would be lost under it or written over what git brought.
        if (Unsaved())
        {
            await Announce($"Save before {doing}", "The window holds changes that are not on disk. "
                                                  + "Save them, or close them unsaved, and try again.").ConfigureAwait(true);
            return;
        }

        var tracking = _tracking;
        var path = Path.Combine(git.Directory, git.Name);
        var before = Read(path);
        var conflicted = false;

        await Run(running, failed, async () => conflicted = await command(git).ConfigureAwait(true)).ConfigureAwait(true);

        if (!conflicted && !Read(path).AsSpan().SequenceEqual(before))
        {
            await Reopened(path, tracking).ConfigureAwait(true);
        }
    }

    private static byte[] Read(string path) => File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();

    private Task Remote(string doing, string failed, Func<ProjectGit, Task> command)
        => _git is { } git ? Run(doing, failed, () => command(git)) : Task.CompletedTask;

    private async Task Run(string doing, string failed, Func<Task> command)
    {
        _busy = true;
        _state.Text = doing;
        Enable();

        ProjectGitException? refusal = null;

        try
        {
            await command().ConfigureAwait(true);
        }
        catch (ProjectGitException failure)
        {
            refusal = failure;
        }
        finally
        {
            _busy = false;
        }

        await Refresh().ConfigureAwait(true);

        if (refusal is { })
        {
            await Announce(failed, refusal.Message).ConfigureAwait(true);
        }
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 11,
        FontWeight = FontWeight.SemiBold,
        Opacity = 0.7,
        Margin = new Thickness(0, 6, 0, 0)
    };

    private static Button Action(string content, Func<Task> click)
    {
        var button = new Button
        {
            Content = content,
            FontSize = 11,
            Padding = new Thickness(8, 2),
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center
        };

        button.Click += async (_, _) => await click();

        return button;
    }
}
