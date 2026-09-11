---
title: "Svg.Viewer.Skia.Avalonia"
---

# Svg.Viewer.Skia.Avalonia

`Svg.Viewer.Skia.Avalonia` is an embeddable SVG viewer for the SVG expression extension: it opens a drawing, zooms and pans it, and builds a control for every parameter the document declares.

## Install

```bash
dotnet add package Svg.Viewer.Skia.Avalonia
```

## Choose this package when

- you want a ready-made SVG viewer pane in an Avalonia app,
- you want the parameters a drawing declares exposed as sliders, colour pickers and checkboxes without writing that UI,
- you want zoom, pan, fit and actual-size behaviour that already works,
- you want the same stack `src/Svg.Studio` is built from.

## Main types

| Type | Role |
| --- | --- |
| `SvgViewer` | The drop-in: toolbar, canvas, parameter panel and status strip |
| `SvgViewerCanvas` | The drawing surface alone, owning scale and offset |
| `SvgViewerDeclarationPanel` | One control per declared parameter, and one row per declared let |
| `SvgViewerElementTree` | Every element of the open drawing, as a tree, with a filter box |
| `SvgViewerElementNode` | One row: the element, its address, its name and its id |
| `SvgViewerLet` | A let row: the name and body being typed, what it evaluates to, and what is wrong with it |
| `SvgExpressionPresenter` | Paints an expression box by token, in place of a `TextBox`'s own presenter |
| `SvgViewerDocument` | A loaded drawing, its declarations, and any declaration error |
| `SvgViewerParameterFactory` | Declarations to bindable rows, seeded from their defaults |

## Minimal embed

```xml
<viewer:SvgViewer x:Name="Viewer" />
```

```csharp
await Viewer.LoadAsync("badge.svg");
```

## Public host seams

| Member | Use it for |
| --- | --- |
| `LoadAsync` / `LoadTextAsync` / `OpenAsync` | Opening a drawing from a path, text, stream or picker |
| `OpenRequested` | Taking over what a picked or dropped file does — a tab per drawing, say |
| `Close` | Releasing the open document when the viewer itself is discarded |
| `Parameters` / `ParameterValues` | Reading what is declared and what is bound |
| `TrySetParameterValue` / `ResetParameters` | Driving values from host UI |
| `ShowToolBar` / `ShowDeclarationPanel` / `ShowStatusBar` | Supplying your own chrome. `ShowDeclarationPanel` is the whole right-hand strip, element tree included |
| `ShowElementTree` / `Elements` | The tree under the parameters — on by default; `Elements.Filter` is the box above it |
| `SelectedElement` / `ElementSelected` | Which element is picked |
| `ShowBounds` | Outlining the drawing's own edges — on by default, since an icon with transparent margins otherwise ends nowhere the eye can see |
| `SidePanels` | Panels of your own beside the parameters: the right pane becomes a strip of tabs while there are any, yours first and so the first one it opens on, and holds the parameters alone again when there are none |
| `Rewrite` / `Notice` | Drawing a document derived from the file — an svgc project applying a recipe — and saying so when it cannot be |
| `DeclarationTarget` | Where the parameter panel writes, when the drawing's declarations are not in the drawing |
| `FileDialogService` | Custom storage or picker integration |
| `Canvas` | Direct access to the surface for zoom and pan; `Show` lays out several drawings at once, and `TryGetPlacementAt` says which one a click fell on |
| `DocumentOpened` / `ErrorRaised` / `ParameterValueChanged` | Syncing host titles and status |
| `SvgViewerSourceColorizer` / `SourceResourceKey` | Colouring a source view of your own the way this one is coloured — Svg.Studio paints an svgc recipe with them |

## Ranges come from the document

A `number` parameter uses the `min`, `max` and `step` its author declared, falling back to `0` to `1` when it declares none:

```xml
<e:param name="hue" type="number" default="217" min="0" max="360" step="1" />
```

Every row is seeded by *evaluating* the declared `default`, so `default="tau / 4"` works as well as a literal does.

A `default` that will not evaluate, or a range whose ends are the wrong way round, does not stop the
parameter being offered: the drawing still renders and the value is still bindable, the row falls back
to a placeholder and the default range. What is wrong with it is said on the declaration panel, in
place of the row it would have had — the drawing's status line does not repeat it.

A drawing with mistakes in it says so from the moment it opens, in two places for two different
things.

**A note in the status bar** — *"6 errors"* — for everything said in detail elsewhere. Errors and
warnings are counted apart and worded apart (*"1 error and 1 warning"*), because a warning is
something the drawing opened in spite of; a note that is only warnings is painted in the warning
colour rather than the error one. The count is all it gives, because the row that carries the mistake
is where the detail belongs: an expression is explained under the attribute row holding it in the
Element panel, and a refused declaration in place of the parameter row it would have had. It sits
beside the status rather than under it, so it takes no room and the viewer does not shift as it comes
and goes while you edit. It is a standing statement, not a reaction: it does not wait for a control to
be touched, and it does not change when one is.

**A card over the drawing, frosting it**, for what has no line to be put on — chiefly a document
that would not load at all, where there is no pane to mark because there is no drawing. In every one
of those the drawing on screen is not what the file says, and the frosting says
so before the sentence is read — a wide blur with a wash over it, because defocus alone reads as a
drawing out of focus rather than as glass in front of one. The card takes no room either, and the
drawing can still be panned around it. Both reach a host through `ErrorRaised`.

The format itself — `<e:code>`, the operators, and the placeholder mechanism — is what `Svg.Expressions` implements.

## Editing, not just showing

Moving a control is a **preview**: it rebuilds the picture and leaves the file alone. Everything
below is an edit, and each writes the drawing's own text through
[Svg.SourceEditing](svg-sourceediting):

| | |
|---|---|
| `AddParameterAsync` | Asks for a parameter and splices it into the `<e:code>` block, creating the block and the namespace if the drawing has neither |
| `CommitParameterDefaults` | Writes every value that differs from its declared default into the document as that default |
| `EditParameterAsync` | Asks what one parameter should declare and writes the answer, carrying every use of its name when it is renamed |
| `RemoveParameter` | Takes a parameter out, refusing while anything still names it |
| `MoveParameter` | Puts a parameter at another position; any order is allowed |
| `CommitLet` | Writes what a let row says, declaring it below the lets already there or rewriting the one it stands for |
| `MoveLet` | Puts a let at another position among the lets, refusing a move that would leave one unresolved |
| `RemoveLet` | Takes a let out, refusing while anything still names it |

A row's `⋯` button, which appears while the pointer is over it, is how a parameter is edited. Its
type is not offered: every expression naming a parameter was checked against the type it has.
Its grip drags it up and down, into any order. The `✕` removes it, refused while the drawing still
uses it — the
refusal says how many uses there are, since a button that did nothing would say less.

Every box that holds an expression — a let's body, and a parameter's `default`, `min`, `max` and
`step` — is coloured by what the language says each piece is, live as it is typed, from the same
table everything else here paints an expression with. `SvgExpressionPresenter` is what does it: a control theme puts it
in place of a `TextBox`'s own presenter, so the caret, the selection, composition and undo stay the
box's. Selected text keeps its colours, which is what a reader wants of an expression.

A let has no form and no `⋯`: it is a name and an expression, so the row is the editor. `Add let…`
leaves an empty row to type into, `Enter` or leaving the row writes it, `Escape` puts it back. What
is typed is checked against the parameters and the lets above it as it is typed, and nothing is
written until it checks. Beside each row is what the let evaluates to right now, which follows the
sliders, and a `✕` that removes it on the same terms as a parameter — a row nothing has been
typed into yet is simply dropped, since there is nothing in the document to take out.

Its grip drags it up and down, because **where a let sits is what it can name**: one resolves against
what is declared above it and nothing below. A drag is held inside the positions that still check, so
there is nothing to refuse; `MoveLet` refuses anyway, since the document reads back perfectly well
either way and only type checking can tell.

All of them go through the drawing's own history, so there is one record of what was done: an
addition that had to declare a namespace and open a block is one thing to take back, and so is a
resize that wrote three attributes. Nothing needs the pane to be open, because the pane shows the
drawing rather than holding it. What it shows afterwards is the file as it was, with one line added:
every comment and every placeholder where the author left them.

`ParameterDialogService` is how the form is asked for, replaceable for the reason `FileDialogService`
is. `SvgParameterFormView` is the form itself, a plain control, for a host that wants to ask its own
way.

## Saying what is wrong

There used to be a **Source** toggle here, opening a read-only pane under the drawing that showed the
document as it was read and drew a wavy underline under every mistake. It is gone. It made sense while
the text was the truth; once the truth became a tree, the pane was a transcript of what that tree
would write — a second account of the drawing that took 220px from the canvas, had to be re-coloured
on every rebuild, and answered no question the element tree does not answer better.

What it was really for was **where** a mistake is, and that moved to the rows the mistakes are about:

- An expression is explained under the attribute row holding it, in the **Element** panel. The panel
  already checked what a row said against the declarations in scope, so the sentence is the one the
  analyser files against the span.
- A declaration the reader refuses is said on the **parameter** panel, in place of the row it would
  have had — a refused `<e:param>` produces no row, and "This drawing declares no parameters" about a
  file that declares one was simply wrong.
- The count is in the status bar, as before.

`SourceDiagnostics` is the whole list, as ranges into `Source`, if you would rather show it your own
way — a problems panel, a gutter, a report. Nothing about it changed: it is analysed on first ask
rather than when anything is opened, so whether a drawing is at fault is knowable before anyone has
picked a row. Each one carries `Start`, `Length`, `Severity` and `Message`; an element name this
renderer does not know, or an id used twice, is a warning, since the drawing still opens either way.

That covers the drawing's expressions and the `<e:code>` block alike: a name nothing declares, a range
on a colour, a `min` above its `max`, a `default` that will not resolve.

It covers the SVG as well. An attribute value the parser's own converter will not take —
`width="abc"`, `stroke-miterlimit="20%"`, a unit this renderer does not implement — is reported where
it is written, which is the one failure the library is least able to report for itself: the value is
dropped, the property keeps its default, and the drawing renders wrong without a word. A declaration
inside `style="…"` is reported the same way, and on the declaration rather than the whole attribute. So
is `clip-path="url(#gone)"` — a reference to an id the drawing does not contain, which is the most
ordinary way for a picture to come out wrong and, until now, the quietest. An expression written in an
attribute that does not take one, `stroke-width="{%{{{ w }}}%}"`, is reported too, and says which
attributes do — as is one written in an attribute that takes a *different* kind, such as a colour in
`opacity`. What counts as a mistake is [Svg.Highlighting](svg-highlighting)'s answer, which is the
language's own checker.

## Reading the drawing's text

`Source` is the whole drawing as the tree writes it — comments, formatting and `{%{{{ … }}}%}`
expressions exactly as their author wrote them, with every unsaved edit in it. `SetSource` is how text
arrives from outside: it is an edit like any other, one entry on the history, and text that will not
read back is refused rather than held.

Undo and redo are bound on the canvas, taken from the platform rather than written down, so the
gestures reach the drawing's history while somebody is looking at the drawing and a parameter box
keeps its own.

`IsSourceModified` says whether there are edits not on disk and `SourceModifiedChanged` announces it;
`SaveSourceAsync` writes them back, asking through `FileDialogService` when the drawing has no file
of its own. In `src/Svg.Studio` that is Cmd/Ctrl+S, a dot on the tab, and a prompt before anything
throws work away — closing a tab asks about that drawing, closing the window asks once about every
unsaved one it is holding. The control raises, the host decides, the same way opening works.

A save keeps the byte order mark the file arrived with, and nothing changes in a part of it you did
not edit — the writer remembers the bytes of every tag it read and replays them, splicing only the
values that changed. There is no size at which a drawing becomes read-only: that limit existed
because the pane had to hold the text, and there is no pane.

`SvgViewerDocument.SourceText` is the text captured while loading — what the picture was built from,
rather than whatever the file says later, and unchanged by edits. Drawings loaded from text or from a
stream carry it too, so a viewer fed by a database or an archive answers like any other.

## The element tree

Under the parameters, in the same column, split by a splitter of its own. That column is the full
height of the viewer, and the drawing has the rest of it. It lists **every** element
of the open drawing — `<defs>` and its contents, the `<e:code>` block, a `<title>` — because what is
in a file is the question it answers, and half of that never reaches the canvas. On by default;
`ShowElementTree = false` gives the height back and stops the work — a hidden tree holds nothing,
because it is rebuilt every time typing pauses and that is 27ms at 4,000 elements.

Picking a row rings the element on the drawing and fills the **Element** panel with the attributes
the file writes it with. The ring is the element's own **silhouette**, traced from the scene
geometry rather than drawn around its bounds — a circle rings as a circle, a stroked path rings round
both edges of the stroke rather than down the middle of it, and a group rings as its parts rather
than as the box containing them. It is one orange line, whose colour sweeps and settles over about
two seconds when it appears — that is what finds it on a busy picture, and once settled it costs
nothing. Clicking the drawing does the reverse and selects the row. A drag
still only pans, and a click that lands on nothing changes nothing — the tree is read alongside the
drawing, and a click two pixels wide of a shape should not throw away the row somebody was looking
at.

Rows are **dragged** the way a project's rows are: pick one up and a line shows where it will land —
between two rows to go beside one, or an outline round a row to go inside it. Dropping writes the
move into the drawing, so the element really is somewhere else afterwards, and the order it paints
in changes with it. **New group** on the tree's own menu writes an empty `<g>` beside the picked row
for things to be dragged into. Both arrive as one step to take back.

What cannot be done is refused with a sentence rather than attempted: a row cannot land in its own
branch, a drawing written on one line has no line to move, a tag that closes itself has no inside,
and a drop that would carry an element across a `<defs>`, a `<clipPath>` or a `<mask>` would change
what the drawing paints.

Three things are worth knowing before relying on it:

- **Not everything can be ringed.** Anything that never reaches the drawing has no scene node at all
  — the row still selects and is still shown in the text. And only the seven basic shapes carry
  geometry, so text and images ring as their own measured bounds, which for those is the answer
  rather than an approximation of one.
- **Not everything can be shown in the text.** The tree comes from the parsed document and the spans
  come from a second reading of the file (`SvgSourceElements`), and the two are checked against each
  other by name before the caret moves. Under a recipe the drawing has rows its file has never heard
  of, and those are the disagreement that remains; a half-typed document is no longer one of them,
  since text that will not read back never becomes the drawing.
- **A `<use>` has one row, not one per use.** What is listed is what is written. Picking the
  definition rings it everywhere it is drawn, and clicking any of those copies selects that one row.

Rows are held by the child-index address `SvgElementAddress` spells, not by element. A drawing
rebuilt from edited text shares no element with the one it replaced, so that is what keeps the
selection and the open branches across a keystroke; a selection whose element has been deleted is
dropped.

## One document per viewer

The control shows one drawing, and opening another replaces it. A host that wants several at once
puts a viewer in each pane and handles `OpenRequested`, which is raised for every file the user
picks or drops before any of them is read:

```csharp
viewer.OpenRequested += (_, request) =>
{
    request.Handled = true;                              // the viewer loads nothing
    request.Completion = OpenInTabsAsync(request.Paths); // what OpenAsync waits on
};
```

Hand back what you started. The event is synchronous, so without `Completion` a host has no way to
say it has not finished, and `OpenAsync` completes while the files are still being read.

`src/Svg.Studio` is that host: one viewer per tab, a new tab per file opened, and `Close` on the
viewer whose tab goes away. A path it recognises as an svgc project opens a pane beside the tabs
instead of a tab, which is why the request carries paths rather than drawings.

`SizeRequest` is the seam that host opens a project's drawings through: a size applied to the parsed
document on every build, the file left as it was written. `Edit → Resize…` is the other half of the
pair and the opposite choice — it rewrites the drawing's own text.

`Rewrite` is the second such seam, and goes further: the drawing built is not the file at all. Studio
sets it to a project's recipe, so what is on screen is the document `svgc` compiles — colours turned
into expressions, and the recipe's parameters declared. Everything else still works from the file:
`Source` is it and a save writes it, and every rebuild goes back through the rewrite.
Because the declarations then belong to the recipe rather than to the drawing, a host sets
`DeclarationTarget` to say where the parameter panel should write: the recipe is a different file
held as a tree of its own, and `ISvgViewerDeclarationTarget` is the seam to it. Left unset the
commands go into the drawing, which is what a drawing declaring for itself wants — and what a recipe refuses to be
applied to. `Notice` is where a host says a rewrite could not be
set up at all; it appears on the status line beside the viewer's own count of what is wrong.

## Two things worth knowing

It does **not** host `Avalonia.Svg.Skia.Svg`. That control sizes itself to the drawing it fits, so it cannot fill a viewport and its zoom is bounded by its own clip. The viewer draws onto `SKCanvasControl` and owns the transform, which is what makes fit and actual-size exact.

Loading is the only work off the UI thread. Binding a value evaluates a model that is already compiled, on the UI thread, coalesced to one call per frame, so a slider drag stays smooth and two changes keep the order they were made in.

## Related docs

- [Source Generator and svgc](../guides/source-generator-and-svgc)
- [Svg.Controls.Skia.Avalonia](svg-controls-skia-avalonia)
- [Skia.Controls.Avalonia](skia-controls-avalonia)
