---
description: Land work by merging a pull request on the remote, never with a local git merge - push the current branch, open a PR against a target branch, merge that PR on GitHub, and land; or push and finish a PR that already exists. This is the only way work lands in this repository.
argument-hint: [target-branch, or the number of a PR that already exists]
allowed-tools: SlashCommand, Bash(git status:*), Bash(git diff:*), Bash(git log:*), Bash(git ls-files:*), Bash(git add:*), Bash(git commit:*), Bash(git push:*), Bash(git branch:*), Bash(git rev-parse:*), Bash(git rev-list:*), Bash(git checkout:*), Bash(git switch:*), Bash(git restore:*), Bash(git pull:*), Bash(git fetch:*), Bash(git merge:*), Bash(gh auth:*), Bash(gh pr:*), Bash(gh run:*), Bash(gh repo:*), Bash(dotnet build:*), Bash(dotnet test:*), Bash(dotnet format:*)
---

Take a branch all the way in: push it, open a pull request, merge that, and clean up after it.

**It always pushes first**, whichever form is used, so what merges is what I have in front of me.

**$1** is either the branch to merge into, or the number of a pull request `/pr` has already
opened — a value that is all digits is a number, anything else is a branch name.

**Invoking this is the permission for all of it** — the commit, the push, the merge, and deleting
the branch at both ends. CLAUDE.md says to ask before committing or branching; this command is the
asking. Do not stop to confirm each step.

Stop at the first thing that looks wrong. Never force, never `-D`, never merge past a red build.

**Always wait for CI.** The gates `/push` runs here cover one operating system. CI also runs Windows
and Linux, and those are where platform-specific bugs show up: a branch once passed every local test
and failed 32 on Windows, because git writes read-only files and ships `core.autocrlf=true` there. A
pending check is not a pass. Merge only once every check has finished and none has failed.

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

2. **Make sure it can merge.** The target may have moved while the branch was open and now
   conflict with it:

   ```sh
   gh pr view --repo SamoZ256/Svg.Skia <number> --json mergeable,mergeStateStatus
   ```

   `CONFLICTING` (state `DIRTY`) means GitHub cannot make the merge commit, and `gh pr merge` would
   refuse with "the merge commit cannot be cleanly created". `UNKNOWN` means GitHub hasn't worked it
   out yet, so ask again a moment later. On a conflict, resolve it on the head branch by merging the
   target *into* it. Never rebase: a rebased branch can only be pushed by force.

   ```sh
   git fetch origin
   git merge origin/<target>
   ```

   - **Resolve each conflicted file** so that both sides' intent survives: read the target's
     commits that touch it, not just the markers. Where the two sides want different things and the
     code can't tell you which should win, stop and ask me rather than choosing. `git merge --abort`
     puts the branch back if it goes wrong.
   - **Then run the gates again** on the result: format what the resolution touched, build, and run
     the suite. A merge that resolved cleanly can still fail to compile or pass.
   - **Commit and push:** `git add` the resolved files, `git commit --no-edit` (the default merge
     message says what it is), and `git push`. That push starts CI again, and step 3 waits for it.

   This is the only local merge this command makes. The branch still lands through the pull
   request.

3. **Wait for CI**, then read the result before doing anything else:

   ```sh
   gh pr checks --repo SamoZ256/Svg.Skia <number> --watch
   ```

   Windows tests take about ten minutes, which is longer than a foreground command may run, so
   run the watch in the background and act on the notification. Once it has finished, list the
   checks on their own: never put the listing and the merge in one command, since then the result
   is read only after the merge. A merge went past two red jobs that way once.

   - **Every check passed or was skipped:** go on to step 4.
   - **Any check failed:** stop and do not merge. Report each failing job, the failed test names
     and their error, from `gh run view --repo SamoZ256/Svg.Skia <run id> --log-failed`. Also say
     whether the target branch fails the same way, so a failure that was already there is not
     blamed on this branch.

4. **Merge it** with that number:

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
   step 6's job — it would then find nothing to do and report success for work it never did.

5. **Delete the remote branch**: `git push origin --delete <head branch>`. This is what gives the
   prune in the next step something to report, and keeps merged branches from accumulating on the
   remote.

6. **Run `/land <target branch>`** — but only while I am on the pull request's head branch. That is
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
