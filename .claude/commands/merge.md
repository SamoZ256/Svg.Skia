---
description: Land work by merging a pull request on the remote, never with a local git merge - push the current branch, open its PR against the branch it grew from (or take the one already open), merge that PR on GitHub, and land. This is the only way work lands in this repository.
allowed-tools: SlashCommand, Bash(git status:*), Bash(git diff:*), Bash(git log:*), Bash(git ls-files:*), Bash(git add:*), Bash(git commit:*), Bash(git push:*), Bash(git branch:*), Bash(git rev-parse:*), Bash(git rev-list:*), Bash(git checkout:*), Bash(git switch:*), Bash(git restore:*), Bash(git pull:*), Bash(git fetch:*), Bash(git for-each-ref:*), Bash(git merge:*), Bash(gh auth:*), Bash(gh pr:*), Bash(gh run:*), Bash(gh repo:*), Bash(dotnet build:*), Bash(dotnet test:*), Bash(dotnet format:*)
---

Take a branch all the way in: push it, open a pull request, merge that, and clean up after it.

It takes no arguments: what merges is the branch I am on, through its pull request, into the branch
that pull request targets. **It always pushes first**, so what merges is what I have in front of me.

**Invoking this is the permission for all of it** — the commit, the push, the merge, and deleting
the branch at both ends. CLAUDE.md says to ask before committing or branching; this command is the
asking. Do not stop to confirm each step.

Stop at the first thing that looks wrong. Never force, never `-D`, never merge past a red build.

**Always wait for CI.** The gates `/push` runs here cover one operating system. CI also runs Windows
and Linux, and those are where platform-specific bugs show up: a branch once passed every local test
and failed 32 on Windows, because git writes read-only files and ships `core.autocrlf=true` there. A
pending check is not a pass. Merge only once every check has finished and none has failed.

1. **Get a pull request to merge, and the branch it is going into.**

   First look for one already open for the branch I am on — `git rev-parse --abbrev-ref HEAD`
   names it:

   ```sh
   gh pr list --repo SamoZ256/Svg.Skia --head <branch> --state open --json number,baseRefName,url
   ```

   Say whether one was found, and its number and base. Then **run `/pr`** either way. It checks
   where I am and that the branch pushes to itself, checks `gh`, runs `/push` — the diff, the
   formatting, the build, the tests, the message — and pushes. It takes the open pull request where
   there is one, and otherwise opens one against the branch this one grew from. Let it do all of
   that rather than repeating any of it here. If it stops, this stops with it: nothing below should
   run against a failing build, a test you have not seen, or a branch with nothing on it.

   Then read what is to be merged, and stop if its state is not `OPEN`:

   ```sh
   gh pr view --repo SamoZ256/Svg.Skia <branch> --json number,state,headRefName,baseRefName,url
   ```

   Record three things: the number, the head branch — the one I am on, and the one to delete at the
   end — and the target, its `baseRefName`.

2. **Make sure it can merge.** The target may have moved while the branch was open, and now
   conflict with it or merely be ahead of it:

   ```sh
   gh pr view --repo SamoZ256/Svg.Skia <number> --json mergeable,mergeStateStatus
   ```

   `CONFLICTING` (state `DIRTY`) means GitHub cannot make the merge commit, and `gh pr merge` would
   refuse with "the merge commit cannot be cleanly created". `BEHIND` means the target has commits
   the branch lacks; the ruleset on `master` refuses to merge it until it has them, since CI never
   tested the two together. `BLOCKED` is the ruleset waiting for checks, which step 3 does, and
   `UNKNOWN` means GitHub hasn't worked it out yet, so ask again a moment later. On a conflict or
   when behind, merge the target *into* the head branch. Never rebase: a rebased branch can only be
   pushed by force.

   ```sh
   git fetch origin
   git merge origin/<target>
   ```

   - **Resolve each conflicted file** so that both sides' intent survives: read the target's
     commits that touch it, not just the markers. Where the two sides want different things and the
     code can't tell you which should win, stop and ask me rather than choosing. `git merge --abort`
     puts the branch back if it goes wrong.
   - **Then run the gates again** on the result: format what the resolution touched, build, and run
     the suite. A merge that resolved cleanly, or had nothing to resolve, can still fail to compile
     or pass.
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
   to mistake for one. A real refusal because the branch is behind means the target moved during
   CI: go back to step 2.

   A merge commit, not a squash and not a rebase, because that is how this repository's history
   reads and squashing would throw away the commit bodies `/push` just wrote. Confirm it really
   merged before going on — `gh pr view --repo SamoZ256/Svg.Skia <number> --json state,mergeCommit`.

   **Do not pass `--delete-branch`.** `gh` would delete the local branch and switch away, which is
   step 6's job — it would then find nothing to do and report success for work it never did.

5. **Delete the remote branch**: `git push origin --delete <head branch>`, which keeps merged
   branches from accumulating on the remote. The push removes the local `origin/<head branch>`
   with it, so `/land`'s prune will not list this one as gone.

6. **Run `/land <target branch>`.** I am on the pull request's head branch, since step 1 found the
   pull request from it, and that is the branch `/land` deletes. Let it do its own checking — do
   not pre-empt its steps or skip it because you already know the answer.

Report the pull request number, what the target branch moved to, and whatever `/land` says about the
final state. If `/land` reports that more came down than this branch's own commits, repeat that
prominently: it means the target moved while the branch was open, and what is local is no longer
what was tested.
