---
title: "Architecture"
---

# Architecture

The editor stack is layered so applications can pick the lowest level that still solves the problem. None of the layers depends on a UI framework.

## Dependency order

| Layer | Depends on | Main responsibility |
| --- | --- | --- |
| `Svg.Editor.Core` | `Svg.Model` | Host-agnostic state such as the active document, outline tree, selected ids, settings, clipboard, and undo/redo history |
| `Svg.Editor.Svg` | `Svg.Editor.Core`, `Svg.Model`, `Svg.Skia` | SVG document loading, save/export helpers, resource-path rebasing, properties, layers, patterns, swatches, symbols, styles, and creation tools |
| `Svg.Editor.Skia` | `Svg.Editor.Core`, `Svg.Editor.Svg`, `Svg.Model`, `Svg.Skia` | Transactional document controller, retained selection, viewport, transforms, path editing, align/distribute, and overlays |

## Package responsibilities

### `Svg.Editor.Core`

Use this layer when your host already has its own UI and needs a durable editor session object. `SvgEditorSession` stores the document reference, current file, title, filter text, selected ids, artboards, outline nodes, settings, clipboard contents, and undo/redo snapshots.

### `Svg.Editor.Svg`

This layer owns document mutation and resource-oriented models. It is the package behind a property inspector, layer browser, pattern list, swatches, symbols, and styles. It also carries `SvgDocumentService` for open, save, XML round-tripping, and export helpers. `SvgResourcePathService` preserves relative resource locations when a document is saved into another directory, including nested XML bases and CSS URLs.

### `Svg.Editor.Skia`

This layer owns the editing math and framework-independent document controller. `SvgEditorController` provides SVG transactions, dirty checkpoints, selection, geometry commands, and retained pictures. `SvgEditorViewport` handles camera transforms and anchored zoom without changing the document. The existing `SelectionService`, `PathService`, `AlignService`, and `RenderingService` remain available, as do the `SvgEditorInteractionController` and `SvgEditorOverlayRenderer` adapters.

## Hosts in this repository

- [Svg Studio](../guides/svg-studio), the fork's editor, draws its canvas with [Svg.Viewer.Skia.Avalonia](../packages/svg-viewer-skia-avalonia), which takes selection bounds and handle hit-testing from `Svg.Editor.Skia`'s `SelectionService`.
- `samples/UnoDraw`, upstream's Uno Platform sample, hosts the workspace in `src/Svg.Editor.Skia.Uno`: a canvas that replays `SvgEditorController`'s cached picture straight into the host's Skia surface, with tabbed documents, an outline, an inspector and command search around it. The sample owns the window, branding, startup document and close lifecycle. Neither project is packed in this fork.

## Choosing the smallest useful layer

- Choose `Svg.Editor.Skia` when you are building your own interactive surface or canvas control.
- Choose `Svg.Editor.Svg` when the task is document/resource mutation without UI.
- Choose `Svg.Editor.Core` when you only need common session state and history primitives.
