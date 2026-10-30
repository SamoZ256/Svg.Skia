---
title: "CLI Conversion"
---

# CLI Conversion

`Svg.Skia.Converter` is the repository's command-line conversion tool.

## Install

```bash
dotnet tool install -g SamoZ256.Svg.Skia.Converter --prerelease
```

## Supported inputs and outputs

Inputs:

- single files,
- multiple files,
- directory scans with pattern filters,
- stored configuration files.

Outputs:

- png,
- jpg or jpeg,
- webp,
- pdf,
- xps.

## Common examples

Single file to png:

```bash
Svg.Skia.Converter -f ./Assets/icon.svg --outputFiles ./out/icon.png
```

Directory to webp with scaling:

```bash
Svg.Skia.Converter \
  -d ./Assets \
  -o ./out \
  -p "*.svg" \
  --format webp \
  --quality 90 \
  --scale 2
```

Batch conversion from a saved configuration:

```bash
Svg.Skia.Converter --load-config ./svgskia.json
```

## Important options

- `--inputFiles` or `--inputDirectory`
- `--outputFiles` or `--outputDirectory`
- `--pattern`
- `--format`
- `--quality`
- `--background`
- `--scale`, `--scaleX`, `--scaleY`
- `--systemLanguage`
- `--quiet`
- `--load-config`, `--save-config`

## NativeAOT note

The repository includes a `NativeAOT.md` note under [`samples/Svg.Skia.Converter`](../reference/samples-and-tools) with publish commands for `win-x64`, `linux-x64`, and `osx-x64`.
