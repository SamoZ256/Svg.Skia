---
description: List the hub's desks — which agent's worktree is where, on what, and whether it has work in it
allowed-tools: Bash(git rev-parse:*), Bash(git worktree:*), Bash(git status:*), Bash(git rev-list:*), Bash(git branch:*), Bash(git log:*)
---

Show every desk in the hub. Read-only: do not fetch, switch, commit or remove anything.

The hub is the parent of `git rev-parse --path-format=absolute --git-common-dir`. List its
worktrees with `git -C <hub> worktree list --porcelain`, skipping the bare one, and for each desk
report one line:

- **name** — the directory's name;
- **on** — the branch it has checked out, or `detached at origin/master` when it rests there, or
  the short commit when it is detached anywhere else;
- **work** — the number of changed files (`git -C <desk> status --short`), or `clean`;
- **unpushed** — `git -C <desk> rev-list --count HEAD --not --remotes`, where it is not `0`;
- **behind** — for a branch with an upstream, how far behind it is.

Mark the desk this session is running in. Say nothing about a desk beyond what these show.
