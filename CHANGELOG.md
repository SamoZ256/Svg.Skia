# Svg.Skia Changelog

## Unreleased

* **Renaming a variable carries every use of it, wherever the use is.** Two places a rename went
  round, each of which left a drawing that still parsed and no longer drew. What an element *says*
  was one: `<text>{{ label }}</text>` binds a variable exactly as an attribute does — it has its own
  row in the element panel, and a variable can be dropped onto it — but the walk that carries a
  rename only ever visited attributes and `<e:let>` bodies. The same walk answers "how often is this
  used", so a variable only a text named could also be removed without a word. Both are fixed at the
  walk, so renaming, counting and the removal refusal all learned about it together.

  The other was a group. A group declares for the drawings under it, and the rename stopped at the
  group's own block — so every drawing beneath it went on naming something gone, and not even
  visibly: what gets written into a drawing is narrowed to the names it reaches, so the renamed
  parameter stopped being written in at all. A group's rename now reaches every drawing and every
  nested block beneath it, as one thing to take back. It is worked out before any of it is written,
  so a drawing that cannot be read, or that declares the new name itself, refuses the whole rename
  and names itself while doing it — nothing is left half renamed. A drawing open in a tab with edits
  nobody has saved refuses too, because saving that tab afterwards would write the old name back
  over the rename for that one drawing.

* **An expression variable is a variable like the others.** Its name was a box you typed over, which
  made it the one variable that could not be dragged onto the attribute it should drive — a press
  inside a text box belongs to the caret, so the gesture every other variable has was unreachable on
  exactly the kind most likely to be worth binding. The name is a label now, with the hand cursor and
  the tooltip a value's has, and it carries onto a row the same way; and the row has the `⋯` a value
  row has, opening a form with the name and the body in it. **Add variable… → Expression…** asks for
  the name first, and leaves the row with the caret in its body, which is still where a body is
  written — what it comes to and what is wrong with it are said there as it is typed, rather than in
  one sentence after the fact. A row nobody has written yet has no `⋯`, as it has no grip: there is
  nothing for a form to change that the row is not already holding.

* **Panels split panes instead of taking sides, and the project tree is one of them.** A panel
  dropped along the bottom ran the full width of the window and cut the strip beside the drawing in
  half, with no way to ask for anything else — the foot was a row of the body and the sides were
  columns inside it, so the shape was the model rather than a choice. There are no sides now: a
  panel dropped on the edge of a pane splits that pane, so against the foot of the drawing it sits
  under the drawing and against the edge of the window it runs under everything. Same gesture, two
  targets, no setting — which is how Visual Studio, Rider and Qt Creator all answer it. The project
  tree joins the same arrangement, so it can be folded, carried to the other side, or put behind the
  variables; and the panels belong to the window rather than to a tab, showing whatever is in front,
  so the arrangement holds still while you move between drawings. A strip keeps the width it is given
  and the runs dividing it keep their share of it. A window nobody has arranged comes up with a
  300px strip either side of the drawing — the project tree over the variables on the left, the
  settings and the elements sharing a run over the attributes on the right. **Reset layout** in
  Settings now moves the panels while you are looking at them, rather than waiting for the window to
  close.

* **The panels round a drawing are arranged, and the arrangement is kept.** Four of them shared one
  340-wide column and did not fit: the attributes got 217px of the 1200 they ask for, while the
  element tree — the one panel whose content is elastic — had a fixed 200 and was the best served of
  the four. A splitter could rub a panel out with no way back, and hiding the tree left the 6px
  splitter row under it behind, so every drawing tab paid for a splitter nobody could see. They come
  up two open and two in a strip now — the tree and the project's say sharing the run at the top,
  the variables and the attributes with a run each, weighted 1 : 1.3 : 1.7 so the panel that wants
  most stops getting least. Fold a run to its header with its chevron; take a panel by that header
  and drop it on another to sit behind it, above or below a run, or on an edge of the drawing to
  start a strip down the other side or along its foot, with the landing drawn before you let go.
  One arrangement for every tab, written into the settings file and there again next time, with
  **Reset layout** in Settings for when it has gone somewhere you would rather it had not. The
  viewer's body and the copy Svg.Studio kept for a group's board are one class now, which is how the
  board got all of this without being told about any of it.

* **A variable is dragged onto the attribute it should drive.** Binding one meant reading its name
  off the Variables pane, clicking over to Element, finding the row and typing `{{ name }}` into it
  — carrying the name in your head and spelling it right, with nothing anywhere saying which
  attributes that variable could even go on. The two panes were tabs of one strip, so there was
  never a moment when both ends of the obvious gesture were on screen. They are regions of it now,
  each with a splitter, and only the host's own panes keep a tab strip; a viewer given none has no
  strip at all, which is less chrome than it had. Take a row by its **name** — the grip still
  reorders, and where a let sits decides what it can name — and every row that would take it is
  outlined as you carry it, by the same check typing it would have gone through, so a number never
  lights up a `fill` and nothing lights up on a `transform`, where an expression has to be one
  function argument rather than the whole value. The drop writes the whole value through the same
  commit as typing, refusal and history entry and readout included, and a row under **Not set**
  takes one too. Svg.Studio built that column a second time by hand for a group's board, and the
  two had already drifted — a 220 tall tree against a 200 tall one; there is one of them now, so
  the board got all of this without being told about any of it.

* **A name on a board is shrunk to fit what it names, and drawings are named again.** A group's name
  is written inside its frame and was written whole, so a group called after what it holds ran out
  over whatever was beside it — and the strip the frame is taken hold of went with it. It is now
  sized to the frame, across it and down it, and whole rather than cut short: half a name is no use
  where a set of icons is full of names that differ at the end. On the same rule each drawing is
  named again, in its own top corner: the row's own name, one line, shrunk to the drawing. So a
  name costs nothing — the arrangement and the fit never hear about it, and a board that names what
  is on it is laid out exactly like one that does not, where the writing used to widen every column
  and keep two lines free under every row. On unless you say otherwise, from the board's **Captions**
  toggle or from Settings.

* **The Parameters pane is now Variables, and shows one list.** It had a section per kind because
  the model behind it keeps two lists — a fact about the language rather than anything a reader
  needs. A value and an expression are both variables the drawing names, so they are one list now,
  values first, with one `Add variable…` asking which kind. A run of rows declared in the same place
  wears one heading across both kinds, where two sections said it twice. Dragging still never
  crosses between them: the model has nowhere to write a position that puts a value among the
  expressions.

* **A value bound to an expression is refused rather than ignored.** `SetExpressionValues` resolved
  the parameters into a fresh table and then wrote each let's own result over that name, so a value
  supplied under a let's name was dropped without a word — the caller got back the drawing it
  already had and no reason why. It now says which name it was and what kind of thing that name is.

* **The words of a `<text>` are a row in the Element pane.** They are a child node rather than an
  attribute, so nothing in the editor could reach them, and the one value somebody picked a text
  element for was the one value only a text editor could change. The row is first, above the
  attributes, and takes either the words or the `{{ … }}` that produces them — so binding text to a
  parameter and unbinding it again is one box. `<text>`, `<tspan>` and `<textPath>` only: a `<tref>`
  takes its text from what its href names and is told so, and an element whose text is split across
  child elements says so rather than offering a row that could not write it back without flattening
  the children away.

* **A string expression is described rather than thrown over.** `DescribeUse` named the colour,
  boolean and number uses and threw `NotSupportedException` on the rest, under a comment saying no
  attribute held a string. Four do — `font-family`, `font-weight`, `font-style` and `text-anchor` —
  and the element pane evaluates that call inside an argument list while catching only an
  `ExprException`, so typing `{{ face }}` into the `font-family` row threw out of a keystroke
  handler instead of showing the red line the row exists for.

* **A `<tref>` no longer lifts an expression it cannot use.** Its text comes from whatever its href
  names and the text compiler answers for one before reading any content, so a `{{ … }}` written in
  a `<tref>` was lifted, blanked the element's nodes on the way, and was never evaluated.

* **A row says where it came from even when every row on show came from the same place.** Heading a
  run only when another was beside it made sense while a pane usually held several; narrowing the
  rows to what the selection reaches leaves one run most of the time, so the rule hid the origin
  exactly where it was least obvious — a drawing showing one inherited row looked no different from
  one declaring that row itself. A row with no label is still unheaded, which is what keeps a plain
  viewer plain.

* **A selected group shows what it reaches, not what the board does.** The question "which of the
  names above this are reached" was being asked of every drawing the tab lays out rather than of the
  branch under the selection, so on the project's own tab — where every name is reached by
  something — selecting a group filtered nothing and showed the whole tree.

* **A frame's name is clicked before the drawings under it.** It is drawn over them, so it has to be
  answered for over them, or what is clicked is not what is seen. The strip is only as wide as the
  name: the frame's full width would take the top off every drawing standing under it.

* **A group's name sits inside its frame**, in the top corner, rather than on a line above it. A
  frame is now exactly what its bounds say: nothing is added to it when the view is fitted, and
  `SvgViewerFrame` no longer takes a label size, since there is no room to leave for writing that
  is already inside. The strip the frame is taken hold of by moved down with the name.

  The name is drawn over the drawings rather than with the frame, which is drawn under them. A frame
  is a ground and a name is not: written with the frame it went behind every drawing that reaches
  into the corner it sits in.

* **A board writes nothing under its drawings.** The name and class under each one, and the Captions
  toggle that asked for them, are gone: a board of icons is read as pictures, the tree beside it
  already names every row, and picking one names it above the element tree. The writing that is left
  is a group's name above its frame, which is what that frame is taken hold of and selected by.

  `SvgViewerPlacement` is a picture and a place, `SvgViewerSpread.Item` a picture and a size, and the
  spread no longer measures text to size its columns or keeps two lines of room under a row.
  `GroupPanel.Board` says which drawing each placement came from — a caller used to read that off the
  writing under it, and a picture was never a good place to keep an identity.

* **A group is selected by its name or by the line round it**, and wears the same orange ring a
  picked element does while it is. A board has one selection whether that is a shape, a drawing or a
  group, so the panes follow it: selecting a group makes the tab about that group, showing what it
  inherits and what it declares itself. A click on the board beside everything lets it go, and the
  ring is taken again from where a rebuild has just put the frame.

* **A caption is drawn at one size, whatever the zoom.** A drawing's name and a group's are chrome —
  they name what they sit by rather than being part of what is drawn — and scaling them with the
  drawings left them unreadable zoomed out and enormous zoomed in. The strip a frame is taken hold of
  by went with it, which made the target change size as you approached it. The frame's own outline
  and its dashes were already held at one pixel the same way.

  `LabelSize` on a placement and on a frame is now the room an arrangement leaves for a caption
  rather than what the caption measures, and `SvgViewerCanvas.TitleOf` is where a frame's name lands
  — only something that knows the zoom can say. How big it is drawn is **a setting**, since how big
  is readable is about the screen somebody is at; thirteen pixels to start with rather than eleven,
  a caption on a board sitting against drawings rather than inside a pane.

* **The board can be moved around again.** Three things had made panning a group's tab nearly
  impossible once it held anything: the wheel always zoomed, a press carried whatever was under it,
  and a frame counted as under the pointer anywhere inside it — which on a full board is nearly
  everywhere. The way out was the middle button, and a trackpad has none.

  The wheel now moves the view, and Command — Control elsewhere, as the keyboard's own zoom already
  is — makes it zoom; a trackpad pinch zooms too, the platform reporting that as a gesture of its
  own rather than as a wheel with a modifier held. A frame is taken hold of by the strip its name is written on rather than by
  anywhere inside it, which is what Figma does with a frame and what the name is there for; its
  inside goes back to being canvas. A drawing is still carried from anywhere in its own box, being
  the thing somebody means to move.

  **A move on a board can now be taken back**, which matters because the first one on a board that
  was never arranged writes a place for every row on the tab: one drag nobody meant turned a spread
  into an arrangement, and the project has no undo of its own, so the only way back was by hand.
  Undo and Redo on a group's tab put every place back, not only the one that was dragged.

* **A drawing inherits only the declarations it reaches.** Its own `<e:code>` is its stated API and
  is built in whole; what a group declares above it is ambient, and only the part its expressions
  actually name comes with it — closed over the inherited lets, so a let drags in whatever its body
  needs. Without it every inherited `<e:param>` became an argument of the generated `Draw`, and a
  project declaring forty variables would have given every icon a forty-argument method.

  The names a drawing's own block declares count as reached even where nothing uses them, so a
  drawing redeclaring what its group declares is still refused rather than quietly shadowing it. An
  expression that will not read keeps the whole chain: dropping a declaration that is in fact used
  breaks the drawing, and whatever reads the text next says what is wrong with it far better.

  A drawing that inherits nothing is unaffected, so `svgc` generates exactly what it did.

  The pane reads the same way. A group's tab lists what is declared above the selection only where
  the selection reaches it, since a project's variables are every drawing's to inherit and listing
  all of them listed mostly rows that drive nothing in front of you. What the selection declares
  itself is shown whole, being its own to add to and take away from. What is left out is counted
  under the panel rather than simply missing, because naming a variable in the drawing is what brings
  it back and that is not a thing to guess.

  **Clicking the board beside the drawings lets go of the one being looked at**, which is what makes
  that work: with a drawing selected the group is above it like anything else, so the way back to
  what the group declares is to select none of them. A click that misses the ink *inside* a drawing
  still keeps it — the pane is read alongside the picture, and missing by two pixels should not throw
  away what was being looked at.

* **Converting a PaintCode document places its variables where they are shared.** PaintCode declares
  once for the whole library and the conversion gives every drawing a copy of what it uses; a name
  two drawings in a desk share now goes to that desk's group, one two desks share goes to the
  project, and one drawing's own stays with it. The convert dialog asks, ticked — unticking it puts
  them all on the project instead.

  It is a project operation rather than a PaintCode one: `ProjectPlacement` reads the blocks the
  drawings already carry, so any project that repeats a declaration can be tidied the same way.
  A name two drawings mean differently is left where it is — they are not one declaration, and
  hoisting either would silently change what the other draws — and a hoisted let brings what it
  reads up with it, since the blocks merge outermost first.

  This is also what puts back the family slider a PaintCode project lost when the parameter fan-out
  was scrapped: those identical per-drawing blocks were what used to tie one together.

* **A group in Svg.Studio declares.** A `<group>` — and `<studio>`, which is one — may carry an
  `<e:code>` block, and every drawing under it is built with that block written in front of its own,
  outermost first. One parameter, in one place, driving the family that sits under it.

  It replaces a guess. Since recipes came out of the editor, a group's panel showed whichever drawing
  was picked and fanned a slider out to every sibling whose own declaration looked near enough alike
  — same name, type and bounds. Nothing owned the parameter: a drawing joined the family by being
  typed the same way and left it by gaining a bound, silently both times. Now a drawing inherits or
  it does not, and which is which is a fact about the tree.

  The order is the point of doing it by splicing. `SvgExpressionDeclarations` already merges every
  `<e:code>` in a document in document order and already refuses a name declared twice, so outermost
  first is what the order means and a drawing redeclaring what its group declares is refused rather
  than quietly shadowing it. `SharesValuesWith` is gone with the guess that needed it.

  A group's tab shows the whole chain, each row saying where it came from, and every row is editable
  where it is shown — so is an inherited row on a drawing's own tab, and the edit goes to the group
  that holds it. Removal counts the uses in the branch, not in the block, so taking away a parameter
  three drawings still name is refused instead of breaking all three. What the drawing's own text
  says is untouched throughout: the tab shows, edits and saves the drawing, and the blocks are the
  project's. This is the split `SvgViewer.Rewrite` and `DeclarationTarget` were built for.

  One consequence worth knowing: inherited parameters come first in the merged list, so they come
  first in the generated `Draw`. A group parameter with no default above a drawing parameter that has
  one makes every argument required — the existing optional-args-last rule, reached by a new route.

  The pane shows the whole of what the selection is built with: the chain is the selected thing's own
  ancestry, so picking a drawing two groups down on the project's tab shows the project's block, both
  groups' and the drawing's own, in the order the drawing is built — which is the order its generated
  arguments come out in. The rows are grouped under a heading naming what declares them, and there is
  no heading at all where everything came from one place. A row a drawing declares for itself moves
  that drawing alone: two drawings each declaring a `ring` of their own are two parameters that
  happen to be spelled alike.

  **Add** asks where the parameter goes when there is more than one answer, offering everything from
  the tab's own group down to the selection — the one in the middle is usually the one meant — and
  does not ask when there is only one.

* **An Edit mode**, on the viewer and on a group's tab in Svg.Studio. Turn it on and the picked
  element is drawn with handles: drag the body to move it, a handle to scale it, the stalk above to
  turn it. What a drag comes to is written into that element's own `transform` attribute as one undo
  step, and a press that never travels writes nothing at all.

  It is a mode rather than a gesture because a left drag already pans and the two cannot share the
  button. With the mode off, every press is exactly the press it was. With it on, the canvas offers
  the element first and anything the element does not claim falls through, so the handles are always
  reachable — and on a board, where a press anywhere inside a drawing would otherwise pick the whole
  drawing up, the toggle takes the board out of reach for as long as it is on, so one toggle means
  one thing.

  `SvgViewerGizmo` holds the arithmetic and nothing else: it draws nothing and writes nothing,
  composing every gesture in the element's own geometry space through the inverse of its total
  transform, captured once at the press. An element whose transform is written by an expression is
  refused rather than silently flattened.

* **A group's board in Svg.Studio holds still while it is edited.** A tab was fitted afresh on
  almost every change -- a drawing dropped somewhere, a parameter added, an attribute typed, a
  glance at another tab -- so zooming in on one icon of forty to line it up with another lasted
  exactly until the drop. A board is fitted when it first opens and never again on its own; **Fit**
  is what asks for the whole of it back, and also what to press when a row with no place lands
  outside a zoomed view.

  The ring, the row in the element tree and the Element tab stay on the drawing that moved, where a
  rebuild used to empty all three -- on a board that happened every time you dragged anything, which
  is how a board is arranged at all.

  Underneath, a tab reads a drawing again only when something the build reads has changed: its text,
  or the size its groups ask for. So arranging a board rebuilds nothing, editing one drawing rebuilds
  that one, and a tab keeps what it built when it is not the one on screen -- a tab switch, or a tab
  dragged along the strip, used to re-parse the whole group. Every save also refreshed every open
  board twice, through two subscribers to one event.

* `Svg.Viewer.Skia.Avalonia`'s canvas gained **`Rearrange`**, which lays the same set of drawings out
  again without re-fitting -- what `Replace` is for one drawing, for several. It holds the view
  whether or not anybody has zoomed, because a board's neighbours must not move when one of them
  does; `Show` goes on meaning a new arrangement has arrived, and fits it.

  The view is now anchored in the arrangement's own coordinates rather than on the top left of what
  is placed, which is what makes that possible: an arrangement that grows to the left used to move
  its own anchor and slide every drawing that had not moved. `Canvas.OffsetX` and `OffsetY` therefore
  read differently for an arrangement that does not begin at the origin -- they say where arranged
  `(0,0)` goes, not where the ink starts.

* Every item of a group in Svg.Studio can say where it sits: **`x` and `y`** on a `<drawing>` and on
  a `<group>`, relative to the board the group holds. A group's tab lays its rows out there instead
  of in the near-square grid the count decided, and a group with a place of its own is drawn as a
  labelled frame round what it holds and carried as a unit — its children are written against it, so
  moving a group is one attribute.

  A group nobody has arranged is that grid still. The first drag settles the whole tab at the
  coordinates the grid had just given it, so nothing jumps under the hand and what appears is a frame
  round each group. Rows with no place — added, pasted, or copied — wait in a grid beside the
  arrangement. A row dragged into another group keeps its place, written in the coordinates of the
  board it arrives on, so it stays where it was rather than jumping into that queue; where those
  coordinates would say nothing — a board that is still a grid, or a group between the two that names
  no place of its own — it joins the queue with the rest.

  It is layout and nothing else: the build sees exactly what it saw, which the tests assert by
  flattening one project with places and one without and comparing what comes out. Two things it is
  deliberately not. It does not inherit, since a group's place is in its parent's coordinates and
  offering that as a drawing's own default would be a number about somewhere else. And it is not part
  of the size trio, or a drawing would become its own size owner and stop inheriting the scale its
  group builds it at — which would show up only as every icon dropping to its natural size the moment
  somebody moved one.

* `Svg.Viewer.Skia.Avalonia`'s canvas can be taken hold of. `Show` takes `SvgViewerFrame`s beside the
  placements — named rectangles drawn under the drawings, for a host that wants to show what belongs
  to what — and `Grip` gives a host first refusal on a press, which until now the pan claimed before
  anything knew what was under the pointer. What is carried is drawn where the pointer has it while
  everything else holds still, and `Moved` is a request rather than a change: the canvas commits
  nothing, so a host that does nothing about it has refused. Unset, a press pans exactly as it did.

* `src/Svg.Studio` has a project format of its own: **`.svgstudio`**, one XML file holding the
  settings, the tree and the drawings themselves. A project is a thing you can move, diff or hand to
  somebody, rather than a file plus a scattering of `.svg` files around it that have to travel with
  it.

  It replaces `.svgcproj` **in the editor only**. `svgc --projectFile`, the
  `Svg.CodeGen.Skia.Projects` package and the PaintCode library go on reading and writing the svgc
  format, and Studio builds through the same `SvgcProjectBuild` the tool runs — a build item can now
  carry its drawing rather than point at one, which is all that took.

  Opening an `.svgcproj` converts it: the drawings are read in, written beside the original as
  `.svgstudio`, and what the conversion cost is said once. It is one way and deliberately lossy in
  two places. A **recipe is baked into the drawings it painted**, because the parameters it declared
  were what drove them and a conversion that dropped them would hand back a set of flat pictures;
  and a file the old project named twice becomes two drawings, which no longer edit each other.
  Importing a PaintCode document writes one file rather than a folder of drawings and a project
  naming them.

  Every row of a project is named now — `name` on a `<drawing>` and on a `<group>` — and that is what
  the tree, the tab and a drawing's fallback class read. The old format had nothing to tell one group
  from another, so a row was labelled by the settings it handed down: two groups beside each other
  could be named off different attributes, and one that set neither read "group".

  The file is held as an `SvgSourceDocument` rather than as a plain tree, which is the whole reason
  it is not the svgc document with different element names. That type re-serialises, and
  re-serialising somebody's drawing reformats it — measured over the 2,988 drawings in the two
  suites, writing a tree back the ordinary way returned 28 of them unchanged. What a `.svgstudio`
  reads it writes, drawings included.

* **Recipes are out of Svg.Studio.** A recipe existed so a family of drawings could be parameterised
  without editing them; the project holds the drawings now, so an edit can go where the value is.
  The recipe tab, the Replacements pane, `Apply…` and the `recipe` setting are gone from the editor,
  and a drawing's declarations are written where its elements already were.

  `svgc` keeps them: `-r`, `--emit svg`, `Svg.Expressions.Recipes` and the demo are untouched. A
  slider on a group's tab still moves every drawing that declares the same parameters, which is what
  that panel always read — a recipe was one way of making them declare it, and writing the block in
  each drawing is another.

* A drawing that declares a parameter with no default after one that has a default now generates,
  rather than being refused. C# takes optional arguments last, so such a document cannot keep both
  its order and its defaults — it keeps the order and gives up the defaults: every argument is
  generated as required.

  The order is the half worth keeping. It is what a positional call means and what a reader matches
  against the `<e:param>` block, and losing a default is a compile error at the call site, where
  whoever has to act can see it. **Reordering** the parameters so the required ones come first was
  the alternative and would also always compile, but a positional call pairing two same-typed
  arguments the wrong way round fails silently — and `SkiaCSharpRenderTests` binds its arguments
  positionally for the same reason a caller does. **Inventing** defaults for the required parameters
  was rejected too: `ExprEvaluator` throws on a missing value, so the generated API would have
  accepted an omission the interpreter refuses, and a parameter deliberately left required would have
  quietly become grey.

  It applies only where the conflict exists, so nothing that compiles today changes — the previous
  rule permitted no other order.

  The subtle part is a colour default, which is normally emitted as `SKColor?` and coalesced into a
  local because `new SKColor(…)` cannot be an argument default (CS1736). Once the colour is required
  that local has nothing to coalesce, and the body has to name the parameter instead. Getting one of
  those two wrong is CS0019 or CS0103 **in the generated file**, so the decision is made once, by
  `EmitsDefaultArguments`, and read by the signature and the colour locals together — `Resolve`
  follows for free, since it already asks the same question. A case in `SkiaCSharpRenderTests` covers
  it: that harness compiles the generated code and diffs it against the runtime renderer at a zero
  threshold, which is the only thing that would catch a body reading the right name and the wrong
  value.

  Said in three places: a comment above the generated class, a `warning:` line from `svgc`, and
  **SVG0002** at warning severity from `Svg.SourceGenerator.Skia` — which runs inside the compiler
  and has nowhere to print, so without a diagnostic its generated API would have started requiring
  arguments in silence.

* `svgc` can leave room around a drawing: `--padding`, and `<padding>` in a project file.

  It pads **inside** the size asked for. `--width 512 --padding 10%` gives a 512×512 picture whose
  art occupies the middle 410×410, so `--width` goes on describing the file you get rather than the
  art inside it. Values are fractions of that target — `10%` and `0.1` are the same thing — which is
  what lets one setting serve a batch generated at several sizes. A bare `10` is read as the fraction
  and refused, rather than quietly taken for ten percent.

  Sides are written the CSS way, one to four values, because that is the order anyone writing four
  numbers for four sides already has in mind.

  **It never crops.** The space goes outside the frame the document declares, so a drawing whose
  author already left it room keeps that room and gets more. That falls out of what the resize
  already did: it measures the document's own `width`/`height`, then its `viewBox`, and only looks at
  what is actually drawn when the document declares neither — the one case where there is no authored
  padding to lose.

  Padding cannot be asked of `preserveAspectRatio`, which has nine alignments and no offsets, so it
  is written as a viewBox whose aspect matches the viewport — which makes the fit exact and leaves
  `preserveAspectRatio` nothing to do. With every side zero that reduces to what `xMidYMid meet`
  already produces, but the shorter path is kept for the unpadded case regardless: there is no reason
  to move generated output for drawings nobody asked to pad. Where a drawing's shape and the size
  asked for disagree, the leftover centres as it always did, so a side can end up with more clear
  space than it asked for and never less.

  Unlike `--width`/`--height`/`--scale`, which replace one another as a group, padding overlays on
  its own — it says how much room to leave rather than what size to be, so an item naming it keeps
  the project's sizing. It is refused alongside `--emit svg` for the reason a resize is: that
  conversion rewrites the document's text and never compiles it, so either would be silently lost.

* Fixed a let edit being written to the drawing twice. Committing a row with `Enter` and then
  clicking away reported either **"This drawing declares no let called 'deep'."** or **"'deeper' is
  declared more than once."** — one message for a rename, the other for a new let, both from the same
  mistake.

  A row goes on calling itself modified until the rebuild its own edit caused replaces it, and the
  box it was in leaving the tree *is* a focus loss. So the row settled a second time and asked the
  document for an edit it had already taken: a rename of a name that had just been renamed away, or
  a declaration of a name that had just been declared. The panel now remembers the last edit it
  handed over and does not hand the same one over again.

* Every box that holds an expression is syntax-coloured as it is typed — a let's body, and a
  parameter's `default`, `min`, `max` and `step` — from the same table the source pane paints with,
  so `tau` cannot be one colour in the pane and another in the row above it. Not the name boxes: a
  name is an identifier, and colouring it would say it was an expression.

  **It is still a `TextBox`.** A text box paints with one foreground and the only thing that can give
  it more is whatever builds its layout, so `SvgExpressionPresenter` replaces that and nothing else;
  the caret, the selection, composition, the clipboard and undo stay Avalonia's. A control theme puts
  it in place per box, applied with `Theme=`, so nothing global changes and no upstream template is
  copied — `TextBox` requires exactly one part, `PART_TextPresenter`, which is what makes a ~25-line
  template of our own enough.

  The presenter passes an **unbounded** width to the layout rather than shadowing the private
  constraint two layout passes maintain upstream. That field was the one genuinely fragile part of
  this approach, and for a one-line box that neither wraps nor aligns the width changes nothing — so
  it is designed out rather than reproduced. What is reproduced is composition: an input method shows
  what is being typed before the box has it, and laying out the committed text alone would drop it.

  **Selected text keeps its colours.** Avalonia's own presenter repaints a selection in a single
  brush; this one does not, because the source pane does not either — AvaloniaEdit's theme sets the
  selection background and leaves its foreground commented out. Two panes showing one expression
  should not disagree about what colour it is. There is a test for that, since it is the kind of
  difference somebody later fixes by accident.

  The palette moved out of `SvgViewer`'s own resources into a dictionary of its own, which the theme
  carries. Declared inside the control, it was unreachable from anything shown in a window of its
  own — so the parameter form's boxes resolved every brush to null and painted flat while the pane
  beside them coloured the same text.

  `Svg.Highlighting` gained `SvgSourceHighlighter.Expression`, which splits one expression with no
  document around it. Everything it guarantees comes free from the existing splitter: a body that
  will not lex is coloured as far as the language got, and entities decode before lexing, so
  `a &lt; b` colours as the comparison it is.

* Parameters reorder by drag too, with the same grip a let has, and into **any** order.

  Unlike a let, whose position is what it can name, a parameter's position is presentation: a default
  may not name another parameter, so every order renders the same picture. The C# generator does want
  one — its signature is written in declaration order, so the parameters with defaults have to come
  last — and it refuses a document that puts them otherwise, reported by `svgc` as an `error:` line
  when somebody runs it. That stays a restriction of that back end. A drawing is not stopped from
  saying what it means because one of the things that reads it would rather it said it differently.

  The drag itself is now written once for both lists rather than twice, which was the point at which
  it had to be: it carries four details that were each found the hard way — capture the list and not
  the row, swap at a neighbour's midpoint, treat a release nobody saw as an end, and lay out before
  placing the carried row. A second copy would have been a second place for those to be forgotten.

  Dropping now asks rather than tells: the panel hands the move to the document and puts the row back
  if it is declined. The window keeps a drag inside what is legal, so a refusal means the splice
  declined for its own reasons — a list left showing an order the drawing does not have is worse than
  a drag that does not land. That was a real hole in the let drag as well, where a refused move left
  the rows reordered against a file that was not.

* Added removing a let — `SvgDeclarationEditor.RemoveLet`, and a `✕` on its row — on the same terms
  as a parameter: refused while anything still names it, with the uses counted. The rule is sharper
  here, since being named is the whole of what a let is for, so one nothing names is the only kind
  there is any sense in taking away.

  `Remove` became the same method with the element name passed in, now that there is a second caller
  to justify one. A row nobody has typed into yet never reaches the editor at all — the panel drops
  it, because there is nothing in the document to take out.

* Added removing a parameter — `SvgDeclarationEditor.Remove`, and a `✕` beside each row's `⋯` in the
  viewer's panel.

  It is **refused while anything still names it**, with a count of the uses. Removing a used
  declaration leaves a document that parses perfectly and draws nothing, which is the one outcome
  this splicing exists to prevent; and a count is what separates a button that did nothing from one
  that did something unintended. Removing and then reporting the breakage was rejected for the same
  reason `Open` refuses a document whose declarations are already wrong: the pane would fill with
  errors about a drawing the application had just broken itself.

  The uses are the ones renaming rewrites, so `SvgDeclarationReferences.Rename` was widened into
  `Uses` and now consumes what it finds rather than owning the walk — one answer to "where is this
  named", found by lexing every `{{ … }}` and every `<e:let>` body. A `default`, `min`, `max` or
  `step` is not searched, because the language puts nothing the document declares in scope there, so
  a name in one is a different name; a test pins that.

  The declaration goes with the line it sat on, reusing what reordering already needed. The
  `<e:code>` block stays even when it empties: taking it away is a second decision — about a `<defs>`
  that may hold other things, and an `xmlns` nothing declares any more — and adding a parameter
  writes into the block that is already there.

* Fixed a source view reporting an error against text nobody typed. An expression reaches a file
  XML-escaped — a let holding `a < b` can only be written `a &lt; b`, since a bare `<` opens a tag —
  and the highlighter lexed that span raw, stopped at the ampersand, and reported **"Expected
  `&&`"**. Every `&lt;`, `&gt;` and `&amp;` in a let body, a `{{ … }}` placeholder or a declaration's
  `default`/`min`/`max`/`step` was marked as broken, and the underline sat on the entity rather than
  on anything wrong.

  Older than the GUI editing that surfaced it, but that is what made it routine: writing `<` from a
  row *must* produce `&lt;`, so the pane reliably painted an error on text the application had just
  written itself.

  Each span is now decoded before it is lexed, keeping a map from every decoded character back to
  where it was written — so a rule that reports where it stopped is still marked on the characters
  somebody typed rather than four columns to their left. That decoding already existed as
  `SvgDeclarationReferences.Decode`, which renaming needs for the same reason; it moved to
  `Svg.Expressions.ExprText`, the one package both this and `Svg.SourceEditing` can see, rather than
  being written twice.

* Gave the viewer's panel the other half of an `<e:code>` block: a **Lets** section beside the
  parameters, where a let is declared, renamed, rewritten and reordered without opening the source.
  `Svg.SourceEditing` gained `AddLet`, `UpdateLet` and `MoveLet` for it, and
  `SvgViewerParameterPanel` became `SvgViewerDeclarationPanel` — it no longer holds only parameters.

  **A let has no form.** It is a name and an expression, so the row is the editor: `Add let…` leaves
  an empty row to type into, `Enter` or leaving it writes it, `Escape` puts it back. A modal would
  have held the same two boxes the row already has. What is typed is checked against the parameters
  and the lets above it *as it is typed*, and nothing is spliced until it checks — a half-typed body
  written into the drawing would stop it rendering, in the pane right beside the row. Beside each row
  is what the let evaluates to now, which is the thing a source view cannot show and the reason to
  have the section at all; it is read by evaluating the let's own name against the map
  `ExprEvaluator.Create` has already filled, so the lets are folded once rather than once per row.

  **Where a let sits is what it can name**, since one resolves against what is declared above it and
  nothing below. That made reordering a change of meaning, and exposed a hole: `Verify` re-read the
  document after every splice but only *parsed* it, and parsing records a let without checking its
  body — so a let dragged above the one it names read back perfectly and rendered as nothing. Every
  edit now folds the symbol table and type checks each body in order. Only a let the edit is
  answerable for: the document as it was is checked too, and a body that named nothing before and
  still names nothing is not the edit's fault — refusing on that would make a parameter uneditable in
  a drawing somebody is part-way through fixing. The original is re-read only once something failed,
  so an ordinary splice pays nothing for it.

  A drag is then held inside the positions that still check, rather than refused on the drop: a
  refused drop reads as the drag having failed. The window is contiguous — moving up is legal until
  the let passes what it names, and down until it passes what names it — so it is found by trying
  each candidate order in memory. `MoveLet` refuses anyway, as the backstop that does not depend on
  the panel getting it right.

  The reordering splice moves the let's **whole line as it was written** rather than re-rendering it,
  and refuses a let sharing its line with something else instead of cutting it out of one. A body is
  written with only `&` and `<` escaped, not the four an attribute needs: somebody who types
  `t > 0.5` should see `t > 0.5` in the pane.

  Reused rather than rebuilt: renaming carries the uses through `SvgDeclarationReferences.Rename`
  unchanged — it already walked `<e:let>` bodies as well as placeholders, by lexing rather than
  searching — and `AppendToBlock`, `CreateBlock`, `Render` and the `Builder` seeding were widened to
  serve both kinds instead of gaining copies. The drag follows what the shell's tab strip already
  solved (`MainWindow.axaml.cs`): capture the container and not the row, swap at a neighbour's
  midpoint, treat a release nobody saw as an end. The three `ToExpression()` overrides collapsed into
  one `SvgViewerParameterFactory.Describe`, so a readout and a committed default cannot disagree.
  `SvgViewerDocument.Declarations` widened from the parameter list to the whole
  `SvgExpressionDeclarations`, and `SKSvg` gained `ExpressionDeclarations` with `ExpressionParameters`
  now a projection of it.

  **Removing a let is not here yet**, and no `RemoveLet` was written for a caller that does not exist.
  Neither is a node editor: what it would graph is a one-line arithmetic expression, where text is
  already the better notation, and a node dropped but not wired has no text representation at all —
  which a pane showing the drawing's own source makes visible immediately.

* Added `Svg.SourceEditing`, which changes what an SVG document declares by replacing spans of the
  document's own text, and used it to give `Svg.Viewer.Skia.Avalonia` two edits: `AddParameterAsync`
  declares a parameter from a form in the panel, `EditParameterAsync` changes what one says, and
  `CommitParameterDefaults` writes the values somebody chose into the drawing as its declared
  defaults. Moving a control stays a preview.

  Renaming carries the uses with it: the identifier in every `{{ … }}` and every `<e:let>` body
  moves with the declaration, found by lexing each site rather than by searching the file's
  characters — an expression reaches the file XML-escaped, and `amp` is a name the language
  allows, so a search would rename the inside of `&amp;`. A type cannot be changed: every
  expression naming a parameter was checked against the type it had.

  The alternative was to parse the drawing, change the tree and write it back, and that was measured
  rather than assumed: a round trip through `SvgDocument` and `Write` renders identically — `#3c83f5`
  before and after with `hue = 217`, because foreign attributes are keyed by namespace URI and read
  back into the key the pipeline uses — but **deletes every comment**, since the reader's node switch
  has no case for them, turns `fill="{{ primary }}"` into `style="fill:gray;"` plus `e:fill`, and
  adds a doctype, `version`, `xmlns:xlink` and `xmlns:xml`. Reformatting somebody's whole file to add
  one parameter is not something a pane showing them that file can do. So an edit is a splice, and
  everything outside it is untouched byte for byte.

  Spans rather than a rewritten document is also what makes undo work: assigning a text editor's
  buffer wholesale resets the caret, the scroll and the undo stack, while a span goes through the
  editor's own replace. An addition that had to declare a namespace and open an `<e:code>` block is
  three spans and one undo step, and it lands on the same stack as the lines typed into the pane.

  Nothing in the new package decides what is legal: a proposed declaration goes through
  `SvgExpressionDeclarations.Builder`, the rules both readers already enforce, and the result is read
  back with `Parse` before it is handed over — so a splice that would leave the document saying
  something other than what was asked for is refused rather than applied. A document that is not
  well-formed yet, or whose declarations are already wrong, is refused with a reason rather than
  guessed at.

  Where a new block goes and which prefix it takes are `Svg.Expressions.Recipes`' existing answers,
  now shared rather than copied: first inside `<defs>`, creating one if absent, and whatever prefix
  the document already binds the extension to.

  The viewer's source buffer and its source pane are now separate. `IsSourceModified` and
  `SaveSourceAsync` used to require the pane to have been opened, which would have made an edit from
  the panel unsaveable; they now ask whether the editor holds the drawing, which an edit makes true
  on its own. A drawing nobody opens the pane for or edits still costs nothing.

* Added `Svg.Viewer.Skia.Avalonia`, a reusable Avalonia viewer for drawings using the expression
  extension, with `src/Svg.Studio` as the application built on it. It opens a file by picker or
  drop, zooms and pans — wheel about the cursor, drag, and fit / 1:1 / reset with a percentage
  readout — and builds a control per declared parameter: a slider honouring any `min`/`max`/`step`
  for a `number`, a colour picker for a `color`, a checkbox for a `boolean`, each seeded by
  *evaluating* the declared `default` rather than parsing it, so `default="tau / 4"` works. Nothing
  blanks the drawing: a failed load keeps the previous document, a malformed `<e:code>` block is
  reported but still renders its placeholders, and a rejected value leaves the last good rendering in
  place. It draws onto `SKCanvasControl` and owns its transform rather than using the
  `Avalonia.Svg.Skia.Svg` control, which sizes itself to the drawing it fits — a 100x100 document in
  a 400x200 pane arranges at 200x200 — and so cannot fill a viewport.

  Opening through the **file picker** currently crashes on macOS with Avalonia 12.0.0, inside the
  native storage provider as the panel is dismissed. `samples/TestApp` crashes there identically, so
  the fault is upstream rather than in this package, and it reproduces in a bare Avalonia app. The
  workaround is `AppBuilder.UseManagedSystemDialogs()`, Avalonia's own managed picker, which
  `src/Svg.Studio` applies on macOS; dropping a file on the viewer or handing a path to
  `LoadAsync` avoids the picker entirely.

  Zooming is on the scroll wheel and on `Ctrl`/`Cmd` `+`/`-`/`0`/`1` as well as the toolbar. A
  trackpad two finger scroll arrives as a wheel event with a fractional delta and so zooms smoothly
  on the same curve a mouse notch steps along. A trackpad *pinch* is a separate platform gesture that
  Avalonia 12.0.0 raises only through its internal `Gestures` class, so it cannot be subscribed to
  from outside the framework yet.

* `<e:param>` now takes optional `min`, `max` and `step` attributes describing the range a host
  should offer for a `number` — the ends of a slider and its increment. Each is an expression like
  `default` is, so `max="tau"` and `step="1/60"` work, and each resolves against nothing at all, so a
  bound cannot reference another parameter. `min` and `max` come as a pair; `step` may stand alone
  against the 0..1 a parameter has when it declares none. `SvgExpressionParameter` grows
  `MinExpression`, `MaxExpression`, `StepExpression`, `HasRange` and `ResolveRange()`, the last of
  which is total and returns that 0..1 fallback — exactly the range hosts hardcoded before the format
  could express anything else. The range is advice to a host and never a constraint: nothing clamps,
  a `default` outside its own range is legal, and **generated code is unchanged**, since the code
  generator has no use for it. Whether a range is structurally allowed is settled while the
  declarations are read, so a range on a colour is caught immediately; whether the numbers make sense
  is settled by `ResolveRange()`, because reading a document must not evaluate anything.

* A `color` parameter may now declare a `default`. It could not before, because `new SKColor(...)`
  is not a C# compile-time constant (CS1736) — a limit of the target language that had leaked into
  the format, since the runtime evaluator always handled such a default without a special case. A
  colour parameter carrying one is now generated as `SKColor? tint = null` and coalesced to the
  declared default inside the method, so omitting the argument or passing `null` gives that default.
  A colour parameter *without* a default is generated exactly as before, so no existing signature
  or generated file changes.

* Fixed generated code converting an expression gradient stop to `SKColorF` differently from the
  rest of the library. `SvgToColorF` divided each channel by `255f` while `ShimSkiaSharp.SKColor`
  multiplies by `1 / 255.0f`, and the two disagree for 126 of the 256 byte values — enough for a
  gradient to differ by one level on a pixel. Generated code was the inconsistent side: a *literal*
  stop is emitted as the floats the model already converted by the reciprocal, so it disagreed with
  its own literal stops as well as with the runtime. **Generated output changes** for documents with
  expression gradient stops: the body of the `SvgToColorF` helper, and nothing else.

* `samples/SvgExpressionsDemo` and `samples/SvgRecipeDemo` no longer generate and compile C# to
  render. Both evaluate the scene model directly, so neither references Roslyn any more, and neither
  ships `Microsoft.CodeAnalysis.dll`. A parameter change now re-evaluates rather than re-parsing,
  re-generating and re-compiling into a fresh collectible `AssemblyLoadContext`. The demos no longer
  display the generated C#; `svgc` remains the way to see that.

* Added expression support to `SKSvg`: `ExpressionParameters` reports what a document declares,
  `SetExpressionValues` binds values and re-renders, `ExpressionValues` reports what is bound, and
  `ClearExpressionValues` goes back to the design-time placeholders. Loading is unchanged — it renders
  the placeholders and does not evaluate, so a document whose parameters have no defaults still loads
  and no existing use of `SKSvg` is affected. Supplying values is strict: a parameter with neither a
  value nor a `default` is an error, matching the generated code, and nothing is applied unless the
  whole set resolves. Re-evaluating does not re-parse the document or recompile the scene.

* Fixed `NonSvgElement.DeepCopy` losing the element's namespace. The copy kept its name and
  attributes but claimed to be in the SVG namespace, so anything matching a foreign element on name
  *and* namespace silently stopped matching — found when a cloned document reported no `<e:code>`
  declarations.

* Added `SvgDocument.ExpressionDeclarations`, which reads a document's `<e:code>` block from the
  parsed tree rather than from source text. `Load(XmlReader)` and a document handed over directly
  never had text to re-parse, so this is what lets any route into a document be evaluated.
  `SvgExpressionDeclarations.Parse` is unchanged and still what `svgc` and the source generator use;
  both now go through the new `SvgExpressionDeclarations.Builder` so they validate identically.

* Added `SvgSceneExpressionEvaluator.Evaluate` in `Svg.SceneGraph`, which turns a picture holding
  expressions into one holding values. It rewrites the model rather than changing any renderer, so
  `SkiaModel` and the Avalonia controls draw an evaluated drawing with no changes of their own.
  Nothing is mutated and untouched subtrees are returned as the same instances, so re-evaluating with
  new values costs one walk of the parts that carry expressions.

* Added a runtime evaluator for the SVG expression language: `ExprEvaluator` and `ExprValue` in
  `Svg.Expressions` compute an expression against values instead of rendering it as C#, so a
  renderer can show real values rather than the design-time placeholder. `ExprEvaluator.Create`
  binds values to a document's `<e:code>` declarations and resolves its lets; a parameter with
  neither a supplied value nor a `default` is an error, which is the rule generated code already
  enforces.

* `Svg.Expressions` now targets `netstandard2.0;net6.0;net8.0;net10.0` rather than netstandard2.0
  alone. Generated code calls `MathF`, which arrived with netstandard2.1, so the evaluator has to as
  well to give the same answer; the netstandard2.0 build falls back to the double-precision
  functions and differs by at most one ulp for `sin`, `cos`, `tan` and `pow`.

* **Breaking:** the SVG expression language's lexer, parser and type checker moved to a new
  `Svg.Expressions` package, and `ExprType` and `ExprException` moved with them from namespace
  `Svg.CodeGen.Skia.Expressions` to `Svg.Expressions`. Source-compatible after updating a `using`;
  not binary-compatible, and a type forwarder cannot bridge a namespace change. `ExprCompiler`
  stays where it was, as a facade over the checker and the C# back end. `ExprCompiler.FunctionNames`
  and `ConstantNames` are now `ExprFunctions.FunctionNames` and `ExprFunctions.ConstantNames`.

* **Breaking:** the `<e:code>` declarations moved to `Svg.Expressions` and were renamed —
  `SvgCodeDeclarations`, `SvgCodeParameter` and `SvgCodeLet` are now `SvgExpressionDeclarations`,
  `SvgExpressionParameter` and `SvgExpressionLet`. They are the symbol table the expression
  language is checked against, so they belong beside it rather than in the code generator, which
  is no longer the only back end that reads them. The two members that produce C# stayed behind as
  extension methods in `Svg.CodeGen.Skia`: `Resolve()` is unchanged, and
  `declarations.DefaultCodeFor(parameter)` is now `parameter.DefaultCode()`. A `color` parameter
  carrying a `default` is now rejected by `Parse` rather than when C# is emitted, so the same
  document is accepted or refused identically whichever back end reads it.

* Added SVG 1.1 animation object-model coverage in `Svg.Custom` for `animate`, `set`, `animateMotion`, `animateColor`, `animateTransform`, and `mpath`.
* Added typed `pointer-events` support, geometry-aware hit testing, topmost-element targeting, and routed interaction dispatch with capture, tunnel, bubble, and cursor resolution.
* Added shared animation playback in `SKSvg`, including animation time control, invalidation events, layered redraw, throttling helpers, and native-composition scene extraction.
* Added host animation backends for Avalonia and Uno, including resolved-backend diagnostics and Avalonia retained `NativeComposition` playback with fallback.
* Added an animation benchmark harness in `tests/Svg.Skia.Benchmarks` and exposed animation/backend controls in `samples/TestApp`.
* Updated HarfBuzzSharp dependencies to `8.3.1.3` so Android consumers restore native assets with 16 KB page-size support.

## 0.3.0

* Updated NuGet packages.
* Update SVG sources.

## 0.2.0

* Updated NuGet packages.

## 0.1.9

* Updated NuGet packages.

## 0.1.8

* Added fixes for Xamarin.Forms Android/iOS.

## 0.1.7

* Strong name signed assemblies.

## 0.1.6

* Fixed `marker` exception.
* Fixed `use` to accept `svg` element.
* Added native build support using CoreRT.
* Added referenced properties support for `filter` element.
* Added `feImage` referenced image `preserveAspectRatio` support.
* Improved `Filter Effects` validation.
* Fixed `fill` and `stroke` validation.
* Added `SKFontManager` typeface provider.
* Added custom font loader helper class `CustomTypefaceProvider`.

## 0.1.5

* Fixed `systemLanguage` validation.
* Removed debug code.

## 0.1.4

* Added `switch` element support.
* Added `systemLanguage` attribute support.

## 0.1.3

* Updated `Svg.Skia.Converter` tool.
* Use `Svg.Custom` build of the `Svg` library.
* Initial support for new `Filter Effects`.

## 0.1.2

* Added referenced properties support for `linearGradient` element.
* Added referenced properties support for `radialGradient` element.
* Changed bitmap creation to use `SKImageInfo`.

## 0.1.1

* Added `Overflow` property to `Drawable`.
* Added `FilterQuality=SKFilterQuality.High` for `ImageDrawable`.
* Added transform support for `image` `svg` fragment.
* Added support for embeded `svgz` images.

## 0.1.0

* Added `Svg.Custom` project for `Svg` library.
* Refactored utility classes.
* Added custom font support via `ITypefaceProvider`.

## 0.0.12

* Fixed deffered `stop` color paint server.
* Fixed invalid `SvgUnit` default value handling.
* Added `Filer Effects` utility class.

## 0.0.11

* Fixed `mask` processing.
* Updaed `feColorMatrix` filter processing.

## 0.0.10

* Added new `Filter Effects` support.
* Added `mask` element support.
* Fixed `clipPath` element processing.

## 0.0.9

* Added `Filer Effects` prcessing.

## 0.0.8

* Fixed `stoke` and `file` validation.
* Refactored utility classes.
* Added generic referenced element support.

## 0.0.7

* Added `Xamarin.Forms` sample application.
* Initial `IImage` implemetation for `Avalonia`.
* Fixed `rect` attributes validation.

## 0.0.6

* Made `Drawable` classes public.
* Added initial `HitTest` implemetation for `Drawable`.

## 0.0.5

* Removed `SKSvgRenderer` implemetation.
* Added `Drawable` object model.

## 0.0.4

* Refactored `SKSvgRenderer` class.

## 0.0.3

* Added `marker` element support.

## 0.0.2

* Added `pattern` element support.
* Added `image` element support.

## 0.0.1

* Initial release.
