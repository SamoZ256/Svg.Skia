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

## Related docs

- [Svg.Expressions](svg-expressions) — the format the parameters are written in
- [Svg.CodeGen.Skia](svg-codegen-skia) — what the project it writes is built by
