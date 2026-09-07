---
description: Push the current branch, open a PR against a target branch, merge it, and land; or push and finish a PR that already exists
argument-hint: [target-branch, or the number of a PR that already exists]
allowed-tools: SlashCommand, Bash(git status:*), Bash(git diff:*), Bash(git log:*), Bash(git ls-files:*), Bash(git add:*), Bash(git commit:*), Bash(git push:*), Bash(git branch:*), Bash(git rev-parse:*), Bash(git rev-list:*), Bash(git checkout:*), Bash(git switch:*), Bash(git restore:*), Bash(git pull:*), Bash(git fetch:*), Bash(gh auth:*), Bash(gh pr:*), Bash(gh repo:*), Bash(dotnet build:*), Bash(dotnet test:*), Bash(dotnet format:*)
---

Take a branch all the way in: push it, open a pull request, merge that, and clean up after it.

**It always pushes first**, whichever form is used, so what merges is what I have in front of me.

**$1** is either the branch to merge into, or the number of a pull request `/pr` has already
opened — a value that is all digits is a number, anything else is a branch name.

**Invoking this is the permission for all of it** — the commit, the push, the merge, and deleting
the branch at both ends. CLAUDE.md says to ask before committing or branching; this command is the
asking. Do not stop to confirm each step.

Stop at the first thing that looks wrong. Never force, never `-D`, never merge past a red build.

1. **Get a pull request to merge, and the branch it is going into.**

   **`$1` is a branch name:** run `/pr $1`. It checks where I am, checks `gh`, runs `/push` — the
   diff, the formatting, the build, the tests, the message — and opens the pull request against
   `$1`, pinned to my repository. Let it do all of that rather than repeating any of it here. If it
   stops, this stops with it: nothing below should run against a failing build, a test you have not
   seen, or a branch with nothing on it. The target is `$1`.

   **`$1` is a number:** the pull request is already open, so do not run `/pr` — but this still
   pushes, because whatever I have been doing since it was opened is what I mean to merge. Read the
   pull request first: where I am standing decides whether pushing is safe, and the target is
   something GitHub already knows.

   ```sh
   gh pr view --repo SamoZ256/Svg.Skia $1 --json number,state,headRefName,baseRefName,url
   ```

   The target is its `baseRefName`. Stop if its state is not `OPEN`.

   Then **run `/push`, but only while I am on the pull request's head branch.** On any other branch
   it would commit whatever is lying around there and push it somewhere this merge is not about;
   leave it alone, say which branch was left and that nothing was pushed, and carry on to step 2
   with what the pull request already has.

   `/push` stops when there is nothing to commit. For a branch that has already been pushed that is
   the ordinary case, not a failure — carry on. What it does not cover is a clean tree with commits
   the remote has not got, since it stops before pushing: check `git log @{u}..HEAD` afterwards and
   `git push` if anything is there. Merging a pull request that does not have my latest work in it
   is the thing this step exists to prevent.

   Either way, record three things: the number, the head branch — which is the one to delete at the
   end — and the target branch.

2. **Merge it** with that number:

   ```sh
   gh pr merge --repo SamoZ256/Svg.Skia <number> --merge
   ```

   The number is not optional. With `--repo` pinned, `gh` cannot infer the pull request from the
   current branch and prints its usage instead of merging — which reads like a refusal and is easy
   to mistake for one.

   A merge commit, not a squash and not a rebase, because that is how this repository's history
   reads and squashing would throw away the commit bodies `/push` just wrote. Confirm it really
   merged before going on — `gh pr view --repo SamoZ256/Svg.Skia <number> --json state,mergeCommit`.

   **Do not pass `--delete-branch`.** `gh` would delete the local branch and switch away, which is
   step 4's job — it would then find nothing to do and report success for work it never did.

3. **Delete the remote branch**: `git push origin --delete <head branch>`. This is what gives the
   prune in the next step something to report, and keeps merged branches from accumulating on the
   remote.

4. **Run `/land <target branch>`** — but only while I am on the pull request's head branch. That is
   always so when this ran `/pr`, and may not be when a number was passed: `/land` deletes the
   branch I am on, and if that is not the one that merged it would be deleting the wrong thing.
   Where I am somewhere else, skip it, run `git fetch --prune` instead, and say which local branch
   was left alone.

   Otherwise let `/land` do its own checking — do not pre-empt its steps or skip it because you
   already know the answer.

Report the pull request number, what the target branch moved to, and whatever `/land` says about the
final state. If `/land` reports that more came down than this branch's own commits, repeat that
prominently: it means the target moved while the branch was open, and what is local is no longer
what was tested.
