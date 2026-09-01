---
title: "Source Generator and svgc"
---

# Source Generator and svgc

Svg.Skia has two ways of turning a drawing into C#. Both compile the document to a
`ShimSkiaSharp.SKPicture` and emit the SkiaSharp calls that replay it, through the same
[Svg.CodeGen.Skia](../packages/svg-codegen-skia) — what differs is when they run and how much of the
generator they expose.

- **`Svg.SourceGenerator.Skia`** runs inside the compiler, on `.svg` files the project already owns.
- **`src/svgc`** is a command-line tool run as its own step, and reaches the options the
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
declares become C# parameters in the order the document declares them, with their declared defaults,
and there is **no** `Picture` property — there is no single picture to have, since the drawing depends
on its arguments:

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

`src/svgc` is a command-line front end over the same generator, packaged as a `dotnet` tool. From a
clone it is run from the repository:

```bash
dotnet run --project src/svgc/svgc.csproj -c Release -- --help
```

Build it once and invoke the assembly directly if you are running it repeatedly:

```bash
dotnet build src/svgc/svgc.csproj -c Release
dotnet src/svgc/bin/Release/net10.0/svgc.dll --help
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
a flag width joining a project scale would be a contradiction rather than an override. A scale is
one factor for both axes and cannot be given beside a width or a height, which say the same thing a
second way.

`src/Svg.Studio` does the same arithmetic from **Edit → Resize…**, padding included, and writes the
answer into the drawing's own `width`, `height` and `viewBox` rather than into generated code — so a
document resized there arrives at svgc already the size it should be.

### Leaving room around it

```bash
svgc -i ./Assets/icon.svg -o ./Generated/Icon.cs --width 512 --padding 10%
```

`--padding` leaves space around the drawing **inside** the size it was given: that command produces a
512×512 picture whose art occupies the middle 410×410, so `--width` still describes the file you get.
Values are fractions of the target — `10%` or `0.1` say the same thing — which means one setting fits
every size a batch is generated at. A bare `10` is a fraction too, so it asks for ten times the
canvas and is refused rather than quietly read as ten percent.

Sides are written the way CSS writes them, one, two, three or four values:

```bash
svgc … --padding "10%"              # every side
svgc … --padding "5% 10%"           # down, across
svgc … --padding "5% 10% 0 10%"     # top, right, bottom, left
```

**It never crops.** The space is added outside the frame the document declares, so a drawing whose
author already left it room keeps that room and gets more — padding is a floor, not a target. Where
the drawing's shape and the size asked for disagree, what the aspect ratio leaves over centres, the
same way an unpadded mismatch does, so a side may end up with more clear space than it asked for and
never less.

Unlike the three sizing values, `--padding` overlays on its own: it says how much room to leave
rather than what size to be, so naming it does not replace a project file's width.

### When the defaults cannot come along

C# takes optional arguments last. A drawing that declares a parameter *without* a default after one
*with* a default cannot have both its order and its defaults, so `svgc` keeps the order and gives up
the defaults: every argument is generated as required, and it says so.

```
warning: badge.svg declares a parameter with no default after one that has a default, so every
argument is generated as required and 'tint' loses its default.
```

The source generator reports the same thing as **SVG0002**, since it runs inside the compiler and has
nowhere to print. The generated file carries a comment above the class for whoever reads the
signature and wonders.

The order is kept rather than the defaults because the order is what a positional call means, and
what a reader matches against the `<e:param>` block. Losing a default is a compile error where the
caller can see it; a silently reordered argument list is not. Declaring the parameters without
defaults first avoids the whole question.

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
<!-- icons.svgcproj -->
<svgc>
  <recipe>icons.recipe</recipe>
  <namespace>Demo.Icons</namespace>
  <singleFile>Icons.cs</singleFile>
  <cache>lastValue</cache>
  <padding>10%</padding>

  <svg input="home.svg" class="Home" />
  <svg input="badge.svg" class="Badge" padding="0" />

  <group namespace="Demo.Icons.Large" scale="2">
    <svg input="badge.svg" class="BadgeLarge" />
  </group>
</svgc>
```

```bash
svgc --projectFile ./icons.svgcproj
```

- Every setting at the top is a default each `<svg>` may override, so a shared recipe or namespace is
  named once and an item only says what differs. `padding` is one of them, and unlike `width`,
  `height` and `scale` it overrides on its own: the drawing above keeps the project's sizing while
  asking for no room around it.
- A `<group>` is folded into its drawings as the project is read, so a drawing carries what its
  groups said as if it had said it itself.
- `<singleFile>` folds every drawing into one C# file, which also lets them share one copy of the
  emitted expression helpers. Without it, each `<svg>` needs its own `output`.
- A command line flag beats the project file, which beats the built-in default.

`src/Svg.Studio` opens a project as well as a drawing — `File → Open`, a drop, or as a command line
argument. It opens as a workspace rather than as a document: the group tree goes in a pane beside the
tabs and the project itself takes no tab, one project at a time, closed with `Project → Close`.
Clicking a node opens it — a drawing in a viewer **at the size its groups build it at** rather than
the one it was written with, a group as its settings and what they come to. A double click is left
to the tree, which folds a node with it. Settings are edited
there and saved back with the file's comments and layout intact. The resize is applied to the picture
and never to the file, so what a group says about a drawing stays the group's.

A drawing under a `recipe` is shown **as the recipe makes it** — the colours the recipe names already
turned into expressions, and its parameters on the panel with sliders on them, which is the whole
loop a recipe is written for. The file is untouched: the source pane shows, edits and saves the
drawing itself, and `File → Export…` writes what the project builds, which is the document
`--emit svg` produces. The parameter panel writes into the **recipe** there rather than into the
drawing — the parameters are the recipe's, so adding one, editing one, reordering them or committing
the values as defaults all go back to the file that declared them. Written into the drawing they
would be a declaration block of its own, and a recipe refuses a document that already has one.
A recipe that cannot be read, or that this drawing refuses, is said on the status line under the
drawing, which still opens.

A `recipe` is named with buttons rather than typed as a path. With none, the row offers **Add…** for
one that exists and **New…** to write one — a recipe that does not exist yet cannot be picked, and
leaving for a text editor to make an empty one was most of what made recipes awkward to start using.
What **New…** writes applies as it stands: it declares a `hue` and an `accent` computed from it and
recolours nothing, so there is a slider to drag before a line of it has been edited. Underneath it
names **the colours the drawings under that node actually paint**, one commented `<replace>` each, so
starting a recipe no longer means reading them out of the files yourself. Commented because the file
has to apply as it stands — binding them all to the one let above would repaint the whole set the
moment it was made. A file already at that name is named rather than written over.
With one named, the row is the file and a **✕** that stops using it — the file itself is left where
it is. **Double-clicking the file opens it**, in a tab of its own: the recipe as text, coloured the
way the source pane colours a drawing, with what the parser makes of it said underneath as it is
typed. One recipe is usually named by several groups, so it opens once however many of them ask for it. A
drawing's tab answers for the recipe behind it as well as for itself: it takes the unsaved mark, ⌘S
saves it, ⌘Z takes back the last edit made to it once the drawing's own text has nothing left to take
back, and closing the project asks about it — including when the tab it was edited from has gone.

There is one buffer per open recipe, so the drawings under it follow it **as it is typed** rather
than when it is saved — a tab you are not looking at is read again when you come back to it.

A drawing under a recipe also gets a **Colours** tab beside its Project and Parameters: every colour
the drawing paints with, how much of the drawing each one is, and the expression the recipe gives it.
Typing one writes the `<replace>` rule; emptying the box takes it away. What the expression comes to
is read out beside it and follows the sliders, and what is wrong with one is said under the box it was
typed in — checked as a colour, since a rule's body lands in `fill`, `stroke` and `stop-color`, so an
expression that is well formed and the wrong type is caught here rather than by the drawing. Nothing
is written while a row has trouble. A rule for a colour this
drawing does not have is listed underneath as "not in this drawing" rather than hidden, since one
recipe covers a family and a rule is for whichever of them has the colour. The list is what the
build would act on: it comes from the same walk that does the rewriting, so a colour under a `style`
declaration is offered and the dead attribute beneath it is not.

The tree is editable. Each row carries **Add group**, **Add SVG…** and **Remove**; `Delete` removes
the selected row, and a row is dragged to move it — dropped on the top or bottom quarter of a group
it lands beside it, and in the middle it goes inside. A drawing's own settings — its `output`, its
`class`, anything it overrides — are a **Project** tab in the right pane of the drawing's own tab,
in front of the parameters the drawing declares for itself: the same pane a group keeps its settings
in, saved the same way, and the tab a drawing opened from the tree lands on. A project usually builds one file several times with nothing but the class to
tell the rows apart, so this is the only place those rows can be told apart at all.

Adding, removing and moving write the file as they are made; settings are held until the tab is
saved, like everything else in a tab. A group is confirmed before it is removed, and there is no
undo — the project is a text file, and reverting one is what a version control system is for.

`Project → Build` writes the project's outputs, exactly as `svgc --projectFile` would — through the
same build, so the two cannot come to disagree. Recipes are applied, since the output would not be
what `svgc` produces otherwise, and what a recipe matched or failed to match is reported along with
the files written.

### Options

| Option | |
| --- | --- |
| `-i`, `--inputFile` / `-o`, `--outputFile` | One drawing in, one file out |
| `-p`, `--projectFile` | A whole build, described in XML |
| `-r`, `--recipeFile` | A recipe applied to the input before generating |
| `-n`, `--namespace` / `-c`, `--class` | Names for the generated type; `Svg` and `Generated` by default |
| `--emit` | `csharp` (default), or `svg` for the document the recipe produced |
| `--width` / `--height` / `--scale` | Resize the document before it is compiled |
| `--padding` | Room to leave around the drawing inside that size, in CSS order: `10%`, `"5% 10%"`, `"5% 10% 0 10%"` |
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
