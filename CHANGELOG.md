# Svg.Skia Changelog

## Unreleased

* Added `Svg.Viewer.Skia.Avalonia`, a reusable Avalonia viewer for drawings using the expression
  extension, with `src/Svg.Studio` as the application built on it. It opens a file by picker or
  drop, zooms and pans — wheel about the cursor, drag, and fit / 1:1 / reset with a percentage
  readout — and builds a control per declared parameter: a slider honouring any `min`/`max`/`step`
  for a `number`, a colour picker for a `color`, a checkbox for a `boolean`, each seeded by
  *evaluating* the declared `default` rather than parsing it, so `default="tau / 4"` works. Nothing
  blanks the drawing: a failed load keeps the previous document, a malformed `<e:code>` block is
  reported but still renders its placeholders, and a rejected value leaves the last good rendering in
  place. It draws onto `SKCanvasControl` and owns its transform rather than using the
  `Avalonia.Svg.Skia.Svg` control, which sizes itself to the drawing it fits — a 100x100 document in
  a 400x200 pane arranges at 200x200 — and so cannot fill a viewport.

  Opening through the **file picker** currently crashes on macOS with Avalonia 12.0.0, inside the
  native storage provider as the panel is dismissed. `samples/TestApp` crashes there identically, so
  the fault is upstream rather than in this package, and it reproduces in a bare Avalonia app. The
  workaround is `AppBuilder.UseManagedSystemDialogs()`, Avalonia's own managed picker, which
  `src/Svg.Studio` applies on macOS; dropping a file on the viewer or handing a path to
  `LoadAsync` avoids the picker entirely.

  Zooming is on the scroll wheel and on `Ctrl`/`Cmd` `+`/`-`/`0`/`1` as well as the toolbar. A
  trackpad two finger scroll arrives as a wheel event with a fractional delta and so zooms smoothly
  on the same curve a mouse notch steps along. A trackpad *pinch* is a separate platform gesture that
  Avalonia 12.0.0 raises only through its internal `Gestures` class, so it cannot be subscribed to
  from outside the framework yet.

* `<e:param>` now takes optional `min`, `max` and `step` attributes describing the range a host
  should offer for a `number` — the ends of a slider and its increment. Each is an expression like
  `default` is, so `max="tau"` and `step="1/60"` work, and each resolves against nothing at all, so a
  bound cannot reference another parameter. `min` and `max` come as a pair; `step` may stand alone
  against the 0..1 a parameter has when it declares none. `SvgExpressionParameter` grows
  `MinExpression`, `MaxExpression`, `StepExpression`, `HasRange` and `ResolveRange()`, the last of
  which is total and returns that 0..1 fallback — exactly the range hosts hardcoded before the format
  could express anything else. The range is advice to a host and never a constraint: nothing clamps,
  a `default` outside its own range is legal, and **generated code is unchanged**, since the code
  generator has no use for it. Whether a range is structurally allowed is settled while the
  declarations are read, so a range on a colour is caught immediately; whether the numbers make sense
  is settled by `ResolveRange()`, because reading a document must not evaluate anything.

* A `color` parameter may now declare a `default`. It could not before, because `new SKColor(...)`
  is not a C# compile-time constant (CS1736) — a limit of the target language that had leaked into
  the format, since the runtime evaluator always handled such a default without a special case. A
  colour parameter carrying one is now generated as `SKColor? tint = null` and coalesced to the
  declared default inside the method, so omitting the argument or passing `null` gives that default.
  A colour parameter *without* a default is generated exactly as before, so no existing signature
  or generated file changes.

* Fixed generated code converting an expression gradient stop to `SKColorF` differently from the
  rest of the library. `SvgToColorF` divided each channel by `255f` while `ShimSkiaSharp.SKColor`
  multiplies by `1 / 255.0f`, and the two disagree for 126 of the 256 byte values — enough for a
  gradient to differ by one level on a pixel. Generated code was the inconsistent side: a *literal*
  stop is emitted as the floats the model already converted by the reciprocal, so it disagreed with
  its own literal stops as well as with the runtime. **Generated output changes** for documents with
  expression gradient stops: the body of the `SvgToColorF` helper, and nothing else.

* `samples/SvgExpressionsDemo` and `samples/SvgRecipeDemo` no longer generate and compile C# to
  render. Both evaluate the scene model directly, so neither references Roslyn any more, and neither
  ships `Microsoft.CodeAnalysis.dll`. A parameter change now re-evaluates rather than re-parsing,
  re-generating and re-compiling into a fresh collectible `AssemblyLoadContext`. The demos no longer
  display the generated C#; `svgc` remains the way to see that.

* Added expression support to `SKSvg`: `ExpressionParameters` reports what a document declares,
  `SetExpressionValues` binds values and re-renders, `ExpressionValues` reports what is bound, and
  `ClearExpressionValues` goes back to the design-time placeholders. Loading is unchanged — it renders
  the placeholders and does not evaluate, so a document whose parameters have no defaults still loads
  and no existing use of `SKSvg` is affected. Supplying values is strict: a parameter with neither a
  value nor a `default` is an error, matching the generated code, and nothing is applied unless the
  whole set resolves. Re-evaluating does not re-parse the document or recompile the scene.

* Fixed `NonSvgElement.DeepCopy` losing the element's namespace. The copy kept its name and
  attributes but claimed to be in the SVG namespace, so anything matching a foreign element on name
  *and* namespace silently stopped matching — found when a cloned document reported no `<e:code>`
  declarations.

* Added `SvgDocument.ExpressionDeclarations`, which reads a document's `<e:code>` block from the
  parsed tree rather than from source text. `Load(XmlReader)` and a document handed over directly
  never had text to re-parse, so this is what lets any route into a document be evaluated.
  `SvgExpressionDeclarations.Parse` is unchanged and still what `svgc` and the source generator use;
  both now go through the new `SvgExpressionDeclarations.Builder` so they validate identically.

* Added `SvgSceneExpressionEvaluator.Evaluate` in `Svg.SceneGraph`, which turns a picture holding
  expressions into one holding values. It rewrites the model rather than changing any renderer, so
  `SkiaModel` and the Avalonia controls draw an evaluated drawing with no changes of their own.
  Nothing is mutated and untouched subtrees are returned as the same instances, so re-evaluating with
  new values costs one walk of the parts that carry expressions.

* Added a runtime evaluator for the SVG expression language: `ExprEvaluator` and `ExprValue` in
  `Svg.Expressions` compute an expression against values instead of rendering it as C#, so a
  renderer can show real values rather than the design-time placeholder. `ExprEvaluator.Create`
  binds values to a document's `<e:code>` declarations and resolves its lets; a parameter with
  neither a supplied value nor a `default` is an error, which is the rule generated code already
  enforces.

* `Svg.Expressions` now targets `netstandard2.0;net6.0;net8.0;net10.0` rather than netstandard2.0
  alone. Generated code calls `MathF`, which arrived with netstandard2.1, so the evaluator has to as
  well to give the same answer; the netstandard2.0 build falls back to the double-precision
  functions and differs by at most one ulp for `sin`, `cos`, `tan` and `pow`.

* **Breaking:** the SVG expression language's lexer, parser and type checker moved to a new
  `Svg.Expressions` package, and `ExprType` and `ExprException` moved with them from namespace
  `Svg.CodeGen.Skia.Expressions` to `Svg.Expressions`. Source-compatible after updating a `using`;
  not binary-compatible, and a type forwarder cannot bridge a namespace change. `ExprCompiler`
  stays where it was, as a facade over the checker and the C# back end. `ExprCompiler.FunctionNames`
  and `ConstantNames` are now `ExprFunctions.FunctionNames` and `ExprFunctions.ConstantNames`.

* **Breaking:** the `<e:code>` declarations moved to `Svg.Expressions` and were renamed —
  `SvgCodeDeclarations`, `SvgCodeParameter` and `SvgCodeLet` are now `SvgExpressionDeclarations`,
  `SvgExpressionParameter` and `SvgExpressionLet`. They are the symbol table the expression
  language is checked against, so they belong beside it rather than in the code generator, which
  is no longer the only back end that reads them. The two members that produce C# stayed behind as
  extension methods in `Svg.CodeGen.Skia`: `Resolve()` is unchanged, and
  `declarations.DefaultCodeFor(parameter)` is now `parameter.DefaultCode()`. A `color` parameter
  carrying a `default` is now rejected by `Parse` rather than when C# is emitted, so the same
  document is accepted or refused identically whichever back end reads it.

* Added SVG 1.1 animation object-model coverage in `Svg.Custom` for `animate`, `set`, `animateMotion`, `animateColor`, `animateTransform`, and `mpath`.
* Added typed `pointer-events` support, geometry-aware hit testing, topmost-element targeting, and routed interaction dispatch with capture, tunnel, bubble, and cursor resolution.
* Added shared animation playback in `SKSvg`, including animation time control, invalidation events, layered redraw, throttling helpers, and native-composition scene extraction.
* Added host animation backends for Avalonia and Uno, including resolved-backend diagnostics and Avalonia retained `NativeComposition` playback with fallback.
* Added an animation benchmark harness in `tests/Svg.Skia.Benchmarks` and exposed animation/backend controls in `samples/TestApp`.
* Updated HarfBuzzSharp dependencies to `8.3.1.3` so Android consumers restore native assets with 16 KB page-size support.

## 0.3.0

* Updated NuGet packages.
* Update SVG sources.

## 0.2.0

* Updated NuGet packages.

## 0.1.9

* Updated NuGet packages.

## 0.1.8

* Added fixes for Xamarin.Forms Android/iOS.

## 0.1.7

* Strong name signed assemblies.

## 0.1.6

* Fixed `marker` exception.
* Fixed `use` to accept `svg` element.
* Added native build support using CoreRT.
* Added referenced properties support for `filter` element.
* Added `feImage` referenced image `preserveAspectRatio` support.
* Improved `Filter Effects` validation.
* Fixed `fill` and `stroke` validation.
* Added `SKFontManager` typeface provider.
* Added custom font loader helper class `CustomTypefaceProvider`.

## 0.1.5

* Fixed `systemLanguage` validation.
* Removed debug code.

## 0.1.4

* Added `switch` element support.
* Added `systemLanguage` attribute support.

## 0.1.3

* Updated `Svg.Skia.Converter` tool.
* Use `Svg.Custom` build of the `Svg` library.
* Initial support for new `Filter Effects`.

## 0.1.2

* Added referenced properties support for `linearGradient` element.
* Added referenced properties support for `radialGradient` element.
* Changed bitmap creation to use `SKImageInfo`.

## 0.1.1

* Added `Overflow` property to `Drawable`.
* Added `FilterQuality=SKFilterQuality.High` for `ImageDrawable`.
* Added transform support for `image` `svg` fragment.
* Added support for embeded `svgz` images.

## 0.1.0

* Added `Svg.Custom` project for `Svg` library.
* Refactored utility classes.
* Added custom font support via `ITypefaceProvider`.

## 0.0.12

* Fixed deffered `stop` color paint server.
* Fixed invalid `SvgUnit` default value handling.
* Added `Filer Effects` utility class.

## 0.0.11

* Fixed `mask` processing.
* Updaed `feColorMatrix` filter processing.

## 0.0.10

* Added new `Filter Effects` support.
* Added `mask` element support.
* Fixed `clipPath` element processing.

## 0.0.9

* Added `Filer Effects` prcessing.

## 0.0.8

* Fixed `stoke` and `file` validation.
* Refactored utility classes.
* Added generic referenced element support.

## 0.0.7

* Added `Xamarin.Forms` sample application.
* Initial `IImage` implemetation for `Avalonia`.
* Fixed `rect` attributes validation.

## 0.0.6

* Made `Drawable` classes public.
* Added initial `HitTest` implemetation for `Drawable`.

## 0.0.5

* Removed `SKSvgRenderer` implemetation.
* Added `Drawable` object model.

## 0.0.4

* Refactored `SKSvgRenderer` class.

## 0.0.3

* Added `marker` element support.

## 0.0.2

* Added `pattern` element support.
* Added `image` element support.

## 0.0.1

* Initial release.
