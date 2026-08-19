---
description: Push the current branch, open a PR against a target branch, merge it, and land
argument-hint: [target-branch]
allowed-tools: SlashCommand, Bash(git status:*), Bash(git diff:*), Bash(git log:*), Bash(git ls-files:*), Bash(git add:*), Bash(git commit:*), Bash(git push:*), Bash(git branch:*), Bash(git rev-parse:*), Bash(git rev-list:*), Bash(git checkout:*), Bash(git switch:*), Bash(git restore:*), Bash(git pull:*), Bash(git fetch:*), Bash(gh auth:*), Bash(gh pr:*), Bash(gh repo:*), Bash(dotnet build:*), Bash(dotnet test:*), Bash(dotnet format:*)
---

Take the branch I am on all the way into `$1`: push it, open a pull request against `$1` on my
repository, merge that, and clean up after it.

Target branch: **$1**

**Invoking this is the permission for all of it** — the commit, the push, the merge, and deleting
the branch at both ends. CLAUDE.md says to ask before committing or branching; this command is the
asking. Do not stop to confirm each step.

Stop at the first thing that looks wrong. Never force, never `-D`, never merge past a red build.

1. **Check where I am.** `git rev-parse --abbrev-ref HEAD`. Stop if it is `$1` — there is nothing
   to open a pull request from — or `HEAD`, which means a detached head. Record the name: it is the
   branch to merge, and the one to delete at the end.

2. **Check `gh` is logged in** — `gh auth status`. If it is not, stop here and say so. Everything
   from step 4 needs it, and finding out halfway leaves a pushed branch and no pull request.

3. **Run `/push`.** It looks at the diff, formats, builds, tests, writes the message and pushes; let
   it do that rather than repeating any of it here. If it stops, this stops with it — nothing below
   should run against a failing build or a test you have not seen.

   One exception: `/push` stops when there is nothing to commit. That is fine here as long as the
   branch is already pushed, so carry on in that case rather than treating it as a failure.

   Then confirm the branch is actually worth a pull request: `git rev-list --count $1..<branch>`
   must be more than `0`. If it is `0` there is nothing to merge — stop and say so.

4. **Open the pull request** against `$1`, pinned to my repository:

   ```sh
   gh pr create --repo SamoZ256/Svg.Skia --base $1 --fill
   ```

   `--repo` is not optional here, and it has to be that **literal** — not a shell variable. This
   clone is a **fork** of `wieslawsoltes/Svg.Skia`, and on a fork `gh pr create` targets the *parent*
   by default, so an unpinned create opens a pull request on somebody else's repository: public, and
   awkward to undo. `.claude/hooks/gh-pr-guard.py` blocks both mistakes before they run, and it
   rejects a variable because it cannot see what one holds. Pass the same `--repo` to every later
   `gh pr` call.

   `--fill` takes the title and body from the commits rather than inventing a second description of
   work `/push` has already described. Report the number and the URL.

   If one is already open for this branch, use it instead of opening a second.

5. **Merge it** with `gh pr merge --repo SamoZ256/Svg.Skia --merge`. A merge commit, not a squash and not a
   rebase, because that is how this repository's history reads and squashing would throw away the
   commit bodies `/push` just wrote. Confirm it really merged before going on.

   **Do not pass `--delete-branch`.** `gh` would delete the local branch and switch away, which is
   step 7's job — it would then find nothing to do and report success for work it never did.

6. **Delete the remote branch**: `git push origin --delete <branch>`. This is what gives the prune
   in the next step something to report, and keeps merged branches from accumulating on the remote.

7. **Run `/land $1`.** It switches, pulls, verifies the merge actually landed, deletes the local
   branch with `-d` and prunes. Let it do its own checking — do not pre-empt its steps or skip it
   because you already know the answer.

Report the pull request number, what `$1` moved to, and whatever `/land` says about the final state.
If `/land` reports that more came down than this branch's own commits, repeat that prominently: it
means `$1` moved while the branch was open, and what is local is no longer what was tested.
