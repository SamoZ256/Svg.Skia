---
title: "Svg.SourceGenerator.Skia"
---

# Svg.SourceGenerator.Skia

`Svg.SourceGenerator.Skia` is the build-time package that turns `.svg` additional files into generated C# classes exposing `SKPicture` content. It packages the Roslyn incremental generator used in this repository's samples and lets consuming projects compile SVG assets into code automatically.

## Install

Add the package reference:

```xml
<ItemGroup>
  <PackageReference Include="SamoZ256.Svg.SourceGenerator.Skia" Version="*-*" />
</ItemGroup>
```

Then add one or more SVG files as `AdditionalFiles`:

```xml
<ItemGroup>
  <AdditionalFiles Include="Assets\Camera.svg"
                   NamespaceName="MyApp.Generated"
                   ClassName="Camera" />
  <AdditionalFiles Include="Assets\Logo.svg"
                   NamespaceName="MyApp.Generated"
                   ClassName="Logo" />
</ItemGroup>
```

## Choose this package when

- SVG assets should become strongly named classes during the build,
- runtime parsing should be reduced or eliminated,
- your project already owns the source `.svg` files,
- you want generated pictures without adding a separate generation step.

## Output shape

For each `.svg` file, the generator emits a C# source file containing a generated class with:

- a static `Picture` property,
- a static `Draw(SKCanvas)` method,
- generated code produced by [Svg.CodeGen.Skia](svg-codegen-skia).

If `ClassName` is not supplied, the generator falls back to `Svg_<file-name>`.

If `NamespaceName` is not supplied, the generator falls back to:

1. the project-wide `NamespaceName` build property when set,
2. otherwise the default namespace `Svg`.

## Consuming generated pictures

Once the build runs, use the generated classes directly:

```csharp
using MyApp.Generated;

var picture = Camera.Picture;
```

In Avalonia, pair it with [Skia.Controls.Avalonia](skia-controls-avalonia):

```xml
<SKPictureControl Picture="{x:Static local:Camera.Picture}" Stretch="Uniform" />
```

## Important project setup notes

- The generator looks at `.svg` files added as `AdditionalFiles`.
- `NamespaceName` can be configured per file or as a project-wide MSBuild property.
- When consuming the published NuGet package, the `.props` file is imported automatically.
- When referencing the generator project directly inside this repository, import `Svg.SourceGenerator.Skia.props` explicitly, as shown in the sample project.

## When to choose something else

- Choose [Svg.CodeGen.Skia](svg-codegen-skia) when generation should be a manual or pipeline-driven step with explicit output files.
- Choose [Svg.Skia](svg-skia) when runtime loading is acceptable or preferred.
- Choose packaged tools under [Samples and Tools](../reference/samples-and-tools) when a CLI better matches the workflow.

## Related docs

- [Source Generator and svgc](../guides/source-generator-and-svgc)
- [Skia Controls and SKPictureImage](../xaml/skia-controls-and-skpictureimage)
