---
title: "Svg.Highlighting"
---

# Svg.Highlighting

`Svg.Highlighting` splits an SVG document into coloured pieces for a source view. It draws nothing and knows nothing about how the pieces are shown: no brushes, no controls, no UI framework. It is what `Svg.Viewer.Skia.Avalonia`'s source pane is built on.

## Install

```bash
dotnet add package Svg.Highlighting
```

## Choose this package when

- you are showing SVG text to someone and want it coloured as markup,
- you want the SVG expression extension's `{%{{{ … }}}%}` placeholders and `<e:let>` bodies recognised as code rather than as strings and prose,
- you need to colour a large file without laying all of it out at once,
- you want positions into the document — for a diagnostic, a squiggle, or a jump — rather than copies of parts of it.

## Main types

| Type | Role |
| --- | --- |
| `SvgSourceHighlighter` | `Tokenize` for a whole document, `Lines` for one row at a time |
| `SvgSourceToken` | A range into the source and what it is; `Text` cuts it only when asked |
| `SvgSourceTokenKind` | Text, punctuation, element, attribute, value, comment, and the expression kinds below |
| `SvgSourceLine` | One line's tokens, its number and range, and the rest of it past a limit |
| `SvgSourceDiagnostics` | `Analyse` — what is wrong with the expressions in a document |
| `SvgSourceDiagnostic` | A range, a severity and a message, in the same coordinates as a token |

## Colouring a document

```csharp
foreach (var line in SvgSourceHighlighter.Lines(File.ReadAllText("badge.svg")))
{
    foreach (var token in line.Tokens)
    {
        Paint(token.Text, BrushFor(token.Kind));
    }
}
```

## The expression language is split too

`{%{{{ … }}}%}` placeholders, `<e:let>` bodies and a declaration's `default`, `min`, `max` and `step` are
not left as one piece: they are handed to `Svg.Expressions`' own lexer, so what you see coloured is
what the compiler reads. That last group is easy to miss — `max="tau"` and `step="1/60"` look like
ordinary attribute values and are code. That matters more
than it sounds:

| Written | Coloured as |
| --- | --- |
| `hsl`, `mix`, `lerp` | `ExpressionFunction` — names the language defines |
| `pi`, `tau` | `ExpressionConstant` |
| `55%` | one `ExpressionNumber`, sign included — a percent is a *suffix on a literal*, never an operator |
| `and`, `or`, `not`, `lt`, `ge` | `ExpressionKeyword` — word forms of the symbolic operators, which exist because XML escaping makes `<` and `&&` awkward inside an attribute |
| `#3fb5b5` | `ExpressionColor` |
| `true`, `false` | `ExpressionConstant` — they lex as names, but the parser reads them as boolean literals |
| `hue`, `sweep` | `ExpressionIdentifier` — a parameter, a let, or a typo; telling those apart needs the document |
| {%{`{{`, `}}`}%}, whitespace | `Expression` |

A tokenizer written by hand here would have coloured the percent as an operator and the word forms
as names. Reusing the lexer is what keeps the pane and the compiler saying the same thing.

An expression the language refuses — a lone `=`, a half-typed call — colours as far as it read and
leaves the remainder plain. Someone reading a file to find out why it will not compile is exactly
who has a source view open.

## Saying what is wrong

`Analyse` reports mistakes in a document's expressions, through the language's own checker — so an
unknown name, a function called with the wrong number of arguments and a type mismatch are worded
and decided by the compiler, not by an imitation of it:

```csharp
foreach (var diagnostic in SvgSourceDiagnostics.Analyse(text))
{
    Underline(diagnostic.Start, diagnostic.Length, diagnostic.Message);
}
```

Ranges are in the same coordinates as tokens, so a view that already has the tokens can mark the
offending one without being told anything else. Where the checker refuses mid-expression, the mark
covers the piece it stopped on rather than the whole line.

Scope follows the language rather than convenience. A `{%{{{ … }}}%}` sees everything the document
declares; an `<e:let>` sees the parameters and the lets before it, but not itself; a `default`, `min`,
`max` or `step` sees **neither** — a default may not reference other parameters, because an ordering
dependency between them would be invisible in the document, so checking one against the full table
would accept what the code generator then rejects.

One boundary worth knowing: it does not know what an *attribute* expects. `opacity="{%{{{ tint }}}%}"` is
a well-formed colour expression written where a number belongs, and saying so needs the table of
which SVG attribute takes which type, which lives in the scene compiler.

## The declarations are checked too

A mistake in the `<e:code>` block is reported the same way, at the attribute it is about rather than
as a sentence somewhere else:

```xml
<e:param name="tint" type="color" min="0" max="1" />
<!--                                   ^ a colour has no range -->
```

Every declaration is read, so a block with three mistakes in it reports three rather than hiding two
behind the first, and the parameters after a bad one are not lost with it. A rule about something the
document left out — a missing `type`, a `<e:let>` with nothing in it — has nothing of its own to
point at, so it marks the declaration. A document that is not well-formed XML is reported here as
well, which is the one moment a source view is most likely to be open.

A document whose declarations are wrong reports **those and nothing else**. With a declaration
refused, the symbol table is missing what it would have contributed, so every use of that name would
read as undeclared — a hundred of those bury the few that are real.

Rules and places stay apart. The rules live in `SvgExpressionDeclarations.Builder`, where both
readers of a block reach them, and each names the `SvgDeclarationPart` it means; turning a part into
a place is the reader's half, because only the one that reads source text has positions to give. If
you want the same thing without a source view, `SvgExpressionDeclarations.Parse(text, out var
diagnostics)` is what this calls.

Some of it needs numbers rather than types — `min` above its `max`, a `step` of zero, a `default`
that type-checks and still will not produce a value — so `Analyse` evaluates those, which reading a
document deliberately never does.

Splitting is context-free and analysing is not, which is why they are separate calls: colouring a
placeholder needs only the span, checking it needs the whole file. Splitting a 132KB drawing twice —
once for lines, once for analysis — costs about 12ms.

## It describes rather than validates

A malformed document still colours — refusing to colour the file someone is trying to find the fault in would be perverse. What keeps that honest is an invariant asserted over CDATA, processing instructions, an unterminated comment and an unclosed tag alike: **concatenating every token reproduces the input exactly**. Colouring can never quietly alter, drop or reorder what a file says.

## Two limits, and why they are not the same

Splitting is cheap — under 7ms for 200,000 characters — so there is no limit on tokenizing. The cost lands on whoever draws the result: one styled run per token costs 130ms at 1,100 runs, 433ms at 4,500 and 18 seconds at 45,000, in a single text block.

`Lines` is the answer to the first half: a view that colours only the lines on screen — an editor, a virtualising list — pays for the screenful, so a 132KB drawing costs what a 2KB one does. `RowTokenLimit` is the answer to the second: colouring by line bounds a document but not a *line*, and a minified drawing is the whole file on one of them — 132KB of it took 1.1 seconds coloured whole, and 39ms stopping at 250 pieces. Nothing is hidden either way, since the text past the limit is simply left plain.

## Related docs

- [Svg.Viewer.Skia.Avalonia](svg-viewer-skia-avalonia)
- [Svg.CodeGen.Skia](svg-codegen-skia)
