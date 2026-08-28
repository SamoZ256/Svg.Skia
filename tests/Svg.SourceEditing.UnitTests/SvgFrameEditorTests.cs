using Svg.SourceEditing;
using Xunit;

namespace Svg.SourceEditing.UnitTests;

/// <summary>
/// Writing the frame of a drawing: three attributes on the root element, as spans, so that
/// everything else about the file survives being resized.
/// </summary>
public class SvgFrameEditorTests
{
    private static string Apply(string svgText, SvgSourceEditResult result)
    {
        Assert.True(result.Succeeded, result.Refusal);

        return SvgTextEdit.ApplyAll(svgText, result.Edits);
    }

    [Fact]
    public void A_Width_And_Height_Are_Replaced_Where_They_Stand()
    {
        const string source = """
            <svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
              <rect width="256" height="256" fill="#3366cc" />
            </svg>
            """;

        var rewritten = Apply(source, SvgFrameEditor.SetFrame(source, "512", "512", null));

        Assert.Contains("""<svg xmlns="http://www.w3.org/2000/svg" width="512" height="512" viewBox="0 0 256 256">""", rewritten);

        // The drawing under it is not the frame: a resize has no business touching it.
        Assert.Contains("""<rect width="256" height="256" fill="#3366cc" />""", rewritten);
    }

    [Fact]
    public void A_Missing_ViewBox_Is_Added_Beside_The_Others()
    {
        const string source = """<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24"><rect /></svg>""";

        var rewritten = Apply(source, SvgFrameEditor.SetFrame(source, "48", "48", "0 0 24 24"));

        Assert.Contains("""width="48" height="48" viewBox="0 0 24 24">""", rewritten);
    }

    [Fact]
    public void What_The_Author_Wrote_Around_It_Survives()
    {
        // Formatting, attribute order and comments are exactly what a rewritten document loses, and
        // exactly why this is an edit rather than a save.
        const string source = """
            <?xml version="1.0" encoding="utf-8"?>
            <!-- a drawing worth keeping -->
            <svg
                 height='24'
                 xmlns="http://www.w3.org/2000/svg"
                 width='24'>
              <!-- and a comment inside it -->
              <rect width="24" height="24" />
            </svg>
            """;

        var rewritten = Apply(source, SvgFrameEditor.SetFrame(source, "48", "48", null));

        Assert.Contains("<!-- a drawing worth keeping -->", rewritten);
        Assert.Contains("<!-- and a comment inside it -->", rewritten);
        Assert.Contains("""<?xml version="1.0" encoding="utf-8"?>""", rewritten);

        // Written in apostrophes and replaced in apostrophes, in the order the author had them.
        Assert.Contains("height='48'", rewritten);
        Assert.Contains("width='48'", rewritten);
    }

    [Fact]
    public void Writing_What_It_Already_Says_Is_No_Edit_At_All()
    {
        const string source = """<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24"></svg>""";

        var result = SvgFrameEditor.SetFrame(source, "24", "24", null);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Edits);
    }

    [Fact]
    public void A_Document_That_Is_Not_Xml_Yet_Is_Refused_Rather_Than_Half_Written()
    {
        var result = SvgFrameEditor.SetFrame("<svg width=\"24\"", "48", "48", null);

        Assert.False(result.Succeeded);
        Assert.Contains("cannot be read as XML", result.Refusal);
    }

    [Fact]
    public void A_Broken_Declaration_Block_Does_Not_Stop_A_Resize()
    {
        // The declarations are not this edit's business: a drawing with a fault in them still has a
        // size somebody may want to change.
        const string source = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="24" height="24">
              <defs><e:code><e:param name="hue" /></e:code></defs>
            </svg>
            """;

        var rewritten = Apply(source, SvgFrameEditor.SetFrame(source, "48", "48", null));

        Assert.Contains("width=\"48\" height=\"48\"", rewritten);
    }
}
