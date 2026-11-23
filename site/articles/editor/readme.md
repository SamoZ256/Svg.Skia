---
title: "Editor"
---

# Editor

The `Svg.Editor.*` packages are the parts of an SVG editor that do not depend on a UI framework: session state and history, document mutation, and the selection, path and overlay math a canvas needs. The host supplies the window, the canvas and the panels. The fork's own editor is [Svg Studio](../guides/svg-studio), whose canvas in [Svg.Viewer.Skia.Avalonia](../packages/svg-viewer-skia-avalonia) takes its selection bounds and handle hit-testing from `Svg.Editor.Skia`.

## Start here when

- you need reusable SVG mutation, layer, symbol, swatch, and property services,
- you are drawing your own canvas and want selection, path editing, alignment and overlay rendering.

## Package map

| Package | Role | Guide |
| --- | --- | --- |
| `Svg.Editor.Core` | Shared editor session state, settings, selection ids, outline nodes, artboards, clipboard, and undo/redo history. | [Svg.Editor.Core](../packages/svg-editor-core) |
| `Svg.Editor.Svg` | SVG document mutation services and reusable models for layers, patterns, swatches, symbols, styles, and properties. | [Svg.Editor.Svg](../packages/svg-editor-svg) |
| `Svg.Editor.Skia` | Render-model editing helpers, selection math, path editing, alignment, and overlay rendering. | [Svg.Editor.Skia](../packages/svg-editor-skia) |

## Recommended reading order

1. [Architecture](architecture) for dependency order and package boundaries.
2. [Rendering and Svg Services](rendering-and-svg-services) for the services each package exposes.
3. [API Reference](../../api) when you want the generated type-level surface for the editor assemblies.

## UnoDraw

`samples/UnoDraw` is upstream's Uno Platform editor, built on these packages through the workspace in `src/Svg.Editor.Skia.Uno`. Both build with `Svg.Skia.slnx`, but this fork publishes neither; [Uno Sample Publishing](../advanced/uno-sample-publishing) shows how to run it. [UnoDraw MCP](mcp) covers its automation, and the [MCP Tool Reference](mcp-tools) every operation and input schema.

The [UnoDraw guide](https://github.com/wieslawsoltes/Svg.Skia/blob/master/docs/unodraw.md) records keyboard controls, renderer versus authoring coverage, migration gaps and validation commands.

If you only need the editor package documentation per NuGet, continue with [Packages](../packages). If you want the exact generated API inputs, see [API Coverage Index](../reference/api-coverage-index).
