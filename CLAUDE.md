# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

`AGENTS.md` also applies — it covers commit/PR conventions and the W3C Chrome-override capture
workflow in detail. This file covers build commands and architecture.

## Do not commit without asking

Leave finished work in the working tree and say it is ready. Ask before running `git commit`, and
wait for an answer — "the change is complete" is not permission to commit it. The same goes for
`git push` and anything else that leaves the working tree.

## Setup

Requires the **.NET 10 SDK** (`global.json` pins `10.0.100`, `rollForward: latestMinor`). The
build fails outright on .NET 9.

Submodules are mandatory, not optional — `src/Svg.Custom` compiles its sources directly out of
`externals/SVG`, and the test suites read fixtures from `externals/W3C_SVG_11_TestSuite` and
`externals/resvg`. Without them the solution does not build:

```sh
git submodule update --init --recursive
```

## Commands

```sh
dotnet build Svg.Skia.slnx -c Release
dotnet test  Svg.Skia.slnx -c Release
dotnet format Svg.Skia.slnx --no-restore     # before committing
```

Single project, single framework (test projects are net10.0 only):

```sh
dotnet test tests/Svg.Skia.UnitTests/Svg.Skia.UnitTests.csproj -f net10.0 -c Release
```

Single test or subset — `--filter` matches on fully qualified name:

```sh
dotnet test tests/Svg.Skia.UnitTests/Svg.Skia.UnitTests.csproj -f net10.0 -c Release \
  --filter "FullyQualifiedName~W3CTestSuiteTests.Tests"
```

The full suite takes ~1 minute for ~3200 tests; `tests/Svg.Skia.UnitTests` is the bulk of it and
runs the W3C and resvg image-comparison suites.

`build.sh` / `build.cmd` run the NUKE build in `build/build/_build.csproj` and will download a
private SDK into `.nuke/temp` if the global one does not match. For ordinary work `dotnet build`
is faster and sufficient.

### Known failure

`W3CTestSuiteTests.Tests(name: "text-ws-02-t")` fails on a clean checkout (error `0.023` against
a `0.022` threshold — a marginal text-raster diff). Verify against a clean worktree before
assuming a change caused it.

## Architecture

The library is a pipeline. Each stage has its own object model, and understanding which stage you
are in is usually the key to placing a change.

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

**ShimSkiaSharp** is the hinge. It mirrors the SkiaSharp API surface but records rather than
draws, so the model can be inspected, cloned, diffed and code-generated without a GPU. Anything
added to the model must be handled by all three consumers above.

**`Svg.SceneGraph` is the live path, not `Svg.Model`.** Both contain a `PaintingService` with
near-identical `GetColor` / `CombineWithOpacity` / gradient code. `SvgSceneRuntime.CreateModel` —
the entry point used by `SKSvg`, `svgc` and the source generator — goes through
`SvgScenePaintingService`. The `Svg.Model` copy survives for filter flood-colour only. Changing
the wrong one produces code that compiles, passes review and does nothing.

**Two front ends share one generator.** `samples/svgc` (CLI) and `src/Svg.SourceGenerator.Skia`
(Roslyn generator, driven by `AdditionalFiles` with `NamespaceName`/`ClassName` metadata) both
call `SkiaCSharpCodeGen.Generate`.

### Traps worth knowing

- **`Svg.SourceGenerator.Skia` `<Compile Include>`s the `Svg.CodeGen.Skia` sources file by
  file**, rather than referencing the assembly, because an analyzer cannot take an ordinary
  project reference. A new file in that project is invisible to it until it is listed in its
  csproj. `svgc` used to do the same and now references the assembly — linking the sources *and*
  referencing a library that references them properly is CS0121 on every extension method.
- **The source generator is an analyzer**, so `EnforceExtendedAnalyzerRules` applies to every
  file linked into it. `Environment.NewLine` and similar are banned (RS1035).
- **`Svg.CodeGen.Skia` targets netstandard2.0** and carries its own `IsExternalInit` shim;
  records need it.
- **Paints are cached by value.** `SolidFillPaintCacheKey` (a record struct over `SKColor`) and a
  `Dictionary<float, SKPaint>` for layer opacity mean two elements with equal values share one
  paint object. Anything that adds per-element state to a paint must widen the key or bypass the
  cache.
- **`ICanvasCommandVisitor` has no default implementations** (netstandard2.0/net461), so adding a
  `CanvasCommand` is a breaking change for implementors, and also needs entries in the
  `DeepClone` switch in `SKCanvas.cs`.
- `ShimSkiaSharp.UnitTests.CloneCoverageTests` asserts every exported class in the
  `ShimSkiaSharp` namespace supports `ICloneable` or `IDeepCloneable<T>`. New public types there
  will fail it until they do.

## SVG expression extension

This repository carries a non-standard extension letting SVG attributes hold expressions, which
are compiled into the generated C#:

```xml
<circle fill="{{ hsl(hue, 74%, 55%) }}" visibility="{{ level > 3 }}" />
```

**`SVG_EXPRESSIONS.md` in the repo root is the specification. Keep it current in the same change
as any modification to the extension** — supported attributes, language surface, placeholder
values, diagnostics, generated-code shape and limitations all live there.

Its parts: the `{{ }}` lift and placeholder substitution in
`Svg.Custom/SvgExpressionAttributes.cs`, the symbolic value model in `ShimSkiaSharp/Symbolic/`,
attribute reading in `Svg.SceneGraph/SvgSceneExpressions.cs`, and the language itself (lexer,
parser, type checker, C# emitter) in `Svg.CodeGen.Skia/Expressions/`.

Two invariants hold the design together:

1. **A symbolic value always carries a concrete one.** `SKColor.Expression` sits beside real
   RGBA channels, so every consumer that ignores expressions keeps working untouched. This is
   why adding the feature required no changes to `SkiaModel` or `AvaloniaPicture`.
2. **Placeholders are chosen so the element still paints.** The renderer short-circuits on
   `fill="none"`, `opacity="1"` and `visibility="hidden"`, each of which would remove the paint
   or subtree an expression needs to attach to.

`src/Svg.Expressions.Recipes` converts a finished SVG into that format from a recipe file. It is
a source-to-source rewriter and knows nothing about the expression language — the recipe's
`<code>` block is copied verbatim, and the code generator remains the only type checker. `svgc`
applies one with `-r`, and `--emit svg` writes the converted document instead of C# without
building a scene model. A whole build — drawings plus settings — is described by a project file
(`-p`), parsed by `src/Svg.CodeGen.Skia.Projects`. The source generator has no equivalent, so
generator-driven projects convert ahead of time and check in the result.

`samples/SvgExpressionsDemo` is the worked example; it also has a `--render <dir>` mode that
writes PNGs without opening a window, which is the practical way to verify rendering changes.
`samples/SvgRecipeDemo` does the same for the recipe path and links that demo's `LiveCompiler.cs`
by file rather than duplicating it, so an edit there changes both.

## Conventions

- `Directory.Packages.props` centralises package versions; `build/*.props` are per-dependency
  imports that projects `<Import>` rather than declaring `PackageReference` directly.
- Multi-targeting is common (`netstandard2.0;net461;net6.0;net8.0;net10.0` in the shim). Check a
  project's `TargetFrameworks` before using a modern BCL API.
- `Svg.Skia.slnx` is the solution (XML `.slnx` format, not `.sln`). New projects must be added to
  it to take part in the solution build.
