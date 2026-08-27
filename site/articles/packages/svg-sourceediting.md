---
title: "Svg.SourceEditing"
---

# Svg.SourceEditing

`Svg.SourceEditing` changes what an SVG document declares by replacing spans of the document's own
text. It adds a parameter to an `<e:code>` block, or writes what one of them says, and gives back the
spans to replace rather than a rewritten file. It draws nothing and knows no UI framework; it is what
`Svg.Viewer.Skia.Avalonia`'s parameter panel edits through.

It depends on `Svg.Expressions` alone — for the declarations it edits, the rules it validates
against, and the position map that says where in the text each of them was written.

## Install

```bash
dotnet add package Svg.SourceEditing
```

## Choose this package when

- you want to add or change a parameter from a GUI and still show the file somebody wrote,
- you are editing a document that a person also edits by hand, and the two have to be the same
  document,
- you want an edit that lands on a text editor's undo stack rather than replacing its buffer.

## Main types

| Type | Role |
| --- | --- |
| `SvgDeclarationEditor` | `Add` a parameter, `Set` one attribute of one, `SetDefaults` for many at once |
| `SvgTextEdit` | One span to replace, and `ApplyAll` for a caller holding only a string |
| `SvgSourceEditResult` | The spans, or why nothing can be done |

## Why spans and not a document

The obvious way to do this is to parse the drawing, change the tree, and write it back. It works, and
it is measurably wrong for a file somebody is looking at.

Feeding an eleven-line parametric drawing through `SvgDocument` and `Write` gives back a document
that still renders identically — `#3c83f5` before and after, with `hue = 217` — because foreign
attributes are keyed by namespace URI and read back into the key the pipeline uses. What it also
does, to a file nobody asked to reformat:

- **every comment is gone**, because the SVG reader's node switch has no case for them,
- `fill="{{ primary }}"` becomes `style="fill:gray;"` and `e:fill="primary"`,
- a `<!DOCTYPE>`, `version="1.1"`, `xmlns:xlink`, `xmlns:xml` and a comma-separated `viewBox` appear.

The drawing survives all of that. The file does not. So an edit here is a splice: everything outside
the spans is untouched, byte for byte.

The same choice is what makes undo work. Handing a host a whole new document forces it to assign the
text wholesale, which resets the caret, the scroll and the undo stack. A span goes through the
editor's own replace, and a parameter added from a panel comes off the undo stack in the order it was
done, among the lines that were typed by hand.

## It decides nothing about what is legal

A proposed declaration goes through `SvgExpressionDeclarations.Builder`, the rules both readers of a
document enforce — identifier-shaped names, nothing reserved, nothing declared twice across params
*and* lets, a type that parses, `min`/`max`/`step` only on a number, and a range with both ends or
neither. What it refuses is the refusal you get back.

The result is then read again with `SvgExpressionDeclarations.Parse` before it is handed over. A
splice can go wrong in ways that still look like text — a quote landed on, a tag left open — and the
reader is the one thing that can say so. Two extra reads of a document measured at 3ms each is the
whole cost, and it is why an edit that would leave the document saying something other than what was
asked for is refused instead of applied.

## What it refuses

- a document that is not well-formed XML yet, which is what one looks like halfway through being
  typed,
- a document whose declarations are already wrong, which is fixed by hand first,
- anything the language would not accept.

None of these is worth a mode. The action declines, says why, and works again as soon as the text
does.

## Where a new block goes

First inside `<defs>`, creating one if the drawing has none — the same place, and for the same
reason, that `Svg.Expressions.Recipes` puts it: the declarations read as the document's preamble
rather than as one more definition among the gradients. A drawing that has been through a recipe and
one that has been through this should not differ in where they keep it.

The namespace prefix is whatever the document already binds the extension to, so a drawing writing
`x:param` keeps writing it; failing that `e`, and `e2` if something else has taken `e`. That choice
lives in `Svg.Expressions` because both this and the recipe rewriter have to reach the same answer.

Line endings and indentation are read off the document, never assumed: a file written with tabs stays
written with tabs, and a file with CRLF does not come back with a mixture.

## Related docs

- [Svg.Expressions](svg-expressions) — the language, its declarations and the rules quoted above
- [Svg.Highlighting](svg-highlighting) — the other half of a source view, which reads text for display
- [Svg.Viewer.Skia.Avalonia](svg-viewer-skia-avalonia) — the panel that edits through this
