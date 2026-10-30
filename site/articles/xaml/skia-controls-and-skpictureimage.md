---
title: "Skia Controls and SKPictureImage"
---

# Skia Controls and SKPictureImage

`Skia.Controls.Avalonia` is useful even outside the SVG packages. It gives you a set of reusable controls for drawing Skia content directly.

## Available controls

- `SKCanvasControl`
- `SKBitmapControl`
- `SKPathControl`
- `SKPictureControl`
- `SKPictureImage`

## `SKCanvasControl`

Hook the draw event and render with raw SkiaSharp:

```csharp
CanvasControl.Draw += (_, e) =>
{
    e.Canvas.DrawRect(SKRect.Create(0f, 0f, 100f, 100f), new SKPaint { Color = SKColors.Aqua });
};
```

## `SKPictureControl`

This control is a natural companion to `Svg.Skia` because `SKSvg.Picture` can be assigned directly:

```xml
<local:SKPictureControl Picture="{x:Static local:Pictures.Tiger}" Stretch="Uniform" />
```

## `SKPictureImage`

Use `SKPictureImage` when a normal Avalonia `Image` should render an `SKPicture`:

```xml
<Image>
  <Image.Source>
    <local:SKPictureImage Source="{x:Static local:Pictures.Camera}" />
  </Image.Source>
</Image>
```

This is useful when:

- the picture is generated once and reused many times,
- the source is already an `SKPicture`,
- you want to mix SVG-produced pictures with non-SVG Skia content in the same app.
