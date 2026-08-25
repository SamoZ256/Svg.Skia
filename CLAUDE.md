# CLAUDE.md

A map for working in this repository. `AGENTS.md` covers commit and PR conventions, and the W3C
Chrome-override capture workflow.

**Keep this file under 100 lines.** It is a map, not a record of what anyone learned. Detail belongs
beside the thing it is about: a trap goes in a comment next to the code, the expression format goes
in `SVG_EXPRESSIONS.md`, a package's design and its measurements go in `site/articles/packages/`,
and why a line is the way it is goes in the commit that wrote it. Do not append here after every
change — add only what someone could not find by reading the code.

## Rules

- **Ask before `git commit`, `git push`, or creating a branch, and wait for an answer.** Finishing a
  change is not permission to record it. A branch decides how work lands, which is a decision to
  raise before starting rather than to present as already taken.
- **Prefer removing code to adding it.** Before adding a helper, a type or an option, widen the one
  already doing that job; before adding a branch, see whether the case can be stopped from arising.
  A net addition is worth a sentence saying what was reused and what could not be.
- **Run the app when you change one.** For `src/SvgViewer`, the editor, or any GUI project, build it,
  launch it and leave it running so the change can be seen — rather than reporting it done from a
  green test run.
- **Format only the files you changed** (`--include`, below). Measured: 16.8s scoped against 73s
  solution-wide, and solution-wide reformats `src/Svg.Expressions/ExprLexer.cs` and the whole
  `externals/SVG` submodule every time — churn that then has to be reverted.
- **Keep `SVG_EXPRESSIONS.md` current in the same change** as anything altering the expression
  extension. It specifies syntax and usage only; how any of it works belongs in comments.

## Setup

The **.NET 10 SDK** (`global.json` pins `10.0.100`); the build fails outright on .NET 9. Submodules
are mandatory, not optional — `src/Svg.Custom` compiles its sources straight out of `externals/SVG`,
and the suites read fixtures from `externals/W3C_SVG_11_TestSuite` and `externals/resvg`.

```sh
git submodule update --init --recursive
```

## Commands

```sh
dotnet build Svg.Skia.slnx -c Release
dotnet test  Svg.Skia.slnx -c Release                          # ~4300 tests, about a minute
dotnet test tests/Svg.Skia.UnitTests/Svg.Skia.UnitTests.csproj -c Release \
  -f net10.0 --filter "FullyQualifiedName~W3CTestSuiteTests"   # one project, one subset
dotnet format Svg.Skia.slnx --no-restore --include <the .cs files you changed>
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
  `PaintingService`; everything real goes through `SvgScenePaintingService`, and the `Svg.Model`
  copy survives for filter flood-colour only. Changing the wrong one compiles, reviews well, and
  does nothing.
- **Two front ends share one generator**: `samples/svgc` and `src/Svg.SourceGenerator.Skia` both
  call `SkiaCSharpCodeGen.Generate`.
- **The suites cover `SkiaModel`, not the generator.** Generated C# is checked for *drawing* only by
  `SkiaCSharpRenderTests`, which compiles it with Roslyn and diffs it against the runtime renderer at
  a zero threshold. Add a case there when emitting anything new, or an emitter change can be green
  across thousands of tests and still draw the wrong picture.
- **The expression extension** (`{{ … }}` in attributes) spans `Svg.Custom`, `ShimSkiaSharp/Symbolic`,
  `Svg.SceneGraph`, `src/Svg.Expressions` and `Svg.CodeGen.Skia/Expressions`. `SVG_EXPRESSIONS.md`
  specifies it; `src/Svg.Highlighting` colours and diagnoses it for a source view.

## Known state

A clean checkout builds clean and the suite is fully green — anything failing is something you did
or something that drifted, so investigate rather than assume it was already broken. The 48 `CS0618`
warnings are `Svg.Custom` deprecating its own paint-server API, and are expected. W3C text rows use
per-fixture thresholds calibrated against a particular native Skia: a hairline failure after a
SkiaSharp bump usually wants a threshold nudge and almost never a re-captured baseline.
