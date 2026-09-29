# CLAUDE.md

A map for working in this repository. Procedures live in `.claude/skills` — `format` before a commit,
`chrome-references` for a render baseline; commit and pull request conventions are in
`.claude/commands/`. Reference: [SVG 1.1](https://www.w3.org/TR/SVG11/) ·
[Avalonia](https://github.com/AvaloniaUI/Avalonia) · [its docs](https://github.com/AvaloniaUI/avalonia-docs) ·
[ILSpy](https://github.com/icsharpcode/ILSpy), for reading what a dependency actually compiled to.

**Keep this file under 100 lines.** It is a map, not a record of what anyone learned. Detail belongs
beside the thing it is about — a trap in a comment next to the code, a package's design and its
measurements in `site/articles/packages/`, why a line is the way it is in the commit that wrote it.
Add only what someone could not find by reading the code.

## Rules

- **Never `git push` or create a branch without asking** — a branch decides how work lands. Commit
  unasked only to break a large change into several as you go; a change that is one commit asks first.
- **Prefer removing code to adding it.** Before adding a helper, a type or an option, widen the one
  already doing that job; before adding a branch, see whether the case can be stopped from arising.
  A net addition is worth a sentence saying what was reused and what could not be.
- **Comment only what the code cannot say** — a trap, a measurement, a rejected alternative — in a
  sentence or two. Never restate the line below it. Much of the existing prose is longer than this
  allows; match the rule, not the surroundings.
- **Run the app when you change one.** For `src/Svg.Studio`, the editor, or any GUI project, build and
  launch it and leave it running, rather than reporting it done from a green test run.
- **Keep `samples/TestApp/TestApp.json` out of a commit.** Tracked, and `TestApp` rewrites it on exit
  (`App.axaml.cs:50-52`), so running the sample leaves it modified without anyone having edited it.
- **Format only the files you changed.** The `format` skill has the command and the measurements;
  a solution-wide run rewrites `ExprLexer.cs` and the whole `externals/SVG` submodule every time.

## Setup

The **.NET 10 SDK** (`global.json` pins `10.0.100`); the build fails outright on .NET 9. Submodules
are mandatory, not optional — `src/Svg.Custom` compiles its sources straight out of `externals/SVG`,
and the suites read fixtures from `externals/W3C_SVG_11_TestSuite` and `externals/resvg`. Fetch them
with `git submodule update --init --recursive`.

## Worktrees

Work happens in a hub: a directory holding the bare repository in `.bare`, a `.git` file saying
`gitdir: ./.bare` so git answers from the hub itself, `master/` — never removed, where you pull and
run Studio from — and one directory per task, named after its branch. Each has its own `bin/` and
`obj/`, so a suite can run in one while another builds; never run two in the same worktree.

```sh
git clone --bare https://github.com/SamoZ256/Svg.Skia.git .bare && echo "gitdir: ./.bare" > .git
git config remote.origin.fetch "+refs/heads/*:refs/remotes/origin/*" && git fetch origin
git worktree add master master                                    # once; then per task:
git fetch origin && git worktree add -b <branch> <branch> origin/master   # a branch: ask first
for s in externals/SVG externals/W3C_SVG_11_TestSuite externals/resvg; do # from master, offline
  git -C <branch> submodule update --init --reference "$PWD/master/$s" --dissociate -- $s; done
git worktree remove <branch> && git branch -d <branch> && git worktree prune   # after it lands
```

A fresh worktree has empty `externals/` and will not build until its submodules are in; `master/`
fetches its own with `git submodule update --init --recursive`. `--dissociate` copies master's
objects rather than borrowing them, so no worktree breaks when another goes. One branch per
worktree — git refuses a second checkout of one, which is why `/land`'s switch to `master` means
removing the worktree and pulling in `master/`. Studio's settings, recent files and recovery copies
live in `~/Library/Application Support/Svg.Studio` and are shared by every worktree.

## Commands

```sh
dotnet build Svg.Skia.slnx -c Release
dotnet test  Svg.Skia.slnx -c Release                          # ~5100 tests, about a minute
dotnet test tests/Svg.Skia.UnitTests/Svg.Skia.UnitTests.csproj -c Release \
  -f net10.0 --filter "FullyQualifiedName~W3CTestSuiteTests"   # one project, one subset
dotnet format Svg.Skia.slnx --no-restore --include <changed .cs files>   # or just: the format skill
dotnet build Svg.Skia.slnx -c Release --no-incremental -v n    # the only way to count warnings
```

`Svg.Skia.slnx` is the solution (XML `.slnx`, not `.sln`); a new project must be added to it to be
built at all. Six sit outside it — CI builds the three MAUI ones separately, and `MauiSvgSkiaSample`,
`UnoSvgSkiaSample` and `tests/Avalonia.Svg.Skia.UiTests` are built by nothing.

`Directory.Packages.props` centralises versions, `build/*.props` are per-dependency imports to
`<Import>`, and multi-targeting is common — check `TargetFrameworks` before a modern BCL API.

## Architecture

The library is a pipeline. Each stage has its own object model, and knowing which stage you are in
is usually the whole of placing a change.

```
.svg text
  │  Svg.Custom            fork of SVG.NET (sources from externals/SVG) → SvgDocument DOM
  ▼
SvgDocument
  │  Svg.SceneGraph        SvgSceneCompiler builds a retained scene, SvgSceneRenderer walks it
  ▼
ShimSkiaSharp.SKPicture   flat list of CanvasCommand — the renderer-independent model
  │
  ├─ Svg.Skia              SkiaModel translates commands to real SkiaSharp for rendering
  ├─ Svg.Controls.Avalonia AvaloniaPicture translates them to Avalonia draw commands
  └─ Svg.CodeGen.Skia      emits C# source that replays the commands
```

- **ShimSkiaSharp is the hinge.** It mirrors SkiaSharp's surface but records rather than draws, so
  the model can be inspected, cloned, diffed and code-generated without a GPU. Anything added there
  must be handled by all three consumers above, and `CloneCoverageTests` fails a new public type
  until it clones.
- **`Svg.SceneGraph` is the live path, not `Svg.Model`.** Both hold a near-identical
  `PaintingService`; everything that decides a colour goes through `SvgScenePaintingService`, and
  the `Svg.Model` copy survives for small helpers beside it — `IsAntialias`, `CombineWithOpacity`,
  font resolution. Changing the wrong one compiles, reviews well, and does nothing.
- **Two front ends share one generator**: `src/svgc` and `src/Svg.SourceGenerator.Skia` both
  call `SkiaCSharpCodeGen.Generate`.
- **The suites cover `SkiaModel`, not the generator.** Generated C# is checked for *drawing* only by
  `SkiaCSharpRenderTests`, which compiles it with Roslyn and diffs it against the runtime renderer at
  a zero threshold. Add a case there when emitting anything new, or an emitter change can be green
  across thousands of tests and still draw the wrong picture.
- **The expression extension** (`{{ … }}` in attributes) spans `Svg.Custom`, `ShimSkiaSharp/Symbolic`,
  `Svg.SceneGraph`, `src/Svg.Expressions`, `Svg.CodeGen.Skia/Expressions` and `src/Svg.Highlighting`.

## Known state

A clean checkout builds clean and the suite is fully green — anything failing is something you did
or something that drifted, so investigate rather than assume it was already broken. The 48 `CS0618`
warnings are `Svg.Custom` deprecating its own paint-server API, and are expected. W3C text rows use
per-fixture thresholds calibrated against a particular native Skia: a hairline failure after a
SkiaSharp bump usually wants a threshold nudge and almost never a re-captured baseline.
`Svg.Studio.UnitTests` builds its own `Application`, so nothing in `App.axaml` is covered by a test.
