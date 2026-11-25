---
title: "Svg.Custom"
---

# Svg.Custom

`Svg.Custom` packages the SVG DOM and parser used by the rest of the repository. It is the layer that understands SVG documents as `SvgDocument`, `SvgElement`, and related DOM types before the repository-specific model and rendering layers take over.

## Install

```bash
dotnet add package SamoZ256.Svg.Custom --prerelease
```

## Choose this package when

- you need to parse or inspect SVG documents directly,
- you want to modify the SVG DOM before rendering,
- you want the exact DOM package used by `Svg.Model` and `Svg.Skia`,
- you are building tooling that should stay at the SVG-document level.

## Important packaging note

Unlike most packages in this repository, `Svg.Custom` is packaged under the `MS-PL` license because it vendors and republishes the upstream SVG implementation it is built from.

If you depend on `Svg.Custom` directly, review that license decision alongside the rest of your dependency policy.

## Namespace and role

| Package | Namespace | Role |
| --- | --- | --- |
| `Svg.Custom` | `Svg` | Parsed document model and parser |

Most applications do not need to reference `Svg.Custom` explicitly because higher layers bring it in transitively. Direct references make sense when you want DOM access in application or tooling code.

## Basic usage

```csharp
using Svg;
using Svg.Skia;

var document = SvgDocument.Open("Assets/icon.svg");

if (document is not null)
{
    Console.WriteLine($"{document.Width} x {document.Height}");

    using var renderer = new SKSvg();
    renderer.FromSvgDocument(document);
}
```

That pattern is useful when a document should be parsed once, inspected or modified, and only then rendered.

## What this package adds in this repository

The repository wraps the vendored SVG sources with project-level concerns such as:

- the package identity used by the repo,
- trimming and AOT annotations for supported target frameworks,
- embedded SVG 1.1 DTD resources,
- analyzer and generator integration used by the vendored code path,
- repository-local SVG 1.1 animation object-model types,
- typed `pointer-events` support used by the shared hit-test and interaction layers,
- `SvgElementNames.NameOf` and `SvgElementAddress`, which answer the two questions the vendored
  `SvgElement` cannot: what an element is called, and which element it is,
- `<sweepGradient>`, a gradient that turns around a centre.

The rendering behavior still lives above this package, not inside it.

### Naming and addressing an element

`SvgElement.ElementName` is `protected internal`, so nothing outside this package can say what an
element is called. `SvgElementNames.NameOf` carries the rules once: a document is `svg`, a
foreign-namespace element carries its own name, an element whose name this renderer does not
recognise keeps what it was written with, and everything else is named by its type.

`SvgElementAddress` is the child-index path from the root — `Create(element).Key` is `0/2/1`, and
`Resolve(document)` walks it back down. It is the identity that survives a document being rebuilt:
a host reloading a drawing from edited text gets a new `SvgDocument` and new elements every time, so
a reference taken a moment ago names nothing. `SvgSceneNode.ElementAddressKey` spells the same path,
and so does `SvgSourceElements` in `Svg.Highlighting`, which is what lets a scene node, a row of text
and an element all be talked about as the same thing.

### Sweep gradients

`<sweepGradient>` is Skia's sweep gradient: the colour turns around a centre instead of running
along a line or out from a point. No SVG specification has it, so it is read as an element of the
SVG namespace and `SvgSweepGradientServer` types it.

```xml
<sweepGradient id="hue" gradientUnits="userSpaceOnUse" cx="15" cy="15"
               gradientTransform="rotate(-90 15 15)">
  <stop offset="0" stop-color="#FF2323" />
  <stop offset="0.5" stop-color="#23FFFF" />
  <stop offset="1" stop-color="#FF2323" />
</sweepGradient>
<circle cx="15" cy="15" r="13" fill="url(#hue) #FF2323" />
```

| Attribute | Default | Meaning |
| --- | --- | --- |
| `cx`, `cy` | `50%` | The centre, a length resolved as `radialGradient`'s is. |
| `startAngle`, `endAngle` | `0`, `360` | Where offsets 0 and 1 sit, in degrees written without a unit. |
| `spreadMethod` | `pad` | What lies outside `startAngle`..`endAngle`. [Avalonia's brush](svg-controls-avalonia) draws every spread as `pad`. |
| `gradientUnits`, `gradientTransform`, `href` | as on any gradient | |

Stops, and expressions on `stop-color` and `stop-opacity`, work as on any gradient. Through `href`
the stops, units, transform and spread come from any gradient; `cx`, `cy` and the angles come only
from another `sweepGradient`, as a `radialGradient` takes its centre only from a `radialGradient`.

**Angles are Skia's.** 0° points along the gradient space's +x axis, which is 3 o'clock with no
transform, and angles grow clockwise on screen. Nothing wraps: a direction is measured in [0°, 360°),
so under the default `pad`, `startAngle="-90"` leaves the first stops unused rather than turning the
gradient. Turn it with `gradientTransform`. CSS's `conic-gradient(from A at X Y, …)` starts at 12 o'clock, so it is written
`cx="X" cy="Y" gradientTransform="rotate(A−90 X Y)"`.

**Bounding-box units.** Under the default `objectBoundingBox` the angles are measured in the box's
unit square, so a box that is not square skews them, as it turns a `radialGradient` into an ellipse.
Write `userSpaceOnUse` where the angles have to be true.

**Refused values.** A `startAngle` greater than `endAngle`, or any value that is not finite, paints
the fallback, because Skia builds no gradient from it. `startAngle="90deg"` is not read at all: the
default stands, and `Svg.Highlighting` marks the value.

**In a browser.** Browsers do not know the element, and `url(#id)` that names it paints nothing.
Write a fallback colour after it, as in `fill="url(#hue) #FF2323"`, and a browser paints that.

**Against TapHome's hand-drawn ones.** `SkiaCSharpRenderTests.ButtonHelper` draws the four sweep
drawings in TapHome's `ButtonHelper.cs` with their own SkiaSharp calls and compares each with the SVG
that replaces it, at 256 px. The hue wheel measures 0.0008 RMS at most and the hue colour picker
0.0001, held to 0.0011 and 0.001. The thermostat ring measures 0.0003 enabled and 0.0010 at most
disabled, held to 0.001 and 0.0016, the second because Ubuntu's x64 raster rounds its 0.2 opacity
differently and measured 0.0011. The thermostat dial measures 0.0009, 0.0047, 0.0036 and 0.0053 at
levels 0, 0.25, 0.5 and 1, held to 0.0012, 0.0061, 0.0047 and 0.007: Skia strokes ButtonHelper's arc,
which starts at 120°, up to a fifth of a pixel off the whole ring that the SVG clips. Those are Apple
Silicon's numbers, and each bound leaves up to a third more for the raster pipelines of CI's x64
machines. Every bound is exceeded by the same drawing a little off, such as the hue turned 2° or the
arc 3° short. Compiled against SkiaSharp 3.119, the generated code measures within 0.00013 of these,
inside every bound, and draws without allocating.

## Animation DOM coverage

`Svg.Custom` now includes the repository's SVG 1.1 animation object-model overlay in the `Svg` namespace.

That surface includes:

- `SvgAnimationElement`
- `SvgAnimationAttributeElement`
- `SvgAnimationValueElement`
- `SvgAnimate`
- `SvgSet`
- `SvgAnimateMotion`
- `SvgAnimateColor`
- `SvgAnimateTransform`
- `SvgMPath`

The DOM layer also adds typed enums and converters for common animation attributes such as `attributeType`, `restart`, `fill`, `calcMode`, `additive`, `accumulate`, and transform `type`.

This package does not execute animation by itself. It only parses and stores the DOM. Runtime evaluation lives in [Svg.Skia](svg-skia).

## Good use cases

- validating raw SVG input before rendering,
- rewriting document structure or attributes,
- custom importers that output `SvgDocument`,
- applications that already understand the upstream `Svg` DOM and want to pair it with `Svg.Skia`.

## When not to choose `Svg.Custom`

- Choose [Svg.Skia](svg-skia) when you want a renderer, not just the DOM.
- Choose [Svg.Model](svg-model) when your work starts after parsing and focuses on the intermediate drawables or model.
- Choose a UI package when the main goal is Avalonia integration.

## Related docs

- [Source Formats and Assets](../concepts/source-formats-and-assets)
- [Interaction and Animation](../guides/interaction-and-animation)
- [Svg.Skia](svg-skia)
- [Svg.Model](svg-model)
