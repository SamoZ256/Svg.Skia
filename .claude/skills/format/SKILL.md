---
name: format
description: Run dotnet format on Svg.Skia scoped to the files a change touched, and check for collateral. Use whenever this repository needs formatting — before a commit, after an edit, or when asked to format — rather than running dotnet format across the solution.
---

# Format only the files you changed

`dotnet format` over `Svg.Skia.slnx` rewrites two things that have nothing to do with any change:
`src/Svg.Expressions/ExprLexer.cs`, and the whole `externals/SVG` submodule. Both come back dirty
every single time, and both then have to be reverted before anything can be committed. Scoping the
run to the files a change actually touched avoids the collateral entirely and is four times faster.

Measured on this machine, 2026-08-26: **15s** scoped over 8 files against **68s** solution-wide,
which dirtied `ExprLexer.cs` and **217 files** under `externals/SVG`.

## The command

```sh
FILES=$( { git diff --name-only --diff-filter=ACMR HEAD -- '*.cs'
           git ls-files -o --exclude-standard -- '*.cs'; } | sort -u )
[ -n "$FILES" ] && dotnet format Svg.Skia.slnx --no-restore --include $FILES
```

Two sources, because neither alone is the set of files a commit will carry: `git diff` finds what is
modified and staged, `git ls-files -o` finds new files that are not tracked yet.

Keep the `[ -n "$FILES" ]` guard. Without it an empty set is still safe — `--include ""` formats
nothing, and `--include` with no value at all is a hard error — so neither failure mode silently
widens the run to the solution. The guard just makes the no-op explicit.

Formatting a specific set by hand is the same call with the paths written out:

```sh
dotnet format Svg.Skia.slnx --no-restore --include path/one.cs path/two.cs
```

## Afterwards

Run `git status --short` and confirm it lists only files you touched.

Scoped runs produce no collateral, so this is a safety net rather than a step you expect to need.
If something else did come back dirty:

```sh
git checkout -- <file>
git -C externals/SVG checkout -- .     # the submodule, if it is showing as ` m externals/SVG`
```

Repeat until `git status --short` shows your change and nothing else.

## Notes

- `.slnx` is XML, not `.sln` — `Svg.Skia.slnx` is the only solution file here.
- `--no-restore` is deliberate: a restore on every format run costs time and changes nothing.
- Only `.cs` files are collected. `.axaml` is not formatted by this tool, so an XAML-only change has
  nothing to run and should skip straight to `git status`.
