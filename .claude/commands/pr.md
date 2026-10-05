---
description: Push the current branch and open a PR on my repository against the branch it grew from
allowed-tools: SlashCommand, Bash(git status:*), Bash(git diff:*), Bash(git log:*), Bash(git ls-files:*), Bash(git add:*), Bash(git commit:*), Bash(git push:*), Bash(git branch:*), Bash(git rev-parse:*), Bash(git rev-list:*), Bash(git fetch:*), Bash(git for-each-ref:*), Bash(git checkout:*), Bash(git restore:*), Bash(gh auth:*), Bash(gh pr:*), Bash(dotnet build:*), Bash(dotnet test:*), Bash(dotnet format:*)
---

Push the branch I am on and open a pull request for it on my repository. It takes no arguments:
the target is the branch this one grew from, which step 5 finds.

**Invoking this is the permission for the commit and the push.** CLAUDE.md says to ask before
committing; this command is the asking. Do not stop to confirm each step. It stops at the pull
request — nothing is merged here.

1. **Check where I am.** `git rev-parse --abbrev-ref HEAD`. Stop if it is `HEAD`, a detached head,
   or `master`, which is where work lands rather than something to open a pull request from.
   Record the name.

2. **Check the branch pushes to itself.** A pull request's head is the branch on the remote, so the
   branch here has to push to the one of the same name. If `git rev-parse --abbrev-ref @{u}`
   answers, it must be `origin/<branch>`. Anything else — `origin/master`, or a branch under
   another name — means the push and the pull request would be about a different branch from the
   one in front of me: stop and say what it tracks. With no upstream at all, carry on; the push in
   step 6 creates `origin/<branch>`.

3. **Check `gh` is logged in** — `gh auth status`. If it is not, stop here and say so. Finding out
   after the push leaves a pushed branch and no pull request.

4. **Push what this branch is built on.** The target has to be on the remote in full: GitHub diffs
   against `origin/<target>`, so a commit only the local target has would show up as this branch's
   own. `git fetch origin --prune`, then list every local branch this one contains, with the commits
   no remote branch has:

   ```sh
   for b in $(git branch --merged HEAD --format='%(refname:short)'); do
     [ "$b" = "<branch>" ] && continue
     echo "$(git rev-list --count "$b" --not --remotes=origin) $b"
   done | grep -v '^0 '
   ```

   Push each one it prints — `git push origin <b>`, or `git push -u origin <b>` when it has no
   upstream — and say which were pushed and how many commits each carried. These are branches this
   desk's work is built on, so pushing them is part of this command. Checking every ancestor rather
   than a target found first matters because a parent never pushed has no `origin/` ref: the search
   in step 5 could not offer it and would pick `master` instead. If a push is rejected, the remote
   has moved on independently — stop and say so rather than forcing.

5. **Find the target.** If a pull request is already open for this branch, its base is the target
   and it is the one to use:

   ```sh
   gh pr list --repo SamoZ256/Svg.Skia --head <branch> --state open --json number,baseRefName,url
   ```

   Otherwise the target is the remote branch this one grew from. Count, for every remote branch but
   this one's own, the commits this branch has that it lacks:

   ```sh
   for ref in $(git for-each-ref --format='%(refname:short)' refs/remotes/origin); do
     case "$ref" in origin|origin/HEAD|"origin/<branch>") continue ;; esac
     echo "$(git rev-list --count "$ref..HEAD") ${ref#origin/}"
   done | sort -n | head -5
   ```

   The fewest is the one it grew from. A branch started on `master` has only its own commits beyond
   it; one stacked on a feature branch has fewer beyond that branch than beyond `master`. When two or
   more tie for the fewest, `master` among them or not, git cannot say which it grew from: stop and
   ask me which one, naming them.

   Say which target was chosen, and the runner-up with its count, so a wrong guess is caught before
   the pull request exists rather than after.

6. **Run `/push`.** It looks at the diff, formats, builds, tests, writes the message and pushes; let
   it do that rather than repeating any of it here. If it stops, this stops with it — nothing below
   should run against a failing build or a test you have not seen.

   One exception: `/push` stops when there is nothing to commit. That is fine as long as the branch
   is already pushed, so carry on in that case rather than treating it as a failure. If the branch
   has no upstream yet, it still needs pushing — `git push -u origin <branch>` — and one with
   commits the remote has not got (`git log @{u}..HEAD`) needs a `git push`.

   Then confirm the branch is actually worth a pull request: `git rev-list --count origin/<target>..<branch>`
   must be more than `0`. If it is `0` there is nothing to open one for — stop and say so.

7. **Open the pull request** against the target, pinned to my repository — unless step 5 found one
   already open, in which case that is the pull request and nothing is opened:

   ```sh
   gh pr create --repo SamoZ256/Svg.Skia --base <target> --head <branch> --fill --title "<what the branch does>"
   ```

   `--repo` is not optional here, and it has to be that **literal** — not a shell variable. This
   clone is a **fork** of `wieslawsoltes/Svg.Skia`, and on a fork `gh pr create` targets the *parent*
   by default, so an unpinned create opens a pull request on somebody else's repository: public, and
   awkward to undo. `.claude/hooks/gh-pr-guard.py` blocks both mistakes before they run, and it
   rejects a variable because it cannot see what one holds. Pass the same `--repo` to every later
   `gh pr` call.

   `--head` is the branch step 2 checked, named outright rather than left for `gh` to work out.

   `--fill` takes the body from the commits rather than inventing a second description of work
   `/push` has already described.

   **Always pass `--title`.** `--fill` only takes a title from a commit when the branch has exactly
   one; with several it falls back to the branch name, and "feature/svgcproj in studio" describes
   nothing to anyone reading a list of pull requests. Write the same shape as a commit summary —
   imperative, under 72 characters, saying what the branch does rather than naming the area it
   touches. It is also what the status line shows next to the number, so it is read far more often
   than it is written.

Report the branch, the target and how it was found, the pull request number and its URL, and what
`/push` did — whether it committed or found nothing to commit, and the test result it saw. Say
plainly that nothing has been merged.
