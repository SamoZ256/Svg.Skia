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
| A gradient laid by dragging its two ends | The same two points, in `userSpaceOnUse` |
| A library colour desaturated or shadowed | The shade PaintCode derives, worked out at import |

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
- **A gradient turned to an angle off the axes.** PaintCode places one from the shape's own middle,
  which is not its box's; the conversion lays it across the box. Only where the gradient was turned
  by a dial — one laid by dragging its two ends carries them, and those are written exactly.
- **A library colour derived by an operation with no equivalent.** Alpha, saturation and shadow are
  carried; anything else keeps the colour it came from and is reported. A derived colour is also
  worked out at import rather than followed live, so a symbol handed a different colour to derive
  from draws the shade the canvas was saved with.
- **A driven transform inside a group that draws into a layer.** The layer's bounds were measured
  from where its children were, so the number is written instead — the same rule the format states.
- **A blend mode.** PaintCode's numbering is not SVG's, and one that is nearly right is worse than
  one that is reported.
- **A canvas the document does not contain.** Reported apart from the rest, because it is the one
  thing here no amount of work on the converter can draw: a symbol naming a canvas that was renamed
  or deleted upstream without its references being repointed.

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

Every canvas is drawn at each combination of the booleans PaintCode varies it on, and at the ends and
middle of each number, and must come within **0.03** of PaintCode. The ones that cannot are listed in
`TestAssets/Oracle/exceptions.csv` with the reason each cannot — an entry without a cause is refused,
so a canvas cannot join the list by having a number written beside it, and one that starts meeting
the bound has to be taken out rather than left sitting there.

**The conversion is not at parity and the list says where it is not.** Of 1014 canvases, 954 meet the
bound. Of the 60 that do not: 16 have a sweep an expression drives, which path data keeps literal; 16
draw text, which SVG anchors where PaintCode measures; 4 want a whole gradient or a blend mode the
format has no word for; 2 are the document pointing at canvases it does not contain; 2 are the
reference being older than the document rather than anything to fix; and **20 are not yet
understood**, which is the number to watch.

One caution about the reference itself. It is generated from the document and can fall behind it:
two canvases draw a shape the archive plainly holds, visible and unbound, that PaintCode's own export
has no trace of — in neither the SkiaSharp it was transliterated to nor the Android export it came
from. Where the conversion and the reference disagree, check which is older before assuming which is
wrong.

Two things put a floor under the bound and neither is the conversion's. The two sides reach Skia
through different models, where a single axis-aligned rect costs about 0.0045 and an icon is many;
and PaintCode's generated code rounds every literal to two decimals, so what is compared against is
itself a rounded drawing — of the 24 canvases it rounds nothing in, 23 match outright. For scale,
this repository's own W3C rows sit at 0.022 for whole rendered pages.

Seventeen canvases draw text, and they are compared only when `SVG_PAINTCODE_FONTS` names the folder
holding the faces PaintCode asks for. Nothing sets `TypefaceManager.FontNamePrefix` of its own
accord, so PaintCode's lookup otherwise falls back to whatever family the system lists first without
reporting it — which is worse than failing. Given the faces, both sides draw the same words in the
same one; without them these rows stand aside rather than compare against an arbitrary font.

## Related docs

- [Svg.Expressions](svg-expressions) — the format the parameters are written in
- [Svg.CodeGen.Skia](svg-codegen-skia) — what the project it writes is built by
