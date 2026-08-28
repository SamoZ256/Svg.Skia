# Svg.Skia Changelog

## Unreleased

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
