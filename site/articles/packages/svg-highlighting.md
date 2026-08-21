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
- you want the SVG expression extension's `{{ … }}` placeholders and `<e:let>` bodies recognised as code rather than as strings and prose,
- you need to colour a large file without laying all of it out at once,
- you want positions into the document — for a diagnostic, a squiggle, or a jump — rather than copies of parts of it.

## Main types

| Type | Role |
| --- | --- |
| `SvgSourceHighlighter` | `Tokenize` for a whole document, `Lines` for one row at a time |
| `SvgSourceToken` | A range into the source and what it is; `Text` cuts it only when asked |
| `SvgSourceTokenKind` | Text, punctuation, element, attribute, value, comment, and the expression kinds below |
| `SvgSourceLine` | One line's tokens, its number, and the rest of it past a limit |

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

`{{ … }}` placeholders, `<e:let>` bodies and a declaration's `default`, `min`, `max` and `step` are
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
| `{{`, `}}`, whitespace | `Expression` |

A tokenizer written by hand here would have coloured the percent as an operator and the word forms
as names. Reusing the lexer is what keeps the pane and the compiler saying the same thing.

An expression the language refuses — a lone `=`, a half-typed call — colours as far as it read and
leaves the remainder plain. Someone reading a file to find out why it will not compile is exactly
who has a source view open.

## It describes rather than validates

A malformed document still colours — refusing to colour the file someone is trying to find the fault in would be perverse. What keeps that honest is an invariant asserted over CDATA, processing instructions, an unterminated comment and an unclosed tag alike: **concatenating every token reproduces the input exactly**. Colouring can never quietly alter, drop or reorder what a file says.

## Two limits, and why they are not the same

Splitting is cheap — under 7ms for 200,000 characters — so there is no limit on tokenizing. The cost lands on whoever builds a styled run per token: 130ms at 1,100 runs, 433ms at 4,500 and 18 seconds at 45,000, in a single text block.

`Lines` is the answer to the first half: a view that virtualises rows lays out only what is on screen, so a 132KB drawing costs what a 2KB one does. `RowTokenLimit` is the answer to the second: virtualising by line bounds a document but not a *line*, and a minified drawing is the whole file on one of them — 132KB of it took 1.4 seconds as a single row, and 340ms once the row stopped colouring past 250 pieces. `SvgSourceLine.Rest` hands back the remainder so nothing is hidden.

## Related docs

- [Svg.Viewer.Skia.Avalonia](svg-viewer-skia-avalonia)
- [Svg.CodeGen.Skia](svg-codegen-skia)
