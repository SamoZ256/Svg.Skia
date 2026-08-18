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

`dotnet format` reformats files that were already committed with deviating style — most reliably
`src/Svg.CodeGen.Skia/Expressions/ExprLexer.cs` and the whole `externals/SVG` submodule working
tree, since `Svg.Custom` compiles those sources. Run it, then check `git status --short` and
revert everything outside your change:

```sh
git checkout -- <file>
git -C externals/SVG checkout -- .
```

To count compiler warnings, build with `--no-incremental -v n`. An incremental build that has
nothing to do prints none at all, and the solution build at default verbosity drops per-project
ones — both will tell you a change is clean when it is not.

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

### Known state

**A clean checkout builds with no errors and the suite is fully green.** Anything failing is
something you did, or something that drifted since — investigate rather than assume it was already
broken.

`Svg.JavaScript.UnitTests…AppendChild_MovesTextNodeOutOfPreviousParent` failed once in a full
run and passed on three consecutive reruns. One sighting only — re-run before assuming you broke
it.

The W3C text rows are compared against Chrome captures with per-fixture thresholds in
`GetEffectiveThreshold`, and those are calibrated to a particular native Skia. A SkiaSharp bump
moves glyph antialiasing and can push a row over its threshold without anything being wrong; the
2026-07-01 move to SkiaSharp 4 did exactly that. When a text row fails by a hair, diff the images
first — glyph-edge outlines with most pixels off by one is a raster difference and wants a
threshold nudge, while displaced or doubled glyphs is a real regression. Re-capturing the Chrome
baseline is almost never the answer, since the baseline is not what moved.

`text-ws-02-t` is the one to watch: it asks for `SVGFreeSansASCII,sans-serif`, its Chrome override
disables SVG fonts so that first name never resolves, and the bundled `CustomTypefaceProvider`s
match on exact family name — so `sans-serif` falls through to the **system** font manager and picks
a different face on each of the ubuntu, windows and macos CI legs. Its error is expected to differ
by platform.

A `-v n` build reports 48 `CS0618` warnings, all of them **`Svg.Custom` deprecating its own
API** — `SvgDeferredPaintServer.Document` and its `(SvgDocument, string)` constructor, still
called from `SvgDeferredPaintServer.cs` and `SvgPaintServerFactory.cs`. Both are local overrides
that diverge from `externals/SVG` deliberately, and clearing them means deciding what replaces a
paint-server API rather than doing a rename.

Nothing else is deprecated: SkiaSharp 4 obsoleted every mutating method on `SKPath`, and both the
generated code and the hand-written renderer now build through `SKPathBuilder`.

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

**A resize happens to the document, not to the picture.** `svgc --width/--height/--scale` (and
the matching project settings and `<svg>` attributes) go through `SvgSceneSizing.Apply` in
`Svg.SceneGraph`, which sets the document's width and height — synthesizing a viewBox from the
natural size when it has none, since without one those are a viewport rather than a scale — and
then compiles as usual. Nothing scales the finished `SKPicture`, and the aspect ratio is always
preserved: one dimension derives the other, and a pair that does not match the drawing's shape
letterboxes through `preserveAspectRatio`. The source generator has no equivalent.

### Traps worth knowing

- **An analyzer's dependencies have to be shipped twice over.** `Svg.SourceGenerator.Skia`
  references every dependency ordinarily, with `PrivateAssets="all"`. For the package they are
  packed into `analyzers/dotnet/cs` by the `PackAnalyzerAssemblies` target, which globs this
  project's own `$(OutputPath)` — inside a target, because a wildcard in an `ItemGroup` expands at
  evaluation time, before anything is built. For the project-to-project case each one also needs a
  `TargetPathWithTargetPlatformMoniker` in `GetDependencyTargetPaths`, since the sample consumes
  the generator as an `Analyzer` with `ReferenceOutputAssembly="False"`. Miss that and the
  compiler cannot load the generator — no build error, just no generated files, so
  `samples/Svg.SourceGenerator.Skia.Sample` failing to find a generated class is the test.
  (It used to `<Compile Include>` the `Svg.CodeGen.Skia` sources file by file instead. That was
  historical, not necessary: the packing machinery was already there for five other references.)
- **`Svg.CodeGen.Skia` sets `EnforceExtendedAnalyzerRules`** because its assembly is loaded into
  the compiler as part of the generator. RS1035 is an *error* there — `Environment.NewLine` and
  file IO are banned, which is why `ExprSyntax.cs` writes `"\n"` by hand.
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
- **The W3C and resvg suites cover `SkiaModel`, not the code generator.** Generated C# is checked
  for its text by `SkiaCSharpCodeGenExpressionTests`, for compiling by the source generator
  sample, and for *drawing* only by `SkiaCSharpRenderTests` — which compiles it with Roslyn and
  compares its rendering against the runtime renderer at a zero threshold. Without that, an
  emitter change can be green across 2,700 tests and still produce a wrong picture. Add a case
  there when emitting anything new.
- **SkiaSharp 4 obsoletes some APIs as errors.** `SKCanvas.SetMatrix(SKMatrix)` is `CS0619`, not
  a warning, so generated code that passes a matrix by value does not compile at all. Emit
  `in`-overload calls through a local, since an expression argument is ambiguous between the two.
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
values, diagnostics, generated-code shape, the run-time rendering API and limitations all live
there.

**It documents syntax and usage only.** How any of it works belongs here instead, so anything
about layering, which project owns what, or why an internal does what it does goes in this file
rather than that one. A change to the extension usually touches both: what an author or a caller
sees there, how it is built here.

Its parts: the `{{ }}` lift and placeholder substitution in
`Svg.Custom/SvgExpressionAttributes.cs`, the symbolic value model in `ShimSkiaSharp/Symbolic/`,
attribute reading in `Svg.SceneGraph/SvgSceneExpressions.cs`, the language itself — lexer, parser,
type checker, the `TypedExpr` it produces and the `<e:code>` declarations that are its symbol
table — in `src/Svg.Expressions`, and the C# back end in `Svg.CodeGen.Skia/Expressions/`.

**The front end knows no target language.** `ExprChecker` returns a `TypedExpr`;
`ExprCSharpBackend` is what knows that `sin` is `MathF.Sin`, and `ExprCompiler` is a facade over
the two kept for the code generator's convenience. Two consequences worth knowing before touching
either: `ExprChecker` holds the symbol table **by reference** because
`SvgCodeDeclarationsExtensions.Resolve` adds each let to it after construction, and the checker
throws on the first error in a fixed visit order that several tests pin — operands before their
operator, a condition before its branches, arity before any argument.

`SvgExpressionDeclarations` splits along the same line: `Parse`, the parameter and let lists and
`CreateSymbolTable()` describe the document and live in `Svg.Expressions`, while `Resolve()` and
`DefaultCode()` produce C# and are extension methods in `Svg.CodeGen.Skia`.

`Parse` used to refuse a `color` parameter carrying a `default`, because `new SKColor(…)` cannot be a
C# argument default (CS1736). That was a target-language limit leaking into the format, and the
evaluator had always handled such a default without any special case. The emitter deals with it
instead: `ColourFallbacks()` names a local per such parameter, `BuildParameterList` emits
`SKColor? tint = null`, and the local coalesces to the compiled default before the lets. The body has
to read the local, so `ExprCompiler` carries a symbol-rewrite map that `ExprCSharpBackend` applies to
`TypedSymbol` — a colour parameter *without* a default is untouched, so no existing signature moved.
Compute the local names in `ColourFallbacks()` alone: `Resolve()` needs them to rewrite references
and the generator needs them to declare the locals, and the two disagreeing emits a body referring to
a local that does not exist.

**There are two readers for `<e:code>`, and adding a rule means adding it to
`SvgExpressionDeclarations.Builder`, not to either one.** `Parse` works from source text, which is
what `svgc` and the source generator have; `SvgDocument.ExpressionDeclarations` walks the parsed
tree, which is all `SKSvg.Load(XmlReader)` or an editor holding a document ever has. The text reader
exists because a foreign element's namespace is `protected internal` on `SvgElement` — invisible
outside `Svg.Custom`, so an unqualified `<param>` out there could belong to any extension. The tree
reader lives inside `Svg.Custom` and can see it. `SvgDocumentExpressionDeclarationsTests` asserts the
two agree, including every diagnostic's exact wording, which only holds because the validation is
shared.

**There are two back ends now, and they must agree numerically.** `ExprCSharpBackend` renders C#;
`ExprValueBackend` computes an `ExprValue`, behind the `ExprEvaluator` facade. Three traps, all of
them things that reading the source will not tell you:

- The evaluator computes in **`float`**, because generated code narrows every literal and calls
  `MathF`. `Svg.Expressions` is multi-targeted for `MathF`, which netstandard2.0 lacks; its
  fallback differs by up to one ulp for `sin`/`cos`/`tan`/`pow`, which is why `ExprMathFallback` is
  compiled on every target — otherwise the only framework running that code would be one nothing
  tests.
- `hsl` is reimplemented from `SKColor.FromHsl`, whose final byte conversion **truncates**. Rounding
  instead disagrees on 76% of the domain.
- Do not "simplify" the short-circuiting in `ExprValueBackend`. `clamp` with a reversed range throws,
  so an eagerly evaluated operand changes behaviour rather than just wasting work.

**Two differential suites, at two layers, and they do not cover for each other.**
`ExprEvaluatorDifferentialTests` compiles the emitted C# and compares it against the evaluated value
bit for bit — but only for the *language*, a `TypedExpr`. `SymNodeDifferentialTests` does the same a
layer up, for a `SymNode` and the helpers `SymCSharpEmitter` calls: the alpha scale, the linear-RGB
conversion, and the `SKColorF` conversion a gradient stop goes through. Add a case to whichever layer
you touched.

The gap between them was not hypothetical. `SvgToColorF` divided a channel by `255f` while
`ShimSkiaSharp.SKColor` multiplies by `1 / 255.0f`; those disagree for **126 of the 256** byte
values, the expression-level suite could not see it, the picture-level test compared stops to three
decimal places, and it surfaced only as one pixel in a demo render. Both suites compare bits for
this reason — a float comparison with any tolerance lets exactly this class of bug through.

**Evaluation rewrites the picture; it does not teach the renderers anything.**
`SvgSceneExpressionEvaluator.Evaluate` returns a new `SKPicture` with concrete colours and resolved
conditionals, so `SkiaModel` and `AvaloniaPicture` are untouched — which is also the only thing that
could work, since `Avalonia.Svg.Skia.SvgSource` holds one *static* `SkiaModel` with nowhere to keep a
document's parameter values. It never mutates: paints are shared between elements, and the symbolic
picture has to survive for the next set of values. Untouched subtrees come back as the same
instances, so a document without expressions allocates nothing.

What it reaches: a paint's `Color`, a `ColorShader`, the `SKColorF[]` of the three gradient shaders,
an opacity `SaveLayer`'s paint, and a `BlendModeColorFilter` — recursing through
`DrawPictureCanvasCommand` and through a `PictureShader`, which is how a pattern's contents are
covered. The `SKColor LightColor` on the six lit image filters is deliberately **not** walked: no
document can attach an expression there, so the code would be unreachable and untested. Adding a
model path that can carry an expression means adding it here too.

**On `SKSvg`, loading never evaluates and `Model` is whatever is being rendered.** `Load` leaves the
placeholders in place and does not even read the declarations, so no existing consumer is affected and
a malformed `<e:code>` cannot fail a load. `SetExpressionValues` evaluates against a retained
`_symbolicModel` — no re-parse, no scene recompile — and assigns the result to `Model`, having
evaluated first so a rejected set leaves the previous rendering intact. The plan for this had `Model`
staying symbolic with the evaluated picture hidden behind `Picture`; that was dropped because there
are eleven places that convert a model (`SKSvg.Model.cs`, `SKSvg.AnimationLayers.cs`,
`SKSvg.SceneGraph.cs`), and threading a second model through all of them to keep one property
"pure" would have been both riskier and less useful — hit testing, `Save`, the wireframe and the
animation layers all want the drawing as rendered.

A false conditional **keeps the range's `Save`/`Restore`/`SetMatrix`/clip commands and drops only the
drawing ones.** Generated code deletes the whole range instead, which it can afford because it
assigns `SetMatrix(TotalMatrix)` while the runtime applies `Concat(DeltaMatrix)`. Measured, though:
`SvgSceneRenderer` opens the range around everything a node contributes, so a matrix inside one is
always inside a `Save` the range's own `Restore` pops, and through any real document the two are
indistinguishable — deleting the range passes every render test. `ConditionalRangeTests` pins the
difference on hand-built pictures, because no document produces one; that is the suite that fails if
this changes, not the render tests.

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
(`-p`), parsed by `src/Svg.CodeGen.Skia.Projects`. A `<group>` there scopes settings to some of
the drawings and is folded into them as the project is read, so `SvgcProject.Items` is a flat
list of resolved items whether the file uses groups or not, and nothing downstream knows. The source generator has no equivalent, so
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
