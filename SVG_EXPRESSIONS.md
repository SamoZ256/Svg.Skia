# SVG Expressions

An extension to SVG that lets attribute values be **expressions** instead of literals. Compile the
document to C# with `svgc` or `Svg.SourceGenerator.Skia` and the expressions become parameters of
the generated drawing method; or render it directly with `SKSvg` and supply the values at run time
([§8](#8-rendering-at-run-time)).

This document describes the syntax and how to use it. For how any of it is implemented, see
`CLAUDE.md`.

```xml
<svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="64" height="64">
  <defs>
    <e:code>
      <e:param name="level" type="number" default="0" />
      <e:let name="alert">level ge 0.8</e:let>
    </e:code>
  </defs>

  <circle cx="32" cy="32" r="20"
          fill="{{ level > 0.9 ? #ff0000 : hsl(200, 74%, 55%) }}"
          visibility="{{ alert }}" />
</svg>
```

becomes

```csharp
public static SKPicture Record(float level = 0f)
{
    bool alert = (level >= 0.8f);
    ...
    if (alert)
    {
        skPaint0.Color = ((level > 0.9f) ? new SKColor(255, 0, 0, 255) : SvgHsl(200f, 0.74f, 0.55f));
        skCanvas0.DrawPath(skPath0, skPaint0);
    }
}
```

---

## 1. Two pieces of syntax

### 1.1 Inline expressions

An expression is written directly in the attribute it drives, wrapped in double braces:

```xml
<rect fill="{{ primary }}" opacity="{{ fade }}" />
```

The whole attribute value must be the expression. A value that merely *contains* braces is left
alone — `fill="url(#g) {{ x }}"` is an ordinary (invalid) value, not an expression.

Whitespace inside the braces is trimmed, so `{{primary}}` and `{{ primary }}` are equivalent.

### 1.2 The declaration block

Parameters and named intermediates are declared in a foreign-namespace element, which conforming
SVG renderers ignore:

```xml
<defs>
  <e:code>
    <e:param name="t"    type="number"  default="0" />
    <e:param name="hue"  type="number"  default="217" min="0" max="360" step="1" />
    <e:param name="tint" type="color"   />
    <e:param name="bold" type="boolean" default="false" />

    <e:let name="wave">(sin(t * tau) + 1) / 2</e:let>
    <e:let name="shade">mix(tint, #000000, wave)</e:let>
  </e:code>
</defs>
```

The namespace URI is `https://svg.skia/expr/1.0`. Matching is by **URI, not prefix** — call it
`e:`, `expr:` or anything else. The block may appear anywhere in the document (`<defs>` is
conventional), and multiple blocks are merged in document order.

This part stays namespaced because there is no inline form for a block of declarations.

**`<e:param>`** declares a method parameter. `name` and `type` are required.

`default` is an expression, and makes the generated parameter optional. With no `default` the
parameter is **required** — nothing is invented, so the signature never carries a value that
appears nowhere in the document.

Two rules follow from C# argument defaults being compile-time constants:

- A default may use literals, constants and functions but **not other parameters**, since an
  ordering dependency between them could not be honoured.
- The parameters *with* defaults have to come **last**. Declaring one without a default after one
  with a default is an error rather than a silent reordering, which would change the meaning of
  every positional call site.

A `color` parameter may carry a default like any other. Because `new SKColor(…)` is not a constant,
one that does is generated as a **nullable** parameter and falls back inside the method:

```csharp
public static SKPicture Record(SKColor? tint = null)
```

Passing `null`, or omitting the argument, gives the declared default. A colour parameter *without* a
default is generated as a plain required `SKColor`, unchanged.

`min`, `max` and `step` describe the range a host should offer — the ends of a slider, and its
increment. All three are optional, and each is an expression like `default` is, so `max="tau"`,
`max="100%"` and `step="1/60"` all work.

```xml
<e:param name="hue"  type="number" default="217" min="0" max="360" step="1" />
<e:param name="fade" type="number" default="0.5" step="5%" />
```

`min` and `max` are given together or not at all. `step` may stand alone, against the range of `0` to
`1` a parameter has when it declares none. All three are for `number` only — a colour or boolean
carrying one is an error, as is a `min` above its `max`, or a `step` of zero or less.

They are **advice to a host, not a constraint on the value**. Nothing clamps: a value supplied at run
time is accepted wherever it lies, a `default` outside its own range is legal, and generated code
does not mention the range at all.

**`<e:let>`** declares a local. Its type is **inferred** from the expression. Lets resolve in
document order, so a let may reference parameters and earlier lets, but not later ones.

Names must be valid identifiers (letter or `_`, then letters, digits or `_`), must not collide
with a built-in constant, function or operator word (see [§3.3](#33-operators)), and must be
unique across params and lets.

---

## 2. The placeholder mechanism

`{{ ... }}` is not a valid SVG attribute value. When the parser sees one it **lifts the text out**
and substitutes a placeholder, so the rest of the pipeline sees a well-formed document:

| Attribute | Placeholder |
|---|---|
| `fill`, `stroke`, `stop-color` | `#808080` |
| `opacity` | `1` |
| `visibility` | `visible` |

The placeholders are fixed and not configurable. Each is chosen so that the element still paints:
a value like `fill="none"`, `opacity="0"` or `visibility="hidden"` would remove the very thing the
expression is meant to drive.

> **Consequence.** Because the expression replaces the attribute rather than sitting beside it,
> the document has no design-time value. Tools that do not understand the extension render the
> **placeholder** — a file full of expressions opens as a grey drawing in a browser or Inkscape,
> not as its intended colours. In exchange there is a single source of truth per attribute, with
> no second value to keep in sync.

---

## 3. Language reference

### 3.1 Types

`number` (single-precision float), `color` (RGBA, 8 bits per channel), `boolean`.

There are no implicit conversions between them.

### 3.2 Literals

| Form | Type | Notes |
|---|---|---|
| `1`, `1.5`, `.5` | number | Decimal only. No exponent form. |
| `55%`, `7.4%` | number | `%` is a **suffix**, meaning "divide by 100". `55%` is `0.55`. |
| `#f80` | color | 3 hex digits, each doubled. |
| `#f808` | color | 4 hex digits, RGBA, each doubled. |
| `#ff8800` | color | 6 hex digits, RGB, alpha 255. |
| `#ff880080` | color | 8 hex digits, RGBA. |
| `true`, `false` | boolean | |

`%` is **only** a suffix and never an operator. Use `mod(a, b)` for the remainder. This keeps
`55%` unambiguous; writing `a % b` is a syntax error with a message saying so.

### 3.3 Operators

Loosest to tightest binding. Each comparison and logical operator has an equivalent **word
form**, listed alongside:

| Precedence | Operators | Word form | Operands | Result |
|---|---|---|---|---|
| 1 | `c ? a : b` (right-associative) | | `c` boolean; `a`, `b` same type | that type |
| 2 | `\|\|` | `or` | boolean | boolean |
| 3 | `&&` | `and` | boolean | boolean |
| 4 | `==` `!=` | `eq` `ne` | both operands the same type | boolean |
| 5 | `<` `<=` `>` `>=` | `lt` `le` `gt` `ge` | number | boolean |
| 6 | `+` `-` | | number | number |
| 7 | `*` `/` | | number | number |
| 8 | `-x` (unary) | | number | number |
| 8 | `!x` | `not x` | boolean | boolean |

Parentheses group as usual.

Arithmetic on colours is rejected — use `mix(a, b, t)` to blend. Ordering comparisons
(`<`, `>`, …) are numbers only; `==` and `!=` work on any type provided both sides match.

### Escaping, and the word forms

XML requires `<` and `&` to be escaped, which makes `t < 5` and `a && b` awkward to author.
`>` does **not** need escaping and can always be written literally.

The word forms exist to avoid the problem entirely, and are exact aliases — same precedence,
same generated code:

```xml
<e:let name="inRange">t gt 0.1 and t lt 0.9</e:let>
<e:let name="escaped">t &gt; 0.1 &amp;&amp; t &lt; 0.9</e:let>   <!-- identical -->
```

The full set is provided rather than only `lt`/`le`/`and`, so the language does not have a word
form for `<` but not for `>`. Both spellings may be mixed freely.

A `CDATA` section also works, since element text and CDATA are read the same way:

```xml
<e:let name="cdata"><![CDATA[t < 0.9 && t > 0.1]]></e:let>
```

All nine words — `and or not lt le gt ge eq ne` — are reserved and cannot be used as parameter
or let names.

### 3.4 Constants

| Name | Type | Value |
|---|---|---|
| `pi` | number | π |
| `tau` | number | 2π |

### 3.5 Functions

Numeric:

| Signature | Notes |
|---|---|
| `sin(x)` `cos(x)` `tan(x)` | **Radians.** For a 0..1 cycle use `sin(t * tau)`. |
| `abs(x)` `sqrt(x)` `floor(x)` `ceil(x)` `round(x)` | |
| `pow(x, y)` `min(a, b)` `max(a, b)` | |
| `mod(a, b)` | Remainder; the sign follows the dividend. |
| `clamp(x, lo, hi)` | |
| `lerp(a, b, t)` | `a + (b - a) * t`. **`t` is not clamped**, so it extrapolates outside 0..1. |

Colour:

| Signature | Notes |
|---|---|
| `rgb(r, g, b)` | Channels **0..255**, clamped. Alpha 255. |
| `rgba(r, g, b, a)` | Channels 0..255; alpha **0..1**, clamped. |
| `hsl(h, s, l)` | `h` in **degrees**, wrapped, so `h + 45` needs no `mod`. `s` and `l` are **0..1**, clamped — write them as `74%`. |
| `hsla(h, s, l, a)` | As `hsl`, alpha 0..1. |
| `mix(a, b, t)` | Per-channel linear blend including alpha. `t` clamped to 0..1. |
| `withAlpha(c, a)` | Replaces alpha; `a` is 0..1, clamped. |

Note the deliberate asymmetry: `rgb` takes 0..255 and `hsl` takes degrees plus fractions,
matching CSS rather than being internally uniform.

### 3.6 Grammar

```
conditional    := or ( '?' conditional ':' conditional )?
or             := and ( ( '||' | 'or' ) and )*
and            := equality ( ( '&&' | 'and' ) equality )*
equality       := comparison ( ( '==' | '!=' | 'eq' | 'ne' ) comparison )*
comparison     := additive ( ( '<' | '<=' | '>' | '>=' | 'lt' | 'le' | 'gt' | 'ge' ) additive )*
additive       := multiplicative ( ( '+' | '-' ) multiplicative )*
multiplicative := unary ( ( '*' | '/' ) unary )*
unary          := ( '-' | '!' | 'not' ) unary | primary
primary        := number | color | 'true' | 'false'
                | identifier
                | identifier '(' ( conditional ( ',' conditional )* )? ')'
                | '(' conditional ')'
```

---

## 4. Supported attributes

| Attribute | Expression type | Effect |
|---|---|---|
| `fill` | color | Fill colour. An element filled with `url(#gradient)` is parameterised through its `stop-color`s instead. |
| `stroke` | color | Stroke colour. |
| `stop-color` | color | Gradient stop colour. |
| `opacity` | number | Group opacity — becomes the alpha of the layer paint. |
| `visibility` | boolean | **Wraps the element's draw calls in an `if`.** |

Everything else — `x`, `y`, `cx`, `cy`, `width`, `height`, `d`, `transform`, `display`,
`stroke-width`, `fill-opacity` — is baked at generation time. See [§7](#7-limitations).

### 4.1 Interaction with literal attributes

Ordinary attributes still apply to a symbolic value, folded in at generation time:

- `fill-opacity` / `stroke-opacity` / `stop-opacity` scale the expression's alpha. Written as
  literals they still work: `fill="{{ primary }}" fill-opacity="0.5"` emits
  `SvgScaleAlpha(primary, 0.5f)`. A factor of exactly 1 folds away.
- `color-interpolation="linearRGB"` wraps the expression in an sRGB→linear conversion, so the
  generated colour matches what the model holds.

### 4.2 `visibility` is different

`visibility` is the only attribute that is not a value substitution. Because a hidden element
contributes no drawing commands at all, it cannot be expressed by swapping a value; instead the
element's commands are bracketed and emitted inside a condition.

The expression is a **boolean** — `true` meaning visible. SVG's third value, `collapse`, means
the same as `hidden` outside CSS table layout, so nothing is lost.

---

## 5. Generated code

**Without declarations**, output is unchanged from a plain SVG: a cached picture built in a
static constructor.

```csharp
public static SKPicture Picture { get; }
static Generated() { Picture = Record(); }
private static SKPicture Record() { ... }
public static void Draw(SKCanvas skCanvas) { skCanvas.DrawPicture(Picture); }
```

**With parameters**, the cache disappears — the picture depends on its arguments:

```csharp
public static SKPicture Record(float t = 0f, bool bold = false)
{
    float wave = ((MathF.Sin((t * (MathF.PI * 2f))) + 1f) / 2f);
    SKColor primary = SvgHsl(200f, 0.74f, (bold ? 0.62f : 0.55f));
    ...
}

public static void Draw(SKCanvas skCanvas, float t = 0f, bool bold = false)
    => skCanvas.DrawPicture(Record(t, bold));
```

Parameters appear in declaration order, typed `float` / `SKColor` / `bool`, and are optional only
where a `default` was declared ([§1.2](#12-the-declaration-block)):

```csharp
public static SKPicture Record(SKColor tint, float t = 0f)
```

Lets become typed locals in declaration order.

`--skiaSharp 3` emits paths against `SKPath` directly; the default targets SkiaSharp 4 and builds
them through `SKPathBuilder`. Nothing else in the output differs between the two.

Small `private static` helpers (`SvgHsl`, `SvgMix`, `SvgScaleAlpha`, `SvgToColorF`, …) are emitted
into the class **only when used**.

> **Cost.** `Record()` allocates and records a **new `SKPicture` on every call**. `Draw` disposes
> the picture it builds; what `Record` returns, the caller owns. See [§5.2](#52-reusing-the-last-picture)
> for the opt-in cache.

### 5.1 One file for a whole set

`svgc --singleFile` ([§9](#9-converting-an-existing-drawing)) emits every drawing of a batch into
one file. Classes are unchanged; what differs is that the helpers are **shared** rather than
repeated once per class, which for a set of fifty icons is the difference between one copy and
fifty:

```csharp
// <auto-generated />

using System;
using SkiaSharp;
using static SvgExpressionHelpers;

file static class SvgExpressionHelpers
{
    internal static SKColor SvgHsl(float h, float s, float l) => …
}

namespace Icons
{
    public static class Home   { … SvgHsl(200f, 0.74f, 0.55f) … }
    public static class Search { … }
}
```

The helper class sits outside every namespace, so drawings in different namespaces share it, and
it is imported with `using static` so call sites stay unqualified exactly as they are when the
helpers are private members.

`file` scoping means the class is invisible outside its file, so any number of generated files
coexist. It is a **C# 11** feature, and `LangVersion` follows the target framework rather than the
SDK — `netstandard2.1` defaults to C# 8 and rejects it until the project sets
`<LangVersion>11</LangVersion>`. Two fallbacks avoid that:

| `--helperScope` | Emits | Notes |
|---|---|---|
| `file` (default) | `file static class` | C# 11. Any number of files coexist. |
| `internal` | `internal static class` | Any C# version. The name is derived from the output file (`Icons.cs` → `Icons_SvgExpressionHelpers`) because an internal type is assembly-wide and two generated files would otherwise collide. |
| `perClass` | private members, as usual | Any C# version, no new type at all, helpers repeated per class. |

Single-drawing output is unaffected by all of this: helpers stay private members of the one class
that uses them.

### 5.2 Reusing the last picture

`svgc --cache lastValueLocked` makes `Draw` keep the picture it built and reuse it while the
arguments are unchanged:

```csharp
private static readonly object s_cacheLock = new object();
private static SKPicture s_cachedPicture;
private static float s_arg_h;

public static void Draw(SKCanvas skCanvas, float h)
{
    lock (s_cacheLock)
    {
        if (s_cachedPicture is null
            || s_arg_h != h)
        {
            s_cachedPicture?.Dispose();
            s_cachedPicture = Record(h);
            s_arg_h = h;
        }

        skCanvas.DrawPicture(s_cachedPicture);
    }
}
```

Measured on a 24×24 icon: **3.1 µs on a hit against 9.2 µs on a miss**, and no allocation on the
hit. One entry rather than a dictionary, which fits how these are used — a hover or a theme flip
moves the arguments, drawing happens every frame — and degrades gracefully, since a miss costs one
comparison per parameter against the cost of recording.

It is **off by default**. It turns `Draw` from stateless into stateful and holds one picture per
class for the life of the process, which is not a trade to make on a consumer's behalf. About
2 KB per drawing, so a few hundred of them are single-digit megabytes.

| `--cache` | Emits |
|---|---|
| `none` (default) | No cache. `Draw` records a picture per call and disposes it, and stays stateless. |
| `lastValue` | The cache without a lock. **Not safe to call from several threads**: one can replace and dispose the picture another is midway through drawing. |
| `lastValueLocked` | The cache guarded by a lock held across the draw. |

- `Record` is untouched by all three — it still returns a picture the caller owns. The cache lives
  only in `Draw`, where the picture cannot escape and can therefore be disposed when it is
  replaced.
- Under `lastValueLocked` the draw stays **inside** the lock. Releasing it earlier would reopen
  the race the lock exists to close.
- A parameterless document is skipped: it already caches better, in the static constructor, with
  no comparison at all.
- A `float` argument of `NaN` never equals itself, so it misses every time. That costs a
  re-record and nothing else.

---

## 6. Diagnostics

Expressions are type-checked at generation time. Errors carry an offset into the expression and
render with a caret:

```
error: 'hsl' takes 3 argument(s), but 2 were given.
    hsl(t, 50%)
    ^
```

`svgc` prints this and exits non-zero. `Svg.SourceGenerator.Skia` reports it as `SVG0001`, so it
fails the build. Diagnostics are not yet mapped to a location in the `.svg` file.

Checks include: unknown names (listing what is in scope), unknown functions (listing what
exists), wrong arity, wrong argument types, mismatched conditional branches, arithmetic on
colours, a paint expression that is not a colour, a `visibility` expression that is not a
boolean, forward references between lets, redeclaring a built-in, duplicate names, a parameter
without a default declared after one that has one, a `min`/`max`/`step` on something that is not a
number, one end of a range without the other, a reversed range, and a `step` of zero or less.

---

## 7. Limitations

**Unsupported attributes fail silently.** `stroke-width="{{ w }}"` is not lifted by the parser,
so it is treated as an ordinary malformed value and ignored — no error. A typo in an attribute
name behaves the same way.

**Geometry and transforms are not supported.** Coordinates cannot be expressions. Bounds, clips,
filter regions and `objectBoundingBox` gradient units are all derived from geometry, and each needs
a number rather than something that varies per call.

**`display` is not supported.** Unlike `visibility` it affects layout, so it feeds those same
bounding boxes and runs into the same problem as geometry.

**No design-time preview.** See [§2](#2-the-placeholder-mechanism).

**`{{` conflicts with C# raw string literals.** `$"""... {{ x }} ..."""` fails to compile
(CS9006). Use a non-interpolated `"""..."""` literal.

---

## 8. Rendering at run time

A document does not have to be compiled to C# to show real values. `SKSvg` can evaluate the
expressions directly and render the result:

```csharp
using var svg = new SKSvg();
svg.Load("badge.svg");                       // renders the placeholders

foreach (var parameter in svg.ExpressionParameters)
{
    // Name, Type, and DefaultExpression if the document declares one.
    // ResolveRange() gives a number's slider bounds, declared or the 0..1 fallback.
}

svg.SetExpressionValues(new Dictionary<string, ExprValue>
{
    ["tint"] = ExprValue.Color(255, 0, 0, 255),
    ["fade"] = ExprValue.Number(0.5f),
});
```

Two rules, pulling in opposite directions on purpose:

- **Loading never evaluates.** A document whose parameters have no defaults still loads and still
  renders, as the placeholders it was authored to fall back to ([§2](#2-the-placeholder-mechanism)).
  Nothing about an existing use of `SKSvg` changes, and a malformed `<e:code>` block cannot turn a
  successful load into an exception.
- **Supplying values is strict.** Every declared parameter must end up with a value, supplied or
  defaulted; one with neither is an `ExprException` naming it. That is the same rule the generated
  code enforces by making such a parameter required ([§5](#5-generated-code)). A host that would
  rather show a placeholder can bind one as a value.

Nothing is applied unless the whole set resolves, so a rejected call leaves the previous rendering
in place rather than a half-applied one. `SetExpressionValues` neither re-parses the document nor
recompiles its scene, so changing a value repeatedly — dragging a slider, say — is cheap.

`Model` is the picture being rendered: placeholders before any values are bound, values afterwards.
`ExpressionValues` reports what is currently bound, and `ClearExpressionValues()` returns to the
placeholders.

Errors are the same ones [§6](#6-diagnostics) describes, raised as `ExprException` rather than
printed, and an expression fault carries the same caret.

### 8.1 Evaluating a picture directly

A host that already holds a compiled scene can evaluate it without going through `SKSvg`. The result
is an ordinary picture that any renderer draws without knowing the extension exists:

```csharp
var picture = SvgSceneRuntime.CreateModel(document, assetLoader);
var evaluated = SvgSceneExpressionEvaluator.Evaluate(picture, declarations, values);
```

The input is left untouched, so the same compiled picture can be evaluated repeatedly with different
values. `SvgExpressionDeclarations` comes either from `Parse(svgText)` or, when only a parsed
document is at hand, from `SvgDocument.ExpressionDeclarations`.

---

## 9. Converting an existing drawing

Authoring by hand is fine for a drawing built for the purpose. For a set of finished SVGs coming
out of a design tool, `svgc` converts them mechanically: a **recipe** file lists the declarations
and says which colours become which expressions, and `-r` rewrites the drawing on the way in.

```sh
svgc -i badge.svg -r badge.recipe -o Badge.cs -n Icons -c Badge
```

The conversion lives in `src/Svg.Expressions.Recipes` — `SvgRecipe.Parse` and
`SvgRecipeRewriter.Apply` — and `samples/svgc/Example` holds a worked pair.

`--emit svg` writes the converted document to `-o` instead of C#, which is what a
generator-driven project needs, since the source generator takes no recipes:

```sh
svgc -i badge.svg -r badge.recipe -o badge.expr.svg --emit svg
```

That path **builds no scene model** — it is read, rewrite, write. A recipe is a text
transformation, so a drawing the renderer cannot handle still converts. It needs `-r`, since
without one the output would be a copy of the input; it cannot be combined with `--singleFile`,
since an svg document holds one drawing; and it cannot be combined with a resize, since there is
nothing compiled for a size to apply to.

### 9.1 The project file

A whole build — the drawings and the settings that apply to them — lives in one document, so an
icon set is generated by naming it and nothing else:

```sh
svgc -p icons.svgcproj
```

```xml
<?xml version="1.0" encoding="utf-8"?>
<svgc>
  <recipe>icons.recipe</recipe>
  <namespace>Icons</namespace>
  <singleFile>Generated/Icons.cs</singleFile>

  <svg input="art/home.svg"   class="Home" />
  <svg input="art/search.svg" class="Search" recipe="search.recipe" />
</svgc>
```

Settings are elements, drawings are `<svg>` elements, and a drawing overrides a setting by naming
its own: `namespace`, `class`, `recipe`, `width`, `height` and `scale` as attributes, plus
`output` where each drawing writes its own file.

| Setting | |
|---|---|
| `recipe` | applied to every drawing that does not name its own |
| `namespace`, `class` | defaults for the generated classes |
| `emit` | `csharp` or `svg`, as [above](#9-converting-an-existing-drawing) |
| `cache` | `none`, `lastValue` or `lastValueLocked` ([§5.2](#52-reusing-the-last-picture)) |
| `helperScope` | `file`, `internal` or `perClass` ([§5.1](#51-one-file-for-a-whole-set)) |
| `skiaSharp` | `4` (default) or `3`, the major version the output is compiled against |
| `singleFile` | fold the whole build into one C# file; per-drawing `output` is then ignored |
| `width`, `height`, `scale` | the size the drawings are generated at; see below |

**A resize is one setting in three parts.** `width` and `height` are in pixels, `scale` is a
factor of the size the document already has, and the three are `--width`, `--height` and
`--scale` on the command line. They move together rather than singly: whichever level names any
of them supplies all three, so a drawing's `width` replaces the project's `scale` outright
instead of joining it.

The aspect ratio is always kept. A width or a height on its own derives the other, and a pair
that does not match the drawing's own shape fits it into that box and centres it rather than
stretching it — a document's own `preserveAspectRatio="none"` is left to mean what it says. The
drawing is resized *before* it is compiled, by giving the document a different width and height
against its viewBox, so it is built at the new size and `Picture.CullRect` reports it. Nothing
scales the finished picture. A document with no viewBox is given one from the size it already
had, since without one a width and a height only reframe the drawing rather than scaling it.

**A `<group>` scopes settings to some of the drawings.** Between naming a setting once for the
whole project and repeating it on every drawing there is a third place. A group carries the same
attributes a drawing does, less `input` and `output` — those are about one file — and every
drawing inside it reads them as if it had named them itself:

```xml
<svgc>
  <namespace>Icons</namespace>

  <svg input="art/logo.svg" class="Logo" />

  <group namespace="Icons.Nav" recipe="nav.recipe" scale="2">
    <svg input="art/home.svg"   class="Home" />
    <svg input="art/search.svg" class="Search" recipe="search.recipe" />
  </group>
</svgc>
```

Groups nest, and the nearest wins: an inner group overrides an outer one, a drawing overrides
both, and the three parts of a resize are replaced together at whichever level names any of them.
A group holds `<svg>` and `<group>` and nothing else — its own settings are attributes on it, so
a setting *element* inside one is an error rather than something silently ignored. Drawings keep
their declared order across groups, so a `singleFile` build is emitted in the order the project
reads.

Three things follow from it being a project rather than a list of jobs:

**Paths resolve against the file**, not the working directory, so a project describes the same
build wherever it is run from.

**An unknown element or attribute is an error.** A mistyped setting is reported rather than
ignored, which is the difference between a project file and a bag of properties.

**A command-line flag beats the file**, which beats the built-in default — so a one-off
`svgc -p icons.svgcproj --emit svg` converts a build that ordinarily generates C#. It beats the
project-wide settings only: what a drawing or one of its groups names is the more specific of the
two, and wins over the flag.

Without `singleFile`, every drawing needs an `output`; with it, they are folded into one file in
declared order, namespaces grouped in the order they first appear, so the output of a given build
does not move between runs. Two drawings of one class name in one namespace is an error rather
than a collision in the emitted file.

The source generator has no equivalent: `AdditionalFiles` metadata carries `NamespaceName` and
`ClassName` but no recipe, so a generator-driven project converts with `--emit svg` first and
checks in the converted documents.

`samples/SvgRecipeDemo` runs the same chain as a live editor — recipe, converted SVG, generated
C# and the drawing, all updating as the recipe is typed. It also has a `--render <dir>` mode that
writes PNGs without opening a window.

### 9.2 The recipe file

The recipe is written in the extension's own namespace, so the `<code>` block is exactly the
block that ends up in the output — copied, not re-serialised.

```xml
<?xml version="1.0" encoding="utf-8"?>
<recipe xmlns="https://svg.skia/expr/1.0">

  <code>
    <param name="hue"  type="number"  default="217" />
    <param name="bold" type="boolean" default="false" />

    <let name="primary">hsl(hue, 91%, bold ? 66% : 60%)</let>
    <let name="deep">hsl(hue + 5, 71%, 40%)</let>
    <let name="alert"><![CDATA[hue < 100 ? #ff0000 : #ff6600]]></let>
  </code>

  <replace color="#3b82f6">primary</replace>
  <replace color="rgb(30,64,175)">deep</replace>
  <replace color="red">alert</replace>
</recipe>
```

**`<code>`** holds the `<param>` and `<let>` declarations of [§1.2](#12-the-declaration-block),
with the same meaning. It is copied verbatim into `<defs>` of the output as an `<e:code>` block,
placed first so it reads as the document's preamble. Several `<code>` blocks merge in document
order. The declarations are *not* checked here — the code generator owns the symbol table, and a
second implementation of the type checker could only disagree with the first.

**`<replace color="…">`** substitutes every occurrence of one colour. The expression is the
element's text, so `<` and `&` can be written in a `CDATA` section as they can in a `<let>`.

Given the recipe above:

```xml
<circle cx="128" cy="128" r="86" stroke="#3b82f6" />
<path d="…" style="fill:#3b82f6;stroke:red;stroke-width:2" />
```

becomes

```xml
<circle cx="128" cy="128" r="86" stroke="{{ primary }}" />
<path d="…" style="stroke-width:2" fill="{{ primary }}" stroke="{{ alert }}" />
```

### 9.3 What counts as an occurrence

The attributes searched are `fill`, `stroke` and `stop-color` — the colour-valued members of the
table in [§4](#4-supported-attributes). `opacity` and `visibility` have no colour to match on and
are not reachable from a recipe.

Matching is **by value, not by spelling**. The recipe's colour and the document's are both parsed
with `SvgColourConverter`, the converter the document parser itself uses, so a rule written
against `#3388ff` also claims `#38f`, `rgb(51, 136, 255)` and any other name for it. The parity
matters: a value this tool understood but the parser did not would attach an expression to a
colour that was never painted. Two rules that resolve to the same colour are an error, since one
of them could never apply.

`none`, `inherit`, `currentColor` and `url(#…)` references are **not colours**. They select a
paint rather than name one, and substituting an expression would change what the element does
rather than what shade it is.

A colour found in a **`style` attribute is promoted** to a presentation attribute — `{{ }}` is
only ever lifted out of attributes ([§2](#2-the-placeholder-mechanism)), never out of a style
declaration, so an expression left inside `style` would have no effect. The declaration is
removed from `style`, which is what keeps the new attribute the winning value; the rest of the
declarations keep their original text. When `style` declares a property the recipe does not
match, the presentation attribute of the same name is left alone even if it *would* match: the
cascade means it was never painting anything.

An attribute that already holds an expression is left as the author wrote it.

### 9.4 What the conversion preserves

Whitespace between elements, comments and the XML declaration survive, so re-running a recipe
after the drawing is exported again gives a diff of the colours and nothing else. Layout *inside*
a tag does not: an XML reader does not retain it, so attributes spread over several lines come
back on one.

The extension namespace is declared on the root as `e:`, or on the next free prefix if that one
is taken. An existing declaration of the namespace is reused whatever its prefix.

### 9.5 Limits of the conversion

**Only colours, and only by value.** There is no way to target an element by `id`, by class or by
selector, so the attributes with no distinctive literal value — `opacity` and `visibility` — have
to be written by hand afterwards. Two elements that share a colour but need different expressions
also have to be separated by hand.

**CSS rules are not searched.** A colour set by a `<style>` element rather than by an attribute or
a `style` attribute is not found. Presentation attributes and `style` attributes cover what
drawing tools export; a stylesheet needs the cascade to resolve.

**A rule that matches nothing is a warning, not an error.** One recipe usually covers a family of
drawings, and not every drawing uses every colour. It is still the first thing to check when the
output is not what was meant.

**Converting an already-converted document is refused.** It would declare the parameters twice,
and almost always means the output path was passed as the input.
