---
description: Push the current branch and open a PR against a target branch on my repository
argument-hint: [target-branch]
allowed-tools: SlashCommand, Bash(git status:*), Bash(git diff:*), Bash(git log:*), Bash(git ls-files:*), Bash(git add:*), Bash(git commit:*), Bash(git push:*), Bash(git branch:*), Bash(git rev-parse:*), Bash(git rev-list:*), Bash(git checkout:*), Bash(git restore:*), Bash(gh auth:*), Bash(gh pr:*), Bash(dotnet build:*), Bash(dotnet test:*), Bash(dotnet format:*)
---

Push the branch I am on and open a pull request against `$1` on my repository.

Target branch: **$1**

**Invoking this is the permission for the commit and the push.** CLAUDE.md says to ask before
committing; this command is the asking. Do not stop to confirm each step. It stops at the pull
request — nothing is merged here.

1. **Check where I am.** `git rev-parse --abbrev-ref HEAD`. Stop if it is `$1` — there is nothing
   to open a pull request from — or `HEAD`, which means a detached head. Record the name.

2. **Check `gh` is logged in** — `gh auth status`. If it is not, stop here and say so. Finding out
   after the push leaves a pushed branch and no pull request.

3. **Run `/push`.** It looks at the diff, formats, builds, tests, writes the message and pushes; let
   it do that rather than repeating any of it here. If it stops, this stops with it — nothing below
   should run against a failing build or a test you have not seen.

   One exception: `/push` stops when there is nothing to commit. That is fine as long as the branch
   is already pushed, so carry on in that case rather than treating it as a failure. If the branch
   has no upstream yet, it still needs pushing — `git push -u origin <branch>`.

   Then confirm the branch is actually worth a pull request: `git rev-list --count $1..<branch>`
   must be more than `0`. If it is `0` there is nothing to open one for — stop and say so.

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
   work `/push` has already described.

   If one is already open for this branch, use it instead of opening a second.

Report the branch, the pull request number and its URL, and what `/push` did — whether it committed
or found nothing to commit, and the test result it saw. Say plainly that nothing has been merged.
