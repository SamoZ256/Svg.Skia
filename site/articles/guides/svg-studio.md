---
title: "Svg Studio"
---

# Svg Studio

`src/Svg.Studio` is the editor for a set of drawings: the icons of one product, built at the sizes
and under the names the code that draws them wants. It opens one `.svgstudio` project at a time, and
that project is a folder — the settings in one file, each drawing in a file of its own, and a folder
for each group.

## The format

```
icons.svgstudio/
  studio.xml          the project's own settings, its templates and what it declares
  badge.svg           a drawing: a standalone SVG file, which anything can open
  Large/              a group
    group.xml         the group's settings and what it declares
    badge-large.svg
  fonts/              the .otf and .ttf files its text is measured with
    NotoSans-Bold.ttf
```

```xml
<!-- studio.xml -->
<?xml version="1.0" encoding="utf-8"?>
<studio namespace="Demo.Icons">
</studio>

<!-- Large/group.xml -->
<group namespace=".Large" scale="2" />

<!-- Large/badge-large.svg -->
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
  <circle cx="12" cy="12" r="10" fill="#3b82f6" />

  <metadata><drawing xmlns="https://svg.skia/studio/1.0" class="BadgeLarge" /></metadata>
</svg>
```

- A drawing is its own `.svg`. What Studio keeps about it — its class, its place on the board, where
  it was imported from — goes in a `<metadata>` at the end, and is left out when there is nothing to
  say, so a drawing nobody has touched is the file it was.
- `name` is what every row calls itself — the tree, the tab, and the class a drawing falls back on.
  It is a label rather than an identifier, so a drawing copied three times is three rows reading the
  same thing until one of them is renamed. The file is named after it: `badge.svg`, then
  `badge~2.svg` for the next of that name, with anything a file system cannot hold replaced. Where
  the file name cannot say the name, the name is written beside the drawing as well.
- Rows go by name, in the tree and everywhere else: a folder keeps no order, so the board lays them
  out and the build generates them in that order too.
- Settings are attributes wherever they appear, and a group hands its own down to everything under
  it: `namespace`, `class`, `padding` and the `width`/`height`/`scale` trio, which moves as one. A
  namespace that starts with a dot continues the one above it: `.Large` under `Demo.Icons` is
  `Demo.Icons.Large`, and follows the project's when that is renamed. The project's own namespace is
  written in full, having nothing above it to continue. The project also carries
  `helperScope` and `skiaSharp`, and what shapes the file: `body`,
  `typeface`, and an API class (`api`, `resizing`, `context`, `order`), with `method` and
  `parameters` on a drawing (see [shaping the generated file](../packages/svg-codegen-skia#shaping-the-generated-file)).
  Where the C# goes is not among them:
  an export asks, so nothing in the project can come to disagree with the answer.
- `x` and `y` say where a row sits on the board of the group holding it — both or neither, and
  relative to that board, so a group carries what it holds. They are the one pair that is inherited
  by nobody, and the build never sees them: a group is still folded into its drawings rather than
  composed out of them.
- Every file is written back as it was found — comments, attribute order, indentation and the
  drawings' own bytes — and a save writes only the files that changed.
- **In git** the folder merges file by file, with nothing of Studio's in it: two people adding a
  drawing each add a file each, and only a drawing both of them changed is a conflict. The
  **Changes** panel settles one of those by keeping a side — or both, when both added a drawing of
  one name.

`File → Export…` writes the project through the build `svgc` runs, so the two cannot come to
disagree about what the project says. Another project's build can write the same file without the
app, through [Svg.Studio.Projects](../packages/svg-studio-projects). The settings only an export reads (`helperScope`,
`skiaSharp` and the ones shaping the file) are asked for there rather than shown in Properties.

## Fonts

A project carries the fonts its text is measured with, in the `fonts` folder at its root. Drop
`.otf` or `.ttf` files anywhere on the window, or pick **Add fonts…** in the project's
**Properties**, and they join the project the way a drawing does: the project is edited, undo takes
them out again, the next save writes them and the recovery copy keeps them. **Properties** lists
each face with **✕** to take it out, and **More fonts from** names a folder beside the project as
well (`fontsFolder="../fonts"`, relative to the project's folder), whose fonts come after its own.

The tabs, the board, `File → Export…`, the assistant's code and a build through Svg.Studio.Projects
all measure with those files, so every machine writes the same C#. A text whose family, weight or
style they do not hold is said on the status bar and among the export's warnings — even where
the machine has that font installed, since another machine would measure it with a font of its own.
A project with no fonts measures with the machine's, and says only of a family the machine lacks. A
font's **▾** in **Attributes** offers the families the project's fonts hold.

A font both sides of a merge changed is not settled in the **Changes** panel: keep one with
`git checkout --ours` or `--theirs` and `git add`, as it says, and **Resolve…** settles the rest.

## Opening one

`File → Open`, a drop anywhere on the window including an empty one, or a path on the command line.
A project opens as a workspace rather than as a document: the tree goes in a panel beside the tabs —
one you can fold, move or put behind another — and the project itself takes no tab, one project at a
time, closed with `Project → Close`.

On macOS a `.svgstudio` is one document in Finder and in `File → Open`, and double-clicking it opens
it, once the app bundle has been opened: a publish for an `osx-*` runtime builds it only when asked —
`dotnet publish src/Svg.Studio -c Release -r osx-arm64 -p:StudioAppBundle=true` leaves
`Svg.Studio.app` in the publish folder. Elsewhere it is an ordinary folder, which nothing can be told
to open with Studio: open it from Studio, by picking the `studio.xml` inside it, dropping the folder
on the window or naming it on the command line.

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
it lands beside it, and in the middle it goes inside. A drag held near the top or foot of this or any
other pane scrolls it, faster the nearer the edge, so a row out of sight can still be reached; a tab
held at either end of a strip with more tabs than fit scrolls the strip the same way. Adding
an `.svg` file reads it in; so does pasting one, or pasting the SVG a drawing program puts on the
clipboard. Adding, removing and moving edit the project; nothing reaches the folder until you save it.

Panels sit round the tabs: the **Project** tree, **Changes** and **History**; the **Variables** a
drawing declares and the **Properties** of whatever is in front; the **Attributes** of the element
picked in it, the **Elements** those come from, and the **Assistant**; and **Streamline**. They belong
to the window rather than to a tab, and show whatever tab is in front — so the arrangement holds still
while you move between drawings, and the tree that names them stays beside all of them.

They come up in the **Drawing** layout: down the left the tree, with Changes and History behind it,
over the variables and the properties; Streamline folded under the drawing, which its tab opens; down
the right the attributes over the element tree and the assistant. The variables and the attributes are apart on purpose — a
variable is dragged onto an attribute, and behind a tab each would hide the other. **Board** puts
Streamline under the tree and gives a group's board the window's whole height; **Review** puts
Changes and History wide on the left. Pick one in the control bar's picker, at its right end, or in
the **Window** menu. The picker names the layout you are in, and says *Custom* once the panels are
arranged as none of them.

**History** lists what **Edit → Undo** would take back, in the order it would: the drawing in front
first, then the project. Clicking a row takes back that step and everything above it.

**Arrange them as you like.** The chevron on a header folds that panel away, and a click on a folded
run's tab opens it again. Drag the gap between two panels, which lights up under the pointer, to
resize them. Take a panel by its
header and drop it on another header to sit behind it, or on the edge of a panel to split that panel.
Drop it against the foot of the drawing and it sits under the drawing; drop it against the **edge of
the window** and it runs under everything, taking nothing from the strip beside it. Where it would
land is drawn while you carry it. A tab's own menu, on a right-click, moves it left or right along its
run or into a run of its own below it, and closes it where the **Window** menu can bring it back;
clicking a tab leaves the keyboard with the drawing, and once a tab has it the arrow keys walk the run's tabs, and a run too
narrow for its tabs offers the rest under the **»** at its end. The arrangement is remembered, so it
is there again next time, and **Window → Reset Layout** puts it back for when it has gone somewhere
you would rather it had not. The
**Window** menu also takes a panel off or puts it back for this session — all but the project tree and
**Changes**, which come and go with the project — and moves between tabs
(⌃⇥ and ⌃⇧⇥); **Help → Keyboard Shortcuts** lists every key Studio answers to, and **Help → Search
Commands…** (⌘K, Ctrl+K elsewhere) finds a command, a tool, or a drawing or group of the project by
name, and runs or opens it.

Two bars run across the window and are about whichever tab is in front. The **control bar**, under
the menus, names what is picked — `rect #a`, *3 objects*, *Page* — and holds **Reset parameters**,
**Lock ratio**, **Snap**, **Invisible** and **Captions**, with **Search** at its right end. The
**status bar** at the foot zooms (**−**, a percentage to type, **+**, **Fit**), says the drawing's
name, how many parameters it has and the tool in hand with how it is used, reads the pointer in the
drawing's own units, and carries the note about anything wrong with the drawing. **Snap** lands what
is dragged on the grid, and **Grid** draws the grid's lines; each works without the other. The same
switches are in **View**.

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

A drag writes the project as it is made, and like every other arrangement edit it is one step on the
project's history, *move …* in the Edit menu, so ⌘Z takes it back.

**Captions**, on the control bar and in **View**, writes each drawing's name inside its own top corner, where a group's name sits in its
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

Nothing reaches the `.svgstudio` folder except through `File → Save`. Between the two there is the
project itself, held in the window, which is what every view of it reads.

- A node's **settings** are held on the tab they were typed in until it is committed. A tab hands
  over what was typed in it and nothing else.
- A drawing **open in a tab** is edited through that tab's buffer, so the edit can be taken back
  with ⌘Z.
- A drawing **open in no tab** — one edited from a group's canvas — goes straight into the project,
  as a step on the project's history that ⌘Z takes back. It is still unsaved, and the window says
  so.
- **Arranging** — a row added, removed or dragged in the tree, a drawing dragged on a board — is the
  project's own edit. No tab wears a mark for it, because closing a tab would not lose it; the
  window title does, and closing the project asks about it by name.

⌘Z takes back the front tab's own edits first, and the project's once that tab has none left.

`File → Save` hands the selected tab's work to the project and writes the project — the files that
changed, whatever was typed where. `File → Export…` asks for one `.cs` and writes the whole project
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
— sooner as more piles up behind it, and never the project's own files. Fonts added since the last
save are in it too; the ones already saved are not copied again. Saving throws the copy away,
and so does closing the window, so a copy waiting when you open a project again is one Studio never
got to throw away. It offers it back, and restoring puts the work in the window without touching the
project.

The copy is keyed by the project's own folder, so a project that has never been saved has nothing to
look one up under again and is not covered until you name it. Switch the copies off in
**Settings** — the application menu on macOS, `File → Settings…` elsewhere.

## Selecting

**Select** in the menu bar picks by what the file says. **All** (⌘A, Ctrl+A elsewhere) picks
everything drawn — not `<defs>`, the declarations, or what a clip path or mask holds — and
**Deselect** (⇧⌘A) lets go. **Inverse** picks what is drawn and not picked, keeping a group whole
unless something in it was picked. **Same Fill**, **Same Stroke**, **Same Stroke Width** and **Same
Opacity** pick every element whose value is written the same as the first picked one's: on itself,
or for all but opacity on a group around it, its `style` before its attribute. The text is
compared, so `{{ tint }}` matches `{{tint}}` and not the colour it comes to, and `#2A7DE1` matches
`#2a7de1` but not `red`. Select reads attributes and `style=""`, not a `<style>` sheet; where a
drawing has one and the value is not written, it says so rather than guessing. Picking a shape
leaves groups out; picking a group takes groups too. **Same Type** picks every element of its kind.
⌘A in a box selects its text, and with **Points** out it picks every point of the shape.

A right click on the canvas (a two-finger click on a trackpad) opens the element tree's menu. A
shape not already picked is picked first, and anywhere else the selection stays, so **Same**
compares with the first of what is picked. Its **Select** has all of the above and **Uses of**,
which lists the names the drawing declares and picks every element that reads the one chosen.
Nothing opens while a drawing tool or **Points** is out. A group's tab does the same in the
drawing clicked.

**Swap Fill and Stroke** (⇧X on the canvas or in the element tree, in the **Edit** menu and in the
rows' menu) exchanges what everything picked fills and strokes with, and their opacities, as one
step to undo. The text moves, so `{%{{{ tint }}}%}` goes with its paint, and each value stays where its
element writes that property: a value in `style` stays in the style, its `!important` kept. An
unwritten fill swaps as the black it paints and an unwritten stroke as none, so a plain shape turns
into its outline. What an element only inherits from a group is written on it where it would now
paint otherwise, and where that shadows a group's expression the canvas says so. A `<stop>` has
nothing to swap, a shape in a `<clipPath>` paints nothing, and a paint a `<style>` sheet may set, or
a `<use>` gives what a `<symbol>` holds, is not guessed at; each is refused before anything is written.

## The View menu

**Rulers** (⌘R, Ctrl+R elsewhere) runs a ruler along the top and the left of the canvas. They count
the drawing's own units, its `viewBox`, not the size it is drawn at: a `viewBox="0 0 24 24"` drawn
48 wide reads 24 across. On a group's board they count the drawing being read, the one the
Elements tab shows, from that drawing's own zero, and the board's units before one is read.

Press on a ruler and drag onto the canvas for a guide: a level one from the top ruler, an upright
one from the left. Drag a guide to move it, and drop it on a ruler or anywhere off the canvas to
take it away; with **Snap to Grid** on it lands on the grid, and Escape puts it back. A click on a
guide is a click on whatever is under it, and a drawing tool draws over a guide rather than taking
hold of it. On a drawing's tab a guide keeps the number its ruler gave it, so it stays on the
artwork when the page grows on its left. Guides belong to the tab: nothing is written into the
drawing, and closing the tab takes them away. **Guides** (⌘;) shows and hides them, and hidden,
nothing lands on them.

Whatever is dragged — a shape carried or resized, a point, a page edge, a shape being drawn —
lands on a guide within eight pixels of it. **Smart Guides** (⌘U) adds the other shapes' left and
right edges, tops and bottoms and centres, and the page's: a shape carried near another lines up
its left edge, centre or right edge with it, and what it lines up with is drawn in magenta until it
is let go. On a board that includes the drawings beside it. What is being dragged is never one of
them, and neither is anything in `<defs>`, a clip path or a mask, or anything hidden. Near a grid
line and a guide both, the nearer wins, and with **Snap to Grid** on the grid's own rule still
holds: a shape narrower than a step lands on nothing along that axis. Carrying a whole drawing about
a board lands it on the grid and the guides, not on other shapes. Rulers start off and both kinds
of guide on; all three are remembered.

## The Attributes tab

It lists the attributes of whatever is picked — on the canvas or in the element tree, on a drawing's
own tab or on a group's — under names a person would use, filed in sections by what they do:
**Geometry**, **Fill**, **Stroke**, **Text** and so on. Hover a label for the attribute's name as the
file writes it. A section folds away and stays folded from one pick to the next. The heading names the
element's tag and its id; **Find an attribute** under it lists only the rows whose name holds what is
typed, and finds the attributes the element does not usually list as well.

Every attribute the element writes is listed, and beside them the ones an element of its kind usually
has — a rectangle's corner radii and stroke, a gradient stop's colour and offset, a gradient's
`e:stop-colors` and `e:stop-offsets`, which take a `color[]` and a `number[]` parameter whole, not a
stop's stroke or a rectangle's font. **Show all** lists everything the parser would read on it instead, whether or
not an expression could drive it. A row the file sets is in bold, with a **×** that takes it away.

A row's box holds the value itself, whatever it is. Typing `{%{{{ tint }}}%}` into `fill` writes that into
the drawing; typing `#00ff00` back writes that. Binding and unbinding are the same gesture, so
nothing has to remember what a value used to be, and emptying the box takes the attribute away.
Typing into one of the empty rows adds it.

Some rows have a control beside the box, which writes into it. A colour has a swatch that opens a
picker; an opacity or a stop's offset has a slider; an attribute SVG spells a handful of values for —
a line cap, a text's alignment, a fill's `none` or a gradient the drawing holds, a font the project's
fonts hold — has a **▾** listing them; and **↑**/**↓** step a number in its box, by ten with Shift
and a tenth with Alt, and an opacity by a tenth. The picker writes once when it closes and the slider
once when it is let go, so either is one step to undo. On a row an expression drives, the swatch and
the slider show what the expression comes to and are locked; picking from the **▾**, like typing a
value, lets the expression go. A box an expression decides is outlined in the expression colour, and so
is an empty one a group's expression paints, which reads `{%{{{ tint }}}%} inherited`: giving the element
a value of its own there is still a pick or a keystroke away.

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
rather than written as braces nobody reads. A value the element's `style` attribute sets is shown and
edited in that declaration, which is what paints, and its `!important` is kept; emptying the box takes
it out of the style. Geometry and `transform` set in a style are read-only, as `rotate(45deg) in style`:
what is written for them is attribute grammar, which no browser reads in a style.

The **ID** row renames the element, and what in the drawing points at it follows: `url(#…)` in any
attribute or style, an `href`, an animation timed `id.click`, and `#id` in a `<style>` sheet's
selectors, never its colours. An id another element has, or one that is not a name — `1a`, one with a
space — is refused, and so is taking away an id something points at.

What is typed is written when the box is left or Enter is pressed, and before Save, a command, another
tab or closing the window acts on the drawing. **Escape** puts back what the box was drawn with, and
⌘Z while typing takes back the typing. If the drawing changes under a box being typed in — an undo
the assistant makes, or a reload — the value is not written, and the panel says so.

## Code that calls it the way PaintCode's did

`File → Export…` asks for the settings only an export reads before it asks where the file goes:
the helper methods, the SkiaSharp version, how `Draw` draws (record a picture,
or straight onto the canvas), the **Typeface** expression, and a **PaintCode-style API** block with
the class, the resizing enum, the text context type and the argument order. The answers are kept in
the project, where a build exporting it reads them too, so the next export opens on them; changing them is one step
Undo takes back. A drawing's Properties keep its own **API method** and **API arguments**. A
PaintCode import fills in each drawing's method and the project's argument order. The API takes
effect once its class is named. What the class looks like is in
[a PaintCode-style API](../packages/svg-codegen-skia#a-paintcode-style-api).

## Boxes for the code that draws it

A drawing can reserve a box for its host, such as the place an app lays a value label, and the
generated class reports it as a constant: `public static readonly SKRect @LevelRect`. See
[boxes for the host](../packages/svg-codegen-skia#boxes-for-the-host). A PaintCode import marks one
for every shape named `Embed<X>`.

A box paints nothing, so the canvas draws it for you: a dashed cyan line with the constant written
inside, such as `LevelRect 2, 6, 28, 24`. The numbers are the ones the class will get, as left,
top, right, bottom. A box that a variable moves reads `LevelRect (driven)` instead, because the
class has only the defaults. **Invisible**, on the control bar and in **View**, shows or hides
them on a drawing's tab and a group's board alike, along with the rest of what a drawing has but does not paint (see
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
outlined green for a clip and violet for a mask, and says which. The tree's menu, which a right click
(a two-finger click on a trackpad) on the canvas opens too, has **New clip path** and **New mask**,
which cover the picked element, its stroke and its markers with a rectangular path
and pick that path; a new mask's path is white. With two rows picked it has **Clip … with …** and
**Mask … with …**: a clip path, a mask or a shape in `<defs>` is what clips or masks the other row, and
of two drawn rows the one painted later is moved into the new clip path or mask, staying where it was
drawn and keeping the paint and font its groups gave it. A mask made from a shape masks by its alpha
(`mask-type="alpha"`), so a shape with no fill of its own, which paints black, still shows the element
where it covers it. A board offers all of this, though its rows cannot otherwise be dragged.

The paths a clip or a mask is made of are reshaped where they apply: double-click one on the canvas,
or pick it in the tree and choose **Points** (`⌖`) on the tool strip, and its points are held on the
element it clips. That reaches a shape in `<defs>` drawn only through a `<use>` as well, and a double
click on a `<use>` reshapes the shape it draws, which changes it everywhere it is used.

## Connecting Claude Code

Claude Code can drive Studio from outside, with the same tools the Assistant panel uses: it reads the
project, its drawings and each node's own and inherited settings, lists every problem in them, reads
the C# a drawing is built into, sets attributes and text, renames an element's id with what points at
it, swaps fill and stroke, adds, removes, moves and copies elements,
selects them by key or as the Select menu does, reads and changes a group's declarations, declares
or renames one parameter or let with every use of it, finds where a name, an id or a value is, adds, moves and renames nodes, and saves or commits.
Before a commit, `get_changes` tells it what has changed since the last one, as the Changes panel
would, counting what the project holds unsaved but not edits still held in a tab; `git_history` lists
the project's commits, and `get_drawing` and `render` take any of them as a revision to show a
drawing as it was there. It lists the project's templates and how an import would rank them for a
drawing, makes one from a drawing, and brings a drawing in through one when it names it. It can also
see a drawing, which the panel cannot: `render` draws one as an image, at several sizes side by side
and on light and dark, with parameter values of its own, or as a replacement or a new drawing would
look before either is applied. With a Streamline key set in Settings, `streamline_search` shows it a
sheet of Streamline's icons and `streamline_import` brings them in through the project's templates,
both with your key and against your plan's quota.

It works with files by their full paths: `save` writes a project that was never saved, or a drawing
of no project's, to a path it names; `export` writes the project as one C# file, or with no project
open the drawing in front as SVG or C#; `new_project` starts an empty one; `open_file` opens a drawing,
a project from its folder or anything inside it, or an svgc project or PaintCode document to
convert, and brings the project open already forward; and `reload` reads the open project again
from its folder, or, while a drawing of no project's is in front, that drawing instead. Studio does
not watch the disk, so after a file it has open is changed elsewhere — by another editor, or a
`git pull` in a terminal — Claude Code is told to call
`reload`. Until it does, `save` and `git_commit` refuse to write the project once its files are not
what Studio last read or wrote, and `get_changes` says so first, rather than take Studio's copy for
edits and write it back over the change. Where Studio would stop to ask you something, a tool never opens a
dialog. Removing a node whose tab holds edits you have not saved, starting or opening a project, or
reloading, over unsaved work, asks you in Claude Code whether to throw that work away, and exporting
text the C# cannot write out asks whether to bake it or relax it, this once or from now on; each goes
on as you answer, and does nothing where what it asked about changed before you answered. Where the
client cannot ask, the first are refused, saying why, and the choice stays yours in Studio, as it
does over a recovery copy waiting to be restored. Otherwise a tool goes on as you chose before and
says what it did: a PaintCode document is converted on the terms you last picked, and text an export
cannot ask about is baked unless you chose to relax it for good. A project part way through a merge
still opens if its files read, and Studio offers you the merge when you next come back to it; one
with a file holding the conflict is refused.

You can hand Claude Code a drawing yourself, as a resource: `@svg-studio:svg-studio://front.png`
attaches the drawing in front as an image, `front.svg` its source and `front.cs` its C#, and
`svg-studio://drawing/1/0.svg` the drawing at that path in the project, which is a position among rows
sorted by name, so an add, a rename or a move beside it renumbers it; each section of this
documentation is there too. Three prompts start a common job: `/mcp__svg-studio__new_drawing`
draws one on its group's grid and tries it at small sizes before adding it,
`/mcp__svg-studio__review_drawing` says what is wrong with one and changes nothing, and
`/mcp__svg-studio__add_parameter` turns a value into a parameter whose default keeps the look.

1. In **Settings**, on the **Accounts** tab, turn on **Let Claude Code connect to this Studio**. Studio then listens on
   `127.0.0.1` only, on port 7337 unless you choose another.
2. Press **Copy command** and paste it into a terminal. It runs `claude mcp add` with this Studio's
   address and a token, which Studio keeps in the keychain; **New token** replaces it, and a command
   copied before stops working.
3. In Claude Code, `/mcp` lists `svg-studio` as connected while Studio is open.

Each edit Claude Code makes is one step on the same history as yours, named in the Edit menu as
*Undo Claude Code: …*, so ⌘Z takes it back. Its `undo` tool takes back only its own steps, newest
first, and names each one. While an edit of yours sits on top of its step it takes nothing back, and
names your edit instead; only once it has none of its own left does it take back your last, and says
so. If you have taken back each of its steps yourself already, the first `undo` after takes nothing
back and says so, and the next is the one that reaches yours. Its `redo` puts back what its undo
took, the same way: while a step you took back since is in front of one, it puts nothing back and
names yours; once it makes a new edit, or has nothing of its undo's left, it puts back what **Redo**
in the Edit menu would, and if you have put back or built over all its undo took, the first `redo`
after puts nothing back and says so. `get_history` tells it what each would do now, and names the
steps of the drawing in front and of the project, newest first, up to fifty each way. Neither takes
back or puts back a step that could close a tab holding edits you have not saved, which Studio would
do without asking: one of its own that adds or removes what the tab is on, or, with none of its own
left, any step of the project's. Nor does either step a drawing's text in the project under a tab you
have since opened on it and typed in, whose next save would write over it. It names the tab instead.
Saving, exporting, removing a node and committing are marked as destructive, so Claude Code asks you
before it does them; Studio does not ask again.

Only one Studio can hold a port. A second one open at the same time says in Settings that the port is
taken; give it another and copy its command separately.

## Related docs

- [Source Generator and svgc](source-generator-and-svgc) — the build a project describes
- [Svg.Expressions](../packages/svg-expressions) — the `{%{{{ … }}}%}` format, its operators and functions
- [Svg.Viewer.Skia.Avalonia](../packages/svg-viewer-skia-avalonia) — the viewer Studio is built on
- [Svg.PaintCode](../packages/svg-paintcode) — what an import reads
