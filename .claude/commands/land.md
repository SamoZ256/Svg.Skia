---
description: After a branch is merged upstream, move this desk onto the target, bring it up to date, and delete the merged branch
argument-hint: [target-branch]
allowed-tools: Bash(git status:*), Bash(git branch:*), Bash(git checkout:*), Bash(git switch:*), Bash(git pull:*), Bash(git fetch:*), Bash(git log:*), Bash(git rev-list:*), Bash(git rev-parse:*), Bash(git worktree:*)
---

The branch I am on has been merged into `$1` somewhere else (a pull request, usually). Bring
this desk up to date and clean up.

Target branch: **$1**

Do this in order, stopping at the first thing that looks wrong:

1. **Record the current branch.** That is the one to delete at the end. If it is already `$1`,
   there is nothing to delete: in `main/`, pull, report and stop. In any other desk, detach as
   step 3 says, then report and stop.

2. **Refuse to proceed on a dirty tree.** If `git status --short` is not empty, stop and show me
   what is uncommitted. Do not stash, do not commit, do not switch. Switching branches with
   uncommitted work either fails or carries the changes across, and neither is what I want here.

3. **Fetch, and move onto `$1`.** `git fetch origin --prune`, saying which remote branches went.
   Then see which desk this is (`git rev-parse --show-toplevel`):
   - **`main/`:** switch to `$1` and `git pull --ff-only`. It is the one desk that holds `master`.
   - **Any other desk:** `git switch --detach origin/$1`, even when `$1` is not checked out anywhere.
     A desk rests on the remote's target rather than on a local branch. Taking `master` would stop
     `main/` from switching back to it (see `/desk`). If `main/` holds `$1`, leave it alone, and
     say that it is behind until its own agent pulls.

   Report the commit it moved to, and whether that was a fast-forward.

4. **Confirm the merge actually landed** before deleting anything: the old branch should have no
   commits missing from `origin/$1` (`git rev-list --count origin/$1..<old>` is `0`), and the tree
   it points at should match. If it does not, stop and tell me which commits are unmerged. Do not
   delete.

5. **Delete the old branch with `git branch -d`.** Never `-D`. If git refuses, that refusal is
   information — surface it and stop rather than forcing it.

6. **Report the final state**: what this desk is on, `git branch -vv`, the top few commits, and
   confirm the tree is clean.

Do not run tests or a build unless I ask. If the merge commit brought down more than the branch's
own commits, say so — that means the target moved while the branch was open, and what I have
locally is no longer what I tested.
