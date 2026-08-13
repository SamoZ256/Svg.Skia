---
description: Commit everything in the working tree and push it on the current branch
argument-hint: [optional note about what matters in the message]
allowed-tools: Bash(git status:*), Bash(git diff:*), Bash(git log:*), Bash(git add:*), Bash(git commit:*), Bash(git push:*), Bash(git branch:*), Bash(git rev-parse:*), Bash(dotnet build:*), Bash(dotnet test:*), Bash(dotnet format:*), Bash(git checkout:*), Bash(git restore:*)
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

2. **`dotnet format Svg.Skia.slnx --no-restore`**, then revert its collateral: it reformats
   `ExprLexer.cs` and the whole `externals/SVG` submodule. `git checkout -- <file>` and
   `git -C externals/SVG checkout -- .` until `git status --short` shows only my change.

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
