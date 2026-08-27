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

- **Opens** a file through a picker or a drop, off the UI thread, keeping whatever is on screen if
  the load fails.
- **Zooms and pans**, with a percentage readout. Resizing keeps the drawing fitted until the view is
  adjusted by hand, after which it is left where it was.
- **Builds a control per parameter** — a slider and a value box for a `number`, honouring any
  `min`, `max` and `step` the document declares, a `ColorPicker` for a `color`, a checkbox for a
  `boolean` — seeded from each declared `default`.
- **Declares a parameter**, from a form in the panel, by writing it into the drawing's own text.
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

A row's `⋯` button, which appears while the pointer is over it, is how one is edited. Its type is
not offered: every expression naming a parameter was checked against the type it has.

All three go through the source pane's text buffer rather than around it, which is what makes the undo
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

`SvgViewer` is the drop-in. `ShowToolBar`, `ShowParameterPanel` and `ShowStatusBar` turn off the
chrome, and `SvgViewerCanvas` and `SvgViewerParameterPanel` are usable on their own for a host that
wants to supply its own. `SvgViewerDocument` and `SvgViewerParameterFactory` are plain classes with
no UI, for a host that only wants the loading and seeding.

Replace `FileDialogService` to open files some other way; the default is the platform picker.

No extra theme setup is needed. `ColorPicker` keeps its control theme in its own assembly rather
than in `FluentTheme`, so `SvgViewerParameterPanel` includes it itself — a host that forgets would
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

## Known issue: the file picker crashes on macOS

With Avalonia 12.0.0 on macOS, dismissing the native open panel crashes the process inside
`StorageProvider::OpenFileDialog`'s completion block, reached from
`-[NSSavePanel didEndPanelWithReturnCode:]`, with no managed frames. `samples/TestApp` crashes
there identically, so this is upstream and not specific to this package or to the options it
passes.

The workaround is Avalonia's own managed picker, which the framework draws itself:

```csharp
var builder = AppBuilder.Configure<App>().UsePlatformDetect().UseSkia();

if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
{
    builder = builder.UseManagedSystemDialogs();   // needs Avalonia.Dialogs
}
```

It is an application-wide switch, so it belongs to the host rather than to this library;
`src/Svg.Studio` applies it on macOS. Measured against a bare Avalonia app, dismissing the native
panel exits with SIGSEGV while the managed one returns normally.

Dropping a file on the viewer and handing a path to `LoadAsync` also avoid the picker entirely.
