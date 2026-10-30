---
title: "Composing Panels and Dialogs"
---

# Composing Panels and Dialogs

Use `Svg.Editor.Avalonia` when you want reusable editor UI parts without taking the default `SvgEditorWorkspace`.

## Install

```bash
dotnet add package SamoZ256.Svg.Editor.Avalonia --prerelease
```

If your custom shell also wants the reusable Skia-backed canvas shown below, reference `Svg.Editor.Skia.Avalonia` alongside `Svg.Editor.Avalonia`.

## Reusable panel controls

| Control | Purpose |
| --- | --- |
| `DocumentOutlineView` | Tree and artboard list for document structure |
| `ResourceBrowserView` | Layers, patterns, swatches, brushes, symbols, and styles |
| `PropertyInspectorView` | Filterable property grid with specialized editors for gradients, meshes, stroke profiles, and pattern references |
| `ToolPaletteView` | Tool-selection buttons and stroke-width input |
| `StatusBarView` | Status text plus reset affordance |

These controls expose direct Avalonia properties or public control references so hosts can bind data and attach their own events.

## Example composition

```xml
<Grid xmlns:controls="clr-namespace:Svg.Editor.Avalonia.Controls;assembly=Svg.Editor.Avalonia"
      xmlns:editor="clr-namespace:Svg.Editor.Skia.Avalonia;assembly=Svg.Editor.Skia.Avalonia"
      ColumnDefinitions="280,*,320">
  <controls:DocumentOutlineView Grid.Column="0"
                                Nodes="{Binding Session.Nodes}"
                                Artboards="{Binding Session.Artboards}" />

  <editor:SvgEditorSurface Grid.Column="1" />

  <controls:PropertyInspectorView Grid.Column="2"
                                  FilteredProperties="{Binding FilteredProperties}" />
</Grid>
```

In a custom shell, the host typically provides:

- the `SvgEditorSession` from `Svg.Editor.Core`,
- `ObservableCollection<PropertyEntry>` from `PropertiesService`,
- resource collections from `LayerService`, `PatternService`, `BrushService`, `SymbolService`, and `AppearanceService`,
- command handlers that react to selection or property changes.

## Dialog abstractions

`Svg.Editor.Avalonia` separates the editor workflows from a particular windowing policy:

- `ISvgEditorDialogService` covers insert-element, gradient, gradient mesh, stroke profile, text, swatch, symbol, path, and settings workflows.
- `ISvgEditorFileDialogService` covers open/save/export/image-pick workflows.

The default implementations are `SvgEditorDialogService` and `SvgEditorFileDialogService`, but hosts can replace them with:

- docked panels,
- flyouts,
- embedded tabs,
- custom storage-provider dialogs,
- application-specific naming or validation logic.

## Standalone editor views

The package also exposes the reusable editor views that back the default window wrappers:

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

These views return strongly typed result models such as `GradientStopsEditorResult`, `GradientMeshEditorResult`, `StrokeProfileEditorResult`, `TextEditorResult`, and `SettingsEditorResult`.

## Property inspector hooks

`PropertyInspectorView` exposes delegates for the editor-specific sub-editors:

- `PatternReferenceProvider`
- `CreatePatternReferenceAsync`
- `EditGradientStopsAsync`
- `EditGradientMeshAsync`
- `EditStrokeProfileAsync`

That allows a host to keep the grid itself reusable while redirecting advanced edits into its own UI flow.

## Typical composition split

- Put session and model services in the host view model or document controller.
- Bind collections into the reusable controls.
- Route advanced editing through your own `ISvgEditorDialogService`.
- Add `Svg.Editor.Skia.Avalonia` only if you want `SvgEditorSurface` in the same custom shell.

For the non-UI editing primitives behind those controls, continue with [Rendering and Svg Services](rendering-and-svg-services).
