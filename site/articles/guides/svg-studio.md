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
<studio namespace="Demo.Icons">

  <drawing name="badge" class="Badge">
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
      <circle cx="12" cy="12" r="10" fill="#3b82f6" />
    </svg>
  </drawing>

  <group name="Large" namespace="Demo.Icons.Large" scale="2">
    <drawing name="badge-large" class="BadgeLarge">
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
  project also carries `cache`, `helperScope` and `skiaSharp`. Where the C# goes is not among them:
  an export asks, so nothing in the file can come to disagree with the answer. A project written
  before that carries `output` or `singleFile`; they are dropped as it is read.
- `x` and `y` say where a row sits on the board of the group holding it — both or neither, and
  relative to that board, so a group carries what it holds. They are the one pair that is inherited
  by nobody, and the build never sees them: a group is still folded into its drawings rather than
  composed out of them.
- The file is written back as it was found — comments, attribute order, indentation and the
  drawings' own bytes. Editing one attribute rewrites that attribute.

`File → Export…` writes the project through the build `svgc` runs, so the two cannot come to
disagree about what the project says.

## Opening one

`File → Open`, a drop anywhere on the window including an empty one, or a path on the command line.
A project opens as a workspace rather than as a document: the tree goes in a panel beside the tabs —
one you can fold, move or put behind another — and the project itself takes no tab, one project at a
time, closed with `Project → Close`.

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

Five panels sit round the tabs: the **Project** tree, the **Properties** of whatever is in front, the
**Variables** it declares, the **Attributes** of the element picked in it, and the **Elements** those
come from. They
belong to the window rather than to a tab, and show whatever tab is in front — so the arrangement
holds still while you move between drawings, and the tree that names them stays beside all of them.

They come up as a strip either side of the drawing, both the same width so neither reads as the
important one: the tree over the variables it declares on the left, and on the right the settings and
the elements sharing a run over the picked element's attributes. The tree gets the deeper share of
its column, being the list you scroll where the variables are a handful of rows. The variables and
the element are apart on purpose — a variable is dragged onto an attribute, and behind a tab each
would hide the other.

**Arrange them as you like.** The chevron on a header folds that panel away. Take a panel by its
header and drop it on another header to sit behind it, or on the edge of a panel to split that panel.
Drop it against the foot of the drawing and it sits under the drawing; drop it against the **edge of
the window** and it runs under everything, taking nothing from the strip beside it. Where it would
land is drawn while you carry it. The arrangement is remembered, so it is there again next time, and
**Settings** has **Reset layout** for when it has gone somewhere you would rather it had not.

## A group's tab: several icons at once

A group's tab is a canvas of **every drawing under it**, built the way the project builds them.
Clicking a shape on it — or a row in the **element tree** beside it — picks that drawing, and the
**Variables** and **Attributes** panes then behave as they would on that drawing's own tab. Until
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
undone.

**Captions** writes each drawing's name inside its own top corner, where a group's name sits in its
frame, and a board comes up with them. The name is shrunk until it fits the drawing — across it and
down it — so it takes no room at all: a board that names what is on it is laid out and fitted exactly
like one that does not, and switching them off moves nothing. That is also why the name is only the
row's own — the class and the size used to be a second line, about twice as wide as the icon above
it, and on a small icon it would shrink to nothing legible. Switching them off is remembered, and
**Settings** has the same box.

A group's own name is written the same way whatever the captions are doing, and shrinks to its frame
where the frame is small.

**The board holds still while you work on it.** A tab is fitted when it first opens and never again
on its own: a drop leaves what you dropped where you let go of it, and zooming in on one icon to line
it up with another survives the drop, the parameter you add, the attribute you type and the trip to
another tab and back. **Fit** is what asks for the whole of it again — which is also what to press
when something lands out of sight, since a row with no place arrives beside the arrangement and a
zoomed board may not reach that far.

What is being looked at survives too: the ring, the row in the element tree and the Attributes tab
are all still on the drawing that moved. What a drawing is built from decides whether it is read again,
so arranging a board rebuilds nothing at all, and editing one drawing rebuilds that one.

Dragging a parameter there moves **every drawing that declares that parameter**, which is what makes
a family of icons worth looking at side by side. Sharing is decided one parameter at a time, by what
a drawing declares rather than by where it declared it: the name, the type and the bounds. Two
drawings sharing a `tint` share it whatever else either of them declares, so a drawing with a knob
of its own is still part of the palette — and what it does not share it keeps, at whatever it was
last set to.

The default is left out of the comparison — it is where a drawing starts rather than what it takes,
so a set seeded at different colours is still one family. The bounds are in: two parameters you would
be given different sliders for are not the one parameter, however alike their names.

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
write, whatever was typed where. `File → Export…` asks for one `.cs` and writes the whole project
into it, whatever tab is in front: a project is one build, so there is nothing to pick. It takes the
tab's own work with it, the same as a save does, and leaves the project where it was. With no project
open it is the drawing being looked at that goes — as `.svg` at the size it is being drawn at, or as
`.cs` if the name says so.

### Text an export cannot write out

A drawing whose words an expression writes is generated with the words as an argument, where the
drawing places them simply enough for that: one run, at one origin, painted by a fill. Where it
places them per glyph instead — a `<tspan>` of its own, a `textLength` to fit, a `<textPath>`,
`rotate`, letter- or word-spacing — the positions were measured from the words, so the positions and
the words cannot both be free.

Exporting such a project names those drawings and asks. **Relaxed text layout** gives the rules up
and draws the words at the element's own place, so the argument reaches the drawing; leaving it off
writes the default words in and says which drawings it did that to. Either way the rest of the
project is unaffected, and what was given up or frozen is listed when the export finishes.

**Remember this answer** puts it in **Settings**, which also holds it directly — ask each time,
relax the layout, or keep the default text. Nothing is asked for a project with no driven text in
it, which is most of them.

## The recovery copy

Studio keeps a copy of the project under your application data while it has work that is not on disk
— sooner as more piles up behind it, and never the project's own file. Saving throws the copy away,
and so does closing the window, so a copy waiting when you open a project again is one Studio never
got to throw away. It offers it back, and restoring puts the work in the window without touching the
file.

The copy is keyed by the project's own file, so a project that has never been saved has nothing to
look one up under again and is not covered until you name it. Switch the copies off in
**Settings** — the application menu on macOS, `File → Settings…` elsewhere.

## The Attributes tab

It lists the attributes of whatever is picked — on the canvas or in the element tree, on a drawing's
own tab or on a group's — under names a person would use, filed in sections by what they do:
**Geometry**, **Fill**, **Stroke**, **Text** and so on. Hover a label for the attribute's name as the
file writes it. A section folds away and stays folded from one pick to the next.

Every attribute the element writes is listed, and beside them the ones an element of its kind usually
has — a rectangle's corner radii and stroke, a gradient stop's colour and offset, not a stop's stroke
or a rectangle's font. **Show all** lists everything the parser would read on it instead, whether or
not an expression could drive it. A row the file sets is in bold, with a **×** that takes it away.

A row's box holds the value itself, whatever it is. Typing `{%{{{ tint }}}%}` into `fill` writes that into
the drawing; typing `#00ff00` back writes that. Binding and unbinding are the same gesture, so
nothing has to remember what a value used to be, and emptying the box takes the attribute away.
Typing into one of the empty rows adds it.

Some rows have a control beside the box, which writes into it. A colour has a swatch that opens a
picker; an opacity or a stop's offset has a slider; an attribute SVG spells a handful of values for —
a line cap, a text's alignment, a fill's `none` or a gradient the drawing holds — has a **▾** listing
them; and **↑**/**↓** step a number in its box, by ten with Shift and a tenth with Alt. The picker
writes once when it closes and the slider once when it is let go, so either is one step to undo. On
a row an expression drives, the swatch and the slider show what the expression comes to and are
locked; picking from the **▾**, like typing a value, lets the expression go.

**Or drag the variable onto the row.** **Variables** and **Attributes** have a run each rather than
sharing one, so both are on screen at once: take any variable by its name, of either kind — not the
grip beside it, which reorders the list — and let go of it over an attribute's box, and the box becomes
`{%{{{ that name }}}%}`. While you are carrying one, every row it could be written into is
outlined, so what a variable is good for is something you can see rather than something to try. A
number will not land on a `fill`, and nothing lands on `transform`, where an expression has to be one
function argument rather than the whole value. A row the file does not set yet takes one too, which
adds the attribute already bound.

Only an expression is judged: what it comes to is read out beside it, an expression of the wrong type
is caught in the box, and one written on an attribute the parser lifts nothing out of is refused
rather than written as braces nobody reads. A value the element's `style` attribute sets reads on its
row as `red in style`, and the row takes nothing: the declaration wins over the attribute, so it is
changed under **Inline style** instead.

## Boxes for the code that draws it

A drawing can reserve a box for its host, such as the place an app lays a value label, and the
generated class reports it as a constant: `public static readonly SKRect @LevelRect`. See
[boxes for the host](../packages/svg-codegen-skia#boxes-for-the-host). A PaintCode import marks one
for every shape named `Embed<X>`.

A box paints nothing, so the canvas draws it for you: a dashed cyan line with the constant written
inside, such as `LevelRect 2, 6, 28, 24`. The numbers are the ones the class will get, as left,
top, right, bottom. A box that a variable moves reads `LevelRect (driven)` instead, because the
class has only the defaults. **Invisible** on the toolbar, on a drawing's tab and on a group's
board, shows or hides them, along with the rest of what a drawing has but does not paint (see
[invisible elements](#invisible-elements)).

Click a box's dashed line to pick it. Ink inside a box is still picked by clicking it, and empty
room inside the box is still the page. Once picked, a box moves and resizes like any shape, but it
has no stalk: a turned box would be reported as the upright box round it.

**Box constant**, under **General** in the Attributes tab, names the box. Typing a name there marks
any shape, and the drawing gets the `e:` prefix declared if it had none; **×** unmarks it. A name the
class could not declare is refused as it is typed: one that is not an identifier, one the class
already uses, or one another box of the drawing has. In the element tree a box reads
`▭ LevelRect` beside its id, and the filter finds it by that name.

## Invisible elements

**Invisible** outlines everything a drawing has but does not paint, so it can be seen and picked:

| Outline | What it is |
|---|---|
| Dashed cyan, with a name | A box for the code that draws it, as above. Red when its name is refused. |
| Dashed violet | The content of a `<mask>`, drawn where it masks each element that uses it, labelled with the mask's id. |
| Dashed green | The content of a `<clipPath>`, drawn where it clips each element that uses it, labelled with the clip path's id. |
| Dotted pale cyan | A shape with neither a fill nor a stroke. |
| Long-dashed pink | A hidden element: `display="none"`, where `visibility="hidden"` starts, or a `display` or `visibility` expression that is false for the values bound now. A hidden group is outlined round its children. |

A mask or clip used by several elements is outlined once for each of them, and one nothing uses is
not outlined at all. Click an outline's line to pick what it outlines, as with a box. Ink is picked
first, except on the line of mask or clip content, which usually runs across the very element it
masks or clips. The room inside an outline is still the page.

Picked mask or clip content has handles where it was clicked, and a drag is mapped through the element
it was clicked on. Picked in the element tree, it is held through the first element using it, or the
one it was last clicked on. Moving or scaling it changes it for every element that uses it, since they
share it. A `<use>` inside a clip path is ringed but has no handles: it folds its own place into the
path, so nothing of its own says where that is.

An element that uses a clip path or a mask says so in the element tree, beside its id: `clip #window`
or `mask #sweep`. The filter finds it by that.

**Giving an element one.** Drag a `<clipPath>` or a `<mask>` from the element tree onto an element's
row, or onto the **Clip path** or **Mask** box of the element picked, and it clips or masks that
element; whatever did so before stays in `<defs>`. A shape kept in `<defs>` drops the same way and is
moved into a clip path made for it, or a mask while `Alt` (`⌥`) is held. The row it would land on is
outlined green for a clip and violet for a mask, and says which. The tree's menu has **New clip path**
and **New mask**, which cover the picked element, its stroke and its markers with a rectangular path
and pick that path; a new mask's path is white. With two rows picked it has **Clip … with …** and
**Mask … with …**: a clip path, a mask or a shape in `<defs>` is what clips or masks the other row, and
of two drawn rows the one painted later is moved into the new clip path or mask, staying where it was
drawn and keeping the paint and font its groups gave it. A mask made from a shape masks by its alpha
(`mask-type="alpha"`), so a shape with no fill of its own, which paints black, still shows the element
where it covers it. A board offers all of this, though its rows cannot otherwise be dragged.

The paths a clip or a mask is made of are reshaped where they apply: double-click one on the canvas,
or pick it in the tree and choose **Points** (`⌖`) on the toolbar, and its points are held on the
element it clips. That reaches a shape in `<defs>` drawn only through a `<use>` as well, and a double
click on a `<use>` reshapes the shape it draws, which changes it everywhere it is used.

## Connecting Claude Code

Claude Code can drive Studio from outside, with the same tools the Assistant panel uses: it reads the
project and its drawings, lists every problem in them, sets attributes and text, reads and changes a
group's declarations, adds, moves and renames nodes, and saves or commits.

1. In **Settings**, on the **Accounts** tab, turn on **Let Claude Code connect to this Studio**. Studio then listens on
   `127.0.0.1` only, on port 7337 unless you choose another.
2. Press **Copy command** and paste it into a terminal. It runs `claude mcp add` with this Studio's
   address and a token, which Studio keeps in the keychain; **New token** replaces it, and a command
   copied before stops working.
3. In Claude Code, `/mcp` lists `svg-studio` as connected while Studio is open.

Each edit Claude Code makes is one step on the same history as yours, named in the Edit menu as
*Undo Claude Code: …*, so ⌘Z takes it back. Saving, removing a node and committing are marked as
destructive, so Claude Code asks you before it does them; Studio does not ask again.

Only one Studio can hold a port. A second one open at the same time says in Settings that the port is
taken; give it another and copy its command separately.

## Related docs

- [Source Generator and svgc](source-generator-and-svgc) — the build a project describes
- [Svg.Expressions](../packages/svg-expressions) — the `{%{{{ … }}}%}` format, its operators and functions
- [Svg.Viewer.Skia.Avalonia](../packages/svg-viewer-skia-avalonia) — the viewer Studio is built on
- [Svg.PaintCode](../packages/svg-paintcode) — what an import reads
