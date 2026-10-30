---
title: "Installation"
---

# Installation

## NuGet packages

This fork publishes to nuget.org under the `SamoZ256.` prefix, because upstream owns the plain IDs
there. Every version carries a `-samoz.N` suffix, which makes it a prerelease, hence `--prerelease`.
The rest of these docs call each package by its project name, `Svg.Skia` for `SamoZ256.Svg.Skia`.
Do not mix in upstream's packages: both ship the same assemblies.

Core renderer:

```bash
dotnet add package SamoZ256.Svg.Skia --prerelease
```

Uno control package:

```bash
dotnet add package SamoZ256.Svg.Controls.Skia.Uno --prerelease
```

.NET MAUI control package:

```bash
dotnet add package SamoZ256.Svg.Controls.Skia.Maui --prerelease
```

Avalonia controls backed by Skia:

```bash
dotnet add package SamoZ256.Svg.Controls.Skia.Avalonia --prerelease
```

Avalonia controls backed by the Avalonia drawing stack:

```bash
dotnet add package SamoZ256.Svg.Controls.Avalonia --prerelease
```

Inline SVG authoring for Avalonia XAML:

```bash
dotnet add package SamoZ256.SvgML.Avalonia --prerelease
```

Inline SVG authoring for .NET MAUI XAML:

```bash
dotnet add package SamoZ256.SvgML.Maui --prerelease
```

Inline SVG authoring for Uno Platform XAML:

```bash
dotnet add package SamoZ256.SvgML.Uno --prerelease
```

General-purpose Skia controls for Avalonia:

```bash
dotnet add package SamoZ256.Skia.Controls.Avalonia --prerelease
```

Source generator:

```bash
dotnet add package SamoZ256.Svg.SourceGenerator.Skia --prerelease
```

Converter as a global tool:

```bash
dotnet tool install -g SamoZ256.Svg.Skia.Converter --prerelease
```

## Target frameworks

Most runtime packages in this repository multi-target:

- `netstandard2.0`
- `net461`
- `net6.0`
- `net8.0`
- `net10.0`

The generator and codegen packages target `netstandard2.0`.

Avalonia-specific UI packages, including `Svg.Controls.Avalonia`, `Svg.Controls.Skia.Avalonia`, `Skia.Controls.Avalonia`, and `SvgML.Avalonia`, target `net8.0` and `net10.0`.

`Svg.Controls.Skia.Maui` and `SvgML.Maui` target `net10.0-android`, `net10.0-ios`, and `net10.0-maccatalyst`.

`SvgML.Uno` currently targets `net10.0` through `Uno.Sdk` with `SkiaRenderer` enabled. The current Uno `6.5.x` packages in this repository do not ship `net8.0` assets.

## Repository prerequisites

The repository uses git submodules for upstream SVG sources and external test data. After cloning:

```bash
git submodule update --init --recursive
```

The local docs pipeline also uses a .NET tool manifest for Lunet:

```bash
dotnet tool restore
```

The standalone Uno sample app additionally needs Uno workloads configured through `uno-check`, but the default repository solution does not:

```bash
uno-check --target desktop --target web --target android --target ios
```

The .NET MAUI lane uses its own MAUI-local workspace and requires the .NET MAUI workload:

```bash
cd src/SvgML.Maui
dotnet workload install maui
dotnet build ../Svg.Controls.Skia.Maui/Svg.Controls.Skia.Maui.csproj -c Release
dotnet build SvgML.Maui.csproj -c Release
dotnet build ../../samples/MauiSvgSkiaSample/MauiSvgSkiaSample.csproj -c Release
dotnet build ../../samples/SvgML.Maui.Demo/SvgML.Maui.Demo.csproj -c Release
```

## Project workflow commands

Formatting:

```bash
dotnet format --no-restore
```

Build:

```bash
dotnet build Svg.Skia.slnx -c Release
```

Tests:

```bash
dotnet test Svg.Skia.slnx -c Release
```

Docs:

```bash
./build-docs.sh
./serve-docs.sh
```
