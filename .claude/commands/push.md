---
description: Commit everything in the working tree and push it on the current branch
argument-hint: [optional note about what matters in the message]
allowed-tools: Bash(git status:*), Bash(git diff:*), Bash(git log:*), Bash(git ls-files:*), Bash(git add:*), Bash(git commit:*), Bash(git push:*), Bash(git branch:*), Bash(git rev-parse:*), Bash(dotnet build:*), Bash(dotnet test:*), Bash(dotnet format:*), Bash(git checkout:*), Bash(git restore:*)
---

Commit the working tree and push it on the branch I am on.

**Invoking this is the permission to commit.** CLAUDE.md says to ask first; this command is the
asking. Do not stop to confirm — just do the steps below and report what happened.

The permission is this invocation and nothing after it. A later commit or push in the same session
asks again, however obviously it follows on from this one.

$ARGUMENTS

1. **Look before you write.** `git status --short` and `git diff --stat`, plus the actual diff of
   anything you did not write yourself this session. The message has to describe what is really
   there, not what you remember doing.

   Stop if there is nothing to commit, or if `git rev-parse --abbrev-ref HEAD` is `HEAD` — a
   detached head is never what I meant.

2. **Format what I changed, not the solution.** Use the `format` skill — it has the command, the
   measurements behind it, and what to revert if a run dirties something you did not touch.

3. **Build and test** — `dotnet build Svg.Skia.slnx -c Release` and
   `dotnet test Svg.Skia.slnx -c Release` — unless you already ran both since the last edit, in
   which case say so and skip. Both should be clean: no build errors, no test failures. Anything
   red, stop and tell me rather than committing over it.

   `text-ws-02-t` used to be listed here as a known failure. It is not one any more, and neither is
   the `SvgToPng` build error that sat alongside it. Do not reintroduce a standing exception — a
   permitted failure stops being read after a while, which is how both of those survived.

4. **Write the message.** A summary under 72 characters, imperative, no prefix. Then a body
   explaining *why* — the problem, what was rejected and what it cost, anything a reader would
   otherwise have to rediscover. Numbers and error text where they carry weight. Cite the
   specification section where one settles the question. Not a list of the files touched; the diff
   already says that.

   The body is the pull request description too — `/merge` opens one with `gh pr create --fill`,
   which takes it straight from the commit.

5. **Commit and push.** `git push` if the branch has an upstream, `git push -u origin <branch>` if
   it does not. Report the range that moved, or the branch that was created.

If a step fails, stop there and show me the output. Do not force, do not amend an existing commit,
and do not switch branches to make something work.
