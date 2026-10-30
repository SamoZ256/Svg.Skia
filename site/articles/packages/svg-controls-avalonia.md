---
title: "Svg.Controls.Avalonia"
---

# Svg.Controls.Avalonia

`Svg.Controls.Avalonia` exposes the same high-level Avalonia SVG concepts as the Skia-backed package, but renders through the Avalonia drawing stack instead of wrapping the `Svg.Skia` runtime object.

## Install

```bash
dotnet add package SamoZ256.Svg.Controls.Avalonia --prerelease
```

## Choose this package when

- the main target is Avalonia XAML,
- you want `Svg`, `SvgImage`, `SvgSource`, and `SvgResource` concepts,
- the Avalonia drawing model is the preferred host integration point,
- you do not need direct `SKSvg`, control-coordinate hit testing, zoom, or wireframe features.

## Main types

| Type | Role |
| --- | --- |
| `Avalonia.Svg.Svg` | Control that renders SVG through Avalonia drawing commands |
| `Avalonia.Svg.SvgImage` | `IImage` wrapper for a reusable `SvgSource` |
| `Avalonia.Svg.SvgSource` | Reusable source object backed by the intermediate picture model |
| `SvgImageExtension` | Markup extension for concise image usage |
| `SvgResourceExtension` | Brush-producing markup extension |

## Basic XAML usage

```xml
<Window
    xmlns="https://github.com/avaloniaui"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:svg="clr-namespace:Avalonia.Svg;assembly=Svg.Controls.Avalonia">
  <DockPanel>
    <svg:Svg Path="/Assets/__tiger.svg" Stretch="Uniform" />
  </DockPanel>
</Window>
```

For image properties:

```xml
<Image Source="{SvgImage /Assets/__AJ_Digital_Camera.svg}" />
```

## Reusable sources

```csharp
using System;
using Avalonia.Svg;
using Svg.Model;

var source = SvgSource.Load(
    "avares://MyApp/Assets/icon.svg",
    new Uri("avares://MyApp/"),
    new SvgParameters(null, ".accent { fill: #007ACC; }"));

var image = new SvgImage
{
    Source = source
};

var clone = source.Clone();
```

The package can load from file paths, HTTP URLs, Avalonia resources, and streams. `SvgSource.RebuildFromModel()` refreshes the stored intermediate picture after model-side changes.

## How it differs from the Skia-backed package

This package uses an `AvaloniaPicture` recording and replay path rather than exposing `SkiaSharp.SKPicture` and `SKSvg` directly.

Choose this package when that difference is a feature, not a limitation:

- you want the Avalonia drawing context to remain the main abstraction,
- your app does not need explicit runtime `SKSvg` access,
- you want the simpler shared XAML surface without extra interaction APIs.

Choose [Svg.Controls.Skia.Avalonia](svg-controls-skia-avalonia) instead when you need:

- `SkSvg`,
- hit testing on the control itself,
- zoom and pan support,
- filter toggles,
- wireframe rendering,
- reload support that maps directly to the `Svg.Skia` runtime object.

## Brushes and resources

The same `SvgResource` pattern is available here:

```xml
<Border Background="{SvgResource /Assets/__tiger.svg}" />
```

That lets you centralize SVG-backed brushes in resource dictionaries even when the main renderer is the Avalonia drawing stack.

## Related docs

- [XAML Overview](../xaml/overview)
- [Svg Control and SvgImage](../xaml/svg-control-and-svgimage)
- [SvgResource and Brushes](../xaml/svgresource-and-brushes)
