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
