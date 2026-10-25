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

`Drawings` hands back the same conversion without writing any of it — each desk, and each canvas as
an `XDocument` — which is how a caller that keeps the drawings rather than filing them reads one.

From the command line, `svgc --paintcode Icons.pcvd` imports beside the document and then builds the
project it wrote, so every other flag means what it already meant. In
[Svg Studio](../guides/svg-studio), opening or dropping a `.pcvd` converts it into one project
holding every drawing, opened unsaved and unnamed — saving it asks where it goes.

An import also records how PaintCode's own generated code called each canvas: its method name and
the order of its arguments. A project that names an API class then generates one that callers of
PaintCode's class can keep calling. See [a PaintCode-style API](svg-codegen-skia#a-paintcode-style-api).

## What comes across

| PaintCode | SVG |
| --- | --- |
| A desk | A folder, and a group of the project whose namespace continues the project's (`.Overlays`) |
| A canvas | One `.svg`, and one row of the project — or one drawing of a Studio project |
| A group | `<g>`, with its clip as a `<clipPath>` |
| Bezier, rectangle, rounded rectangle, oval, star, polygon | `<path>`, or `<rect>` and `<ellipse>` where those say it |
| A symbol instance | `<use>` of a copy in the same file's `<defs>` |
| A library colour marked as used | `<e:param type="color">` |
| A colour derived from one | `<e:let>` over `withAlpha`, `withSaturation` or `mix`, following it |
| A variable marked as used | `<e:param>` of the type it was declared as — see below |
| A variable derived from others | `<e:let>` |
| `fill`, `strokeColor`, `fontColor` | `fill`, `stroke`, and the text's own `fill` |
| `alpha`, `visibilityMode` | `opacity`, `display` |
| A text run | `<text>` placed as PaintCode's runtime places it: `dominant-baseline="cap-middle"` centres the font's cap height in the box, the run aligns along the box's width cut to a whole unit, and `font-kerning="none"` |
| The display position, rotation and scale | One argument each of `transform` |
| A gradient chosen by an expression | One `<linearGradient>`, with the expression on every `stop-color` |
| A gradient laid by dragging its two ends | The same two points, in `userSpaceOnUse` |
| A gradient's middle stop | `mix()` of the stops either side, which is how PaintCode draws one |
| A translucent library colour shadowed | The shade PaintCode derives, worked out at import |
| A shape named `Embed<X>` | `e:bounds="<X>Rect"` on its box, a [box for the host](svg-codegen-skia#boxes-for-the-host) |

PaintCode is y-up and SVG is y-down, so everything is turned over on the way: a point at `(x, y)`
is written at `(x, -y)`, and an angle with it.

### The types a variable can have

PaintCode's own menu offers eight, plus Expression for a variable derived from others:

| PaintCode | Becomes | Notes |
| --- | --- | --- |
| Number | `number` | Whatever limit the variable carries, which in a real document is none. |
| Fraction | `number`, `min="0" max="1"` | The range is **not in the document** — no variable carries a limit — so it comes from the type, which is the only place PaintCode keeps it. |
| Angle | `number`, `min="0" max="360" step="1"` | Degrees, which is what the translator already assumes where a trigonometric function wants radians — so the range is PaintCode's own dial, a turn a degree at a time. |
| Text | `string` | |
| Boolean | `boolean` | |
| Point, Size, Rectangle | *refused* | The expression language has no such type. The declaration is named as unusable rather than flattened into something it is not. |
| Expression | `<e:let>` | It declares no type of its own, so it is whatever the expression came out as. |

The distinction lives on the variable, not on the value: Number, Fraction and Angle are all stored as
one double, so a reader going by storage sees one numeric type where PaintCode has three — and a
fraction or an angle read that way arrives indistinguishable from a number, its range lost.

Where a variable does carry a limit, that is the author's own answer to the question the type only
implies, so it displaces the implied ends — a fraction limited to 0.2..0.8 is written that way. An
angle keeps its `step="1"` either way, a step being granularity rather than an end.

The ends are advice rather than a constraint, so an angle saved outside its turn — PaintCode writes
those, its own gradients defaulting to -90 — still arrives as it was written, and a host widens the
dial to reach it. The step is not advice: a slider offering one snaps to it, so a fractional angle is
off the dial as soon as it is dragged.

### Whole numbers, if you ask

None of those three is whole. A value is stored as a real whether or not it looks like an integer — a
width of 15 and an alpha of 1 both are — and a variable's bounds are a minimum and a maximum with no
step, so nothing in the document distinguishes a step enum from a slider that happens to sit on a
whole number.

That leaves the shape PaintCode documents keep reaching for unsaid: a parameter compared with `==`
against 0, 1, 2 and so on, which is an integer carried as a float.

```bash
svgc --paintcode Icons.pcvd --paintcodeIntegers
```

writes a **Number** variable as `<e:param type="integer">` where its value is whole and its bounds are
whole. A Fraction and an Angle keep the type they were given whatever their values, being continuous
by declaration — the range above is theirs either way. It is still **a guess, and yours to make**: a
Number slider that happens to sit on whole ends is retyped too, and only the author knows which it
was. A derived variable is never retyped — its body is PaintCode's arithmetic, and retyping the
answer without the working would refuse the document rather than improve it.

The expression language never converts between the two numeric types, and PaintCode has only the
one, so the translation says where they meet: an integer multiplied by a fraction, compared with one,
or handed to a function that takes numbers goes through `num()`, and `step * animation * -360`
is written `num(step) * animation * -360`. A whole literal needs nothing — the language settles it to
whichever type stands beside it — and a division of two integers is written as the real division
PaintCode meant. A derived variable built only from integers is an integer in turn. A number printed
into a label is rounded first: `stringFromNumber(level * 100)` is `str(int(round(level * 100)))`,
ties to even as the C# export does, where PaintCode's Java export rounds them up.

That crossing used to be missing, and what it cost is worth knowing. The offset a driven transform
carries is evaluated against every declaration at once, so one local the language refused wrote
every driven transform in the document as the number it was saved at, with a note per element and
no cause: every tank drew at its saved level. Two checks now stop that shape of failure. A set of
declarations that does not evaluate as translated refuses the import outright, naming the variable
and the fault, and an expression that translates but does not evaluate is noted with its cause and
the drawing's own value written — rather than written, and refused with the whole drawing when it
is opened.

## What does not, and what happens instead

The rule is that the converter never writes a binding it cannot say. It writes the value the drawing
had, and records a `PaintCodeImportNote` naming the canvas, the element and the property. Nothing
renders wrong, and what was lost is a list rather than a surprise.

- **A driven stroke width, and an oval's driven start and end angle.** `stroke-width` and path data
  are literal in the expression format.
- **A gradient turned to an angle off the axes.** PaintCode places one from the shape's own middle,
  which is not its box's; the conversion lays it across the box. Only where the gradient was turned
  by a dial — one laid by dragging its two ends carries them, and those are written exactly.
- **A library colour derived by an operation with no equivalent.** Alpha, saturation and shadow are
  carried; anything else keeps the colour it came from and is reported. A shadow over a translucent
  colour is worked out at import, since `mix` towards opaque black would raise its alpha as well as
  darken it, so it keeps the shade the canvas was saved with whatever it is derived from.
- **A driven transform inside a group that draws into a layer.** The layer's bounds were measured
  from where its children were, so the number is written instead — the same rule the format states.
- **A blend mode.** PaintCode's numbering is not SVG's, and one that is nearly right is worse than
  one that is reported.
- **A canvas the document does not contain.** Reported apart from the rest, because it is the one
  thing here no amount of work on the converter can draw: a symbol naming a canvas that was renamed
  or deleted upstream without its references being repointed.

## How it is verified

The importer's structure is checked in `tests/Svg.PaintCode.UnitTests`: the archive reads, the markup
it writes is asserted shape by shape, and every drawing loads and binds. Its pixels are checked
against PaintCode itself. PaintCode generates drawing code as well as documents, so the same icons
can be drawn twice, once through the import and once through PaintCode's own generated output, and
compared. A drawing is held to **0.03** in the metric the W3C rows use, at every combination of the
booleans it takes and with every colour swapped.

That comparison needs a document and PaintCode's generated code for it, so it lives in a separate
repository. On the production document it was written against, 985 of 1014 drawings meet the bound.
Each of the other 29 has a named cause: 16 place text from the shape's box where PaintCode measures
it, and 13 are artefacts of the reference itself, such as rounding to two decimals, unantialiased
clips, and a gradient its runtime can't draw.

What runs here on every platform is a slice of it, in `tests/Svg.PaintCode.UnitTests/TestAssets/PaintCode`:
freely licensed documents committed with the raster PaintCode's own code drew for each canvas. Each
document is imported afresh and every canvas is compared within its own threshold, so the slice
covers the import as well as the drawing. The first, SampleStyleKit, exposed four import faults that
the production document never had: colours saved in an older form read as black, a highlight and an
alias derived as their parent, parameters named unlike PaintCode's, and a nearly whole arc that drew
nothing.

## Related docs

- [Svg.Expressions](svg-expressions) — the format the parameters are written in
- [Svg.CodeGen.Skia](svg-codegen-skia) — what the project it writes is built by
