---
title: "Uno Sample Publishing"
---

# Uno Sample Publishing

## UnoDraw

`samples/UnoDraw`, upstream's Uno Platform editor, builds with `Svg.Skia.slnx`,
but this fork does not publish it. The `Docs` workflow builds only this site, and
the `UnoDraw` workflow, which builds the desktop, Android and iOS targets and runs
the sample's own checks, runs only when dispatched by hand. The fork's editor is
[Svg Studio](../guides/svg-studio).

Run the desktop target on Windows, macOS or Linux/X11. It needs no extra
workload; pass an SVG path after `--` to open it at startup.

```bash
dotnet run --project samples/UnoDraw/UnoDraw.csproj -f net10.0-desktop
```

The WebAssembly target is opt-in. Publish it by hand and serve the `wwwroot`
folder of the output from any static server:

```bash
dotnet workload install wasm-tools
dotnet publish samples/UnoDraw/UnoDraw.csproj -c Release -f net10.0-browserwasm \
  -p:UnoDrawTargetFrameworks=net10.0-browserwasm -o artifacts/unodraw-browser
```

The browser package uses relative asset URLs (`WasmShellWebAppBasePath=./`) and
disables WebAssembly threads, so it runs from a subfolder without cross-origin
isolation headers. Its manifest's start URL and scope are also relative, keeping
installation within that folder. Browser file saving uses a file picker or
download according to browser support. `tests/UnoDraw.BrowserSmoke/README.md`
shows how to run the browser checks against that output.

## Uno control sample

The Uno sample app lives at `samples/UnoSvgSkiaSample` and stays out of `Svg.Skia.slnx`, so the default repository build and test workflow remains workload-free.

## Prerequisite workloads

```bash
uno-check --target desktop --target web --target android --target ios
```

## Desktop

```bash
dotnet build samples/UnoSvgSkiaSample/UnoSvgSkiaSample.csproj -c Release -f net10.0-desktop
```

## WebAssembly

```bash
dotnet publish samples/UnoSvgSkiaSample/UnoSvgSkiaSample.csproj -c Release -f net10.0-browserwasm
```

## Android

```bash
dotnet build samples/UnoSvgSkiaSample/UnoSvgSkiaSample.csproj -c Release -f net10.0-android
```

## iOS on macOS

```bash
dotnet build samples/UnoSvgSkiaSample/UnoSvgSkiaSample.csproj -c Release -f net10.0-ios
```

## What to verify manually

- asset loading through `Path`
- inline SVG through `Source`
- shared `SvgSource` resource usage
- runtime CSS switching
- zoom, pan, and hit testing
- wireframe and filter toggle behavior
