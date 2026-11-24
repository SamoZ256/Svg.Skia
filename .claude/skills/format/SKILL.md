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
{ git diff --name-only --diff-filter=ACMR HEAD -- '*.cs'
  git ls-files -o --exclude-standard -- '*.cs'; } | sort -u | tr '\n' '\0' |
  xargs -0 -r dotnet format Svg.Skia.slnx --no-restore --include
```

Two sources, because neither alone is the set of files a commit will carry: `git diff` finds what is
modified and staged, `git ls-files -o` finds new files that are not tracked yet.

The list goes through `xargs`, not a variable, because the shell here is zsh, which does not split an
unquoted `$FILES` into words. Until 2026-11-24 this read `--include $FILES`, which handed `dotnet
format` the whole list as one path that matched nothing: it formatted nothing and said nothing. `-0`
keeps a path with a space in it whole, and `-r` skips the run when nothing changed; without it
`--include` gets no value, which is a hard error, so neither failure widens the run to the solution.

Formatting a specific set by hand is the same call with the paths written out:

```sh
dotnet format Svg.Skia.slnx --no-restore --include path/one.cs path/two.cs
```

## Afterwards

Run `git status --short` and confirm it lists only files you touched, and `git diff` those files
for lines you did not write.

A scoped run usually produces no collateral, but it does reformat a file it is given — not only the
part you changed, and a file last edited while the command formatted nothing may never have been
formatted at all. Revert whatever came back that is not yours:

```sh
git checkout -- <file>
git -C externals/SVG checkout -- .     # the submodule, if it is showing as ` m externals/SVG`
```

Where that file is one you *did* change, restore it and re-apply your own edit without formatting it,
rather than committing the reformatting as though it were part of the change.

Repeat until `git status --short` shows your change and nothing else.

## Notes

- `.slnx` is XML, not `.sln` — `Svg.Skia.slnx` is the only solution file here.
- `--no-restore` is deliberate: a restore on every format run costs time and changes nothing.
- Only `.cs` files are collected. `.axaml` is not formatted by this tool, so an XAML-only change has
  nothing to run and should skip straight to `git status`.
