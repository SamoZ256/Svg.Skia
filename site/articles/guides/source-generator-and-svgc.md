---
title: "Source Generator and svgc"
---

# Source Generator and svgc

Svg.Skia has two ways of turning a drawing into C#. Both compile the document to a
`ShimSkiaSharp.SKPicture` and emit the SkiaSharp calls that replay it, through the same
[Svg.CodeGen.Skia](../packages/svg-codegen-skia) — what differs is when they run and how much of the
generator they expose.

- **`Svg.SourceGenerator.Skia`** runs inside the compiler, on `.svg` files the project already owns.
- **`samples/svgc`** is a command-line tool run as its own step, and reaches the options the
  generator does not: recipes, resizing, batching, caching and the SkiaSharp target.

## Roslyn source generator

Add the package:

```xml
<ItemGroup>
  <PackageReference Include="Svg.SourceGenerator.Skia" Version="*" />
</ItemGroup>
```

Add SVG assets as `AdditionalFiles` — that item type is what the generator looks at, not `Content`
or `EmbeddedResource`:

```xml
<ItemGroup>
  <AdditionalFiles Include="Assets\**\*.svg" />
</ItemGroup>
```

Metadata names the generated type:

```xml
<ItemGroup>
  <AdditionalFiles Include="Assets\Camera.svg"
                   ClassName="Camera"
                   NamespaceName="Svg.Generated" />
</ItemGroup>
```

Neither is required. `NamespaceName` falls back to the project-wide `NamespaceName` MSBuild
property, and then to `Svg`; `ClassName` falls back to the file's own name prefixed with `Svg_`,
with anything not valid in an identifier replaced by `_` — `pservers-pattern-01-b.svg` becomes
`Svg_pservers_pattern_01_b`.

### What comes out

For an ordinary drawing, a static picture built once at type initialisation:

```csharp
public static class Camera
{
    public static SKPicture Picture { get; }
    public static void Draw(SKCanvas skCanvas)
}
```

For a drawing using the [expression extension](../packages/svg-expressions), the parameters it
declares become C# parameters with their declared defaults, and there is **no** `Picture` property —
there is no single picture to have, since the drawing depends on its arguments:

```csharp
public static class Expressions
{
    public static SKPicture Record(float t = 0f, float amp = 1f, bool bold = false)
    public static void Draw(SKCanvas skCanvas, float t = 0f, float amp = 1f, bool bold = false)
}
```

The expressions themselves are folded into the emitted code, so `hsl`, `mix` and `clamp` are
evaluated as C# at call time; nothing is re-parsed and no SVG text ships.

### Diagnostics

Failures are reported as `SVG0001` on the build rather than as a crash. A mistake in an expression —
a name nothing declares, a range on a colour — is reported on its own, without a stack trace, and
says which expression it was in.

### Inside this repository

The sample at `samples/Svg.SourceGenerator.Skia.Sample` references the generator project directly
rather than the package, which means importing the `.props` by hand — the published package does it
for you:

```xml
<ProjectReference Include="..\..\src\Svg.SourceGenerator.Skia\Svg.SourceGenerator.Skia.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="False" />

<Import Project="..\..\src\Svg.SourceGenerator.Skia\Svg.SourceGenerator.Skia.props" />
```

It also sets `EmitCompilerGeneratedFiles`, which writes the generated `.cs` under `obj/` where it can
be read — the quickest way to see what any of this produces.

## Manual code generation with svgc

`samples/svgc` is a command-line front end over the same generator. It is a sample project rather
than a published tool, so it is run from the repository:

```bash
dotnet run --project samples/svgc/svgc.csproj -c Release -- --help
```

Build it once and invoke the assembly directly if you are running it repeatedly:

```bash
dotnet build samples/svgc/svgc.csproj -c Release
dotnet samples/svgc/bin/Release/net10.0/svgc.dll --help
```

`svgc` below stands for whichever of those two you are using.

### One file

```bash
svgc --inputFile ./Assets/icon.svg \
     --outputFile ./Generated/Icon.cs \
     --namespace Svg.Generated \
     --class Icon
```

The short forms are `-i`, `-o`, `-n` and `-c`. The defaults are the namespace `Svg` and the class
`Generated`.

### Resizing as it generates

```bash
svgc -i ./Assets/icon.svg -o ./Generated/Icon.cs --width 512
```

`--width`, `--height` and `--scale` resize the *document* before it is compiled, so the picture is
genuinely built at the new size rather than scaled by a matrix wrapped around the old one. They are
one group: naming any of them on the command line replaces the project file's sizing outright, since
a flag width joining a project scale would be a contradiction rather than an override.

### Recipes: making a flat drawing parametric

A recipe declares parameters and named expressions, then says which literal colours of a drawing
stand for them:

```xml
<recipe xmlns="https://svg.skia/expr/1.0">
  <code>
    <param name="hue"  type="number"  default="217" />
    <param name="bold" type="boolean" default="false" />

    <let name="primary">hsl(hue, 91%, bold ? 66% : 60%)</let>
    <let name="deep">hsl(hue + 5, 71%, 40%)</let>
  </code>

  <replace color="#3b82f6">primary</replace>
  <replace color="rgb(30,64,175)">deep</replace>
</recipe>
```

`--recipeFile` (`-r`) applies it to the input before anything is generated, which is how one recipe
parameterises a whole icon set. Colours are matched by value rather than by spelling, so `#3B82F6`
and `#3b82f6` are one rule, and a colour written inside a `style="…"` declaration is lifted out into
a real attribute — a placeholder has to live on one.

```bash
svgc -i badge.svg -o Badge.cs -r badge.recipe -n Demo.Icons -c Badge
```

```
Generating: /tmp/demo/Badge.cs
  #3b82f6 -> {{ primary }} (3)
  rgb(30,64,175) -> {{ deep }} (2)
```

The generated class takes the recipe's parameters, exactly as a drawing that declared them itself
would:

```csharp
public static SKPicture Record(float hue = 217f, bool bold = false)
public static void Draw(SKCanvas skCanvas, float hue = 217f, bool bold = false)
```

`--emit svg` asks for the rewritten document instead of code — useful for checking what a recipe did,
and for producing a parametric `.svg` to open in the [viewer](../packages/svg-viewer-skia-avalonia):

```bash
svgc -i badge.svg -o badge.parametric.svg -r badge.recipe --emit svg
```

It needs a recipe (without one the output would be a copy of the input) and cannot be combined with
a resize or a single file: the conversion rewrites text and never compiles a drawing, and an SVG
document holds one drawing where a C# file holds any number.

### Caching the last picture

```bash
svgc -i badge.svg -o Badge.cs -r badge.recipe --cache lastValue
```

`--cache` gives `Draw` a one-entry memo, so redrawing with unchanged arguments reuses the picture
instead of recording it again — worth it for a parametric drawing on a slider or an animation.
`lastValueLocked` is the same guarded by a lock held across the draw, for a drawing shared between
threads. It applies only to parameterised drawings; one without parameters already caches better
than this, as a single picture built in the static constructor.

### A whole build in one project file

More than one drawing goes in an XML project file rather than a shell loop:

```xml
<!-- icons.svgc -->
<svgc>
  <recipe>icons.recipe</recipe>
  <namespace>Demo.Icons</namespace>
  <singleFile>Icons.cs</singleFile>
  <cache>lastValue</cache>

  <svg input="home.svg" class="Home" />
  <svg input="badge.svg" class="Badge" />

  <group namespace="Demo.Icons.Large" scale="2">
    <svg input="badge.svg" class="BadgeLarge" />
  </group>
</svgc>
```

```bash
svgc --projectFile ./icons.svgc
```

- Every setting at the top is a default each `<svg>` may override, so a shared recipe or namespace is
  named once and an item only says what differs.
- A `<group>` is folded into its drawings as the project is read, so a drawing carries what its
  groups said as if it had said it itself.
- `<singleFile>` folds every drawing into one C# file, which also lets them share one copy of the
  emitted expression helpers. Without it, each `<svg>` needs its own `output`.
- A command line flag beats the project file, which beats the built-in default.

### Options

| Option | |
| --- | --- |
| `-i`, `--inputFile` / `-o`, `--outputFile` | One drawing in, one file out |
| `-p`, `--projectFile` | A whole build, described in XML |
| `-r`, `--recipeFile` | A recipe applied to the input before generating |
| `-n`, `--namespace` / `-c`, `--class` | Names for the generated type; `Svg` and `Generated` by default |
| `--emit` | `csharp` (default), or `svg` for the document the recipe produced |
| `--width` / `--height` / `--scale` | Resize the document before it is compiled |
| `--singleFile` | Emit a batch into one C# file |
| `--helperScope` | Where shared helpers live in that file: `file` (default, C# 11 file-local), `internal`, or `perClass` |
| `--cache` | `none` (default), `lastValue`, `lastValueLocked` |
| `--skiaSharp` | The SkiaSharp major version the output is compiled against: `4` (default) or `3` |

`--helperScope internal` names the helper class after the output file, so two generated files can sit
in one assembly without colliding on it.

### It reports mistakes like a compiler

A fault in the input or the command line is a diagnostic, not a stack trace, and sets a non-zero exit
code:

```
error: Emitting svg needs a recipe. Pass -r, name one in the project, or emit csharp.

error: Unknown name 'nosuchname' (in scope: alert, bold, deep, hue, pi, primary, tau).
    nosuchname
    ^
```

A recipe rule that matched nothing is a **warning** and exits zero, because one recipe usually covers
a family of drawings and not every drawing uses every colour:

```
warning: nothing in badge.recipe matched '#00ff00'.
```

## When to use which

Use the source generator when:

- the SVG assets already live in the consuming project,
- incremental builds matter,
- you want generated files to stay implicit.

Use `svgc` when:

- generation is part of a separate asset-preparation step,
- you want checked-in generated files,
- a custom pipeline needs direct control over names and destinations,
- or you need something the generator does not expose: a recipe, a resize, a batch in one file, a
  draw cache, or SkiaSharp 3 as the target.

The two are not exclusive. A common arrangement is `svgc --emit svg` to parameterise an icon set
once, the resulting drawings checked in, and the source generator compiling them on every build.

## Related docs

- [Svg.CodeGen.Skia](../packages/svg-codegen-skia) — the generator both front ends call
- [Svg.SourceGenerator.Skia](../packages/svg-sourcegenerator-skia) — the package, and its project setup
- [Svg.Expressions](../packages/svg-expressions) — the `{{ … }}` format, its operators and functions
- [Svg.Viewer.Skia.Avalonia](../packages/svg-viewer-skia-avalonia) — opening a parametric drawing and driving it
