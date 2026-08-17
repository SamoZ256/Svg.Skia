# Svg.Skia Changelog

## Unreleased

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
