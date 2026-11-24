using Svg.SourceEditing;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>What the source editor reads off a group, against what the renderer passes down from one.</summary>
/// <remarks>Here because this is a project that sees both: the editor cannot reference the renderer's table.</remarks>
public class SvgSourceInheritanceTests
{
    [Fact]
    public void Every_Inherited_Property_An_Expression_Drives_Is_Read_Off_A_Group()
    {
        foreach (var name in SvgExpressionAttributes.Supported)
        {
            var source = SvgSourceDocument.Read("<svg xmlns=\"http://www.w3.org/2000/svg\"><g " + name + "=\"{{ v }}\"><rect /></g></svg>", out _)!;
            var read = SvgAttributeEditor.Driven(source, "0/0", name);

            Assert.True(
                SvgExpressionAttributes.IsInherited(name) == read is { },
                $"{name}: the renderer {(SvgExpressionAttributes.IsInherited(name) ? "passes it down" : "does not pass it down")}, and the editor {(read is { } ? "reads it off the group" : "does not")}.");
        }
    }
}
