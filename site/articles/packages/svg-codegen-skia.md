---
title: "Svg.CodeGen.Skia"
---

# Svg.CodeGen.Skia

`Svg.CodeGen.Skia` turns the intermediate `ShimSkiaSharp.SKPicture` model into C# source code. It is the package to choose when SVG parsing should happen ahead of time and the final application should ship compiled `SKPicture` builders instead of raw `.svg` files.

## Install

```bash
dotnet add package SamoZ256.Svg.CodeGen.Skia --prerelease
```

## Choose this package when

- you want a custom asset-preparation step instead of a Roslyn source generator,
- generated files should be checked into source control,
- a build pipeline needs explicit control over file names and output locations,
- you want to generate C# from an already-built `ShimSkiaSharp.SKPicture`.

## Main type

| Type | Role |
| --- | --- |
| `SkiaCSharpCodeGen` | Generates C# source from a `ShimSkiaSharp.SKPicture` |

The generated class includes:

- a static `Record()` method returning a new picture, which the caller owns,
- a static `Draw(SKCanvas)` method,
- all SkiaSharp object creation and disposal logic required to reconstruct the picture.

## Typical workflow

1. Parse the SVG into the repository's intermediate model.
2. Call `SkiaCSharpCodeGen.Generate(...)`.
3. Write the returned string to a `.cs` file.
4. Compile that file into the consuming project.

## Example

```csharp
using System.IO;
using Svg.CodeGen.Skia;
using Svg.Skia;

using var svg = new SKSvg();

if (svg.Load("Assets/icon.svg") is not null && svg.Model is not null)
{
    var code = SkiaCSharpCodeGen.Generate(svg.Model, "MyApp.Generated", "Icon");
    File.WriteAllText("Generated/Icon.g.cs", code);
}
```

This example uses `Svg.Skia` to create the intermediate model, but `Svg.CodeGen.Skia` itself only needs a `ShimSkiaSharp.SKPicture`.

## Drawings that use expressions

A drawing using the [expression extension](svg-expressions) generates: its parameters become C# parameters, and each expression is emitted as code that recomputes the value at call time rather than as the number it happened to have when the drawing was compiled.

A driven `transform` is generated too. The picture records how the matrix was derived — `ShimSkiaSharp`'s `SymMatrix`, the transform functions and their arguments — so the emitted code rebuilds the matrix from the argument values instead of baking one, and the refusals are the same ones the renderer applies: a driven transform under a filter, under an ancestor that opens a layer, or written inside a `<clipPath>` is refused, because each of those was measured against the matrix as the drawing was compiled.

What generation still refuses outright is the other kind of expression, the kind resolved **before** the drawing is recorded — element text, `font-family`, `font-size` and the rest of the text attributes. Generation bakes the picture with the text already measured and the glyphs already positioned, so a parameter driving one could never vary at run time; the document is refused rather than generated with a signature that offers something it cannot do.

## Boxes for the host

A drawing can reserve a box for the code that draws it to lay something of its own into, such as a label, a value or a control. Mark the element with `e:bounds` and a member name:

```xml
<rect id="level" x="2" y="6" width="26" height="18" fill="none" e:bounds="LevelRect" />
```

A project build (`svgc`'s, or Svg Studio's project export) writes it as a constant of the generated class:

```csharp
public static readonly SKRect @LevelRect = new SKRect(2f, 6f, 28f, 24f);
```

The numbers are in the generated picture's coordinates: after the viewBox, any size or padding the build applies, and every transform above the element, so a turned box gives the upright box around it. A box a parameter moves is measured at the declared defaults, with a warning. The build skips two kinds of mark: one under `<defs>`, and one with no place of its own in the drawing, such as a mark inside a `<clipPath>`. It warns about the second. A name that is not letters, digits and underscores, is used twice, or clashes with `Record`, `Draw` or the class itself stops the build. A direct `SkiaCSharpCodeGen.GenerateFile` caller passes the boxes on `SkiaCSharpDrawing`. The source generator and Svg Studio's export of a lone drawing ignore the mark. The [PaintCode import](svg-paintcode) writes one for every shape named `Embed<X>`, and [Svg Studio](../guides/svg-studio#boxes-for-the-code-that-draws-it) shows and names them.

## Shaping the generated file

`GenerateFile` takes an `SvgCodeStyle`. Every member left at its default writes the file the
generator always has, byte for byte. A project build reads the same settings from the project:
`<body>`, `<typeface>`, `<api>`, `<resizing>`, `<context>` and `<order>` on an `.svgcproj` root, and
`method` and `parameters` on an `<svg>`. Svg Studio keeps them as attributes of `<studio>` and
`<drawing>`.

### Drawing straight onto the canvas

`body` `direct` makes `Draw` draw the drawing onto the canvas it is given, instead of recording a
picture and drawing that. Each matrix the drawing sets is composed onto the caller's matrix by the
canvas, so drawing under a transform gives the same pixels as recording and then drawing. A path,
and a paint that no argument changes, is built once in the static constructor and read from a
field, and two built alike share one. A shape's paint that follows an argument and holds no shader,
filter or dash is set on one paint per thread, which the generated `SvgPaint()` resets once a draw,
and only in what differs from the shape before it, so drawing shapes allocates nothing. A driven
paint with a shader or a filter still builds its paint on every draw. A picture the drawing draws,
which is how the scene holds every text element, is replayed onto the canvas under a save rather than
recorded on every draw, in both bodies, and its paths, paints and fonts are statics like the rest: a
typeface is looked up once, when the static constructor runs. A text run then allocates only the
text blob SkiaSharp's `DrawText(string)` builds on every call. A filled rect, round rect,
oval or circle with no layer open around it builds no path in either body: it is drawn with
`DrawRect`, `DrawRoundRect`, `DrawOval` or `DrawCircle`, as the renderer draws it. `Record` stays,
and records `Draw`.

### Matrices, saves and shapes

Both bodies set a matrix only before something is drawn, clipped or layered under it, so a run of
shapes under one matrix sets it once. A direct body sets a pure translation with `Translate`, and a
record body sets every matrix whole, as it always has. A save that
scopes no clip scopes only a matrix, which each draw sets anyway, and is left out; a layer's save
stays. Skia records every save a matrix changes under, so this is most of what a picture holds:
HueBulbBulb's nine strokes recorded 45 operations and record 9.

A shape under nothing but a translation is built with the translation added to its coordinates and
drawn under the caller's matrix, which is what PaintCode's own export did. A shape keeps the canvas
translation where its paint has something in its own coordinates: a shader, an image filter, a dash
or a non-scaling stroke. The moved coordinates round where the matrix would have, so this is not bit
for bit what the interpreter draws. Of TapHome's 1,014 icons drawn at 1×, 2× and 3×, at whole and
half pixel offsets, 77 differ in any pixel: 2,951 of 35.9 million, on antialiased edges, by a median
of 16 and at most 112 of 255.

A run of four or more path commands is built from data rather than a call each: the verbs as bytes
and the coordinates as floats, which the compiler keeps as data in the assembly, replayed by the
generated `SvgPathData` (SkiaSharp 3) or `SvgPathBuilderData` (4). A polygon is a move and its
lines, as Skia adds it. Shorter runs, rects, round rects, ovals and circles stay calls.

Measured on TapHome's 1,014 icons with `body="direct"`, against the export PaintCode itself wrote for
the same icons, through the same `VectorIconsResource.drawXxx` calls: SkiaSharp 3.119, .NET 10,
Apple M4. The timed icons are HueBulbBulb, SymbolIpScanner, SymbolWeather, SymbolSchedules,
SymbolSync and VacationState.

| | PaintCode's export | Generated |
| --- | --- | --- |
| Release DLL | 2.03 MB | 2.74 MB |
| NativeAOT executable, over an empty one (osx-arm64, all rooted) | +3.7 MB | +4.5 MB |
| First draw of every icon, JIT | 314 ms | 199 ms |
| First draw of every icon, NativeAOT | 30 ms | 33 ms |
| Recording one draw into an `SKPictureRecorder`, JIT | 0.59–1.82 µs | 0.31–0.57 µs |
| Recording one draw, NativeAOT | 0.79–2.27 µs | 0.40–0.81 µs |
| Drawing onto a 90 × 90 surface at 3×, JIT | 6.0–18.2 µs | 5.9–17.4 µs |
| Allocated per draw | 0 B | 0 B |
| Operations a recorded HueBulbBulb holds | 9 | 9 |

Both bodies leave out what `ShimSkiaSharp`'s `CommandPlan` finds draws nothing: an antialiased clip
around content that lies inside it, which Skia would otherwise blend everything under through a
coverage mask, and a group opacity over a single shape, whose alpha is multiplied into that shape's
paint instead — at run time, when the opacity is an expression. A clip over a driven transform or
stroke width is kept, since some value could carry the content across it.

### Naming typefaces

The recorded picture holds whatever typeface the compiling machine found. Where *SF UI Display* is
not installed, that is *Times*. `typeface` replaces the font-manager lookup with an expression from
the host, filled in from what the document asked for:

```text
PaintCode.TypefaceManager.GetTypeface("{family-dashed}-{weight}.otf")
  → PaintCode.TypefaceManager.GetTypeface("SF-UI-Display-Medium.otf")
```

The placeholders are `{family}`, `{family-dashed}`, `{weight}` (`Regular`, `Medium`, `Bold`…),
`{weightValue}` (`500`) and `{slant}`.

### A PaintCode-style API

`api` names one more class, appended to the file. It calls every drawing the way code that
PaintCode generated is called:

```csharp
namespace PaintCodeResources
{
    public static partial class VectorIconsResource
    {
        public static void drawTanklevel(SKCanvas canvas, SKColor accentColorOn, SKColor colorPurple,
            SKColor blackColor_, SKColor whiteColor_, bool state, bool accent, bool enabled, bool isLight, float level)
            => global::Icons.Analog.TankLevel.Draw(canvas, /* every argument, by name */);

        public static void drawTanklevel(SKCanvas canvas, SKRect targetFrame,
            global::PaintCode.ResizingBehavior resizing, /* the same */) { /* fitted, then drawn */ }

        public static void drawAnaloglevel(SKCanvas canvas, SKColor accentColorOn, bool enabled,
            float level, out SKRect levelRect) { /* drawn; levelRect = AnalogLevel.LevelRect */ }
    }
}
```

- **Method name.** A drawing's `method`, or `draw` and its class name. A PaintCode import writes
  each drawing's `method` as PaintCode named it.
- **Arguments.** Every parameter the drawing declares, in the order `order` lists them; names not
  in the list come after it, as declared. No argument has a default, so a positional call means what
  it meant to PaintCode. A drawing's `parameters` lists its own exact order instead. A name there
  that the drawing does not declare is still accepted and ignored, and takes its type from another
  drawing that declares it. A PaintCode import writes `order` as the library's parameter colours,
  then its parameter variables.
- **`resizing`** names the host's `ResizingBehavior` enum (`AspectFit`, `AspectFill`, `Stretch`,
  `Center`). Each drawing then gets the overload taking `(SKRect targetFrame, resizing)`, which fits
  the drawing into the target the way PaintCode's `ResizingBehaviorApply` does.
- **`context`** names the type a drawing whose document holds text takes ahead of its own
  arguments. The text counts even if it is hidden.
- **Boxes.** Each `e:bounds` box comes back as an `out SKRect` with its first letter lowered.

The class is `partial`, so the host can keep what else it held beside it. The build refuses an API
class with no single file to go in, two drawings that would be the same overload, and a drawing
namespace that would hide the class from callers (a top-level `VectorIconsResource` namespace beside
a `VectorIconsResource` class).

Measured over the PaintCode sample in [Svg.PaintCode](svg-paintcode), 1,379 of PaintCode's 1,380
overloads have one of the same name, argument types and argument names, in the same order. That
count needs `parameters` on Questionmark and Ventilation, which take arguments nothing they draw
reads; without it, three methods differ. The one left is a canvas with a dynamic frame variable.
Numbers come across as `float`, as PaintCode's are: import without *whole numbers as integers*,
which turns `step` into an `int`.

## Why use this package instead of the source generator

Choose `Svg.CodeGen.Skia` when generation is an explicit build step and you want control over:

- output file locations,
- naming conventions,
- checked-in generated artifacts,
- batch generation outside MSBuild or Roslyn.

Choose [Svg.SourceGenerator.Skia](svg-sourcegenerator-skia) when SVG files already live in the consuming project and implicit build-time generation is the better experience.

## Good scenarios

- design-system icon packs converted during CI,
- SDKs that ship compiled picture classes,
- NativeAOT or trimmed applications that want less runtime parsing work,
- custom CLI tools such as `svgc`.

## Related docs

- [Source Generator and svgc](../guides/source-generator-and-svgc)
- [Svg.SourceGenerator.Skia](svg-sourcegenerator-skia)
- [ShimSkiaSharp](shim-skiasharp)
- [Svg.Expressions](svg-expressions)
