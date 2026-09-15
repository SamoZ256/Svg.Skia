---
title: "Svg.PaintCode"
---

# Svg.PaintCode

`Svg.PaintCode` reads a PaintCode document — a `.pcvd` file — and writes it out as SVG drawings, an
[svgc project](svg-codegen-skia) that builds them, and a note for everything it could not carry
across.

A PaintCode drawing is usually one of a family rather than one picture: its colours and its
visibility follow named variables, which is what its own code generator turns into method parameters.
That part survives the conversion. Variables become the
[expression extension](svg-expressions)'s parameters and locals, and the attributes they drove are
written in double braces.

## Install

```bash
dotnet add package Svg.PaintCode
```

## Use it

```csharp
var result = PaintCodeImport.Run("Icons.pcvd", new PaintCodeImportOptions("out")
{
    ProjectPath = "out/Icons.svgcproj",
    Namespace = "Icons"
});

foreach (var note in result.Notes)
{
    Console.WriteLine(note);
}
```

From the command line, `svgc --paintcode Icons.pcvd` imports beside the document and then builds the
project it wrote, so every other flag means what it already meant. In
[Svg.Studio](../guides/readme), **File → Import PaintCode…** does the same and opens the result;
dropping a `.pcvd` on the window imports it beside itself.

## What comes across

| PaintCode | SVG |
| --- | --- |
| A desk | A folder |
| A canvas | One `.svg`, and one row of the project |
| A group | `<g>`, with its clip as a `<clipPath>` |
| Bezier, rectangle, rounded rectangle, oval, star, polygon | `<path>`, or `<rect>` and `<ellipse>` where those say it |
| A symbol instance | `<use>` of a copy in the same file's `<defs>` |
| A library colour marked as used | `<e:param type="color">` |
| A colour derived from one | `<e:let>` over `withAlpha` |
| A variable marked as used | `<e:param>`, with its range as `min` and `max` |
| A variable derived from others | `<e:let>` |
| `fill`, `strokeColor`, `fontColor` | `fill`, `stroke`, and the text's own `fill` |
| `alpha`, `visibilityMode` | `opacity`, `display` |
| The display position, rotation and scale | One argument each of `transform` |
| A gradient chosen by an expression | One `<linearGradient>`, with the expression on every `stop-color` |

PaintCode is y-up and SVG is y-down, so everything is turned over on the way: a point at `(x, y)`
is written at `(x, -y)`, and an angle with it.

## What does not, and what happens instead

The rule is that the converter never writes a binding it cannot say. It writes the value the drawing
had, and records a `PaintCodeImportNote` naming the canvas, the element and the property. Nothing
renders wrong, and what was lost is a list rather than a surprise.

- **A driven stroke width, and an oval's driven start and end angle.** `stroke-width` and path data
  are literal in the expression format.
- **Text built from a number.** PaintCode's `stringFromNumber` has no equivalent, so the words the
  drawing had are written.
- **A gradient laid at an angle off the axes.** PaintCode places one from the shape's own middle,
  which is not its box's; the conversion lays it across the box.
- **A driven transform inside a group that draws into a layer.** The layer's bounds were measured
  from where its children were, so the number is written instead — the same rule the format states.
- **A blend mode.** PaintCode's numbering is not SVG's, and one that is nearly right is worse than
  one that is reported.

## How it is verified

PaintCode generates drawing code as well as documents, so the same `.pcvd` can be drawn twice — once
through this conversion, once through PaintCode's own generated output — and the two compared as
pixels. `tests/Svg.PaintCode.UnitTests/Oracle` does that for every canvas, at every combination of
the booleans PaintCode varies it on.

That comparison needs the document and the generated code, neither of which belongs in this
repository, so it runs only where they are:

```bash
PaintCodeResourcesDir=/path/to/generated \
SVG_PAINTCODE_SAMPLE=/path/to/Icons.pcvd \
SVG_PAINTCODE_FONTS=/path/to/fonts \
  dotnet test tests/Svg.PaintCode.UnitTests/Svg.PaintCode.UnitTests.csproj -c Release
```

`PaintCodeResourcesDir` is what compiles the comparison in at all. Unset — which is what continuous
integration does — the suite builds and runs without it, and a dozen drawings committed with the
raster PaintCode produced for them are compared instead, on every platform.

**The conversion is not yet at parity, and the suite does not claim it is.** Measured against the
1014-canvas sample, 376 canvases match; 396 are within a hairline of it (0.004–0.01), 182 differ in
visible detail, and 60 are plainly wrong. Most of the drift only appears once a parameter leaves its
default, which is why checking defaults alone had shown the conversion as sound.

So each canvas is pinned at what it currently measures, in `TestAssets/Oracle/parity.csv`: a canvas
that gets worse fails, and the file doubles as the list of what is left to fix. Numbers there above
0.004 are gaps, not allowances; closing one means re-running the report and committing the smaller
number.

One caveat bounds the whole comparison. The generated code rounds every literal to two decimals — a
stroke the document sets at `0.3364` is written `0.34f` — so the oracle is a rounded rendition and a
residual of about a pixel is unreachable by construction. Canvases that emit nothing finer than two
decimals reach parity roughly twice as often as the rest.

Seventeen canvases draw text, and they are compared only when `SVG_PAINTCODE_FONTS` names the folder
holding the faces PaintCode asks for. Nothing sets `TypefaceManager.FontNamePrefix` of its own
accord, so PaintCode's lookup otherwise falls back to whatever family the system lists first without
reporting it — which is worse than failing. Given the faces, both sides draw the same words in the
same one; without them these rows stand aside rather than compare against an arbitrary font.

## Related docs

- [Svg.Expressions](svg-expressions) — the format the parameters are written in
- [Svg.CodeGen.Skia](svg-codegen-skia) — what the project it writes is built by
