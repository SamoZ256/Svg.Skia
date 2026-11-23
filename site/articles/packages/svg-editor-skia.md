---
title: "Svg.Editor.Skia"
---

# Svg.Editor.Skia

`Svg.Editor.Skia` contains the editing math and overlay-rendering helpers that an interactive canvas is built on.

## Install

```bash
dotnet add package SamoZ256.Svg.Editor.Skia --prerelease
```

## Choose this package when

- you are building your own canvas or surface control,
- you need reusable handle hit-testing, transform math, and path editing,
- you want align/distribute helpers for selected SVG elements,
- you want the editor overlay renderer.

## Main types

| Type | Role |
| --- | --- |
| `SvgEditorInteractionController` | Public adapter over selection and path-edit behavior |
| `SvgEditorOverlayRenderer` | Public adapter over overlay drawing |
| `SelectionService` | Bounds, transforms, resizing, skewing, flipping, and handle hit-testing |
| `PathService` | Editable path points and path operations |
| `AlignService` | Align and distribute commands |
| `RenderingService` | Grid, layer, selection, and path overlay drawing |
| `BoundsInfo` | Computed selection handles |
| `PathPoint` | Individual editable path point |

## Minimal setup

```csharp
using Svg.Editor.Skia;

var interaction = new SvgEditorInteractionController
{
    SnapToGrid = true,
    GridSize = 10
};

var overlays = new SvgEditorOverlayRenderer();
```

In a custom surface, the interaction controller is the entry point for:

- `GetBoundsInfo(...)`
- `HitHandle(...)`
- `SetRotation(...)`
- `SetTranslation(...)`
- `SetScale(...)`
- `SetSkew(...)`
- `ResizeElement(...)`
- path-edit operations such as `StartPathEditing(...)`, `MoveActivePathPoint(...)`, and `MakePathPointSmooth(...)`

## Relationship to the higher packages

[Svg.Viewer.Skia.Avalonia](svg-viewer-skia-avalonia), which Svg Studio is built on, takes its selection bounds and handle hit-testing from `SelectionService`.

## Related docs

- [Rendering and Svg Services](../editor/rendering-and-svg-services)
- [Svg.Viewer.Skia.Avalonia](svg-viewer-skia-avalonia)
- [Hit Testing and Scene Inspection](../guides/hit-testing-and-model-editing)
