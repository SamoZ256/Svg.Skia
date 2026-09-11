---
title: "Svg.SourceEditing"
---

# Svg.SourceEditing

`Svg.SourceEditing` holds an SVG document as a tree that writes back as the file it was read from,
and edits it. It adds a parameter to an `<e:code>` block, moves an element, writes an attribute — and
what comes out is the file with the edit in it and nothing else changed. It draws nothing and knows
no UI framework; it is what `Svg.Viewer.Skia.Avalonia` edits through.

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
- you want one entry to take back per gesture, however many attributes or elements it moved.

## Main types

| Type | Role |
| --- | --- |
| `SvgDeclarationEditor` | `Add` a parameter, `Update` one, `Remove` one, `MoveParameter` one, `Set` one attribute of one, `SetDefaults` for many; `AddLet`, `UpdateLet`, `MoveLet` and `RemoveLet` for the other half of the block |
| `SvgRecipeRuleEditor` | `SetRule` and `RemoveRule` for an svgc recipe's replacement rules — the half of a recipe that is not declarations |
| `SvgAttributeEditor` | `SetAttribute` on any element, named by its address, and `Attributes` to read what one is written with |
| `SvgSourceDocument` | A drawing as a tree, `Read` from text and written back by `ToText` |
| `SvgSourceWorkspace` | That drawing and its history: `Commit`, `Undo`, `Redo`, `IsModified`, `MarkSaved` |
| `SvgElementEditor` | `Move` an element to where a drop puts it, and `NewGroup` to put things in |
| `SvgTextEdit` | One span to replace, and `ApplyAll` for a caller holding only a string |
| `SvgSourceEditResult` | The spans, or why nothing can be done |

Every editor has two halves: one that writes the tree and answers a refusal or null, and one that
measures spans against text. The first is what a host with a document uses. The second remains
because a drawing under an svgc recipe has its declarations written into the recipe instead, which is
a different file with a text buffer of its own.

## Why a tree, and why not the SVG one

The obvious way to do this is to parse the drawing with the SVG reader, change the tree, and write it
back. It renders identically — and it is measurably wrong for a file somebody is looking at.

- **Every comment is gone**, because the SVG reader's node switch has no case for them: a comment is
  not dropped on the way out, it is never modelled.
- `fill="{{ primary }}"` becomes `style="fill:gray;"` and `e:fill="primary"`.
- A `<!DOCTYPE>`, `version="1.1"`, `xmlns:xlink`, `xmlns:xml` and a comma-separated `viewBox` appear.

So the tree is an `XDocument`. Comments are nodes, an attribute's value is the string it was written
as — `{{ }}` and all — and the order somebody put the attributes in is the order they come back in.

That alone is not enough. Measured over the 2,988 drawings in the two suites, re-serialising an
`XDocument` the ordinary way returned **28** of them unchanged. XML says nothing about the whitespace
inside a start tag, so attributes written one to a line come back on one line and `<rect/>` comes back
as `<rect />`; and a parser must fold CRLF to LF before the tree ever sees it, so every line of a file
written on Windows changes.

So a tag is remembered rather than regenerated. Each element keeps the bytes of the start tag it was
read as, along with where each value sits inside it, and writes those bytes back — splicing a changed
value over the old one, cutting a removed attribute out with the whitespace in front of it, and
putting a new one in before the close. Runs of text are kept the same way, because a parser resolves
`&gt;` to the character and says nothing about which of the two the file used.

**2,980 of 2,988 come back byte for byte.** The eight that do not declare entities of their own,
which a reader expands with no node left to write back; those are refused rather than mangled. The
contract is that what it reads, it writes back exactly.

## One thing to take back per gesture

`SvgSourceWorkspace` is the history. A commit is the whole of what somebody did — a resize writes
three attributes and is one thing to take back — and a step is reversed by reading the drawing's own
text back.

Reversing by snapshot is usually the wrong answer, and it is the right one here for two reasons.
`ToText` is byte-faithful, so the text is a lossless record of the tree rather than an approximation
of it: the annotations that carry the author's bytes are rebuilt from the author's bytes, which is
why undo returns the file and not merely something that renders like it. And an edit needs the text
it started from anyway — a declaration can only be checked against the language's rules after the
tree has been changed, and one of those checks needs the state before it — so a refusal must already
be able to put a half-made edit back. Rollback and undo are one mechanism instead of two.

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

## Renaming carries the uses with it

`Update` and `UpdateLet` rewrite a declaration, and where the name changes they also rewrite every
place the drawing names it: the identifier in each `{{ … }}` and in each `<e:let>` body. Renaming only the declaration
would leave a document that still parses and no longer draws, with nothing about its shape to say
why.

The uses are found by lexing, not by searching the file's characters. An expression reaches the file
XML-escaped, so a let holding `a < b` is written `a &lt; b`; and `amp` is a name the language allows,
so a search would rename the inside of `&amp;` and take the markup with it. Each site is decoded,
lexed, and every identifier token remembers where it was written. A site that will not lex refuses
the whole rename rather than being skipped — a use that cannot be read is still a use.

Changing a **type** is refused. Every expression naming a parameter was checked against the type it
had, so changing one is a change to all of them rather than to the declaration alone.

## Removing needs to know what uses it

`Remove` and `RemoveLet` refuse while anything still names the declaration, and say how many uses
they found. Taking a
used one away leaves a document that parses perfectly and draws nothing, which is the one outcome
this package exists to prevent; the count is what separates a button that did nothing from one that
did something unintended.

The uses are the ones `Rename` rewrites — every `{{ … }}` and every `<e:let>` body, found by lexing —
so the two ask the same question of the same walker. A `default`, `min`, `max` or `step` is not
searched: the language puts nothing the document declares in scope there, so a name in one is a
different name.

The declaration goes with the line it sat on. The `<e:code>` block stays even when it empties, since
taking it away is a second decision — about a `<defs>` that may hold other things, and an `xmlns`
nothing declares any more — and adding a parameter writes into the block that is already there.

## Parameters reorder freely; lets do not

Nothing in this language reads parameters in order: a default may not name another parameter, so
every order renders the same picture. `MoveParameter` therefore allows any of them, and is the one
move here with no rule to check.

A back end may want its own order. The C# generator writes its signature in declaration order, so
the ones with defaults have to come last, and it refuses a document that puts them otherwise — as
`svgc error: …`, when somebody runs it. That is a restriction of that back end and not of the
language, so it is not enforced here: a drawing is not stopped from saying what it means because one
of the things that reads it would rather it said it differently.

## Where a let sits is what it means

A let resolves against what is declared above it and nothing below, so `MoveLet` is a change of
meaning and not of layout. The re-read cannot catch that on its own: a let dragged above the one it
names reads back perfectly well and renders as nothing. So every edit also folds the symbol table and
type checks each body in order, and is refused if it leaves a let unresolved.

Only a let the edit is answerable for. Reading does not type check, so a document can hold a body
that names nothing and still open; refusing on that one too would make an unrelated parameter
uneditable in a document somebody is part-way through repairing. The check runs against the document
as it was as well, and only a let that worked before and does not after stops the edit. The original
is re-read only once something failed, so an ordinary splice pays nothing for this.

`MoveLet` moves the let's whole line as it was written, and refuses a let sharing a line with
something else rather than cutting it out of one.

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

A parameter added to a block that already has some joins the last of them rather than going at the
end of the block. A block is written in two groups — the parameters, then the lets built on them —
and although the reader takes declarations in any order, a parameter written below the lets that use
it reads backwards. With no parameters to join it goes above the first let, rather than between two,
whose order is the one thing about them that does matter.

Line endings and indentation are read off the document, never assumed: a file written with tabs stays
written with tabs, and a file with CRLF does not come back with a mixture.

## A recipe is edited the same way

`SvgDeclarationEditor` finds declarations by namespace — `Descendants(Ns + "code")` — and not by
document shape, so a recipe's own unprefixed `<code>`, `<param>` and `<let>` are the same block to it
as a drawing's `<e:code>`. That is not a coincidence: a recipe is written in the extension's own
namespace precisely so one reader serves both.

`SvgRecipeRuleEditor` writes the other half, the `<replace>` rules. It knows nothing about what a
rule names, deliberately. A colour has many spellings and only `Svg.Expressions.Recipes` can say
which of them are one colour; answering that here would be a second answer to it, and referencing
that package to borrow the first would pull a whole SVG parser into a text editor. So the caller
decides which rule it means — by value, against a parsed recipe — and passes the attribute and the
value as that rule already writes them. Passing a second spelling of a value a rule already names
adds a second rule, which the recipe then refuses to read.

`SvgAttributeEditor` writes anything else: one attribute of one element, named by the child-index
path `SvgElementAddress.Key` spells — as a string, since that type belongs to the SVG parser and this
package deliberately cannot see it. The walk that resolves the path is mirrored rather than borrowed
for the same reason, so the two rules that make the two agree are pinned by a test: the root is the
empty key, and a comment or a run of text takes no index.

It refuses an attribute a `style` declaration overrides. The declaration wins, so writing the
attribute would leave a document where the change paints nothing and nothing said so; editing inside
the declaration needs the scanner that splits one properly, which is internal to the SVG parser.

## Related docs

- [Svg.Expressions](svg-expressions) — the language, its declarations and the rules quoted above
- [Svg.Highlighting](svg-highlighting) — the other half of a source view, which reads text for display
- [Svg.Viewer.Skia.Avalonia](svg-viewer-skia-avalonia) — the panel that edits through this
