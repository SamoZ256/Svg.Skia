using Svg.SourceEditing;
using Xunit;

namespace Svg.SourceEditing.UnitTests;

/// <summary>
/// Writing the frame of a drawing into the tree: three attributes on the root, and nothing else.
/// </summary>
public class SvgFrameEditorDocumentTests
{
    private static SvgSourceDocument Read(string svgText)
    {
        var source = SvgSourceDocument.Read(svgText, out var refusal);

        Assert.NotNull(source);
        Assert.Null(refusal);

        return source!;
    }

    [Fact]
    public void A_Width_And_Height_Are_Written_Where_They_Stand()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
              <rect width="256" height="256" fill="#3366cc" />
            </svg>
            """;

        var source = Read(svgText);

        Assert.Null(SvgFrameEditor.SetFrame(source, "512", "512", null));

        var written = source.ToText();

        Assert.Contains("""<svg xmlns="http://www.w3.org/2000/svg" width="512" height="512" viewBox="0 0 256 256">""", written);

        // The drawing under the frame is not the frame: a resize has no business touching it.
        Assert.Contains("""<rect width="256" height="256" fill="#3366cc" />""", written);
    }

    [Fact]
    public void A_Root_Written_Across_Lines_Keeps_Its_Lines()
    {
        // The root is the likeliest tag in a drawing to be laid out by hand, and a resize writes
        // three of its attributes at once, so this is where a regenerating writer shows first.
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg"
                 width='256'
                 height='256'
                 viewBox="0 0 256 256">
            </svg>
            """;

        var source = Read(svgText);

        Assert.Null(SvgFrameEditor.SetFrame(source, "512", "512", "0 0 512 512"));

        var written = source.ToText();

        Assert.Contains("\n     width='512'\n", written);
        Assert.Contains("\n     height='512'\n", written);
        Assert.Contains("""viewBox="0 0 512 512">""", written);
        Assert.Equal(svgText.Split('\n').Length, written.Split('\n').Length);
    }

    [Fact]
    public void A_Missing_ViewBox_Is_Added_Beside_The_Others()
    {
        const string svgText = """<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24"><rect /></svg>""";

        var source = Read(svgText);

        Assert.Null(SvgFrameEditor.SetFrame(source, null, null, "0 0 24 24"));

        Assert.Contains("""<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24">""", source.ToText());
    }

    [Fact]
    public void A_Null_Leaves_That_Attribute_Alone()
    {
        const string svgText = """<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" />""";

        var source = Read(svgText);

        Assert.Null(SvgFrameEditor.SetFrame(source, "48", null, null));

        // Null means "do not write this one", and never "take it away".
        Assert.Contains("""width="48" height="24" """, source.ToText());
    }

    [Fact]
    public void Writing_The_Frame_It_Already_Has_Changes_Nothing()
    {
        const string svgText = """<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" />""";

        var source = Read(svgText);

        Assert.Null(SvgFrameEditor.SetFrame(source, "24", "24", null));
        Assert.Equal(svgText, source.ToText());
    }
}
