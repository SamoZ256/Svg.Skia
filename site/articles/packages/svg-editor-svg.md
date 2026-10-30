---
title: "Svg.Editor.Svg"
---

# Svg.Editor.Svg

`Svg.Editor.Svg` packages the SVG document mutation and resource-management services used by the editor stack.

## Install

```bash
dotnet add package SamoZ256.Svg.Editor.Svg --prerelease
```

## Choose this package when

- you want to load, save, and round-trip SVG documents in an editor workflow,
- you need reusable property-inspector models,
- you need layer, pattern, brush, swatch, symbol, or style collections,
- your host app already has a UI and only needs the SVG-side editor services.

## Main services

| Type | Role |
| --- | --- |
| `SvgDocumentService` | Open from file or stream, save, round-trip XML, export png/pdf/xps |
| `PropertiesService` | Build editable property-entry collections and apply values back to an element |
| `LayerService` | Load and mutate layer hierarchy |
| `PatternService` | Enumerate and add patterns |
| `BrushService` | Enumerate brush profiles and swatches |
| `SymbolService` | Enumerate and add reusable symbols |
| `AppearanceService` | Load and update style entries |
| `ToolService` | Create and update editor-created elements |

## Main models

`Svg.Editor.Svg.Models` contains the reusable data classes behind the property inspector and resource browser:

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

## Basic workflow

```csharp
using Svg.Editor.Svg;

var documentService = new SvgDocumentService();
var layerService = new LayerService();
var propertiesService = new PropertiesService();

var document = documentService.Open("Assets/__tiger.svg");
layerService.Load(document);

if (document?.Children.Count > 0 && document.Children[0] is Svg.SvgElement element)
{
    propertiesService.LoadProperties(element);
    propertiesService.ApplyAll(element);
}
```

## Why this package exists separately

AvalonDraw originally carried these services in the sample app. Extracting them into `Svg.Editor.Svg` makes the property editor, layer browser, and resource browser reusable without taking the default Avalonia workspace.

## Related docs

- [Rendering and Svg Services](../editor/rendering-and-svg-services)
- [Svg.Editor.Core](svg-editor-core)
- [Svg.Editor.Skia](svg-editor-skia)
