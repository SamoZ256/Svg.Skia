---
title: "Quickstart: Uno"
---

# Quickstart: Uno

## Install

```bash
dotnet add package SamoZ256.Svg.Controls.Skia.Uno --prerelease
```

## Render a packaged asset

```xml
<Page
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:svg="using:Uno.Svg.Skia">
  <svg:Svg Path="/Assets/__AJ_Digital_Camera.svg"
           Stretch="Uniform"
           EnableCache="True" />
</Page>
```

## Render inline SVG text

```xml
<svg:Svg Source="{x:Bind InlineSvg}" Height="140" />
```

```csharp
public string InlineSvg =>
    """
    <svg width="120" height="120" xmlns="http://www.w3.org/2000/svg">
      <circle cx="60" cy="60" r="40" fill="#2563eb" />
    </svg>
    """;
```

## Reuse one `SvgSource`

```xml
<Page.Resources>
  <svg:SvgSource x:Key="TigerSource" Path="/Assets/__tiger.svg" />
</Page.Resources>

<svg:Svg SvgSource="{StaticResource TigerSource}" Height="220" />
```

## Apply runtime CSS

```csharp
MySvg.CurrentCss = ".accent { fill: #ef4444; }";
```

## Interactive features

The Uno control also exposes:

- `Wireframe`
- `DisableFilters`
- `Zoom`
- `PanX`
- `PanY`
- `ZoomToPoint(...)`
- `TryGetPicturePoint(...)`
- `HitTestElements(...)`

## Next steps

- [Svg.Controls.Skia.Uno](../packages/svg-controls-skia-uno)
- [Uno Svg Control](../xaml/uno-svg-control)
- [Uno Sample Publishing](../advanced/uno-sample-publishing)
