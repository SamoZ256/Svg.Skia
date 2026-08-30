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
| `ShowToolBar` / `ShowDeclarationPanel` / `ShowStatusBar` / `ShowSource` | Supplying your own chrome |
| `FileDialogService` | Custom storage or picker integration |
| `Canvas` | Direct access to the surface for zoom and pan |
| `DocumentOpened` / `ErrorRaised` / `ParameterValueChanged` | Syncing host titles and status |

## Ranges come from the document

A `number` parameter uses the `min`, `max` and `step` its author declared, falling back to `0` to `1` when it declares none:

```xml
<e:param name="hue" type="number" default="217" min="0" max="360" step="1" />
```

Every row is seeded by *evaluating* the declared `default`, so `default="tau / 4"` works as well as a literal does.

A `default` that will not evaluate, or a range whose ends are the wrong way round, does not stop the
parameter being offered: the drawing still renders and the value is still bindable, the row falls back
to a placeholder and the default range. What is wrong with it is marked in the source pane, at the
attribute it is wrong in — the panel does not repeat it.

A drawing with mistakes in it says so from the moment it opens, in two places for two different
things.

**A note in the status bar** — *"6 errors, marked in the Source pane"* — for everything the pane
already marks on the line that carries it. Errors and warnings are counted apart and worded apart
(*"1 error and 1 warning"*), because a warning is something the drawing opened in spite of; a note
that is only warnings is painted in the warning colour rather than the error one. The count and the pointer are all it gives, because the
line is where the detail belongs. It sits beside the status rather than under it, so it takes no room
and the viewer does not shift as it comes and goes while you edit. It is a standing statement, not a
reaction: it does not wait for a control to be touched, and it does not change when one is.

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
table the source pane paints with. `SvgExpressionPresenter` is what does it: a control theme puts it
in place of a `TextBox`'s own presenter, so the caret, the selection, composition and undo stay the
box's. Selected text keeps its colours, as it does in the source pane.

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

All of them go through the source pane's text buffer rather than around it, so the undo stack is the one
history of the document: a parameter added from the panel and a line typed into the pane come off it
in the order they were done, and an addition that had to declare a namespace and open a block is
three spans and one undo step.

Neither needs the pane to be open — the buffer and the pane are separate things, so an edit made with
the pane closed still marks the document modified and still saves. What the pane shows afterwards is
the file as it was, with one line added: every comment and every placeholder where the author left
them.

`ParameterDialogService` is how the form is asked for, replaceable for the reason `FileDialogService`
is. `SvgParameterFormView` is the form itself, a plain control, for a host that wants to ask its own
way.

## Reading the drawing's text

The **Source** toggle in the toolbar opens a pane under the drawing showing the document as it was
read — comments, formatting and `{%{{{ … }}}%}` expressions exactly as their author wrote them. It is
read-only; editing SVG is what `Svg.Editor.Skia.Avalonia` is for.

It is coloured as XML, and — because no stock grammar knows the extension — `{%{{{ … }}}%}` placeholders
and `<e:let>` bodies are coloured as the expression code they are, not as strings and prose. The
splitting is [Svg.Highlighting](svg-highlighting), which draws nothing; the palette here is theme
resources you can override:

```xml
<SolidColorBrush x:Key="SvgViewerSourceExpressionBrush" Color="#C586C0" />
```

`…ElementBrush`, `…AttributeBrush`, `…ValueBrush`, `…CommentBrush`, `…PunctuationBrush`,
`…TextBrush` and `…LineNumberBrush` cover the markup, and the expression language has its own:
`…ExpressionNumberBrush`, `…ExpressionColorBrush`, `…ExpressionFunctionBrush`,
`…ExpressionConstantBrush`, `…ExpressionKeywordBrush`, `…ExpressionOperatorBrush`,
`…ExpressionPunctuationBrush` and `…ExpressionIdentifierBrush`.

The pane is an [AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit) editor over one document,
so **a selection can cross a line** and the text can be taken away whole. You need nothing in your
`App.axaml`: AvaloniaEdit supplies its own theme, and the viewer carries the style include regardless.

**It is editable.** Type, and the drawing follows a fifth of a second after you stop — the whole
document is re-read each time, which costs about 43ms for a 132KB drawing, so nothing is incremental
and nothing is stale. Half-typed markup does not parse, and that is the ordinary case: the picture
you already have stays up while the marks move to what is now wrong. Undo, redo and find come with
the editor.

`IsSourceModified` says whether there are edits not on disk and `SourceModifiedChanged` announces it;
`SaveSourceAsync` writes them back, asking through `FileDialogService` when the drawing has no file
of its own. In `src/Svg.Studio` that is Cmd/Ctrl+S, a dot on the tab, and a prompt before anything
throws work away — closing a tab asks about that drawing, closing the window asks once about every
unsaved one it is holding. The control raises, the host decides, the same way opening works.

Two rules worth knowing. A drawing too large to show whole is **read-only**: the pane holds a cut
copy, and saving that would behead the file. And a save keeps the byte order mark the file arrived
with, so nothing changes in a part of it you did not edit.

There is no size at which colouring gives up, because only the lines on screen are ever coloured: a
132KB drawing of 340 lines opens in 102ms. What that does not bound is a single enormous *line* — a
minified drawing is the whole file on one — so a line is coloured for its first 250 pieces and the
rest left plain, which takes that same 132KB minified to 217ms. Nothing is hidden either way; the
uncoloured remainder is still there to read and select.

Mistakes get a wavy underline where they are written, and hovering one shows its message.
`…ErrorBrush` is the key for the mark, and `…WarningBrush` for the lighter one — an element name
this renderer does not know, or an id used twice, is a warning, since the drawing still opens either
way. `SourceDiagnostics` is the same list if you would rather show
it your own way — a problems panel, a status line. That covers the drawing's
expressions and the `<e:code>` block alike: a name nothing declares, a range on a colour, a `min`
above its `max`, a `default` that will not resolve.

It covers the SVG as well. An attribute value the parser's own converter will not take —
`width="abc"`, `stroke-miterlimit="20%"`, a unit this renderer does not implement — is marked where it
is written, which is the one failure the library is least able to report for itself: the value is
dropped, the property keeps its default, and the drawing renders wrong without a word. A declaration
inside `style="…"` is marked the same way, and on the declaration rather than the whole attribute. So
is `clip-path="url(#gone)"` — a reference to an id the drawing does not contain, which is the most
ordinary way for a picture to come out wrong and, until now, the quietest. An
expression written in an attribute that does not take one, `stroke-width="{%{{{ w }}}%}"`, is marked
too, and says which attributes do — as is one written in an attribute that takes a *different* kind,
such as a colour in `opacity`. What counts as a mistake is
[Svg.Highlighting](svg-highlighting)'s answer, which is the language's own checker — and a
declaration that is wrong is marked on the attribute that is wrong, not summarised above the drawing.

The text is `SvgViewerDocument.SourceText`, captured while loading, so it is what the picture was
built from rather than whatever the file says later. A host that would rather show it its own way —
a window, a docked tool panel — reads that property and leaves `ShowSource` off:

```csharp
var text = viewer.Document?.SourceText;
```

Drawings loaded from text or from a stream carry it too, so a viewer fed by a database or an archive
shows source like any other. The pane holds at most 2,000,000 characters — a backstop on what is kept in
memory rather than a layout limit — while `SourceText` itself is always whole.

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

## Two things worth knowing

It does **not** host `Avalonia.Svg.Skia.Svg`. That control sizes itself to the drawing it fits, so it cannot fill a viewport and its zoom is bounded by its own clip. The viewer draws onto `SKCanvasControl` and owns the transform, which is what makes fit and actual-size exact.

Loading is the only work off the UI thread. Binding a value evaluates a model that is already compiled, on the UI thread, coalesced to one call per frame, so a slider drag stays smooth and two changes keep the order they were made in.

## Related docs

- [Source Generator and svgc](../guides/source-generator-and-svgc)
- [Svg.Controls.Skia.Avalonia](svg-controls-skia-avalonia)
- [Skia.Controls.Avalonia](skia-controls-avalonia)
