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
- you want the same stack `src/SvgViewer` is built from.

## Main types

| Type | Role |
| --- | --- |
| `SvgViewer` | The drop-in: toolbar, canvas, parameter panel and status strip |
| `SvgViewerCanvas` | The drawing surface alone, owning scale and offset |
| `SvgViewerParameterPanel` | One control per declared parameter |
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
| `ShowToolBar` / `ShowParameterPanel` / `ShowStatusBar` | Supplying your own chrome |
| `FileDialogService` | Custom storage or picker integration |
| `Canvas` | Direct access to the surface for zoom and pan |
| `DocumentOpened` / `ErrorRaised` / `ParameterValueChanged` | Syncing host titles and status |

## Ranges come from the document

A `number` parameter uses the `min`, `max` and `step` its author declared, falling back to `0` to `1` when it declares none:

```xml
<e:param name="hue" type="number" default="217" min="0" max="360" step="1" />
```

Every row is seeded by *evaluating* the declared `default`, so `default="tau / 4"` works as well as a literal does.

The format itself — `<e:code>`, the operators, and the placeholder mechanism — is specified in `SVG_EXPRESSIONS.md` at the root of the repository.

## One document per viewer

The control shows one drawing, and opening another replaces it. A host that wants several at once
puts a viewer in each pane and handles `OpenRequested`, which is raised for every file the user
picks or drops before any of them is read:

```csharp
viewer.OpenRequested += (_, request) =>
{
    request.Handled = true;   // the viewer loads nothing; the host places the paths itself
    OpenInTabs(request.Paths);
};
```

`src/SvgViewer` is that host: one viewer per tab, a new tab per file opened, and `Close` on the
viewer whose tab goes away.

## Two things worth knowing

It does **not** host `Avalonia.Svg.Skia.Svg`. That control sizes itself to the drawing it fits, so it cannot fill a viewport and its zoom is bounded by its own clip. The viewer draws onto `SKCanvasControl` and owns the transform, which is what makes fit and actual-size exact.

Loading is the only work off the UI thread. Binding a value evaluates a model that is already compiled, on the UI thread, coalesced to one call per frame, so a slider drag stays smooth and two changes keep the order they were made in.

## Related docs

- [Source Generator and svgc](../guides/source-generator-and-svgc)
- [Svg.Controls.Skia.Avalonia](svg-controls-skia-avalonia)
- [Skia.Controls.Avalonia](skia-controls-avalonia)
