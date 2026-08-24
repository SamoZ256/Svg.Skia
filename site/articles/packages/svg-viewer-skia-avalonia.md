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
| `ShowToolBar` / `ShowParameterPanel` / `ShowStatusBar` / `ShowSource` | Supplying your own chrome |
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

## Reading the drawing's text

The **Source** toggle in the toolbar opens a pane under the drawing showing the document as it was
read — comments, formatting and `{{ … }}` expressions exactly as their author wrote them. It is
read-only; editing SVG is what `Svg.Editor.Skia.Avalonia` is for.

It is coloured as XML, and — because no stock grammar knows the extension — `{{ … }}` placeholders
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

There is no size at which colouring gives up. The pane is a row per line in a virtualising list, so
only the lines on screen are ever laid out: a 132KB drawing of 340 lines opens in 94ms with 17 rows
built. What that does not bound is a single enormous *line* — a minified drawing is the whole file on
one — so a row colours its first 250 pieces and shows the remainder plainly, which took that same
132KB minified from 1.4s to 340ms. Nothing is hidden either way; the uncoloured remainder is still
there to read and select.

Mistakes are underlined where they are written, the line's number is marked in the gutter, and the
message is on the line as a tooltip. `…ErrorBrush` is the key for both. That covers the drawing's
expressions and the `<e:code>` block alike: a name nothing declares, a range on a colour, a `min`
above its `max`, a `default` that will not resolve. What counts as a mistake is
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

`src/SvgViewer` is that host: one viewer per tab, a new tab per file opened, and `Close` on the
viewer whose tab goes away.

## Two things worth knowing

It does **not** host `Avalonia.Svg.Skia.Svg`. That control sizes itself to the drawing it fits, so it cannot fill a viewport and its zoom is bounded by its own clip. The viewer draws onto `SKCanvasControl` and owns the transform, which is what makes fit and actual-size exact.

Loading is the only work off the UI thread. Binding a value evaluates a model that is already compiled, on the UI thread, coalesced to one call per frame, so a slider drag stays smooth and two changes keep the order they were made in.

## Related docs

- [Source Generator and svgc](../guides/source-generator-and-svgc)
- [Svg.Controls.Skia.Avalonia](svg-controls-skia-avalonia)
- [Skia.Controls.Avalonia](skia-controls-avalonia)
