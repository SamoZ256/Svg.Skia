---
title: "Svg Studio"
---

# Svg Studio

`src/Svg.Studio` is the editor for a set of drawings: the icons of one product, built at the sizes
and under the names the code that draws them wants. It opens one `.svgstudio` project at a time, and
that project is one file — the settings, the tree and the drawings themselves.

## The format

```xml
<!-- icons.svgstudio -->
<?xml version="1.0" encoding="utf-8"?>
<studio namespace="Demo.Icons" singleFile="Icons.cs">

  <drawing name="badge" class="Badge">
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
      <circle cx="12" cy="12" r="10" fill="#3b82f6" />
    </svg>
  </drawing>

  <group name="Large" namespace="Demo.Icons.Large" scale="2">
    <drawing name="badge-large" class="BadgeLarge" output="Large/BadgeLarge.cs">
      <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">…</svg>
    </drawing>
  </group>

</studio>
```

- A `<drawing>` holds the `<svg>` it draws and nothing else. The project is what you move, diff or
  hand to somebody: there is no second half of it to lose.
- `name` is what every row calls itself — the tree, the tab, and the class a drawing falls back on.
  It is a label rather than an identifier, so a drawing copied three times is three rows reading the
  same thing until one of them is renamed.
- Settings are attributes wherever they appear, and a group hands its own down to everything under
  it: `namespace`, `class`, `padding` and the `width`/`height`/`scale` trio, which moves as one. The
  project also carries `singleFile`, `cache`, `helperScope` and `skiaSharp`, and a drawing carries
  the `output` its C# goes to.
- `x` and `y` say where a row sits on the board of the group holding it — both or neither, and
  relative to that board, so a group carries what it holds. They are the one pair that is inherited
  by nobody, and the build never sees them: a group is still folded into its drawings rather than
  composed out of them.
- The file is written back as it was found — comments, attribute order, indentation and the
  drawings' own bytes. Editing one attribute rewrites that attribute.

`Project → Build` writes the outputs through the build `svgc` runs, so the two cannot come to
disagree about what the project says.

## Opening one

`File → Open`, a drop anywhere on the window including an empty one, or a path on the command line.
A project opens as a workspace rather than as a document: the tree goes in a pane beside the tabs
and the project itself takes no tab, one project at a time, closed with `Project → Close`.

Two other things open as a project, by being converted into one:

- An **svgc project** (`.svgcproj`) is read with its drawings and opened as a project. It is one
  way, and the window says what the conversion cost: a recipe is baked into the drawings it painted
  — the parameters it declared were what drove them — and a file the old project named twice becomes
  two drawings that no longer edit each other. What it came from is left where it is.
- A **PaintCode document** (`.pcvd`) becomes one project with every canvas in it. What could not be
  carried across is listed afterwards.

Neither is written, and neither is named. A conversion opens as unsaved work called **Untitled**, so
the first thing you do with it is look at it; saving asks where it goes, offering the name of the
document it came from. `Project → New` is the same: a project with nothing in it and no file, which
⌘S names. Nothing appears beside the document you opened until you say so.

## The tree, and the tabs

Clicking a row opens it: a drawing in a viewer **at the size its groups build it at** rather than the
one it was written with, a group as its settings and what they come to. A double click is left to the
tree, which folds a row with it. Picking a tab opens the tree back down to the row it came from and
marks it, so a group folded away still says where the tab you are looking at lives.

The tree is editable. Each row carries **Add group**, **Add SVG…** and **Remove**; `Delete` removes
the selected row, and a row is dragged to move it — dropped on the top or bottom quarter of a group
it lands beside it, and in the middle it goes inside. Adding an `.svg` file reads it in; so does
pasting one, or pasting the SVG a drawing program puts on the clipboard. Adding, removing and moving
edit the project; nothing reaches the file until you save it.

A drawing's tab has its settings in the right pane, in front of the parameters the drawing declares —
the same pane a group keeps its settings in, and the tab a drawing opened from the tree lands on.

## A group's tab: several icons at once

A group's tab is a canvas of **every drawing under it**, built the way the project builds them.
Clicking a shape on it — or a row in the **element tree** beside it — picks that drawing, and the
**Parameters** and **Element** tabs then behave as they would on that drawing's own tab. Until
something is picked they say so, because a group builds several drawings and cannot guess which is
meant.

### The board

A group that has never been arranged is laid out in a near-square grid. **Drag anything on it** and
that stops: every row of the tab is written at the place the grid had just given it, so nothing
jumps, and from then on the board is what the file says. `x` and `y` in the settings pane are the
same thing typed rather than dragged.

A **group with a place of its own is drawn as a labelled frame** round what it holds, and dragging
the frame carries the lot — its children are written against it, so that is one attribute. A group
with no place is not a unit on the board: what it holds joins the drawings that have no place yet,
which wait in a grid beside the arrangement until somebody puts them somewhere. That is where a
drawing added, dropped or pasted arrives, and where a copy arrives, since two rows on one spot would
hide each other.

**A row dragged into another group keeps the place it had**, written in the coordinates of the board
it arrives on — where you dropped it in the tree says what holds it and nothing about where it should
sit, so it does not move. A group's outline grows to reach it, which is the outline saying what it
holds. It keeps its corner and not its size: the group it has joined is what builds it now, so a row
dropped into one that doubles its drawings doubles where it stands. Two boards cannot say where a row
is, and there it waits in the grid with the rest: one that is still a grid itself, where the arriving
row would be the first placed one and would push everything nobody touched out to the right of it,
and one with a group between them that names no place of its own.

A drag writes the file as it is made, like every other arrangement edit, and like those it cannot be
undone. And nothing sizes a column to hold a caption once a board is explicit, so two rows put close
together can have their captions overlap — move one.

**The board holds still while you work on it.** A tab is fitted when it first opens and never again
on its own: a drop leaves what you dropped where you let go of it, and zooming in on one icon to line
it up with another survives the drop, the parameter you add, the attribute you type and the trip to
another tab and back. **Fit** is what asks for the whole of it again — which is also what to press
when something lands out of sight, since a row with no place arrives beside the arrangement and a
zoomed board may not reach that far.

What is being looked at survives too: the ring, the row in the element tree and the Element tab are
all still on the drawing that moved. What a drawing is built from decides whether it is read again,
so arranging a board rebuilds nothing at all, and editing one drawing rebuilds that one.

Dragging a parameter there moves **every drawing that declares the same thing**, which is what makes
a family of icons worth looking at side by side. Sharing is decided by what a drawing declares rather
than by where it declared it: the whole parameter list, in order, with each one's type and bounds.
The default is left out of that — it is where a drawing starts rather than what it takes, so a set
seeded at different colours is still one family.

## Where an edit lands

Nothing reaches the `.svgstudio` file except through `File → Save`. Between the two there is the
project itself, held in the window, which is what every view of it reads.

- A node's **settings** are held on the tab they were typed in until it is committed. A tab hands
  over what was typed in it and nothing else.
- A drawing **open in a tab** is edited through that tab's buffer, so the edit can be taken back
  with ⌘Z.
- A drawing **open in no tab** — one edited from a group's canvas — goes straight into the project.
  There is no buffer to hold it, so there is nothing to undo; it is still unsaved, and the window
  says so.
- **Arranging** — a row added, removed or dragged in the tree, a drawing dragged on a board — is the
  project's own edit. No tab wears a mark for it, because closing a tab would not lose it; the
  window title does, and closing the project asks about it by name.

`File → Save` hands the selected tab's work to the project and writes the project — one file, so one
write, whatever was typed where. `File → Export…` writes the drawing being looked at somewhere else
— as `.svg`, at the size the project builds it at and without the indentation the project wrote it
at, or as `.cs` if the name says so.

## The recovery copy

Studio keeps a copy of the project under your application data while it has work that is not on disk
— sooner as more piles up behind it, and never the project's own file. Saving throws the copy away,
and so does closing the window, so a copy waiting when you open a project again is one Studio never
got to throw away. It offers it back, and restoring puts the work in the window without touching the
file.

The copy is keyed by the project's own file, so a project that has never been saved has nothing to
look one up under again and is not covered until you name it. Switch the copies off in
**Settings** — the application menu on macOS, `File → Settings…` elsewhere.

## The Element tab

It lists the attributes of whatever is picked — on the canvas or in the element tree, on a drawing's
own tab or on a group's — in the order the file writes them, then the ones an expression could drive
that the element has no value for yet.

A row's box holds the value itself, whatever it is. Typing `{%{{{ tint }}}%}` into `fill` writes that into
the drawing; typing `#00ff00` back writes that. Binding and unbinding are the same gesture, so
nothing has to remember what a value used to be, and emptying the box takes the attribute away.
Typing into one of the empty rows adds it.

Only an expression is judged: what it comes to is read out beside it, an expression of the wrong type
is caught in the box, and one written on an attribute the parser lifts nothing out of is refused
rather than written as braces nobody reads. A value a `style` declaration overrides is refused, since
the attribute under one paints nothing.

## Related docs

- [Source Generator and svgc](source-generator-and-svgc) — the build a project describes
- [Svg.Expressions](../packages/svg-expressions) — the `{%{{{ … }}}%}` format, its operators and functions
- [Svg.Viewer.Skia.Avalonia](../packages/svg-viewer-skia-avalonia) — the viewer Studio is built on
- [Svg.PaintCode](../packages/svg-paintcode) — what an import reads
