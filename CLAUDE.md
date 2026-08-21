# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

`AGENTS.md` also applies — it covers commit/PR conventions and the W3C Chrome-override capture
workflow in detail. This file covers build commands and architecture.

## Do not commit or branch without asking

Leave finished work in the working tree and say it is ready. Ask before running `git commit`, and
wait for an answer — "the change is complete" is not permission to commit it. The same goes for
`git push`, for creating a branch (`git checkout -b`, `git switch -c`, `git branch`), and for
anything else that leaves the working tree or moves what HEAD points at.

A branch is not a harmless preliminary. It decides how the work will land, which is a decision to
raise before starting rather than to present as already taken. Being told to commit says nothing
about branching, so ask for that separately unless the instruction named a branch itself.

## Prefer removing code to adding it

A change that deletes more than it writes is the better change. Before adding a helper, a type or an
option, look for the one already doing that job and widen it; before adding a branch, see whether
the case can be stopped from arising instead. Net additions are worth a sentence saying what was
reused and what could not be — a feature that lands as pure addition has usually missed a seam.

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

**W3C text rows** use per-fixture thresholds in `GetEffectiveThreshold`, calibrated against a
particular native Skia; a SkiaSharp bump moves glyph antialiasing and can push one over without
anything being wrong. When a text row fails by a hair, diff the images: glyph-edge outlines with
most pixels off by one is a raster difference wanting a threshold nudge, whereas displaced or
doubled glyphs is a regression. Re-capturing the Chrome baseline is almost never right — the
baseline is not what moved. `text-ws-02-t` resolves its font through the **system** font manager,
so its error legitimately differs across the ubuntu, windows and macos legs.

A `-v n` build reports 48 `CS0618` warnings, all of them **`Svg.Custom` deprecating its own
API** — `SvgDeferredPaintServer.Document` and its `(SvgDocument, string)` constructor, still
called from `SvgDeferredPaintServer.cs` and `SvgPaintServerFactory.cs`. Both are local overrides
that diverge from `externals/SVG` deliberately, and clearing them means deciding what replaces a
paint-server API rather than doing a rename.

Nothing else is deprecated: SkiaSharp 4 obsoleted every mutating method on `SKPath`, and both the
generated code and the hand-written renderer now build through `SKPathBuilder`.

**Six projects are outside `Svg.Skia.slnx`**, so the solution build never sees them. CI builds
`SvgML.Maui`, `Svg.Controls.Skia.Maui` and `SvgML.Maui.Demo` separately; `MauiSvgSkiaSample`,
`UnoSvgSkiaSample` and `tests/Avalonia.Svg.Skia.UiTests` are built by **nothing**. That last one is
a test project that runs nowhere.

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

This repository carries a non-standard extension letting SVG attributes hold expressions, which are
either compiled into generated C# or evaluated at run time:

```xml
<circle fill="{{ hsl(hue, 74%, 55%) }}" visibility="{{ level > 3 }}" />
```

**`SVG_EXPRESSIONS.md` is the specification and documents syntax and usage only — keep it current
in the same change.** How any of it works belongs here instead. A change to the extension usually
touches both.

Its parts: the `{{ }}` lift and placeholders in `Svg.Custom/SvgExpressionAttributes.cs`, the
symbolic value model in `ShimSkiaSharp/Symbolic/`, attribute reading in
`Svg.SceneGraph/SvgSceneExpressions.cs`, the language and the `<e:code>` declarations that are its
symbol table in `src/Svg.Expressions`, and the C# back end in `Svg.CodeGen.Skia/Expressions/`.

**The front end knows no target language.** `ExprChecker` returns a `TypedExpr`; the back ends
consume it. Two things to know before touching it: it holds the symbol table **by reference**
because `Resolve` adds each let after construction, and it throws on the first error in a fixed
visit order that several tests pin — operands before their operator, a condition before its
branches, arity before any argument.

**Two back ends, and they must agree numerically.** `ExprCSharpBackend` renders C#;
`ExprValueBackend` computes an `ExprValue` behind the `ExprEvaluator` facade. Traps that reading the
source will not reveal:

- Evaluate in **`float`**, since generated code narrows literals and calls `MathF`.
  `Svg.Expressions` multi-targets for `MathF`; the netstandard2.0 fallback lives in
  `ExprMathFallback`, compiled on every target so a test host can measure it.
- `hsl` is reimplemented from `SKColor.FromHsl`, whose final byte conversion **truncates**.
  Rounding instead disagrees on 76% of the domain.
- Do not un-short-circuit `&&`, `||` or `? :` in `ExprValueBackend`. `clamp` with a reversed range
  throws, so an eagerly evaluated operand changes behaviour.

**Two differential suites, at two layers, that do not cover for each other.**
`ExprEvaluatorDifferentialTests` compares the two back ends for a `TypedExpr`;
`SymNodeDifferentialTests` does the same a layer up for a `SymNode` and the helpers
`SymCSharpEmitter` calls, including the `SKColorF` conversion. Add a case to whichever layer you
touched. Both compare **bits** — a tolerance previously hid a real one-ulp divergence that surfaced
only as a single pixel.

**Two readers for `<e:code>`: add a rule to `SvgExpressionDeclarations.Builder`, never to one
reader.** `Parse` works from source text (`svgc`, the source generator);
`SvgDocument.ExpressionDeclarations` walks the tree, which is all `Load(XmlReader)` or an editor
has. The text reader exists because a foreign element's namespace is `protected internal` and so
invisible outside `Svg.Custom`. Their diagnostics are asserted identical.

**`min`/`max`/`step` on `<e:param>` are expression text, checked in two places on purpose.** The
structural rules — number-typed only, both ends or neither — are `Builder` rules, so both readers
report them identically and a range on a colour fails at read time. The numeric ones (`min <= max`,
`step > 0`) need values, so they live in `SvgExpressionParameter.ResolveRange()`, which shares
`ExprEvaluator.Isolated` with the `default` resolver so the two cannot resolve against different
scopes. Reading a document has to stay evaluation-free: `SKSvg.Load` never evaluates, and
`SvgDocument.ExpressionDeclarations` is recomputed on every access. `ResolveRange()` is total and
falls back to 0..1, the code generator ignores the range entirely, and nothing clamps a bound value
to it.

**A `color` parameter with a `default` emits `SKColor? tint = null`** plus a local coalescing to the
default, because `new SKColor(…)` is not a C# constant (CS1736). The body reads that local — C#
forbids shadowing — via a symbol-rewrite map on `ExprCompiler`. Local names come from
`ColourFallbacks()` **only**: `Resolve` rewrites references with them and the generator declares
them, and disagreement emits a body referencing a local that does not exist. A colour *without* a
default is unchanged.

**Evaluation rewrites the picture; it teaches the renderers nothing.**
`SvgSceneExpressionEvaluator.Evaluate` returns a new `SKPicture`, so `SkiaModel` and
`AvaloniaPicture` are untouched — necessary, since `Avalonia.Svg.Skia.SvgSource` shares one *static*
`SkiaModel`. It never mutates, and untouched subtrees return as the same instances. It reaches a
paint's `Color`, a `ColorShader`, gradient `SKColorF[]`, an opacity `SaveLayer`'s paint and a
`BlendModeColorFilter`, recursing through `DrawPictureCanvasCommand` and `PictureShader`. The lit
image filters' `LightColor` is deliberately not walked; a new model path that can carry an
expression needs adding here.

A false conditional **keeps the range's `Save`/`Restore`/`SetMatrix`/clip commands and drops only
the draws**, because the runtime applies `Concat(DeltaMatrix)` where generated code assigns
`SetMatrix(TotalMatrix)`. Real documents cannot tell the difference — `SvgSceneRenderer` balances
every range — so `ConditionalRangeTests` pins it on hand-built pictures. That suite fails if this
changes; the render tests will not.

**On `SKSvg`, loading never evaluates and `Model` is whatever is being rendered.** `Load` leaves
placeholders and does not read the declarations, so a malformed `<e:code>` cannot fail a load.
`SetExpressionValues` evaluates against a retained symbolic model — no re-parse, no scene recompile
— and only assigns on success.

Two invariants hold the design together:

1. **A symbolic value always carries a concrete one.** `SKColor.Expression` sits beside real RGBA
   channels, so consumers that ignore expressions keep working untouched.
2. **Placeholders are chosen so the element still paints.** `fill="none"`, `opacity="0"` or
   `visibility="hidden"` would remove the paint or subtree an expression needs to attach to.

`src/Svg.Expressions.Recipes` converts a finished SVG into that format from a recipe file. It is
a source-to-source rewriter and knows nothing about the expression language — the recipe's
`<code>` block is copied verbatim, and the code generator remains the only type checker. `svgc`
applies one with `-r`, and `--emit svg` writes the converted document instead of C# without
building a scene model. A whole build — drawings plus settings — is described by a project file
(`-p`), parsed by `src/Svg.CodeGen.Skia.Projects`. A `<group>` there scopes settings to some of
the drawings and is folded into them as the project is read, so `SvgcProject.Items` is a flat
list of resolved items whether the file uses groups or not, and nothing downstream knows. The source generator has no equivalent, so
generator-driven projects convert ahead of time and check in the result.

`src/Svg.Viewer.Skia.Avalonia` is the viewer, with `src/SvgViewer` as its shell. It draws onto
`SKCanvasControl` and owns its own scale and offset rather than using `Avalonia.Svg.Skia.Svg`, whose
`ArrangeOverride` returns `Stretch.CalculateSize(...)` — the control is always exactly the fitted
drawing, so it cannot fill a viewport and its `Zoom`/`PanX`/`PanY` are bounded by its own clip.
Loading is the only thing off the UI thread; values are bound with `SetExpressionValues` on the UI
thread, coalesced to one call per frame, and the render thread draws through `SKSvg.Draw`, which
brackets `BeginDraw`/`EndDraw` so the picture cannot be disposed under it. The control holds one
document, so the shell holds one control per tab and handles `OpenRequested` — raised for every
picked or dropped file before any of them is read — to put each in a tab of its own; `Close` on the
control is what disposes the document of a tab that goes away. A handled request hands its
`Completion` back, because the event is synchronous and `OpenAsync` would otherwise complete while
the files were still being read — which is what failed on CI and passed locally. `ShowSource` opens
a pane of the drawing's text, held as `SvgViewerDocument.SourceText` and captured at load: `SKSvg`
can keep its own source, but only behind the process-wide `CacheOriginalStream` toggle it uses for
reloading, and a viewer must not make every other `SKSvg` in the application retain its file.
`src/Svg.Highlighting` splits it into coloured pieces — hand-written rather than an editor library's
grammar, because the package would then carry a text editor and no stock XML grammar colours
`{{ … }}` or an `<e:let>` body as code. It is **its own project because it draws nothing**: no
brushes, no controls, nothing from Avalonia, which is what lets the editor share it and what makes
the two things belonging there — colouring the expression language, and diagnostics — sit on that
side of the seam rather than in a UI. The first is done: `SvgSourceExpressions` hands a `{{ … }}`
span, an `<e:let>` body, or an `<e:param>`'s `default`/`min`/`max`/`step` — all four of which are
expression text, not words — to **`Svg.Expressions`' own lexer**, reached through an `InternalsVisibleTo`
grant, because a second description of the language would drift from it — `%` is a suffix on a number
literal rather than an operator, and `and`/`or`/`not`/`lt`/`le`/`gt`/`ge`/`eq`/`ne` are word forms of
the symbolic operators, neither of which is guessable from the text. Three traps in using it: a number
token's `Text` excludes the `%` the lexer already consumed, so the span is widened by hand;
`true`/`false` lex as identifiers but the *parser* reads them as boolean literals, so they are
coloured as values; and the lexer throws on malformed input, so a refusal re-lexes the prefix before `ExprException.Position` and
leaves the remainder plain — which is also the position a diagnostic will underline. A token is a range into the document rather than a copy, which is both what keeps a whole file
affordable to hold and what a diagnostic needs to point at. Its invariant is that concatenating the tokens reproduces the input, which is
what lets it describe a malformed document rather than refuse it. The pane is **a row per line in a
virtualising list**, because one text block holding a whole drawing is what made colouring
size-limited at all: tokenizing 200,000 characters takes 7ms, but one styled `Run` each costs 130ms
at 1,100 runs and 18 seconds at 45,000 in a single block. Rows lay out only what is on screen — a
132KB drawing opens in 94ms with 17 rows built. Two things that arrangement needs. The `ScrollViewer`
must be the **pane's**, not a template on the `ItemsControl`: given its own template the list
realises every row it has, measured as 4,000 of 4,000 against 16 when simply placed in a scroller.
And a row colours at most `RowTokenLimit` pieces before showing the rest plainly, since virtualising
by line bounds a document but not a line — the same 132KB minified onto one line took 1.4s before
that, 340ms after.

`samples/SvgExpressionsDemo` is the worked example; it also has a `--render <dir>` mode that
writes PNGs without opening a window, which is the practical way to verify rendering changes.
`samples/SvgRecipeDemo` does the same for the recipe path and links that demo's `LivePreview.cs`
by file rather than duplicating it, so an edit there changes both. Neither demo generates C# any
more — both evaluate — so neither references Roslyn.

## Conventions

- `Directory.Packages.props` centralises package versions; `build/*.props` are per-dependency
  imports that projects `<Import>` rather than declaring `PackageReference` directly.
- Multi-targeting is common (`netstandard2.0;net461;net6.0;net8.0;net10.0` in the shim). Check a
  project's `TargetFrameworks` before using a modern BCL API.
- `Svg.Skia.slnx` is the solution (XML `.slnx` format, not `.sln`). New projects must be added to
  it to take part in the solution build.
