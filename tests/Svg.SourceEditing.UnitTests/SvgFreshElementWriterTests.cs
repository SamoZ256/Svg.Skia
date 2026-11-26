using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Svg;
using Svg.Pathing;
using Svg.Transforms;
using Xunit;

namespace Svg.SourceEditing.UnitTests;

/// <summary>
/// What a parsed element holds of its own, captured so a later edit cannot reach it, and an element
/// that is not in the file written as the markup that reads back as it.
/// </summary>
public class SvgFreshElementWriterTests
{
    private const string Declared = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" xmlns:e="https://svg.skia/expr/1.0">
          <defs>
            <e:code>
              <e:param name="c" type="color" default="#ff0000" />
              <e:param name="w" type="number" default="2" />
              <e:param name="s" type="string" default="Arial" />
            </e:code>
          </defs>
          BODY
        </svg>
        """;

    private static string Drawing(string body) => Declared.Replace("BODY", body, StringComparison.Ordinal);

    private static SvgDocument Load(string text) => SvgDocumentCompatibilityLoader.FromSvg<SvgDocument>(text);

    private static XElement Written(SvgElement element)
    {
        var written = SvgFreshElementWriter.Write(element, "  ", out var refusal);

        Assert.True(written is { }, refusal);

        return written!;
    }

    private static string WrittenText(string body, string id = "a") => Written(Load(Drawing(body)).GetElementById(id)).ToString(SaveOptions.DisableFormatting);

    /// <summary>The element written fresh in place of itself, read again: its own values, custom attributes and text are the same.</summary>
    private static void ReadsBack(string text, string id = "a")
    {
        var element = Load(text).GetElementById(id);
        var before = SvgElementState.Capture(element);
        var tree = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        var original = tree.Descendants().Single(each => (string?)each.Attribute("id") == id);

        original.ReplaceWith(Adopted(Written(element), original.Name.Namespace));

        var after = SvgElementState.Capture(Load(tree.ToString(SaveOptions.DisableFormatting)).GetElementById(id));

        Assert.Equal(Mismatches(before, after), Array.Empty<string>());
    }

    /// <summary>What <c>SvgElementEditor.Insert</c> does to an element written in no namespace.</summary>
    private static XElement Adopted(XElement element, XNamespace ns)
    {
        foreach (var named in element.DescendantsAndSelf().Where(named => named.Name.Namespace == XNamespace.None))
        {
            named.Name = ns + named.Name.LocalName;
        }

        return element;
    }

    private static IReadOnlyList<string> Mismatches(IReadOnlyList<SvgElementState> before, IReadOnlyList<SvgElementState> after)
    {
        var found = new List<string>();

        if (before.Count != after.Count)
        {
            return new[] { $"{before.Count} elements came back as {after.Count}" };
        }

        for (var i = 0; i < before.Count; i++)
        {
            var (was, now) = (before[i], after[i]);
            var tag = SvgElementNames.NameOf(was.Element);

            foreach (var name in was.Attributes.Keys.Union(now.Attributes.Keys))
            {
                if (!SvgValueSpelling.Precise(was.Attributes.GetValueOrDefault(name), now.Attributes.GetValueOrDefault(name)))
                {
                    found.Add($"<{tag}> {name}");
                }
            }

            foreach (var key in was.CustomAttributes.Keys.Union(now.CustomAttributes.Keys))
            {
                // Where a literal was written only matters to an expression at the same name, which neither has.
                if (key.EndsWith(":from", StringComparison.Ordinal)
                    && !was.CustomAttributes.ContainsKey(key.Substring(0, key.Length - ":from".Length))
                    && !now.CustomAttributes.ContainsKey(key.Substring(0, key.Length - ":from".Length)))
                {
                    continue;
                }

                if (key != "style" && was.CustomAttributes.GetValueOrDefault(key) != now.CustomAttributes.GetValueOrDefault(key))
                {
                    found.Add($"<{tag}> custom {key}: '{was.CustomAttributes.GetValueOrDefault(key)}' came back as '{now.CustomAttributes.GetValueOrDefault(key)}'");
                }
            }

            if (was.Content != now.Content)
            {
                found.Add($"<{tag}> text");
            }
        }

        return found;
    }

    // ---- the snapshot ---------------------------------------------------------------------------

    [Fact]
    public void A_Child_Does_Not_Capture_Its_Parents_Fill()
    {
        var rect = Load(Drawing("""<g fill="red"><rect id="a" width="1" /></g>""")).GetElementById("a");

        Assert.Equal(new[] { "id", "width" }, SvgElementState.Of(rect, string.Empty).Attributes.Keys.OrderBy(name => name));
    }

    /// <summary>A value edited in place after it was captured reads as a change: the capture is a copy, a matrix's numbers included.</summary>
    [Fact]
    public void An_Edit_In_Place_After_Capture_Reads_As_A_Change()
    {
        var path = Load(Drawing("""<path id="a" d="M0 0L10 10" transform="matrix(1 0 0 1 5 5)" stroke-dasharray="1 2" />""")).GetElementById("a");
        var before = SvgElementState.Of(path, string.Empty);

        ((SvgPath)path).PathData[1].End = new PointF(20, 20);
        ((SvgMatrix)path.Transforms[0]).Points[4] = 9;
        path.StrokeDashArray[0] = new SvgUnit(3);

        var after = SvgElementState.Of(path, string.Empty);

        Assert.False(SvgValueSpelling.Precise(before.Attributes["d"], after.Attributes["d"]));
        Assert.False(SvgValueSpelling.Precise(before.Attributes["transform"], after.Attributes["transform"]));
        Assert.False(SvgValueSpelling.Precise(before.Attributes["stroke-dasharray"], after.Attributes["stroke-dasharray"]));
    }

    [Fact]
    public void Capture_Records_What_The_Diff_Needs()
    {
        var document = Load(Drawing("""<g><rect id="a" fill="{{ c }}" style="filter: blur(2px)" /><text id="t">hi</text></g>"""));
        var states = SvgElementState.Capture(document);
        var rect = states.Single(state => state.Element.ID == "a");
        var text = states.Single(state => state.Element.ID == "t");

        Assert.Equal("1/0", rect.Key);
        Assert.Equal("c", rect.CustomAttributes[SvgExpressionAttributes.KeyFor("fill")]);
        Assert.Equal("0", rect.CustomAttributes[SvgExpressionAttributes.SourceKeyFor("fill")]);
        Assert.Equal(new[] { new KeyValuePair<int, string>(65536, "blur(2px)") }, rect.Styles["filter"]);
        Assert.Equal("hi", text.Content);
        Assert.Null(rect.Content);
        Assert.Equal(new[] { rect.Element, text.Element }, states.Single(state => state.Key == "1").Children);
    }

    // ---- the fresh writer ------------------------------------------------------------------------

    [Fact]
    public void A_Fresh_Rect_Is_Written_As_Expected()
        => Assert.Equal(
            """<rect id="a" x="1" y="2.5" width="3" height="4" fill="#ff0000" stroke-width="2" style="opacity: 0.5" />""",
            WrittenText("""<rect id="a" x="1" y="2.5" width="3" height="4" fill="red" stroke-width="2" style="opacity:.5" />"""));

    public static TheoryData<string, string> Lifted => new()
    {
        { """<rect id="a" fill="{{ c }}" />""", """fill="{{ c }}" """ },
        { """<linearGradient id="g"><stop id="a" offset="0" stop-color="{{ c }}" /></linearGradient>""", """stop-color="{{ c }}" """ },
        { """<clipPath id="k"><rect id="a" x="{{ w }}" width="5" height="5" /></clipPath>""", """x="{{ w }}" """ },
        { """<rect id="a" style="fill: {{ c }}" />""", """style="fill: {{ c }}" """ },
        { """<rect id="a" style="fill: {{ c }} !important" />""", """style="fill: {{ c }} !important" """ },
        { """<rect id="a" style="fill: blue" />""", """style="fill: #0000ff" """ },
        { """<rect id="a" fill="{{ c }}" style="fill: blue" />""", """style="fill: #0000ff" """ },
        { """<text id="a" font-family="{{ s }}">x</text>""", """font-family="{{ s }}" """ },
        { """<text id="a">{{ s }}</text>""", """>{{ s }}</text>""" },
        { """<rect id="a" transform="translate({{ w }} 2)" />""", """transform="translate({{ w }} 2)" """ },
        { """<rect id="a" stroke-dasharray="{{ w }} 2" />""", """stroke-dasharray="{{ w }} 2" """ },
        { """<clipPath id="k"><path id="a" d="M 0 0 H {{ w }} V 5 Z" /></clipPath>""", """d="M 0 0 H {{ w }} V 5 Z" """ },
        { """<rect id="a" e:bounds="Box" />""", """bounds="Box" """ }
    };

    /// <summary>Each place an expression is lifted from gets it back as written, and the value standing in for it is never written.</summary>
    [Theory]
    [MemberData(nameof(Lifted))]
    public void Every_Lift_Site_Reads_Back_With_Its_Keys(string body, string expected)
    {
        var written = WrittenText(body).Replace(" xmlns:e=\"https://svg.skia/expr/1.0\"", string.Empty, StringComparison.Ordinal);

        Assert.Contains(expected.TrimEnd(), written, StringComparison.Ordinal);
        Assert.DoesNotContain("#808080", written, StringComparison.Ordinal);

        ReadsBack(Drawing(body));
    }

    [Fact]
    public void Bookkeeping_Never_Reaches_The_File()
    {
        var written = WrittenText("""<rect id="a" fill="{{ c }}" style="stroke: red" data-tag="x" class="k" />""");

        Assert.DoesNotContain("from", written, StringComparison.Ordinal);
        Assert.DoesNotContain("svgskia", written, StringComparison.Ordinal);
        Assert.Contains("data-tag=\"x\"", written, StringComparison.Ordinal);
        Assert.Contains("class=\"k\"", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Mix_Blend_Mode_Survives_A_Fresh_Write()
    {
        Assert.Contains("style=\"mix-blend-mode: multiply\"", WrittenText("""<rect id="a" style="mix-blend-mode: multiply" />"""), StringComparison.Ordinal);

        ReadsBack(Drawing("""<rect id="a" style="mix-blend-mode: multiply" />"""));
    }

    /// <summary>A declaration no attribute holds is written where it was: inline in the style, and not at all from a sheet, which says it again.</summary>
    [Fact]
    public void A_Sheet_Rule_Is_Not_Copied_Inline()
    {
        Assert.Contains("style=\"filter: blur(2px)\"", WrittenText("""<rect id="a" style="filter: blur(2px)" />"""), StringComparison.Ordinal);
        Assert.DoesNotContain("blur", WrittenText("""<style>rect { filter: blur(2px) }</style><rect id="a" />"""), StringComparison.Ordinal);
    }

    /// <summary>Text is written as it runs: indentation between a text's children would be text.</summary>
    [Fact]
    public void Text_Holds_No_Indentation_Of_The_Writers()
    {
        Assert.Equal(
            """<text id="a" x="1"><tspan>a</tspan><tspan>b</tspan></text>""",
            WrittenText("""<text id="a" x="1"><tspan>a</tspan><tspan>b</tspan></text>"""));
        Assert.Equal(
            """<g id="a" xml:space="preserve"><rect width="1" /><rect width="2" /></g>""",
            WrittenText("""<g id="a" xml:space="preserve"><rect width="1" /><rect width="2" /></g>"""));
    }

    /// <summary>The text compiler reads a text-decoration as written, and draws nothing for one that is malformed.</summary>
    [Theory]
    [InlineData("underline,overline")]
    [InlineData("underlineoverline")]
    public void A_Text_Decoration_Is_Written_As_It_Was(string decoration)
        => Assert.Contains($"text-decoration=\"{decoration}\"", WrittenText($"""<text id="a" text-decoration="{decoration}">x</text>"""), StringComparison.Ordinal);

    /// <summary>The shorthand once, and a marker set beside it after it in the style, where an attribute would lose to it.</summary>
    [Fact]
    public void A_Marker_Shorthand_Is_Written_Once_Before_What_Overrides_It()
    {
        const string body = """<path id="a" d="M0 0L1 1" style="marker: url(#m); marker-end: url(#n)" />""";

        Assert.Contains("style=\"marker: url(#m); marker-end: url(#n)\"", WrittenText(body), StringComparison.Ordinal);

        ReadsBack(Drawing(body));
    }

    /// <summary>Under a sheet, an inline value goes back inline: as an attribute, any rule matching the element would beat it.</summary>
    [Fact]
    public void Under_A_Sheet_An_Inline_Value_Stays_Inline()
    {
        const string body = """<style>path { stroke-linecap: square }</style><path id="a" d="M0 0L10 0" style="stroke-linecap: round" />""";

        Assert.Contains("stroke-linecap: round", WrittenText(body), StringComparison.Ordinal);

        ReadsBack(Drawing(body));
    }

    /// <summary>An animation's fill is its fill mode, not a paint, and stays the attribute it was.</summary>
    [Fact]
    public void An_Animations_Fill_Is_Not_A_Paint()
        => Assert.Contains(
            "fill=\"freeze\"",
            WrittenText("""<style>rect { opacity: 1 }</style><rect width="1"><set id="a" attributeName="x" to="2" fill="freeze" /></rect>"""),
            StringComparison.Ordinal);

    [Fact]
    public void A_Lifted_Text_Keeps_What_Is_Beside_It()
    {
        const string body = """<text id="a">{{ s }}<set attributeName="fill" to="red" begin="1s" /></text>""";

        Assert.Contains("{{ s }}<set ", WrittenText(body), StringComparison.Ordinal);

        ReadsBack(Drawing(body));
    }

    [Fact]
    public void Text_Inside_A_Link_In_Text_Holds_No_Indentation()
        => Assert.Equal(
            """<text id="a"><a href="#x"><tspan>A</tspan><tspan>B</tspan></a></text>""",
            WrittenText("""<text id="a"><a href="#x"><tspan>A</tspan><tspan>B</tspan></a></text>"""));

    /// <summary>What a command set as text with no node to hold it is written; text and children that disagree are refused.</summary>
    [Fact]
    public void Text_Held_Only_As_Content_Is_Written_And_A_Disagreement_Refused()
    {
        Assert.Equal("<title>Tip</title>", Written(new SvgTitle { Content = "Tip" }).ToString(SaveOptions.DisableFormatting));

        var text = Load(Drawing("""<text id="a">hi</text>""")).GetElementById("a");

        text.Children.Add(new SvgTextSpan());

        Assert.Null(SvgFreshElementWriter.Write(text, "  ", out var refusal));
        Assert.Equal("The text of <text> and the elements in it disagree about what it holds.", refusal);
    }

    [Fact]
    public void An_Href_And_An_Xlink_Href_That_Disagree_Are_Refused()
    {
        Assert.Null(SvgFreshElementWriter.Write(Load(Drawing("""<use id="a" href="#red" xlink:href="#green" />""")).GetElementById("a"), "  ", out var refusal));
        Assert.Equal("<use> is written with an href and an xlink:href that say different things, and a copy could say only one.", refusal);
    }

    [Fact]
    public void An_Empty_Paint_Is_Not_Written()
        => Assert.Equal("""<rect id="a" width="1" />""", WrittenText("""<rect id="a" width="1" fill="" />"""));

    [Fact]
    public void The_Font_Shorthand_Comes_Before_Its_Longhands()
        => ReadsBack(Drawing("""<text id="a" font="12px Arial" font-size="20">x</text>"""));

    /// <summary>A custom property the element sets in its own style goes with it; one a sheet sets, the sheet sets again.</summary>
    [Fact]
    public void A_Custom_Property_Set_Inline_Goes_With_The_Copy()
    {
        const string body = """<style>rect { fill: var(--c) }</style><g id="a" style="--c: #00ff00"><rect width="1" /></g>""";

        Assert.Contains("--c: #00ff00", WrittenText(body), StringComparison.Ordinal);

        ReadsBack(Drawing(body));
    }

    [Fact]
    public void A_Foreign_Element_And_The_Drawing_Are_Refused()
    {
        var document = Load(Drawing("""<g id="a"><x:note xmlns:x="urn:x" /></g>"""));

        Assert.Null(SvgFreshElementWriter.Write(document.GetElementById("a"), "  ", out var foreign));
        Assert.Equal("A <note> is not something a command writes into a drawing.", foreign);

        Assert.Null(SvgFreshElementWriter.Write(document, "  ", out var root));
        Assert.Equal("The drawing itself is not written as a new element.", root);
    }

    /// <summary>Through the insert step, a fresh group lands in its parent's namespace, indented where it lands, and nothing else in the file moves.</summary>
    [Fact]
    public void A_Fresh_Group_Lands_Indented_In_The_Parents_Namespace()
    {
        const string text = "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" xmlns:e=\"https://svg.skia/expr/1.0\">\n"
                            + "\t<g id=\"x\">\n\t\t<rect width=\"1\" />\n\t</g>\n"
                            + "</svg>";
        var group = Load(Drawing("""<g id="a"><rect width="2" fill="{{ c }}" /><use xlink:href="#x" /></g>""")).GetElementById("a");
        var source = SvgSourceDocument.Read(text, out _)!;
        var written = SvgFreshElementWriter.Write(group, source.IndentUnit, out _)!;

        Assert.Null(new SvgSourceEdits("copy", new SvgSourceEdit[] { new SvgSourceEdit.Insert("0", SvgElementDrop.After, written) }).Apply(source, key => key));

        Assert.Equal(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" xmlns:e=\"https://svg.skia/expr/1.0\">\n"
            + "\t<g id=\"x\">\n\t\t<rect width=\"1\" />\n\t</g>\n"
            + "\t<g id=\"a\">\n\t\t<rect width=\"2\" fill=\"{{ c }}\" />\n\t\t<use xlink:href=\"#x\" />\n\t</g>\n"
            + "</svg>",
            source.ToText());
    }

    /// <summary>In a drawing written with no namespace, what is inserted takes none either rather than declaring one of its own.</summary>
    [Fact]
    public void A_Fresh_Element_Takes_A_Drawing_With_No_Namespace_As_It_Is()
    {
        var rect = Load(Drawing("""<rect id="a" width="2" />""")).GetElementById("a");
        var source = SvgSourceDocument.Read("<svg>\n  <g />\n</svg>", out _)!;

        Assert.Null(new SvgSourceEdits("copy", new SvgSourceEdit[] { new SvgSourceEdit.Insert("0", SvgElementDrop.After, Written(rect)) }).Apply(source, key => key));

        Assert.Equal("<svg>\n  <g />\n  <rect id=\"a\" width=\"2\" />\n</svg>", source.ToText());
    }

    /// <summary>
    /// Every top-level element of every drawing in the suites, written fresh in place of itself through
    /// the insert and remove steps and read again, holds what it held — or is refused, never written
    /// wrong. The refusals are pinned at what was measured.
    /// </summary>
    [Fact]
    public void Every_Subtree_In_The_Suites_Reads_Back_Fresh()
    {
        var (written, refused, differ) = (0, new Dictionary<string, int>(StringComparer.Ordinal), new List<string>());

        foreach (var file in SvgSourceDocumentTests.Suites())
        {
            var text = File.ReadAllText(file);
            SvgDocument document;

            try
            {
                document = Load(text);
            }
            catch (Exception)
            {
                continue;
            }

            // Only where the parser's children and the file's line up one for one.
            if (SvgSourceDocument.Read(text, out _) is not { } source || source.Document.Root!.Elements().Count() != document.Children.Count)
            {
                continue;
            }

            var before = document.Children.Select(SvgElementState.Capture).ToList();
            var steps = new List<SvgSourceEdit>();

            for (var i = 0; i < document.Children.Count; i++)
            {
                if (document.Children[i] is NonSvgElement)
                {
                    continue;
                }

                var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);

                if (SvgFreshElementWriter.Write(document.Children[i], source.IndentUnit, out var refusal) is { } fresh)
                {
                    steps.Add(new SvgSourceEdit.Insert(key, SvgElementDrop.After, fresh));
                    steps.Add(new SvgSourceEdit.Remove(key));
                    written++;
                }
                else
                {
                    var kind = refusal!.Length > 70 ? refusal.Substring(0, 70) : refusal;

                    refused[kind] = refused.TryGetValue(kind, out var count) ? count + 1 : 1;
                }
            }

            if (new SvgSourceEdits("fresh", steps).Apply(source, key => key) is { } unwritten)
            {
                differ.Add(Path.GetFileName(file) + ": " + unwritten);

                continue;
            }

            var again = Load(source.ToText());

            for (var i = 0; i < before.Count && i < again.Children.Count; i++)
            {
                foreach (var mismatch in Mismatches(before[i], SvgElementState.Capture(again.Children[i])))
                {
                    differ.Add(Path.GetFileName(file) + ": " + mismatch);
                }
            }
        }

        Assert.True(written > 0, "Nothing was written.");
        Assert.True(differ.Count == 0, $"{differ.Count} came back different, first: " + string.Join("; ", differ.Take(12)));
        // Six hold an element of another namespace, such as RDF metadata, when measured; nothing else is refused.
        var foreign = refused.Where(pair => pair.Key.EndsWith("is not something a command writes into a drawing.", StringComparison.Ordinal)).Sum(pair => pair.Value);

        Assert.True(foreign <= 6 && foreign == refused.Values.Sum(), $"{refused.Values.Sum()} refused: " + string.Join("; ", refused.OrderByDescending(pair => pair.Value).Take(12).Select(pair => $"{pair.Key} ×{pair.Value}")));
    }
}
