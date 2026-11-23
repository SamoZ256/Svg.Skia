---
title: "Rendering and Svg Services"
---

# Rendering and Svg Services

The editor packages are meant for applications that already have a shell or canvas architecture and only need reusable editing services.

## `Svg.Editor.Core`

`Svg.Editor.Core` contains the shared state primitives:

- `SvgEditorSession`
- `ISvgEditorSession`
- `SvgEditorSettings`
- `SvgEditorToolKind`
- `SvgNode`
- `ArtboardInfo`
- `ClipboardSnapshot`

Typical usage:

```csharp
using Svg.Editor.Core;

const string previousDocumentXml = "<svg viewBox=\"0 0 16 16\" />";

var session = new SvgEditorSession();
session.WorkspaceTitle = "My Editor";
session.SetSelectedElementIds(new[] { "layer-1", "shape-42" });
session.PushUndoState(previousDocumentXml);
```

Use this package when the host wants one durable session object that survives view swaps or document-controller abstractions.

## `Svg.Editor.Svg`

`Svg.Editor.Svg` owns SVG document mutation, resource collections, and the property model.

Main services:

| Type | Purpose |
| --- | --- |
| `SvgDocumentService` | Open from path or stream, round-trip XML, save, and export picture output |
| `PropertiesService` | Build editable property entries and apply property changes back to an element |
| `LayerService` | Load, add, remove, reorder, lock, and unlock logical layers |
| `PatternService` | Enumerate and add pattern definitions |
| `BrushService` | Enumerate brush profiles and swatches |
| `SymbolService` | Enumerate and add symbol definitions |
| `AppearanceService` | Load and update style entries |
| `ToolService` | Create and update elements for the editor tool palette |

Main models:

- `PropertyEntry`
- `GradientStopsEntry`
- `GradientMeshEntry`
- `StrokeProfileEntry`
- `LayerEntry`
- `PatternEntry`
- `BrushEntry`
- `SwatchEntry`
- `SymbolEntry`
- `StyleEntry`
- `AppearanceLayer`

Example:

```csharp
using System.Linq;
using Svg.Editor.Svg;

var documentService = new SvgDocumentService();
var layerService = new LayerService();
var propertiesService = new PropertiesService();

var document = documentService.Open("Assets/__tiger.svg");
layerService.Load(document);

var root = document?.Children.OfType<Svg.SvgElement>().FirstOrDefault();
if (root is not null)
{
    propertiesService.LoadProperties(root);
    propertiesService.ApplyAll(root);
}
```

## `Svg.Editor.Skia`

`Svg.Editor.Skia` contains the editing math and overlay rendering that sits on top of `Svg.Skia`.

Main types:

| Type | Purpose |
| --- | --- |
| `SelectionService` | Bounds extraction, transform editing, handle hit-testing, and flip/resize/skew helpers |
| `PathService` | Path-point editing, path operations, blending, offset, and simplify helpers |
| `AlignService` | Align and distribute selected elements |
| `RenderingService` | Draw grid, layer overlay, selection bounds, and path-edit visuals |
| `SvgEditorInteractionController` | Public adapter over `SelectionService` and `PathService` |
| `SvgEditorOverlayRenderer` | Public adapter over `RenderingService` |
| `BoundsInfo` | Handle positions for the active selection |
| `PathPoint` | Editable path-point representation |

Example:

```csharp
using Svg.Editor.Skia;

var interaction = new SvgEditorInteractionController
{
    SnapToGrid = true,
    GridSize = 8
};

var renderer = new SvgEditorOverlayRenderer();
```

Those adapters are the intended entry points when a host is drawing its own canvas and only wants reusable selection and path-editing behavior.

## How the packages fit together

- `Svg.Editor.Core` gives you shared state.
- `Svg.Editor.Svg` turns an SVG document into editor collections and editable property models.
- `Svg.Editor.Skia` turns those collections into interactive editing behavior and overlay drawing.
- The host supplies the window, canvas and panels around them.

If you want the package-by-package NuGet guidance, continue with [Packages](../packages). If you want the full type-level surface, use the generated [API Reference](../../api).
