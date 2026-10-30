---
title: "Svg.Editor.Core"
---

# Svg.Editor.Core

`Svg.Editor.Core` contains the host-agnostic state and orchestration primitives used across the editor stack.

## Install

```bash
dotnet add package SamoZ256.Svg.Editor.Core --prerelease
```

## Choose this package when

- you need shared editor state without taking any UI,
- your application already has its own shell or MVVM layer,
- you want a reusable undo/redo, selection-id, and clipboard container for SVG documents,
- you need the outline and artboard models used by the higher editor packages.

## Main types

| Type | Role |
| --- | --- |
| `SvgEditorSession` | Central mutable editor session state |
| `ISvgEditorSession` | Interface for host abstractions and testing |
| `SvgEditorSettings` | Wireframe, filter, grid, snap, and hidden-element toggles |
| `SvgEditorToolKind` | High-level current-tool enum |
| `SvgNode` | Document-outline tree node |
| `ArtboardInfo` | Artboard metadata |
| `ClipboardSnapshot` | Copy/paste payload container |

## Session example

```csharp
using Svg.Editor.Core;

const string previousXml = "<svg viewBox=\"0 0 16 16\" />";

var session = new SvgEditorSession
{
    WorkspaceTitle = "My SVG Editor",
    CurrentTool = SvgEditorToolKind.Select
};

session.SetSelectedElementIds(new[] { "logo", "shadow" });
session.PushUndoState(previousXml);
```

`SvgEditorSession` also tracks `CurrentFile`, `Document`, `Nodes`, `Artboards`, `ExpandedNodeIds`, `PropertyFilterText`, and the selected-element id list used by the reusable UI layers.

## Relationship to the rest of the stack

- `Svg.Editor.Svg` adds SVG-specific mutation services on top of this state.
- `Svg.Editor.Skia` adds the interaction and rendering logic.
- `Svg.Editor.Avalonia` binds the state into reusable controls.
- `Svg.Editor.Skia.Avalonia` exposes the complete workspace.

## Related docs

- [Editor Overview](../editor)
- [Architecture](../editor/architecture)
- [Svg.Editor.Svg](svg-editor-svg)
