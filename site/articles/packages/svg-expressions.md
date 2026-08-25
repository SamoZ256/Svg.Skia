---
title: "Svg.Expressions"
---

# Svg.Expressions

`Svg.Expressions` is the SVG expression extension: an addition to SVG that lets an attribute value be
an **expression** instead of a literal, so one drawing can stand for a whole family of them.

{%{
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
}%}

The package itself is the language — lexer, parser and type checker — shared by everything that reads
the format. This article is the format: what you may write and what it means. Rendering a drawing
with values supplied is [Svg.Skia](svg-skia); turning one into C# is
[Svg.CodeGen.Skia](svg-codegen-skia) and [Svg.SourceGenerator.Skia](svg-sourcegenerator-skia);
colouring and checking one for a source view is [Svg.Highlighting](svg-highlighting).

## Install

```bash
dotnet add package Svg.Expressions
```

You need it directly only to lex, parse or type-check expression text yourself. The packages above
reference it already.

## 1. Two pieces of syntax

### 1.1 Inline expressions

An expression is written directly in the attribute it drives, wrapped in double braces:

{%{
```xml
<rect fill="{{ primary }}" opacity="{{ fade }}" />
```
}%}

The whole attribute value must be the expression. A value that merely *contains* braces is left
alone — `fill="url(#g) {%{{{ x }}}%}"` is an ordinary (invalid) value, not an expression.

Whitespace inside the braces is trimmed, so {%{`{{primary}}` and `{{ primary }}`}%} are equivalent.

A drawing written this way has no design-time value: the expression replaces the attribute rather
than sitting beside it, so a tool that does not know the extension shows a stand-in colour rather
than the intended one. In exchange there is one source of truth per attribute and no second value to
keep in sync.

### 1.2 The declaration block

Parameters and named intermediates are declared in a foreign-namespace element, which conforming SVG
renderers ignore:

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

The namespace URI is `https://svg.skia/expr/1.0`. Matching is by **URI, not prefix** — call it `e:`,
`expr:` or anything else. The block may appear anywhere in the document (`<defs>` is conventional),
and multiple blocks are merged in document order.

**`<e:param>`** declares a parameter. `name` and `type` are required.

`default` is an expression, and makes the parameter optional. With no `default` the parameter is
**required** — nothing is invented, so a value never appears that is written nowhere in the document.

Two rules follow:

- A default may use literals, constants and functions but **not other parameters**, since an ordering
  dependency between them would be invisible in the document.
- The parameters *with* defaults have to come **last**. Declaring one without a default after one
  with a default is an error rather than a silent reordering.

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
time is accepted wherever it lies, and a `default` outside its own range is legal.

**`<e:let>`** declares a local. Its type is **inferred** from the expression. Lets resolve in document
order, so a let may reference parameters and earlier lets, but not later ones — and not itself.

Names must be valid identifiers (letter or `_`, then letters, digits or `_`), must not collide with a
built-in constant, function or operator word ([§3.3](#33-operators)), and must be unique across
params and lets.

## 2. Which attributes take an expression

| Attribute | Expression type | Effect |
| --- | --- | --- |
| `fill` | color | Fill colour. An element filled with `url(#gradient)` is parameterised through its `stop-color`s instead. |
| `stroke` | color | Stroke colour. |
| `stop-color` | color | Gradient stop colour. |
| `opacity` | number | Group opacity. |
| `visibility` | boolean | `true` meaning visible. Wraps the element's drawing in a condition. |

Everything else — `x`, `y`, `cx`, `cy`, `width`, `height`, `d`, `transform`, `display`,
`stroke-width`, `fill-opacity` — is a literal. Braces written in one of those are read as an ordinary
value and do nothing; a source view marks it.

`visibility` is a boolean rather than a value substitution because a hidden element contributes no
drawing at all. SVG's third value, `collapse`, means the same as `hidden` outside CSS table layout,
so nothing is lost.

Ordinary attributes still apply alongside an expression: `fill-opacity`, `stroke-opacity` and
`stop-opacity` scale its alpha, and `color-interpolation="linearRGB"` converts it, exactly as they
would a literal.

## 3. Language reference

### 3.1 Types

`number` (single-precision float), `color` (RGBA, 8 bits per channel), `boolean`.

There are no implicit conversions between them.

### 3.2 Literals

| Form | Type | Notes |
| --- | --- | --- |
| `1`, `1.5`, `.5` | number | Decimal only. No exponent form. |
| `55%`, `7.4%` | number | `%` is a **suffix**, meaning "divide by 100". `55%` is `0.55`. |
| `#f80` | color | 3 hex digits, each doubled. |
| `#f808` | color | 4 hex digits, RGBA, each doubled. |
| `#ff8800` | color | 6 hex digits, RGB, alpha 255. |
| `#ff880080` | color | 8 hex digits, RGBA. |
| `true`, `false` | boolean | |

`%` is **only** a suffix and never an operator. Use `mod(a, b)` for the remainder. This keeps `55%`
unambiguous; writing `a % b` is a syntax error with a message saying so.

### 3.3 Operators

Loosest to tightest binding. Each comparison and logical operator has an equivalent **word form**,
listed alongside:

| Precedence | Operators | Word form | Operands | Result |
| --- | --- | --- | --- | --- |
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

Arithmetic on colours is rejected — use `mix(a, b, t)` to blend. Ordering comparisons (`<`, `>`, …)
are numbers only; `==` and `!=` work on any type provided both sides match.

#### Escaping, and the word forms

XML requires `<` and `&` to be escaped, which makes `t < 5` and `a && b` awkward to author. `>` does
**not** need escaping and can always be written literally.

The word forms exist to avoid the problem entirely, and are exact aliases — same precedence, same
meaning:

```xml
<e:let name="inRange">t gt 0.1 and t lt 0.9</e:let>
<e:let name="escaped">t &gt; 0.1 &amp;&amp; t &lt; 0.9</e:let>   <!-- identical -->
```

The full set is provided rather than only `lt`/`le`/`and`, so the language does not have a word form
for `<` but not for `>`. Both spellings may be mixed freely.

A `CDATA` section also works, since element text and CDATA are read the same way:

```xml
<e:let name="cdata"><![CDATA[t < 0.9 && t > 0.1]]></e:let>
```

All nine words — `and or not lt le gt ge eq ne` — are reserved and cannot be used as parameter or let
names.

### 3.4 Constants

| Name | Type | Value |
| --- | --- | --- |
| `pi` | number | π |
| `tau` | number | 2π |

### 3.5 Functions

Numeric:

| Signature | Notes |
| --- | --- |
| `sin(x)` `cos(x)` `tan(x)` | **Radians.** For a 0..1 cycle use `sin(t * tau)`. |
| `abs(x)` `sqrt(x)` `floor(x)` `ceil(x)` `round(x)` | |
| `pow(x, y)` `min(a, b)` `max(a, b)` | |
| `mod(a, b)` | Remainder; the sign follows the dividend. |
| `clamp(x, lo, hi)` | |
| `lerp(a, b, t)` | `a + (b - a) * t`. **`t` is not clamped**, so it extrapolates outside 0..1. |

Colour:

| Signature | Notes |
| --- | --- |
| `rgb(r, g, b)` | Channels **0..255**, clamped. Alpha 255. |
| `rgba(r, g, b, a)` | Channels 0..255; alpha **0..1**, clamped. |
| `hsl(h, s, l)` | `h` in **degrees**, wrapped, so `h + 45` needs no `mod`. `s` and `l` are **0..1**, clamped — write them as `74%`. |
| `hsla(h, s, l, a)` | As `hsl`, alpha 0..1. |
| `mix(a, b, t)` | Per-channel linear blend including alpha. `t` clamped to 0..1. |
| `withAlpha(c, a)` | Replaces alpha; `a` is 0..1, clamped. |

Note the deliberate asymmetry: `rgb` takes 0..255 and `hsl` takes degrees plus fractions, matching
CSS rather than being internally uniform.

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

## Related docs

- [Svg.Highlighting](svg-highlighting) — colouring and checking a document that uses the extension
- [Svg.Viewer.Skia.Avalonia](svg-viewer-skia-avalonia) — a viewer that builds a control per parameter
- [Svg.CodeGen.Skia](svg-codegen-skia) — turning a drawing into C#
- [Source Generator and svgc](../guides/source-generator-and-svgc)
