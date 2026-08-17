---
description: Commit everything in the working tree and push it on the current branch
argument-hint: [optional note about what matters in the message]
allowed-tools: Bash(git status:*), Bash(git diff:*), Bash(git log:*), Bash(git ls-files:*), Bash(git add:*), Bash(git commit:*), Bash(git push:*), Bash(git branch:*), Bash(git rev-parse:*), Bash(dotnet build:*), Bash(dotnet test:*), Bash(dotnet format:*), Bash(git checkout:*), Bash(git restore:*)
---

Commit the working tree and push it on the branch I am on.

**Invoking this is the permission to commit.** CLAUDE.md says to ask first; this command is the
asking. Do not stop to confirm — just do the steps below and report what happened.

$ARGUMENTS

1. **Look before you write.** `git status --short` and `git diff --stat`, plus the actual diff of
   anything you did not write yourself this session. The message has to describe what is really
   there, not what you remember doing.

   Stop if there is nothing to commit, or if `git rev-parse --abbrev-ref HEAD` is `HEAD` — a
   detached head is never what I meant.

2. **Format what I changed, not the solution.**

   ```sh
   FILES=$( { git diff --name-only --diff-filter=ACMR HEAD -- '*.cs'
              git ls-files -o --exclude-standard -- '*.cs'; } | sort -u )
   [ -n "$FILES" ] && dotnet format Svg.Skia.slnx --no-restore --include $FILES
   ```

   The whole solution takes 76s against ~15s scoped, and it reformats `ExprLexer.cs` and the whole
   `externals/SVG` submodule every single time — churn that then has to be reverted. Scoped to the
   files a commit touches it produces no collateral at all, so the revert below is a safety net
   rather than a step you expect to run.

   An empty `$FILES` is harmless if the guard is dropped: `--include ""` formats nothing, and
   `--include` with no value at all is a hard error, so neither silently widens to the solution.

   If it does dirty something I did not touch, revert that — `git checkout -- <file>`, and
   `git -C externals/SVG checkout -- .` for the submodule — until `git status --short` shows only
   my change.

3. **Build and test** — `dotnet build Svg.Skia.slnx -c Release` and
   `dotnet test Svg.Skia.slnx -c Release` — unless you already ran both since the last edit, in
   which case say so and skip. `text-ws-02-t` is a known failure; anything else, stop and tell me
   rather than committing over it.

4. **Write the message** per AGENTS.md: a summary under 72 characters, imperative, no prefix. Then
   a body explaining *why* — the problem, what was rejected and what it cost, anything a reader
   would otherwise have to rediscover. Numbers and error text where they carry weight. Not a list
   of the files touched; the diff already says that.

5. **Commit and push.** `git push` if the branch has an upstream, `git push -u origin <branch>` if
   it does not. Report the range that moved, or the branch that was created.

If a step fails, stop there and show me the output. Do not force, do not amend an existing commit,
and do not switch branches to make something work.
