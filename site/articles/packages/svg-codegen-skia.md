---
title: "Svg.CodeGen.Skia"
---

# Svg.CodeGen.Skia

`Svg.CodeGen.Skia` turns the intermediate `ShimSkiaSharp.SKPicture` model into C# source code. It is the package to choose when SVG parsing should happen ahead of time and the final application should ship compiled `SKPicture` builders instead of raw `.svg` files.

## Install

```bash
dotnet add package Svg.CodeGen.Skia
```

## Choose this package when

- you want a custom asset-preparation step instead of a Roslyn source generator,
- generated files should be checked into source control,
- a build pipeline needs explicit control over file names and output locations,
- you want to generate C# from an already-built `ShimSkiaSharp.SKPicture`.

## Main type

| Type | Role |
| --- | --- |
| `SkiaCSharpCodeGen` | Generates C# source from a `ShimSkiaSharp.SKPicture` |

The generated class includes:

- a static `Picture` property,
- a static `Draw(SKCanvas)` method,
- all SkiaSharp object creation and disposal logic required to reconstruct the picture.

## Typical workflow

1. Parse the SVG into the repository's intermediate model.
2. Call `SkiaCSharpCodeGen.Generate(...)`.
3. Write the returned string to a `.cs` file.
4. Compile that file into the consuming project.

## Example

```csharp
using System.IO;
using Svg.CodeGen.Skia;
using Svg.Skia;

using var svg = new SKSvg();

if (svg.Load("Assets/icon.svg") is not null && svg.Model is not null)
{
    var code = SkiaCSharpCodeGen.Generate(svg.Model, "MyApp.Generated", "Icon");
    File.WriteAllText("Generated/Icon.g.cs", code);
}
```

This example uses `Svg.Skia` to create the intermediate model, but `Svg.CodeGen.Skia` itself only needs a `ShimSkiaSharp.SKPicture`.

## Drawings that use expressions

A drawing using the [expression extension](svg-expressions) generates: its parameters become C# parameters, and each expression is emitted as code that recomputes the value at call time rather than as the number it happened to have when the drawing was compiled.

A driven `transform` is generated too. The picture records how the matrix was derived — `ShimSkiaSharp`'s `SymMatrix`, the transform functions and their arguments — so the emitted code rebuilds the matrix from the argument values instead of baking one, and the refusals are the same ones the renderer applies: a driven transform under a filter, under an ancestor that opens a layer, or written inside a `<clipPath>` is refused, because each of those was measured against the matrix as the drawing was compiled.

What generation still refuses outright is the other kind of expression, the kind resolved **before** the drawing is recorded — element text, `font-family`, `font-size` and the rest of the text attributes. Generation bakes the picture with the text already measured and the glyphs already positioned, so a parameter driving one could never vary at run time; the document is refused rather than generated with a signature that offers something it cannot do.

## Why use this package instead of the source generator

Choose `Svg.CodeGen.Skia` when generation is an explicit build step and you want control over:

- output file locations,
- naming conventions,
- checked-in generated artifacts,
- batch generation outside MSBuild or Roslyn.

Choose [Svg.SourceGenerator.Skia](svg-sourcegenerator-skia) when SVG files already live in the consuming project and implicit build-time generation is the better experience.

## Good scenarios

- design-system icon packs converted during CI,
- SDKs that ship compiled picture classes,
- NativeAOT or trimmed applications that want less runtime parsing work,
- custom CLI tools such as `svgc`.

## Related docs

- [Source Generator and svgc](../guides/source-generator-and-svgc)
- [Svg.SourceGenerator.Skia](svg-sourcegenerator-skia)
- [ShimSkiaSharp](shim-skiasharp)
- [Svg.Expressions](svg-expressions)
