---
description: Make a desk — a worktree of the hub for one agent to work in — or take one away. The only way a worktree is ever created or removed.
argument-hint: [name] | remove <name>
allowed-tools: Bash(git rev-parse:*), Bash(git worktree:*), Bash(git fetch:*), Bash(git status:*), Bash(git log:*), Bash(git rev-list:*), Bash(git branch:*), Bash(git ls-tree:*), Bash(git cat-file:*), Bash(git config:*), Bash(git submodule:*), Bash(ls:*), Bash(du:*)
---

A desk is a worktree for one agent. It is not a branch: the agent in it branches, switches and
lands as often as it likes, and the desk stays. Desks rest detached on `origin/master`, which is
what lets every one of them sit at the latest master at once — git refuses a branch in two
worktrees, not a commit. `main/` is the exception: it holds `master` itself, for committing
straight to it, and is never made or removed here.

**Only `main/` is ever on `master`.** Any other desk that ends up there — by `/land`, or by hand —
goes back to `git switch --detach origin/master` before it is left. While it holds `master`,
`main/` cannot switch to it: git refuses with `'master' is already used by worktree at …`.

**Invoking this is the permission to make or remove a worktree.** Nothing else makes one: never
create, remove or move into a worktree unless this command was run or I said so in as many words.

Arguments: **$ARGUMENTS**

## Where the hub is

```sh
git rev-parse --path-format=absolute --git-common-dir     # <hub>/.bare
```

Its parent is the hub. Stop and say so if the common directory is not called `.bare`, or the hub
has no `.modules/` — this is not the layout these instructions are for.

## Making one: `/desk [name]`

1. **Name it.** The argument, or the first free `desk-1`, `desk-2`, … Refuse `main`, a name with a
   slash, and a name already taken by a directory in the hub.

2. **Add it, detached at the remote's master:**

   ```sh
   git -C <hub> fetch origin --prune
   git -C <hub> worktree add --detach <hub>/<name> origin/master
   ```

3. **Put its submodules in, borrowing history from `.modules/`.** Read the paths from `.gitmodules`
   rather than from this file. For each, `.modules/<last part of the path>.git` must hold the commit
   the desk pins (`git -C <hub>/<name> ls-tree HEAD <path>`); if it does not, the submodule has
   moved since the mirror was made, so `git -C <hub>/.modules/<x>.git fetch origin` — the only time
   this touches the network. Then:

   ```sh
   git -C <hub>/<name> submodule update --init --reference <hub>/.modules/<x>.git -- <path>
   ```

   No `--dissociate`: the desk borrows the mirror's objects rather than copying them, which is what
   keeps a desk to the ~120 MB of files it checks out. That is safe because `.modules/` is never
   removed or pruned. Without its submodules a desk does not build — `src/Svg.Custom` compiles
   straight out of `externals/SVG`.

4. **Report** the path, the commit it rests on, its size, and how to start an agent at it:
   `cd <hub>/<name> && claude`. Do not build it, and do not start working in it yourself — it is
   for another agent.

## Taking one away: `/desk remove <name>`

1. **Refuse** `main`, and the desk this session is running in.

2. **Refuse anything that would be lost**, and say what it is:
   - uncommitted work — `git -C <desk> status --short`, and inside each submodule
     (`git -C <desk> submodule foreach --quiet 'git status --short'`);
   - commits no remote has — `git -C <desk> rev-list --count HEAD --not --remotes` must be `0`.

   A branch the desk has checked out is not lost: branches belong to the repository, not the desk,
   and one that has not merged stays for `/land` or for me. Say which branch it was.

3. **Delete the directory, then prune:**

   ```sh
   rm -rf <hub>/<name>
   git -C <hub> worktree prune
   ```

   Not `git worktree remove`: git refuses to move or remove any worktree that has submodules, and
   even deinitialising them is not enough while the worktree's own submodule git directory exists.
   The checks above are what `remove` would have done; the prune takes the rest, submodule data and
   all.

Report what was removed and which branch, if any, it had been on.
