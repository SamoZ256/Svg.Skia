---
title: "Svg.Editor.Avalonia"
---

# Svg.Editor.Avalonia

`Svg.Editor.Avalonia` provides reusable editor UI for Avalonia without requiring the default workspace layout.

## Install

```bash
dotnet add package SamoZ256.Svg.Editor.Avalonia --prerelease
```

## Choose this package when

- you want the editor side panels but not the built-in canvas/workspace,
- you need the typed editor dialogs and result models in another host shell,
- your app has its own menus, docking layout, or document tabs,
- you want to swap the default file-picker or modal window flow.

## Main controls

| Control | Role |
| --- | --- |
| `DocumentOutlineView` | Outline tree and artboard list |
| `ResourceBrowserView` | Layers, patterns, swatches, brushes, symbols, and styles |
| `PropertyInspectorView` | Filterable property grid and specialized property editors |
| `ToolPaletteView` | Tool buttons and stroke-width input |
| `StatusBarView` | Status text and reset button |

## Dialog infrastructure

| Type | Role |
| --- | --- |
| `ISvgEditorDialogService` | Abstracts insert-element, gradient, mesh, stroke, text, swatch, symbol, path, and settings editors |
| `ISvgEditorFileDialogService` | Abstracts open/save/export/image-pick workflows |
| `SvgEditorDialogService` | Default window-based dialog implementation |
| `SvgEditorFileDialogService` | Default Avalonia storage-provider implementation |

## Standalone editor views

The package exposes reusable views for each advanced editing workflow:

- `InsertElementPickerView`
- `PatternEditorView`
- `GradientStopsEditorView`
- `GradientMeshEditorView`
- `StrokeProfileEditorView`
- `TextEditorView`
- `PathSegmentsEditorView`
- `SwatchEditorView`
- `SymbolPickerView`
- `SymbolNameEditorView`
- `SettingsEditorView`

Each view maps to a strongly typed result model under `Svg.Editor.Avalonia.Models`.

## Example panel usage

```xml
<controls:PropertyInspectorView FilteredProperties="{Binding FilteredProperties}" />
```

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Svg.Editor.Avalonia.Controls;
using Svg.Editor.Svg.Models;

var inspector = new PropertyInspectorView
{
    FilteredProperties = new ObservableCollection<PropertyEntry>()
};

inspector.PatternReferenceProvider = Array.Empty<string>;
inspector.EditGradientStopsAsync = entry => Task.FromResult<IReadOnlyList<GradientStopInfo>?>(entry.Stops);
inspector.EditGradientMeshAsync = _ => Task.CompletedTask;
inspector.EditStrokeProfileAsync = entry => Task.FromResult<IReadOnlyList<StrokePointInfo>?>(entry.Points);
```

Use those hooks when the host wants the reusable grid but needs to own the actual advanced editor presentation.

## Related docs

- [Composing Panels and Dialogs](../editor/composing-panels-and-dialogs)
- [Svg.Editor.Skia.Avalonia](svg-editor-skia-avalonia)
- [Svg.Editor.Svg](svg-editor-svg)
