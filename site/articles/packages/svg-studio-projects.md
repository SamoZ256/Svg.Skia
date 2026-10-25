---
title: "Svg.Studio.Projects"
---

# Svg.Studio.Projects

`Svg.Studio.Projects` reads and writes [Svg Studio](../guides/svg-studio)'s `.svgstudio` projects,
and exports one to C# without Studio. It writes the same file Studio's `File → Export…` writes,
because the app calls it too. A build calls it to keep generated code in step with a project
somebody edits in Studio.

## Install

The package and the libraries it depends on are published to SamoZ256's GitHub Packages feed, not
nuget.org. Upstream owns most of these IDs on nuget.org, and its builds lack this fork's API, so a
restore that finds them there still succeeds and the export then fails at run time. Put a
`nuget.config` next to the consuming solution that maps those IDs to the feed:

```xml
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="github" value="https://nuget.pkg.github.com/SamoZ256/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <github>
      <add key="Username" value="SamoZ256" />
      <add key="ClearTextPassword" value="%GITHUB_PACKAGES_TOKEN%" />
    </github>
  </packageSourceCredentials>
  <packageSourceMapping>
    <packageSource key="github">
      <package pattern="Svg.*" />
      <package pattern="ShimSkiaSharp" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

GitHub Packages needs a token to restore, even from a public repository. Use a personal access
token with `read:packages`, set in `GITHUB_PACKAGES_TOKEN`. Versions carry a suffix upstream never
uses, so a cached upstream build cannot take their place:

```bash
dotnet add package Svg.Studio.Projects --version 5.2.2-samoz.1
```

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
Studio and a build on the same machine write the same file; text exported on another machine follows
the fonts installed there.

## In an MSBuild build

A console project of one line does the export:

```csharp
Svg.Studio.ProjectDocument.Load(args[0]).Export(args[1], Svg.SvgTextLayout.Baked, Console.WriteLine);
```

The project that compiles the result runs it before compiling, and only when a file of the
`.svgstudio` has changed since the last export:

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
