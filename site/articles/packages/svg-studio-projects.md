---
title: "Svg.Studio.Projects"
---

# Svg.Studio.Projects

`Svg.Studio.Projects` reads and writes [Svg Studio](../guides/svg-studio)'s `.svgstudio` projects,
and exports one to C# without Studio. It writes the same file Studio's `File → Export…` writes,
because the app calls it too. A build calls it to keep generated code in step with a project
somebody edits in Studio.

## Install

The package and the libraries it depends on are on nuget.org under the `SamoZ256.` prefix.
Upstream owns the unprefixed IDs there, and its builds lack this fork's API. Versions carry a
`-samoz.N` suffix, so name one explicitly:

```bash
dotnet add package SamoZ256.Svg.Studio.Projects --version 5.2.2-samoz.2
```

Only the package IDs carry the prefix: the assemblies are still `Svg.Skia.dll`, `Svg.Custom.dll`
and so on. An application that also gets upstream's `Svg.Skia` through another package ends up
with two copies of the same assembly, so keep upstream's packages out of the graph.

This assembly is not strong-named, unlike the libraries it uses. That is harmless on .NET 8 and
later, which do not validate strong names.

## Use it

```csharp
using Svg;
using Svg.Studio;

ProjectDocument.Load("Icons.svgstudio")
    .Export("obj/Icons.g.cs", SvgTextLayout.Baked, Console.WriteLine);
```

`Load` takes the project folder or any file inside it, such as its `studio.xml`. `Export` writes every
drawing into the one file it is given. The project names no output of its own, and everything else
comes from the project: namespaces, classes, sizes, the API class. It returns the files written. The
log hears what is read and written, and the warnings — the lines starting `warning:` — are worth
passing on.

A project that cannot be built throws `SvgcProjectException`. A faulty expression or recipe throws
what the build throws for it (`ExprException`, `SvgRecipeException`), and a file that cannot be
written throws an IO exception.

## The text layout

Text an expression drives cannot always be written out with the layout it was measured with. Studio
asks whoever exports, or follows their preference, so a build has to say:

| Layout | Text an expression drives |
| --- | --- |
| `Baked` | Written in at its default. Studio's usual answer. |
| `Relaxed` | Drawn from the argument, giving up the layout rules that cannot follow a string. |
| `Strict` | Refused, which stops the build. |

`Survey()` lists what `Baked` would freeze, and is empty for a project of ordinary icons. See
[text an export cannot write out](../guides/svg-studio#text-an-export-cannot-write-out).

Text is measured with Skia, as in Studio, so the package brings SkiaSharp's native library with it.

## Fonts

A project's text is measured with the fonts it holds — the `.otf` and `.ttf` files in the `fonts`
folder at the root of the `.svgstudio` — then with those of the folder `fontsFolder` names beside
it, and only then with the machine's. With the project's fonts every machine writes the same file,
and Studio and a build agree.

| Member | |
| --- | --- |
| `Fonts` | The fonts the project holds, by path inside it, as bytes. `Load` reads them; `Read(files, path)` holds none, so save over a package only a document `Load` read |
| `TypefaceProviders()` | What its text is measured with, for an `SKSvg` of your own: `svg.Settings.TypefaceProviders = new List<ITypefaceProvider>(project.TypefaceProviders())` |
| `FontProblems()` | A font Skia cannot read, a `fontsFolder` that is not there |
| `MissingFonts(picture)` | Each face or character a drawing's text asks for that the fonts do not hold |

An export warns of both: the fonts' problems, and each face a drawing asks for that the project's
fonts lack, even where the machine has it, since another machine would not. A project with no fonts
warns only of a family the machine lacks.

Keep fonts out of line-ending conversion with `*.otf binary` and `*.ttf binary` in
`.gitattributes`. A font kept in Git LFS that was never pulled is a pointer, which reads as a font
Skia cannot read. An older package measures without a project's fonts, and refuses a project that
sets `fontsFolder`.

## In an MSBuild build

A console project of one line does the export:

```csharp
Svg.Studio.ProjectDocument.Load(args[0]).Export(args[1], Svg.SvgTextLayout.Baked, Console.WriteLine);
```

The project that compiles the result runs it before compiling, and only when a file of the
`.svgstudio` — its fonts included — has changed since the last export. A `fontsFolder` beside it
wants its own line among the inputs:

```xml
<ItemGroup>
  <IconsProject Include="Icons.svgstudio/**" />
  <Compile Include="$(IntermediateOutputPath)Icons.g.cs" />
</ItemGroup>

<Target Name="ExportIcons" BeforeTargets="CoreCompile"
        Inputs="@(IconsProject)" Outputs="$(IntermediateOutputPath)Icons.g.cs">
  <Exec Command="dotnet run --project tools/ExportIcons -- Icons.svgstudio &quot;$(IntermediateOutputPath)Icons.g.cs&quot;" />
</Target>
```
