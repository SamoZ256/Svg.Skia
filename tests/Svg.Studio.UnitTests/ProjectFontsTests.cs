using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Svg.CodeGen.Skia.Projects;
using Svg.Skia;
using Svg.Skia.TypefaceProviders;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// The fonts a project measures its text with: those its package holds, then a folder beside it,
/// then the machine's — and what an export says of a face they do not hold.
/// </summary>
/// <remarks>
/// With resvg's committed fonts, which no CI machine has installed, so a test that passes is one
/// where the project's file was used and not the machine's.
/// </remarks>
public class ProjectFontsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() => Scratch.Delete(_directory);

    /// <summary>One of resvg's test fonts, which the suite reads from its submodule.</summary>
    internal static string Font(string name)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../externals/resvg/crates/resvg/tests/fonts", name));

        Assert.True(File.Exists(path), $"{path} is missing: run git submodule update --init --recursive.");

        return path;
    }

    /// <summary>A drawing of one end-anchored text, whose x is its width and whose y is its cap height.</summary>
    internal static string Text(string text, string font, string size = "14") => $"""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 40" width="120" height="40">
          <text x="110" y="20" text-anchor="end" dominant-baseline="cap-middle" font-size="{size}" {font}>{text}</text>
        </svg>
        """;

    private string Package(string body, string attributes = "", params string[] fonts)
    {
        var folder = TestPackage.Write(Path.Combine(_directory, "icons.svgstudio"), $"""
            <studio namespace="Demo.Icons"{attributes}>
            {body}
            </studio>
            """);

        Hold(folder, "fonts", fonts);

        return folder;
    }

    private static void Hold(string folder, string inside, params string[] fonts)
    {
        foreach (var font in fonts)
        {
            Directory.CreateDirectory(Path.Combine(folder, inside));
            File.Copy(Font(font), Path.Combine(folder, inside, font));
        }
    }

    private static string Drawing(string name, string svg) => $"""
          <drawing name="{name}">
        {svg}
          </drawing>
        """;

    /// <summary>The project exported, as the C# and as the warnings the build said.</summary>
    private (string Code, List<string> Warnings) Export(ProjectDocument document)
    {
        var target = Path.Combine(_directory, $"Icons{Guid.NewGuid():N}.g.cs");
        var log = new List<string>();

        document.Export(target, SvgTextLayout.Baked, log.Add);

        return (File.ReadAllText(target), log.Where(line => line.StartsWith("warning: ", StringComparison.Ordinal)).ToList());
    }

    [Fact]
    public void The_Fonts_A_Package_Holds_Are_What_Its_Text_Is_Measured_With()
    {
        var folder = Package(Drawing("badge", Text("Yellowtail", "font-family=\"Yellowtail\"")), fonts: new[] { "Yellowtail-Regular.ttf" });
        var document = ProjectDocument.Load(folder);

        Assert.Equal(new[] { "fonts/Yellowtail-Regular.ttf" }, document.Fonts.Keys);
        Assert.True(document.Fonts["fonts/Yellowtail-Regular.ttf"].Span.SequenceEqual(File.ReadAllBytes(Font("Yellowtail-Regular.ttf"))));

        var providers = document.TypefaceProviders();

        Assert.Equal("Yellowtail", Assert.IsType<CustomTypefaceProvider>(providers[0]).FamilyName);
        Assert.IsType<FontManagerTypefaceProvider>(providers[^2]);
        Assert.IsType<DefaultTypefaceProvider>(providers[^1]);
        Assert.Same(providers, document.TypefaceProviders());

        var (code, warnings) = Export(document);

        Assert.Empty(warnings);

        // The build svgc runs, given the font by hand: what the project's fonts are for is exactly this.
        var project = document.Flatten();
        var settings = SvgcBuildSettings.For(project);
        var target = Path.Combine(_directory, "ByHand.g.cs");

        settings.SingleFile = target;
        settings.TextLayout = SvgTextLayout.Baked;
        SvgcProjectBuild.Run(project, settings, new SkiaSvgAssetLoader(new SkiaModel(new SKSvgSettings
        {
            TypefaceProviders = new List<ITypefaceProvider> { new CustomTypefaceProvider(Font("Yellowtail-Regular.ttf")), new FontManagerTypefaceProvider(), new DefaultTypefaceProvider() }
        })));

        Assert.Equal(File.ReadAllText(target), code);

        Directory.Delete(Path.Combine(folder, "fonts"), recursive: true);

        Assert.NotEqual(code, Export(ProjectDocument.Load(folder)).Code);
    }

    /// <summary>
    /// Straight inside the root's <c>fonts</c> folder, and an .otf or a .ttf: what else is there is
    /// somebody's, and a save leaves it alone.
    /// </summary>
    [Fact]
    public void Only_A_Font_Straight_Inside_The_Fonts_Folder_Is_One_Of_The_Projects()
    {
        var folder = Package(Drawing("badge", Text("A", "font-family=\"Noto Sans\"")));
        var regular = File.ReadAllBytes(Font("NotoSans-Regular.ttf"));
        var strays = new[] { "fonts/._A.ttf", "fonts/A.ttf.~tmp", "fonts/sub/B.ttf", "fonts/readme.txt", "D.ttf", "Large/fonts/D.ttf" };

        Directory.CreateDirectory(Path.Combine(folder, "fonts"));
        File.WriteAllBytes(Path.Combine(folder, "fonts", "A.TTF"), regular);

        foreach (var stray in strays)
        {
            var path = Path.Combine(folder, stray.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, regular);
        }

        var document = ProjectDocument.Load(folder);

        Assert.Equal(new[] { "fonts/A.TTF" }, document.Fonts.Keys);
        Assert.DoesNotContain(document.Root.Children, child => child.Name is "fonts" or "Large");

        document.Root.Namespace = "Demo.Other";
        document.Save();

        Assert.All(strays.Append("fonts/A.TTF"), stray => Assert.True(File.Exists(Path.Combine(folder, stray.Replace('/', Path.DirectorySeparatorChar))), stray));
    }

    /// <summary>
    /// A group at the root called fonts was one before fonts were: it moves to <c>fonts~2</c> on the
    /// next save, and the fonts stay where they are.
    /// </summary>
    [Fact]
    public void A_Root_Group_Called_Fonts_Moves_Out_Of_The_Fonts_Folder()
    {
        var folder = Path.Combine(_directory, "icons.svgstudio");

        Directory.CreateDirectory(Path.Combine(folder, "fonts"));
        File.WriteAllText(Path.Combine(folder, "studio.xml"), "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<studio>\n</studio>\n");
        File.WriteAllText(Path.Combine(folder, "fonts", "a.svg"), """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><rect width="24" height="24" /></svg>""" + "\n");
        File.Copy(Font("NotoSans-Regular.ttf"), Path.Combine(folder, "fonts", "NotoSans-Regular.ttf"));

        var document = ProjectDocument.Load(folder);

        Assert.Equal("fonts", Assert.Single(document.Root.Children).Name);
        Assert.Equal(new[] { "fonts/NotoSans-Regular.ttf" }, document.Fonts.Keys);

        document.Save();

        Assert.True(File.Exists(Path.Combine(folder, "fonts~2", "a.svg")));
        Assert.False(File.Exists(Path.Combine(folder, "fonts", "a.svg")));
        Assert.True(File.Exists(Path.Combine(folder, "fonts", "NotoSans-Regular.ttf")));

        var again = ProjectDocument.Load(folder);

        Assert.Equal("fonts", Assert.Single(again.Root.Children).Name);
        Assert.Equal(new[] { "fonts/NotoSans-Regular.ttf" }, again.Fonts.Keys);
    }

    /// <summary>
    /// Said of every face the project's fonts lack, whether or not this machine has it: what is
    /// said has to be the same on every machine, since what is measured is.
    /// </summary>
    [Fact]
    public void A_Face_The_Fonts_Lack_Is_Warned_Of_On_Every_Machine()
    {
        string Texts(params string[] fonts) => $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 200" width="120" height="200">
            {string.Concat(fonts.Select((font, at) => $"<text x=\"4\" y=\"{20 + at * 20}\" font-size=\"14\" {font}>Ab</text>\n"))}
            </svg>
            """;

        var folder = Package(
            Drawing("badge", Texts(
                "font-family=\"Noto Sans\"",
                "font-family=\"Noto Sans\" font-weight=\"bold\"",
                "font-family=\"&quot;Noto Sans&quot;\"",
                "font-family=\"'Noto Sans', sans-serif\"",
                "font-family=\"Noto Sans\" font-weight=\"600\"",
                "font-family=\"Noto Sans\" font-weight=\"600\"",
                "font-family=\"Noto Sans\" font-style=\"italic\"",
                "font-family=\"noto sans\"",
                "font-family=\"Arial\"",
                "font-family=\"sans-serif\"",
                ""))
            + Drawing("other", Texts("font-family=\"Noto Sans\" font-weight=\"600\"")),
            fonts: new[] { "NotoSans-Regular.ttf", "NotoSans-Bold.ttf" });

        var (_, warnings) = Export(ProjectDocument.Load(folder));
        const string Held = "(Noto Sans 400, 700)";

        Assert.Equal(
            new[]
            {
                $"warning: badge: 'Noto Sans' 600 is not among the project's fonts {Held}, so each machine measures it with a font of its own.",
                $"warning: badge: 'Noto Sans' 400 italic is not among the project's fonts {Held}, so each machine measures it with a font of its own.",
                $"warning: badge: 'noto sans' 400 is not among the project's fonts {Held}, so each machine measures it with a font of its own.",
                $"warning: badge: 'Arial' 400 is not among the project's fonts {Held}, so each machine measures it with a font of its own.",
                $"warning: badge: 'sans-serif' 400 is not among the project's fonts {Held}, so each machine measures it with a font of its own.",
                $"warning: badge: a text naming no font-family is not among the project's fonts {Held}, so each machine measures it with a font of its own.",
                $"warning: other: 'Noto Sans' 600 is not among the project's fonts {Held}, so each machine measures it with a font of its own."
            },
            warnings);
    }

    [Fact]
    public void A_Character_The_Projects_Font_Lacks_Is_Warned_Of()
    {
        var folder = Package(Drawing("badge", Text("A漢", "font-family=\"Noto Sans\"")), fonts: new[] { "NotoSans-Regular.ttf" });

        Assert.Equal(
            "warning: badge: 'Noto Sans' 400 has no '漢' (U+6F22), so each machine draws it from a font of its own.",
            Assert.Single(Export(ProjectDocument.Load(folder)).Warnings));
    }

    /// <summary>With no fonts of its own a project measures with the machine's, and says only of a family the machine lacks.</summary>
    [Fact]
    public void Without_Fonts_Only_A_Family_The_Machine_Lacks_Is_Warned_Of()
    {
        var folder = Package(
            Drawing("missing", Text("A", "font-family=\"Not Installed 7f3a\""))
            + Drawing("generic", Text("A", "font-family=\"sans-serif\""))
            + Drawing("default", Text("A", $"font-family=\"{SkiaSharp.SKTypeface.Default.FamilyName}\""))
            + Drawing("none", Text("A", "")));

        var warning = Assert.Single(Export(ProjectDocument.Load(folder)).Warnings);

        Assert.StartsWith("warning: missing: 'Not Installed 7f3a' is not on this machine, so its text is measured with '", warning, StringComparison.Ordinal);
        Assert.EndsWith("' instead; adding the font to the project measures it the same everywhere.", warning, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> Problems()
    {
        yield return new object[] { "pointer", "", "fonts/Pointer.otf is not a font Skia can read; a Git LFS pointer that was never pulled reads like this." };
        yield return new object[] { "missing", " fontsFolder=\"../missing\"", "The fonts folder '../missing' is not there: " };
        yield return new object[] { "empty", " fontsFolder=\"../empty\"", "The fonts folder '../empty' holds no .otf or .ttf file." };
        yield return new object[] { "unsaved", " fontsFolder=\"../fonts\"", "The fonts folder '../fonts' is found from the project's folder, which it has none of until it is saved; its text is measured without it." };
    }

    /// <summary>What is wrong with the fonts themselves is said once an export, before any drawing.</summary>
    [Theory]
    [MemberData(nameof(Problems))]
    public void What_Is_Wrong_With_The_Fonts_Is_Said_Once(string kind, string attributes, string said)
    {
        var folder = Package(Drawing("badge", """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><rect width="24" height="24" /></svg>"""), attributes);

        Directory.CreateDirectory(Path.Combine(_directory, "empty"));
        File.WriteAllText(Path.Combine(_directory, "empty", "readme.txt"), "not a font");
        Directory.CreateDirectory(Path.Combine(folder, "fonts"));
        File.WriteAllText(Path.Combine(folder, "fonts", "Pointer.otf"), kind == "pointer" ? "version https://git-lfs.github.com/spec/v1\noid sha256:0\nsize 1\n" : string.Empty);

        if (kind != "pointer")
        {
            File.Delete(Path.Combine(folder, "fonts", "Pointer.otf"));
        }

        var document = kind == "unsaved" ? ProjectDocument.Read(ProjectPackage.Read(folder), null) : ProjectDocument.Load(folder);
        var problem = Assert.Single(document.FontProblems());

        Assert.StartsWith(said, problem, StringComparison.Ordinal);
        Assert.Equal($"warning: {problem}", Assert.Single(Export(document).Warnings));
    }

    [Fact]
    public void The_Projects_Fonts_Come_First_Then_The_Folder_Then_The_Machines()
    {
        var folder = Package(string.Empty, " fontsFolder=\"../fonts\"", new[] { "NotoSans-Bold.ttf" });

        Hold(_directory, "fonts", "NotoSans-Regular.ttf");

        var document = ProjectDocument.Load(folder);
        var providers = document.TypefaceProviders();
        var faces = providers.OfType<CustomTypefaceProvider>().Select(face => face.Typeface!.FontWeight).ToList();

        Assert.Equal(new[] { 700, 400 }, faces);
        Assert.Equal(4, providers.Count);
        Assert.Same(providers, document.TypefaceProviders());

        document.Root.FontsFolder = "../elsewhere";

        Assert.NotSame(providers, document.TypefaceProviders());
        Assert.Single(document.TypefaceProviders().OfType<CustomTypefaceProvider>());

        // The same faces as before, made once for every set that holds the same bytes.
        document.Root.FontsFolder = "../fonts";

        Assert.Equal(providers.OfType<CustomTypefaceProvider>(), document.TypefaceProviders().OfType<CustomTypefaceProvider>());
    }

    /// <summary>A project read as text alone never had its folder's fonts, so its save leaves them where they are.</summary>
    [Fact]
    public void A_Project_Read_As_Text_Leaves_The_Folders_Fonts_Alone()
    {
        var folder = Package(string.Empty, fonts: new[] { "NotoSans-Regular.ttf" });
        var document = ProjectDocument.Read(ProjectPackage.Read(folder), folder);

        Assert.Empty(document.Fonts);

        document.Root.Namespace = "Demo.Other";
        document.Save();

        Assert.True(File.Exists(Path.Combine(folder, "fonts", "NotoSans-Regular.ttf")));
    }

    [Fact]
    public void A_Project_Saved_Elsewhere_Takes_Its_Fonts_Along()
    {
        var folder = Package(string.Empty, fonts: new[] { "Yellowtail-Regular.ttf" });
        var other = Path.Combine(_directory, "copy.svgstudio");

        ProjectDocument.Load(folder).Save(other);

        Assert.Equal(File.ReadAllBytes(Font("Yellowtail-Regular.ttf")), File.ReadAllBytes(Path.Combine(other, "fonts", "Yellowtail-Regular.ttf")));
        Assert.Equal(new[] { "fonts/Yellowtail-Regular.ttf" }, ProjectDocument.Load(other).Fonts.Keys);
    }

    /// <summary>A font pulled in under an open project is a change on disk, and a save does not put the old one back.</summary>
    [Fact]
    public void A_Font_Changed_Under_The_Project_Survives_A_Save()
    {
        var folder = Package(string.Empty, fonts: new[] { "NotoSans-Regular.ttf" });
        var document = ProjectDocument.Load(folder);
        var font = Path.Combine(folder, "fonts", "NotoSans-Regular.ttf");

        Assert.False(document.ChangedOnDisk());

        File.Copy(Font("NotoSans-Bold.ttf"), font, overwrite: true);

        Assert.True(document.ChangedOnDisk());

        document.Root.Namespace = "Demo.Other";
        document.Save();

        Assert.Equal(File.ReadAllBytes(Font("NotoSans-Bold.ttf")), File.ReadAllBytes(font));
    }

    /// <summary>Read and let go of: a fonts folder can be deleted, or pulled over, while the project is open.</summary>
    [Fact]
    public void A_Fonts_Folder_Beside_The_Project_Is_Measured_With_And_Left_Unlocked()
    {
        var folder = Package(Drawing("badge", Text("Ab", "font-family=\"Noto Sans\"")), " fontsFolder=\"../fonts\"");

        Hold(_directory, "fonts", "NotoSans-Regular.ttf");

        var document = ProjectDocument.Load(folder);

        Assert.Empty(Export(document).Warnings);

        Directory.Delete(Path.Combine(_directory, "fonts"), recursive: true);

        Assert.Equal("../fonts", document.Root.FontsFolder);

        document.Save();

        Assert.Contains("fontsFolder=\"../fonts\"", File.ReadAllText(Path.Combine(folder, "studio.xml")), StringComparison.Ordinal);
        Assert.Equal("../fonts", ProjectDocument.Load(folder).Root.FontsFolder);
    }

    /// <summary>
    /// The numbers an export writes for the project's own fonts, recorded once and the same on
    /// every machine CI runs on: the font file decides them, not the machine.
    /// </summary>
    /// <remarks>
    /// If one machine disagrees, the project's fonts are not the whole of what decides a measurement,
    /// and that is the bug: do not loosen this to make it pass.
    /// </remarks>
    [Fact]
    public void An_Export_Writes_The_Same_Numbers_On_Every_Machine()
    {
        var folder = Package(
            Drawing("regular", Text("AB12", "font-family=\"Noto Sans\""))
            + Drawing("bold", Text("AB12", "font-family=\"Noto Sans\" font-weight=\"bold\"")),
            fonts: new[] { "NotoSans-Regular.ttf", "NotoSans-Bold.ttf" });
        var (code, warnings) = Export(ProjectDocument.Load(folder));

        Assert.Empty(warnings);

        // Where the end anchor and the cap height put each, the width being the face's own.
        Assert.Equal(
            new[] { "74.91797f, 24.998001f", "75.92969f, 24.998001f" },
            Regex.Matches(code, @"DrawText\(""AB12"", ([^,]+, [^,]+),").Select(found => found.Groups[1].Value));
    }
}
