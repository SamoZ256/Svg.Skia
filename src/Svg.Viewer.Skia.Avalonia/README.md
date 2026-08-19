# Svg.Viewer.Skia.Avalonia

A reusable Avalonia SVG viewer for the [expression extension](../../SVG_EXPRESSIONS.md): open a
drawing, zoom and pan it, and drive the parameters it declares from controls built to match.

```xml
<viewer:SvgViewer x:Name="Viewer" />
```

```csharp
await Viewer.LoadAsync("badge.svg");
```

`samples/SvgViewer` is the whole application built on it, and its window is one control.

## What it does

- **Opens** a file through a picker or a drop, off the UI thread, keeping whatever is on screen if
  the load fails.
- **Zooms and pans**: wheel about the cursor, drag to pan, and fit / 1:1 / reset with a percentage
  readout. Resizing keeps the drawing fitted until the view is adjusted by hand, after which it is
  left where it was.
- **Builds a control per parameter** — a slider and a value box for a `number`, honouring any
  `min`, `max` and `step` the document declares, a `ColorPicker` for a `color`, a checkbox for a
  `boolean` — seeded from each declared `default`.

## Embedding it

`SvgViewer` is the drop-in. `ShowToolBar`, `ShowParameterPanel` and `ShowStatusBar` turn off the
chrome, and `SvgViewerCanvas` and `SvgViewerParameterPanel` are usable on their own for a host that
wants to supply its own. `SvgViewerDocument` and `SvgViewerParameterFactory` are plain classes with
no UI, for a host that only wants the loading and seeding.

Replace `FileDialogService` to open files some other way; the default is the platform picker.

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

Until it is fixed upstream, open a drawing by **dropping it on the viewer** or by handing a path to
`LoadAsync`; neither goes near that code path. `samples/SvgViewer` also accepts a path on the
command line for the same reason.
