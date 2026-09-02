# Svg.Viewer.Skia.Avalonia

A reusable Avalonia SVG viewer for the SVG expression extension: open a drawing, zoom and pan it,
and drive the parameters it declares from controls built to match.

```xml
<viewer:SvgViewer x:Name="Viewer" />
```

```csharp
await Viewer.LoadAsync("badge.svg");
```

`src/Svg.Studio` is the whole application built on it: the shell owns the tabs, and each tab is one
of these controls.

## What it does

- **Opens** a file through a drop, or through `OpenAsync()` when the host asks — which is the
  platform picker. Off the UI thread, keeping whatever is on screen if the load fails.
- **Zooms and pans**, with a percentage readout. Resizing keeps the drawing fitted until the view is
  adjusted by hand, after which it is left where it was.
- **Builds a control per parameter** — a slider and a value box for a `number`, honouring any
  `min`, `max` and `step` the document declares, a `ColorPicker` for a `color`, a checkbox for a
  `boolean` — seeded from each declared `default`.
- **Resizes the drawing**, and leaves room around it, by rewriting the `width`, `height` and
  `viewBox` its root element declares — the same arithmetic svgc resizes by, written back as a text
  edit, so the pane shows it and an undo takes it back.
- **Declares a parameter**, from a form in the panel, by writing it into the drawing's own text.
- **Declares and rewrites a let**, from a row edited in place, showing what each one evaluates to and
  taking a drag to reorder them.
- **Commits values as defaults**, so a session of moving sliders becomes what the document says.

## Input

| | |
|---|---|
| Zoom | Scroll wheel, or a trackpad two finger scroll, both anchored on the pointer |
| | `Ctrl`/`Cmd` `+` and `-` |
| | The toolbar's `+` and `−` |
| Pan | Drag with the left or middle button |
| Fit | `Ctrl`/`Cmd` `0`, or the toolbar |
| Actual size | `Ctrl`/`Cmd` `1`, or the toolbar |
| Undo, in the source pane | Whatever the platform calls it — `Ctrl`/`Cmd` `Z` |
| Redo, in the source pane | `Ctrl`/`Cmd` `Shift` `Z`, or `Ctrl`/`Cmd` `Y` |

AvaloniaEdit binds the undo and redo *commands* and no keys to them, so the pane binds the
platform's own gestures to them as it is attached; a host embedding `SvgViewer` gets them with it.
They reach the pane only while the caret is in it, so a parameter box keeps its own.

A trackpad two finger scroll arrives as a wheel event with a fractional delta, so it zooms smoothly
where a mouse notch steps by 1.2 — both land on the same curve. A trackpad **pinch** is a separate
platform gesture, and Avalonia 12.0.0 keeps `Gestures` internal, so there is no public event to
subscribe to; two finger scroll is the trackpad path until that is exposed.

Keyboard shortcuts need the canvas focused, which a click gives it.

## Editing, not just showing

Moving a control is a **preview**: it rebuilds the picture and leaves the file alone. Two things are
edits, and both write the drawing's own text through
[Svg.SourceEditing](../../site/articles/packages/svg-sourceediting.md):

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
table the source pane paints with. It stays a real text box: only its presenter is replaced, so the
caret, the selection, composition and undo are Avalonia's own.

A let has no form and no `⋯`: it is a name and an expression, so the row is the editor. `Add let…`
leaves an empty row to type into, `Enter` or leaving the row writes it, and `Escape` puts it back.
What is typed is checked against the parameters and the lets above it as it is typed, and nothing is
written until it checks — a half-typed body in the drawing would stop it rendering. Beside each row
is what the let evaluates to right now, which follows the sliders, and a `✕` that removes it on the
same terms as a parameter — a row nothing has been typed into yet is simply dropped, since there is
nothing in the document to take out.

Its grip drags it up and down, because **where a let sits is what it can name**: one resolves against
what is declared above it and nothing below. A drag is held inside the positions that still check, so
there is nothing to refuse; `MoveLet` refuses anyway, since the document reads back perfectly well
either way and only type checking can tell.

All of them go through the source pane's text buffer rather than around it, which is what makes the undo
stack the one history of the document: a parameter added from the panel and a line typed into the
pane come off it in the order they were done. An addition that had to declare a namespace and open a
block is three spans and **one** undo step.

Neither needs the pane to be open. The buffer and the pane are separate, so an edit made with the
pane closed still makes the document modified and still saves — and when the pane is opened, the
change is there, spelled the way somebody would have typed it, with every comment and every
`{{ … }}` placeholder in the file untouched.

Committing replaces an authored expression with the value it currently holds, so a
`default="tau / 4"` becomes `default="1.5708"`. That is a real loss of what the author meant, which
is why the button says so and why one undo puts it back.

`ParameterDialogService` is how the form is asked for, replaceable for the reason
`FileDialogService` is: a modal is the one part of the viewer a test cannot drive.
`SvgParameterFormView` is the form itself, a plain control, for a host that wants to ask its own way.

## Embedding it

`SvgViewer` is the drop-in. `ShowToolBar`, `ShowDeclarationPanel` and `ShowStatusBar` turn off the
chrome, `ShowBounds` turns off the outline around the drawing's own edges — on by default, since an
icon with transparent margins otherwise ends nowhere the eye can see — and `SvgViewerCanvas` and
`SvgViewerDeclarationPanel` are usable on their own for a host that wants to supply its own. `SvgViewerDocument` and `SvgViewerParameterFactory` are plain classes with
no UI, for a host that only wants the loading and seeding.

Opening is the host's to offer: the toolbar zooms and shows the source, and `OpenAsync()` is the
picker — `src/Svg.Studio` calls it from File → Open…. Replace `FileDialogService` to open files some
other way; the default is the platform picker.

No extra theme setup is needed. `ColorPicker` keeps its control theme in its own assembly rather
than in `FluentTheme`, so `SvgViewerDeclarationPanel` includes it itself — a host that forgets would
otherwise get a colour row templated with nothing, present in the tree and invisible on screen.

## Two things worth knowing

**It does not use the `Avalonia.Svg.Skia.Svg` control.** That control sizes itself to the drawing it
fits — a 100×100 document in a 400×200 pane arranges at 200×200 — so it cannot fill a viewport, and
its `Zoom`/`PanX`/`PanY` are bounded by its own clip. The viewer draws onto an `SKCanvasControl` and
owns the transform, which is what makes fit and 1:1 exact.

**Loading is the only thing off the UI thread.** Binding a value is not: `SetExpressionValues`
evaluates a model that is already compiled, and doing it on the UI thread is what keeps two changes
in the order they were made. A burst of changes — dragging a slider — is coalesced into one binding
per frame. Drawing happens on the render thread through `SKSvg.Draw`, which brackets itself against
the picture being replaced underneath it.

## Errors never blank the drawing

A failed load leaves the previous document up. A malformed `<e:code>` block is reported but still
renders, because loading deliberately never reads declarations — the drawing shows its placeholders.
A rejected value leaves the last good rendering exactly where it was, and the control keeps what was
typed so it can be corrected.

## The file picker on macOS: use 12.0.2 or newer

Avalonia 12.0.0 and 12.0.1 crash the process as a native panel is dismissed — a use-after-free in
`StorageProvider::SaveFileDialog`'s completion block, reached from
`-[NSSavePanel didEndPanelWithReturnCode:]`, with no managed frames, and it reproduces in a bare
Avalonia app. `AppBuilder.UseManagedSystemDialogs()` was the workaround, at the cost of a picker
that neither reports the file type chosen in it nor lets that type name the file.

[AvaloniaUI/Avalonia#21313](https://github.com/AvaloniaUI/Avalonia/issues/21313) fixed it in
**12.0.2**, so a host on that or newer should use the native pickers.

Dropping a file on the viewer and handing a path to `LoadAsync` avoid the picker entirely.
