# SVG with expressions — demo

A live editor for the expression extension. Type SVG on the left, watch the drawing update on the
right, and drive the declared parameters from controls that are rebuilt as the document changes.

## Running it

```sh
dotnet run --project samples/SvgExpressionsDemo/SvgExpressionsDemo.csproj -c Release
```

Two headless modes, for machines with no display:

```sh
# render the built-in logo at several values of t
dotnet run --project ... -c Release -- --render frames

# run the live pipeline over one file: parse, compile the scene, evaluate, save a PNG
dotnet run --project ... -c Release -- --live path/to/file.svg out.png
```

## How the editor works

It runs the **real** pipeline at runtime rather than interpreting anything:

```
SVG text ─► SvgService.FromSvg ─► SvgSceneRuntime.CreateModel ─► symbolic picture
                                                                       │
                       drawing ◄── SvgSceneExpressionEvaluator ◄── values from the controls
```

The two halves cost very different amounts, and splitting them is the point:

- **Text changed** — re-parse and recompile the scene. Debounced ~400 ms and run off the UI thread,
  so expect a beat before the drawing catches up.
- **Parameter changed** — evaluate the model that is already there. No parse, no scene compile.

This used to generate C# and compile it with Roslyn into a collectible `AssemblyLoadContext` whose
`Record(...)` was invoked by reflection, because evaluating an expression was something only the
code generator could do. `Svg.Expressions` evaluates directly now, so the demo needs no compiler and
runs anywhere the library does.

An application that renders on the UI thread should reach for `SKSvg.SetExpressionValues` instead of
the model-level API this demo uses. The demo evaluates on the render thread so that the picture it
draws is created and disposed inside a single draw, and never crosses a thread.

Consequences worth knowing:

- A failed edit keeps the last good drawing, so the view does not blank while you type.
- Errors come from the XML parser or the expression type checker, and an expression error carries a
  caret under the offending character — something the Roslyn diagnostics could never do, since they
  named a line of generated C# the author never saw.
- Numeric parameters use the `min`/`max`/`step` the document declares, and fall back to **0..1**
  when it declares none. `samples/SvgViewer` is the full viewer built on the same idea.

## How the extension works

Expressions go inline, in double braces, directly in the attribute they drive:

```xml
<stop offset="0%" stop-color="{{ primary }}" />
<circle fill="{{ dot }}" fill-opacity="0.85" />
<g opacity="{{ markFade }}"> ... </g>
```

The SVG parser lifts the text out of the braces into the element's custom attributes and
substitutes a **placeholder** — `#808080` for colours, `1` for opacities — so the rest of the
pipeline sees a well formed value. Code generators emit the expression in the placeholder's
place; nothing else ever sees it.

The placeholders are not arbitrary. The model branches on these values, and the wrong constant
would delete the very thing being parameterised: `fill="none"` produces no paint at all, and an
opacity of `1` normally skips creating a layer. Each placeholder is chosen to keep the element
paintable so the expression has something to attach to.

> **The trade-off.** `{{ ... }}` is not a valid SVG attribute value, so other tools show the
> placeholder rather than the intended colours — `Svg/logo.svg` opens as a grey logo in a
> browser, not a teal one. In exchange there is a single source of truth per attribute, with no
> second value to keep in sync.

Parameters and named intermediates are declared in a block that conforming SVG renderers ignore.
This part stays namespaced, because there is no inline form for a block of declarations:

```xml
<defs>
  <e:code>
    <e:param name="t" type="number" default="0" />
    <e:param name="bold" type="boolean" default="false" />

    <e:let name="wave">(sin(t * tau) + 1) / 2</e:let>
    <e:let name="primary">hsl(shift, 74%, bold ? 62% : 55%)</e:let>
    <e:let name="dot">wave gt 0.66 ? accent : mix(accent, deep, 0.45)</e:let>
  </e:code>
</defs>
```

`Svg.SourceGenerator.Skia` type checks that and turns it into:

```csharp
public static SKPicture Record(float t = 0f, float hue = 0.52f, float pulse = 1f, bool bold = false)
{
    float wave = ((MathF.Sin((t * (MathF.PI * 2f))) + 1f) / 2f);
    SKColor primary = SvgHsl(shift, 0.74f, (bold ? 0.62f : 0.55f));
    SKColor dot = ((wave > 0.66f) ? accent : SvgMix(accent, deep, 0.45f));
    ...
    skPaint1.Color = SvgScaleAlpha(glow, 0.55f);
    var skShader0 = SKShader.CreateLinearGradient(..., new SKColorF[2] { SvgToColorF(primary), ... });
}
```

Note that `let` types are inferred — `wave` is a number, `primary` a colour, and the generated
locals are typed accordingly. The generated source is written to `obj/GeneratedFiles/`.

`SvgScaleAlpha` and `SvgToColorF` are there because `stroke-opacity="0.55"` cannot be folded into
a literal alpha when the colour is an expression, so the fold is recorded and emitted as a call.
It is a call rather than inline arithmetic so the expression is evaluated exactly once.

One wrinkle if you write tests around this: `{{` clashes with C# raw string interpolation, so
`$"""... {{ x }} ..."""` fails to compile (CS9006). Use a plain `"""..."""` literal.

## The language

Three types: `number`, `color`, `boolean`.

| | |
|---|---|
| literals | `1.5`, `55%`, `#f80`, `#ff8800`, `#ff880080`, `true` |
| operators | `+ - * /`, `< <= > >= == !=`, `&& \|\| !`, `cond ? a : b` |
| word forms | `lt le gt ge eq ne`, `and or not` — aliases, so `<` and `&&` need no XML escaping |
| constants | `pi`, `tau` |
| maths | `sin cos tan abs sqrt pow floor ceil round min max clamp lerp mod` |
| colour | `rgb rgba hsl hsla mix withAlpha` |

`%` is a suffix on a number, never an operator — use `mod(a, b)` for the remainder. Angles are
degrees for `hsl`, radians for `sin`/`cos`/`tan`. `hsl` wraps its hue, so `shift + 45` is fine.

Errors are reported against the expression, with a caret:

```
error: 'hsl' takes 3 argument(s), but 2 were given.
    hsl(t, 50%)
    ^
```

## What is and isn't parameterised

`fill`, `stroke`, `stop-color`, `opacity` and `visibility`. Geometry (`x`, `y`, `cx`,
`width`, ...), `transform`, `display` and stroke widths are baked at generation time — so the
sample logo changes colour, fades and appears, but never moves.

`visibility` is the odd one out: it is a **boolean**, and instead of substituting a value it
wraps the element's draw calls in an `if`:

```csharp
if (showDot)
{
    var skPath4 = new SKPath();
    ...
}
```

Its placeholder is `visible` because a hidden element contributes no commands at all, and there
would be nothing left to make conditional. SVG's third value, `collapse`, means the same as
`hidden` outside of CSS table layout, so nothing is lost by treating this as two-state.

Braces in an unsupported attribute are currently ignored rather than reported, so
`stroke-width="{{ w }}"` silently does nothing.

## The cost of a parameter

`Record()` builds a **new `SKPicture` on every call**, because the picture depends on the
arguments. The editor calls it on every repaint, so dragging a slider records a picture per
frame. That is fine for a logo but is not free — for a large drawing, prefer recording once and
varying paints, or cache by argument value.

The draw callback runs on Avalonia's **render thread**, so it reads a snapshot taken on the UI
thread rather than touching `Slider.Value` or `Bounds` directly. Reading UI objects from there
gives torn values and unresponsive controls.
