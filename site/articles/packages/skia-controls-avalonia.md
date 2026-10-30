---
title: "Skia.Controls.Avalonia"
---

# Skia.Controls.Avalonia

`Skia.Controls.Avalonia` is the general-purpose Avalonia package for displaying or drawing raw SkiaSharp content. It complements the SVG-specific packages, but it is useful even when SVG is not involved at all.

## Install

```bash
dotnet add package SamoZ256.Skia.Controls.Avalonia --prerelease
```

## Choose this package when

- you already have `SKCanvas`, `SKBitmap`, `SKPath`, or `SKPicture` content,
- you want Avalonia controls and images for raw SkiaSharp primitives,
- SVG content is only one of several Skia-based inputs in the app,
- you need reusable display primitives to pair with generated or runtime-built pictures.

## Main controls and images

| Type | Role |
| --- | --- |
| `SKCanvasControl` | Draw event for custom SkiaSharp painting |
| `SKBitmapControl` | Displays an `SKBitmap` |
| `SKPathControl` | Displays an `SKPath` with an optional `SKPaint` |
| `SKPictureControl` | Displays an `SKPicture` |
| `SKBitmapImage` | `IImage` wrapper around `SKBitmap` |
| `SKPathImage` | `IImage` wrapper around `SKPath` |
| `SKPictureImage` | `IImage` wrapper around `SKPicture` |

## `SKCanvasControl`

Use this when you want direct draw-event access:

```csharp
CanvasControl.Draw += (_, e) =>
{
    using var paint = new SkiaSharp.SKPaint
    {
        Color = SkiaSharp.SKColors.Aqua,
        IsAntialias = true
    };

    e.Canvas.DrawCircle(80, 80, 48, paint);
};
```

## `SKPictureControl`

This is the natural bridge from `Svg.Skia` or generated pictures into Avalonia:

```xml
<SKPictureControl Picture="{x:Static local:Tiger.Picture}"
                  Stretch="Uniform" />
```

It is especially useful with:

- `SKSvg.Picture` from [Svg.Skia](svg-skia),
- generated `Picture` classes from [Svg.SourceGenerator.Skia](svg-sourcegenerator-skia),
- picture output produced by your own SkiaSharp pipeline.

## `SKPictureImage`

When an Avalonia `Image` needs to display a picture instead of a control hosting it:

```xml
<Image>
  <Image.Source>
    <SKPictureImage Source="{x:Static local:Camera.Picture}" />
  </Image.Source>
</Image>
```

That keeps the asset reusable across templates, lists, buttons, and resource dictionaries.

## Why this package matters in the repo

`Skia.Controls.Avalonia` is the shared presentation layer for non-SVG-specific Skia content. It is the package that keeps SVG-generated pictures compatible with the rest of an application's Skia assets.

## When not to choose this package

- Choose [Svg.Controls.Skia.Avalonia](svg-controls-skia-avalonia) when you want SVG-specific XAML primitives such as `Svg`, `SvgImage`, `SvgSource`, or `SvgResource`.
- Choose [Svg.Controls.Avalonia](svg-controls-avalonia) when you want the Avalonia drawing-stack SVG package.
- Choose [Svg.Skia](svg-skia) when you do not need Avalonia UI at all.

## Related docs

- [XAML Overview](../xaml/overview)
- [Skia Controls and SKPictureImage](../xaml/skia-controls-and-skpictureimage)
