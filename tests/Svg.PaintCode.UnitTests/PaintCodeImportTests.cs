// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.IO;
using System.Linq;
using Svg.CodeGen.Skia.Projects;
using Xunit;

namespace Svg.PaintCode.UnitTests;

public class PaintCodeImportTests
{
    [Fact]
    public void A_Desk_Becomes_A_Folder_And_A_Canvas_Becomes_A_Drawing_In_It()
        => Imported((result, directory) =>
        {
            var file = Assert.Single(result.Files);

            Assert.Equal(Path.Combine(directory, "overlays", "overlay-error.svg"), file);
            Assert.True(File.Exists(file));
        });

    [Fact]
    public void The_Project_Names_Every_Drawing_Under_A_Group_For_Its_Desk()
        => Imported((result, directory) =>
        {
            var project = SvgcProjectDocument.Load(result.ProjectPath!);
            var group = Assert.IsType<SvgcProjectGroup>(Assert.Single(project.Root.Children));
            var drawing = Assert.IsType<SvgcProjectDrawing>(Assert.Single(group.Children));

            Assert.Equal("Icons", project.Root.Namespace);
            Assert.Equal("Icons.Overlays", group.Namespace);
            Assert.Equal("overlays/overlay-error.svg", drawing.Input);
            Assert.Equal("overlays/overlay-error.cs", drawing.Output);
            Assert.Equal("OverlayError", drawing.Class);
        });

    [Fact]
    public void A_Drawing_Is_Written_Without_A_Declaration_And_With_A_Trailing_Break()
        => Imported((result, _) =>
        {
            var text = File.ReadAllText(result.Files[0]);

            Assert.StartsWith("<svg", text);
            Assert.EndsWith("\n", text.Replace("\r\n", "\n"));
        });

    [Fact]
    public void A_Name_With_A_Space_In_It_Becomes_One_A_File_System_And_Csharp_Both_Take()
    {
        Assert.Equal("number-2-state", PaintCodeSlug.Of("number 2-state"));
        Assert.Equal("Number2State", PaintCodeSlug.Pascal("number 2-state"));
        Assert.Equal("_2State", PaintCodeSlug.Pascal("2-state"));
    }

    /// <summary>The one variable a kind document declares, as the expression format would take it.</summary>
    private static PaintCodeDeclaration Declared(int kind, int type)
        => PaintCodeDeclarations
            .Of(PaintCodeDocument.Parse(KindDocument.Bytes(kind, type)))
            .ByName["test"];

    [Fact]
    public void A_Fraction_Carries_The_Range_Its_Type_Implies()
    {
        // Nowhere in the document — no variable in a real one carries a limit at all — so it comes
        // from the kind, which is the only place PaintCode keeps it.
        var fraction = Declared(kind: 2, type: 2);

        Assert.Equal("number", fraction.Type);
        Assert.Equal(0d, fraction.Minimum);
        Assert.Equal(1d, fraction.Maximum);
    }

    [Theory]
    // A number and an angle are both unbounded: an angle is degrees and runs past 360 as readily as
    // a number runs past anything.
    [InlineData(0)]
    [InlineData(3)]
    public void A_Number_And_An_Angle_Are_Bounded_By_Nothing(int kind)
    {
        var declared = Declared(kind, type: 2);

        Assert.Equal("number", declared.Type);
        Assert.Null(declared.Minimum);
        Assert.Null(declared.Maximum);
    }

    [Theory]
    // The expression language has no such type, so refusing is right — but it should say which.
    [InlineData(6, 9, "point")]
    [InlineData(7, 11, "size")]
    [InlineData(8, 10, "rect")]
    public void A_Kind_The_Language_Has_No_Name_For_Is_Refused_By_Name(int kind, int type, string named)
    {
        var declared = Declared(kind, type);

        Assert.Equal(PaintCodeDeclarationKind.Unusable, declared.Kind);
        Assert.Contains(named, declared.Refusal);
    }

    /// <summary>
    /// The declared type of every variable, whether or not a drawing happens to use it.
    /// </summary>
    private static string? TypeOf(string name, bool integers)
        => PaintCodeDeclarations
            .Of(PaintCodeDocument.Parse(SampleDocument.Bytes()), integers)
            .ByName[name]
            .Type;

    [Fact]
    public void A_Whole_Number_Stays_A_Number_Unless_Integers_Are_Asked_For()
    {
        Assert.False(new PaintCodeImportOptions(".").Integers);
        Assert.Equal("number", TypeOf("level", integers: false));
    }

    [Fact]
    public void Integers_Retypes_A_Whole_Number_And_Guesses_While_It_Does()
    {
        // 'level' is a 0..1 fade whose value happened to be saved at 1, and this retypes it. That is
        // the cost of the option rather than a fault in it: PaintCode has one numeric kind, and a
        // min and a max with no step, so nothing in the document tells a step enum from a slider
        // sitting on a whole number. It is why the author asks for this rather than being given it.
        Assert.Equal("integer", TypeOf("level", integers: true));

        // The kinds that are not numbers are untouched, and so is a derived expression: its body is
        // PaintCode's arithmetic in PaintCode's one numeric type.
        Assert.Equal("boolean", TypeOf("state", integers: true));
        Assert.Equal("boolean", TypeOf("off", integers: true));
        Assert.Equal("color", TypeOf("colorPurple", integers: true));
    }

    private static void Imported(System.Action<PaintCodeImportResult, string> assert)
    {
        var directory = Directory.CreateTempSubdirectory("paintcode");

        try
        {
            var options = new PaintCodeImportOptions(directory.FullName)
            {
                ProjectPath = Path.Combine(directory.FullName, "icons.svgcproj")
            };

            assert(PaintCodeImport.Run(PaintCodeDocument.Parse(SampleDocument.Bytes()), options), directory.FullName);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
