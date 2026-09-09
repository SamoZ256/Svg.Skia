using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Svg.SourceEditing;
using Xunit;

namespace Svg.SourceEditing.UnitTests;

/// <summary>
/// Holding a drawing as a tree that writes back as the file it was read from.
/// </summary>
/// <remarks>
/// The contract is the first test here and the rest are the reasons it is hard: what this reads,
/// it writes back byte for byte, and an edit changes the line it was made on and nothing else.
/// Everything a host does to a drawing rests on that, so it is measured against every drawing in
/// both suites rather than against examples chosen to pass.
/// </remarks>
public class SvgSourceDocumentTests
{
    private static string Read(string svgText)
    {
        var source = SvgSourceDocument.Read(svgText, out var refusal);

        Assert.NotNull(source);
        Assert.Null(refusal);

        return source!.ToText();
    }

    [Fact]
    public void Every_Drawing_In_The_Suites_Is_Written_Back_As_It_Was_Read()
    {
        var files = Suites().ToList();

        // Guards the guard: a path that stops resolving would otherwise pass silently.
        Assert.True(files.Count > 2500, $"Expected the suites, found {files.Count} drawings.");

        var changed = new List<string>();
        var refused = 0;

        foreach (var file in files)
        {
            string text;

            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }

            var source = SvgSourceDocument.Read(text, out _);

            if (source is null)
            {
                refused++;
                continue;
            }

            if (!string.Equals(source.ToText(), text, StringComparison.Ordinal))
            {
                changed.Add(file);
            }
        }

        Assert.True(changed.Count == 0, $"{changed.Count} drawings came back changed, first: {changed.FirstOrDefault()}");

        // The only ones it declines are the eight that declare entities of their own. A rise here
        // means something new is being refused, which is a loss of ground however green it looks.
        Assert.True(refused <= 8, $"{refused} drawings were refused, which is more than the eight that declare entities.");
    }

    [Fact]
    public void A_Comment_Survives_Being_Read_And_Written()
    {
        const string source = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <!-- why this rectangle is here -->
              <rect width="10" height="10" />
            </svg>
            """;

        Assert.Equal(source, Read(source));
    }

    [Fact]
    public void An_Expression_Survives_In_The_Form_It_Was_Written()
    {
        // The whole reason the tree is an XDocument: an SvgDocument round trip spells this
        // style="fill:gray;" plus e:fill="primary", which renders the same and is not the file.
        const string source = """<svg xmlns="http://www.w3.org/2000/svg"><rect fill="{{ primary }}" /></svg>""";

        Assert.Equal(source, Read(source));
    }

    [Fact]
    public void No_Doctype_Or_Version_Is_Added_To_A_Document_Without_One()
    {
        const string source = """<svg xmlns="http://www.w3.org/2000/svg"><rect /></svg>""";

        var written = Read(source);

        Assert.DoesNotContain("<!DOCTYPE", written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("version=", written, StringComparison.Ordinal);
    }

    [Fact]
    public void A_File_Written_With_Carriage_Returns_Keeps_Them()
    {
        // A parser folds every line ending to a newline before the tree sees one, so this is the
        // difference between editing one attribute and rewriting every line of a file.
        const string source = "<svg xmlns=\"http://www.w3.org/2000/svg\">\r\n  <rect />\r\n</svg>\r\n";

        Assert.Equal(source, Read(source));
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\">\r  <rect x=\"1\" />\r</svg>\r")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\">\n  <desc>a\rb</desc>\n  <rect />\n</svg>\n")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\">\r\n  <rect a=\"x\ry\" />\r\n</svg>\r\n")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\">\n  <!-- a\rb -->\n  <rect />\n</svg>\n")]
    public void A_Lone_Carriage_Return_Does_Not_Move_Everything_After_It(string source)
    {
        // XML makes a lone carriage return a line break and so does the reader, so a table that
        // counted only newlines fell a line behind from the first one and every tag remembered
        // after it came from somewhere else in the document. Nothing needed to be edited: the
        // drawing was destroyed by being opened, and the corpus has no such file to notice with.
        Assert.Equal(source, Read(source));
    }

    [Fact]
    public void One_Carriage_Return_Does_Not_Make_A_File_Windows()
    {
        // Asking whether the file holds a return, rather than whether its lines end with one,
        // rewrote every line of a newline file that happened to have one inside a run of text.
        const string source = "<svg xmlns=\"http://www.w3.org/2000/svg\">\n  <desc>a\rb</desc>\n</svg>\n";

        var written = Read(source);

        Assert.Equal(source, written);
        Assert.DoesNotContain("\r\n", written);
    }

    [Fact]
    public void Attributes_Written_One_To_A_Line_Stay_That_Way()
    {
        const string source = """
            <svg xmlns="http://www.w3.org/2000/svg"
                 width="64"
                 height="64">
            </svg>
            """;

        Assert.Equal(source, Read(source));
    }

    [Theory]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><rect x='1' y='2' /></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><rect/></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><rect /></svg>""")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect\t/></svg>")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><g ><rect /></g></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><text>a &gt; b</text></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><style><![CDATA[.a{fill:red}]]></style></svg>""")]
    [InlineData("""<?xml version="1.0" encoding="UTF-8"?><svg xmlns="http://www.w3.org/2000/svg" />""")]
    public void The_Spellings_XML_Says_Nothing_About_Are_Kept(string source)
        => Assert.Equal(source, Read(source));

    [Fact]
    public void Editing_One_Attribute_Changes_The_Line_It_Was_On_And_No_Other()
    {
        const string source = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!-- a dial -->
            <svg xmlns="http://www.w3.org/2000/svg"
                 viewBox="0 0 64 64">
              <rect x='1' y='2' fill="{{ tint }}"/>
              <circle cx="32" cy="32" r="20" fill="#c0392b" />
            </svg>
            """;

        var document = SvgSourceDocument.Read(source, out _)!;
        XNamespace svg = "http://www.w3.org/2000/svg";

        document.Document.Root!.Element(svg + "circle")!.SetAttributeValue("fill", "{{ accent }}");

        var written = document.ToText();
        var before = source.Split('\n');
        var after = written.Split('\n');

        Assert.Equal(before.Length, after.Length);

        var changed = before.Where((line, index) => line != after[index]).ToList();

        Assert.Single(changed);
        Assert.Contains("#c0392b", changed[0]);

        // The rewritten line keeps everything about itself but the value that was edited.
        Assert.Contains("""<circle cx="32" cy="32" r="20" fill="{{ accent }}" />""", written);
    }

    [Fact]
    public void Changing_One_Attribute_Leaves_The_Others_Where_They_Were_Written()
    {
        // The root is the likeliest tag in a file to be hand-wrapped, and the frame editor and the
        // first parameter ever added both rewrite it. Regenerating the whole tag to change one
        // value would fold four lines into one and turn the apostrophes into quotes.
        const string source = """
            <svg xmlns="http://www.w3.org/2000/svg"
                 width='64'
                 height='64'
                 viewBox="0 0 64 64">
            </svg>
            """;

        var document = SvgSourceDocument.Read(source, out _)!;

        document.Document.Root!.SetAttributeValue("width", "128");

        var written = document.ToText();

        Assert.Contains("width='128'", written);
        Assert.Contains("\n     height='64'\n", written);
        Assert.Contains("\n     viewBox=\"0 0 64 64\">", written);
        Assert.Equal(source.Split('\n').Length, written.Split('\n').Length);
    }

    [Fact]
    public void An_Attribute_Added_To_A_Wrapped_Tag_Goes_In_Beside_The_Others()
    {
        // Declaring the namespace is what the first parameter a drawing ever gets does, and it does
        // it to the root -- the one tag most likely to have been laid out by hand.
        const string source = """
            <svg xmlns="http://www.w3.org/2000/svg"
                 width='64'
                 height='64'>
            </svg>
            """;

        var document = SvgSourceDocument.Read(source, out _)!;

        document.Document.Root!.SetAttributeValue(XNamespace.Xmlns + "e", "https://svg.skia/expr/1.0");

        var written = document.ToText();

        Assert.Contains("\n     width='64'\n", written);
        Assert.Contains("""height='64' xmlns:e="https://svg.skia/expr/1.0">""", written);
        Assert.Equal(source.Split('\n').Length, written.Split('\n').Length);
    }

    [Fact]
    public void An_Attribute_Removed_Takes_The_Space_In_Front_Of_It_With_It()
    {
        const string source = """<svg xmlns="http://www.w3.org/2000/svg"><rect fill='red' x="1" y="2" /></svg>""";

        var document = SvgSourceDocument.Read(source, out _)!;
        XNamespace svg = "http://www.w3.org/2000/svg";

        document.Document.Root!.Element(svg + "rect")!.SetAttributeValue("x", null);

        Assert.Contains("""<rect fill='red' y="2" />""", document.ToText());
    }

    [Fact]
    public void A_Value_Spliced_Into_A_Tag_Is_Escaped_For_The_Quote_The_File_Used()
    {
        const string source = """<svg xmlns="http://www.w3.org/2000/svg"><rect fill='red' /></svg>""";

        var document = SvgSourceDocument.Read(source, out _)!;
        XNamespace svg = "http://www.w3.org/2000/svg";

        document.Document.Root!.Element(svg + "rect")!.SetAttributeValue("fill", "a'b\"c");

        var written = document.ToText();

        Assert.Contains("fill='a&apos;b\"c'", written);
        Assert.Equal("a'b\"c", SvgSourceDocument.Read(written, out _)!.Document.Root!.Element(svg + "rect")!.Attribute("fill")!.Value);
    }

    [Fact]
    public void A_Namespace_Nothing_Names_Is_Declared_Rather_Than_Dropped()
    {
        // Writing the local name alone would put the attribute in a different namespace from the
        // one the tree holds it in: xlink:href written as href is a second attribute, not the same
        // one, and the file would read back clean with nothing about it looking wrong.
        const string source = """<svg xmlns="http://www.w3.org/2000/svg"><rect /></svg>""";

        var document = SvgSourceDocument.Read(source, out _)!;
        XNamespace svg = "http://www.w3.org/2000/svg";
        XNamespace xlink = "http://www.w3.org/1999/xlink";

        document.Document.Root!.Element(svg + "rect")!.SetAttributeValue(xlink + "href", "#a");

        var written = document.ToText();
        var back = SvgSourceDocument.Read(written, out _)!;
        var rect = back.Document.Root!.Element(svg + "rect")!;

        Assert.Equal("#a", rect.Attribute(xlink + "href")!.Value);
        Assert.Null(rect.Attribute("href"));
    }

    [Fact]
    public void An_Element_In_A_Namespace_Nothing_Names_Keeps_It_Too()
    {
        const string source = """<svg xmlns="http://www.w3.org/2000/svg"><g /></svg>""";

        var document = SvgSourceDocument.Read(source, out _)!;
        XNamespace svg = "http://www.w3.org/2000/svg";
        XNamespace expr = "https://svg.skia/expr/1.0";

        document.Document.Root!.Element(svg + "g")!.Add(new XElement(expr + "code"));

        var back = SvgSourceDocument.Read(document.ToText(), out _)!;

        Assert.Single(back.Document.Descendants(expr + "code"));
    }

    [Fact]
    public void A_Moved_Element_Is_Spelled_For_The_Scope_It_Lands_In()
    {
        // A prefix means what the scope around it says. Replaying the bytes an element was read as,
        // into a parent that binds the same namespace to a different letter, writes a prefix
        // nothing declares — a file that cannot be read back at all.
        const string source = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <g id="a" xmlns:p="http://example.org/x"><rect p:tag="1" /></g>
              <g id="b" xmlns:q="http://example.org/x" />
            </svg>
            """;

        var document = SvgSourceDocument.Read(source, out _)!;
        XNamespace svg = "http://www.w3.org/2000/svg";
        XNamespace other = "http://example.org/x";

        var rect = document.Document.Descendants(svg + "rect").Single();

        rect.Remove();
        document.Document.Descendants(svg + "g").Single(g => (string?)g.Attribute("id") == "b").Add(rect);

        var back = SvgSourceDocument.Read(document.ToText(), out var refusal);

        Assert.Null(refusal);
        Assert.Equal("1", back!.Document.Descendants(svg + "rect").Single().Attribute(other + "tag")!.Value);
    }

    [Fact]
    public void An_Element_In_No_Namespace_Says_So_Under_One()
    {
        // Written with a bare name inside a default namespace it would silently join it.
        const string source = """<svg xmlns="http://www.w3.org/2000/svg"><g /></svg>""";

        var document = SvgSourceDocument.Read(source, out _)!;
        XNamespace svg = "http://www.w3.org/2000/svg";

        document.Document.Root!.Element(svg + "g")!.Add(new XElement("plain"));

        var back = SvgSourceDocument.Read(document.ToText(), out _)!;

        Assert.Single(back.Document.Descendants(), element => element.Name == (XName)"plain");
    }

    [Fact]
    public void An_Element_That_Gains_A_Child_Stops_Closing_Itself()
    {
        const string source = """<svg xmlns="http://www.w3.org/2000/svg"><g /></svg>""";

        var document = SvgSourceDocument.Read(source, out _)!;
        XNamespace svg = "http://www.w3.org/2000/svg";

        document.Document.Root!.Element(svg + "g")!.Add(new XElement(svg + "rect"));

        // The <g> is written afresh because it can no longer close itself, and the <rect> has no
        // spelling in the file to keep, so it takes the one the rest of the repository writes.
        Assert.Equal("""<svg xmlns="http://www.w3.org/2000/svg"><g><rect /></g></svg>""", document.ToText());
    }

    [Fact]
    public void A_Drawing_That_Declares_Its_Own_Entities_Is_Refused()
    {
        // A reader replaces the reference with what it stands for and keeps no node for the
        // reference, so this could be read and never written back. Refused rather than mangled.
        const string source = """
            <!DOCTYPE svg [<!ENTITY Smile "<rect width='10' height='10' />">]>
            <svg xmlns="http://www.w3.org/2000/svg">&Smile;</svg>
            """;

        Assert.Null(SvgSourceDocument.Read(source, out var refusal));
        Assert.Contains("entities of its own", refusal);
    }

    [Fact]
    public void A_Drawing_That_Is_Not_Well_Formed_Is_Refused_With_A_Sentence()
    {
        Assert.Null(SvgSourceDocument.Read("<svg><rect></svg>", out var refusal));
        Assert.StartsWith("The drawing is not well formed XML:", refusal);
    }

    [Fact]
    public void A_Byte_Order_Mark_Survives()
    {
        const string source = "﻿<svg xmlns=\"http://www.w3.org/2000/svg\" />";

        var document = SvgSourceDocument.Read(source, out _)!;

        Assert.True(document.ByteOrderMark);
        Assert.Equal(source, document.ToText());
    }

    private static IEnumerable<string> Suites()
    {
        foreach (var suite in new[] { "W3C_SVG_11_TestSuite", "resvg" })
        {
            var root = Path.Combine("..", "..", "..", "..", "..", "externals", suite);

            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*.svg", SearchOption.AllDirectories))
            {
                yield return file;
            }
        }
    }
}
