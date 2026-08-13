---
description: After a branch is merged upstream, switch to the target branch, pull, and delete the merged branch
argument-hint: [target-branch]
allowed-tools: Bash(git status:*), Bash(git branch:*), Bash(git checkout:*), Bash(git switch:*), Bash(git pull:*), Bash(git fetch:*), Bash(git log:*), Bash(git rev-list:*), Bash(git rev-parse:*)
---

The branch I am on has been merged into `$1` somewhere else (a pull request, usually). Bring
this clone up to date and clean up.

Target branch: **$1**

Do this in order, stopping at the first thing that looks wrong:

1. **Record the current branch.** That is the one to delete at the end. If it is already `$1`,
   there is nothing to delete — pull, report, and stop.

2. **Refuse to proceed on a dirty tree.** If `git status --short` is not empty, stop and show me
   what is uncommitted. Do not stash, do not commit, do not switch. Switching branches with
   uncommitted work either fails or carries the changes across, and neither is what I want here.

3. **Switch to `$1` and pull.** Report what came down — the commit it moved to, and whether it
   was a fast-forward.

4. **Confirm the merge actually landed** before deleting anything: the old branch should have no
   commits missing from `$1` (`git rev-list --count $1..<old>` is `0`), and the tree it points at
   should match. If it does not, stop and tell me which commits are unmerged. Do not delete.

5. **Delete the old branch with `git branch -d`.** Never `-D`. If git refuses, that refusal is
   information — surface it and stop rather than forcing it.

6. **`git fetch --prune`** to drop remote-tracking refs for branches deleted on the remote, and
   say which ones went.

7. **Report the final state**: `git branch -vv`, the top few commits, and confirm the tree is
   clean.

Do not run tests or a build unless I ask. If the merge commit brought down more than the branch's
own commits, say so — that means the target moved while the branch was open, and what I have
locally is no longer what I tested.
