---
title: "Quickstart: Avalonia"
---

# Quickstart: Avalonia

## Skia-backed Avalonia controls

Install:

```bash
dotnet add package SamoZ256.Svg.Controls.Skia.Avalonia --prerelease
```

Render a file directly:

```xml
<Window
    xmlns="https://github.com/avaloniaui"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:svg="clr-namespace:Avalonia.Svg.Skia;assembly=Svg.Controls.Skia.Avalonia">
  <svg:Svg Path="/Assets/__AJ_Digital_Camera.svg" />
</Window>
```

Use `SvgImage` in an `Image` control:

```xml
<Image Source="{SvgImage /Assets/__tiger.svg}" />
```

Apply CSS overrides:

```xml
<svg:Svg Path="/Assets/__tiger.svg"
         Css=".Black { fill: #FF0000; }" />
```

Use an SVG-backed brush:

```xml
<Border Background="{SvgResource /Assets/__tiger.svg}" />
```

## Avalonia drawing-stack controls

Install:

```bash
dotnet add package SamoZ256.Svg.Controls.Avalonia --prerelease
```

The XAML shape is similar, but the namespace changes:

```xml
<Window
    xmlns="https://github.com/avaloniaui"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:svg="clr-namespace:Avalonia.Svg;assembly=Svg.Controls.Avalonia">
  <svg:Svg Path="/Assets/__AJ_Digital_Camera.svg" />
</Window>
```

## Previewer note

When the Avalonia previewer does not automatically load the custom controls, keep the SVG assemblies alive in `BuildAvaloniaApp()`:

```csharp
GC.KeepAlive(typeof(SvgImageExtension).Assembly);
GC.KeepAlive(typeof(Svg.Controls.Skia.Avalonia.Svg).Assembly);
```

## Next steps

- Use [XAML Overview](../xaml/overview) for the differences between the two Avalonia packages.
- Use [Svg Control and SvgImage](../xaml/svg-control-and-svgimage) for the important properties and behaviors.
- Use [SvgResource and Brushes](../xaml/svgresource-and-brushes) for resource dictionaries and code-behind brush creation.
