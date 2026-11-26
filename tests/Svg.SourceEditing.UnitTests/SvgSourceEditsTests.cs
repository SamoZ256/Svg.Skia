using System;
using System.Linq;
using System.Xml.Linq;
using Svg.SourceEditing;
using Xunit;

namespace Svg.SourceEditing.UnitTests;

/// <summary>A gesture's edits, written into a drawing as one.</summary>
public class SvgSourceEditsTests
{
    private const string Two = """
        <svg xmlns="http://www.w3.org/2000/svg">
          <rect x="1" />
          <rect x="2" />
        </svg>
        """;

    private static SvgSourceDocument Read()
    {
        var source = SvgSourceDocument.Read(Two, out var refusal);

        Assert.True(source is { }, refusal);

        return source!;
    }

    private static SvgSourceEdits Edits(params (string Key, string X)[] members)
        => new("move", Array.ConvertAll(members, member => (SvgSourceEdit)new SvgSourceEdit.SetAttributes(member.Key, [("x", member.X)])));

    [Fact]
    public void Every_Write_Of_Every_Member_Is_Made()
    {
        var source = Read();

        Assert.Null(Edits(("a", "5"), ("b", "6")).Apply(source, key => key == "a" ? "0" : "1"));
        Assert.Equal(Two.Replace("x=\"1\"", "x=\"5\"", StringComparison.Ordinal).Replace("x=\"2\"", "x=\"6\"", StringComparison.Ordinal), source.ToText());
    }

    /// <summary>Found first: the first member was written before the second was looked for, outside a commit left half done.</summary>
    [Fact]
    public void A_Key_The_Host_Cannot_Place_Refuses_Before_Anything_Is_Written()
    {
        var source = Read();

        Assert.Equal(
            "That element is not written in this drawing's file, so it cannot be edited here.",
            Edits(("a", "5"), ("b", "6")).Apply(source, key => key == "a" ? "0" : null));
        Assert.Equal(Two, source.ToText());
    }

    [Fact]
    public void A_Key_That_Names_Nothing_Refuses_Before_Anything_Is_Written()
    {
        var source = Read();

        Assert.Equal("There is no element at 7 in this drawing.", Edits(("a", "5"), ("b", "6")).Apply(source, key => key == "a" ? "0" : "7"));
        Assert.Equal(Two, source.ToText());
    }

    [Fact]
    public void Its_Keys_Are_Every_Steps_In_Order()
        => Assert.Equal(new[] { "b", "a" }, Edits(("b", "5"), ("a", "6")).Keys);

    private const string Three = """
        <svg xmlns="http://www.w3.org/2000/svg">
          <rect id="a" />
          <g id="b">
            <circle id="c" />
          </g>
          <rect id="d" />
        </svg>
        """;

    private static SvgSourceDocument Read(string text)
    {
        var source = SvgSourceDocument.Read(text, out var refusal);

        Assert.True(source is { }, refusal);

        return source!;
    }

    private static SvgSourceEdits Plan(params SvgSourceEdit[] steps) => new("edit", steps);

    private static string? Apply(SvgSourceDocument source, SvgSourceEdits plan) => plan.Apply(source, key => key);

    private static string[] Ids(SvgSourceDocument source)
        => source.Document.Root!.Descendants().Select(element => (string?)element.Attribute("id") ?? element.Name.LocalName).ToArray();

    /// <summary>A key is found once, before anything is written, so a removal does not shift what a later step names.</summary>
    [Fact]
    public void Keys_Name_The_Drawing_As_It_Was_Before_The_Plan()
    {
        var source = Read(Three);

        Assert.Null(Apply(source, Plan(new SvgSourceEdit.Remove("0"), new SvgSourceEdit.Move("2", "1", SvgElementDrop.Before))));

        Assert.Equal(new[] { "d", "b", "c" }, Ids(source));
    }

    [Fact]
    public void A_Step_Naming_What_An_Earlier_Step_Removed_Is_Refused()
    {
        var workspace = SvgSourceWorkspace.Open(Three, out _)!;
        var plan = Plan(new SvgSourceEdit.Remove("1"), new SvgSourceEdit.SetAttributes("1/0", [("r", "4")]));

        Assert.Equal("There is no element at 1/0 in this drawing.", workspace.Commit("edit", source => Apply(source, plan)));
        Assert.Equal(Three, workspace.Text);
    }

    [Theory]
    [InlineData("1", "1/0")]
    [InlineData("1/0", "1")]
    public void A_Parent_And_Its_Child_Are_Removed_Once_In_Either_Order(string first, string second)
    {
        var (planned, edited) = (Read(Three), Read(Three));

        Assert.Null(Apply(planned, Plan(new SvgSourceEdit.Remove(first), new SvgSourceEdit.Remove(second))));
        Assert.Null(SvgElementEditor.Remove(edited, new[] { "1" }));

        Assert.Equal(edited.ToText(), planned.ToText());
    }

    [Fact]
    public void A_Key_Named_Twice_Is_Removed_Once()
    {
        var source = Read(Three);

        Assert.Null(Apply(source, Plan(new SvgSourceEdit.Remove("0"), new SvgSourceEdit.Remove("0"))));

        Assert.Equal(new[] { "b", "c", "d" }, Ids(source));
    }

    [Fact]
    public void A_Plan_Refuses_What_Remove_Refuses()
    {
        const string declared = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
              <defs><e:code><e:param name="w" type="number" default="1" /></e:code></defs>
            </svg>
            """;

        Assert.Equal("The drawing itself cannot be deleted.", Apply(Read(Three), Plan(new SvgSourceEdit.Remove(""))));
        Assert.Equal(SvgElementEditor.Remove(Read(declared), new[] { "0" }), Apply(Read(declared), Plan(new SvgSourceEdit.Remove("0"))));
        Assert.StartsWith("The declarations cannot be deleted", Apply(Read(declared), Plan(new SvgSourceEdit.Remove("0"))), StringComparison.Ordinal);
    }

    /// <summary>A plan is a value: what it inserts is a copy, so its caller's element is never parented or put in a namespace.</summary>
    [Fact]
    public void The_Element_Handed_To_An_Insert_Is_Left_As_It_Was()
    {
        var element = new XElement("rect", new XAttribute("id", "n"));
        var source = Read(Three);

        Assert.Null(Apply(source, Plan(new SvgSourceEdit.Insert("0", SvgElementDrop.After, element))));

        Assert.Null(element.Parent);
        Assert.Equal(XNamespace.None, element.Name.Namespace);
        Assert.Equal(new[] { "a", "n", "b", "c", "d" }, Ids(source));
    }

    public static TheoryData<string, string, string, SvgElementDrop> Written => new()
    {
        { Three, "move", "1", SvgElementDrop.Inside },
        { Three.Replace("  ", "\t", StringComparison.Ordinal), "move", "0", SvgElementDrop.Before },
        { Three.Replace("  ", "\t", StringComparison.Ordinal), "insert", "1", SvgElementDrop.Inside },
        { Three.Replace("\n", "\r\n", StringComparison.Ordinal), "insert", "0", SvgElementDrop.After },
        { Three.Replace("<rect id=\"d\" />", "<g id=\"d\" />", StringComparison.Ordinal), "insert", "2", SvgElementDrop.Inside },
        { Three, "move", "1/0", SvgElementDrop.Inside },
    };

    /// <summary>The steps are the editor's own bodies: the same text, indents, line ends and refusals as the editor writes.</summary>
    [Theory]
    [MemberData(nameof(Written))]
    public void A_Step_Writes_What_The_Editor_Writes(string text, string kind, string target, SvgElementDrop where)
    {
        var (planned, edited) = (Read(text), Read(text));
        var made = new XElement("g", new XElement("rect"));

        var (fromPlan, fromEditor) = kind == "move"
            ? (Apply(planned, Plan(new SvgSourceEdit.Move("2", target, where))), SvgElementEditor.Move(edited, "2", target, where, out _))
            : (Apply(planned, Plan(new SvgSourceEdit.Insert(target, where, made))), SvgElementEditor.Insert(edited, target, where, new XElement(made), out _));

        Assert.Equal(fromEditor, fromPlan);
        Assert.Equal(edited.ToText(), planned.ToText());
    }

    /// <summary>Where each element a step wrote is once every step is in, in step order; what was removed is not anywhere.</summary>
    [Fact]
    public void What_Landed_Is_Each_Element_Written_At_Its_Key_Afterwards()
    {
        var source = Read(Three);
        var plan = Plan(
            new SvgSourceEdit.SetAttributes("0", [("x", "3")]),
            new SvgSourceEdit.SetAttributes("2", [("x", "9")]),
            new SvgSourceEdit.Insert("0", SvgElementDrop.After, new XElement("path")),
            new SvgSourceEdit.Move("1/0", "2", SvgElementDrop.After),
            new SvgSourceEdit.SetAttributes("2", [("y", "9")]),
            new SvgSourceEdit.Remove("0"));

        Assert.Null(plan.Apply(source, key => key, out var landed));

        Assert.Equal(new[] { "path", "b", "d", "c" }, Ids(source));
        Assert.Equal(new[] { "2", "0", "3" }, landed);

        Assert.NotNull(Plan(new SvgSourceEdit.SetAttributes("0", [("x", "1")]), new SvgSourceEdit.Remove("")).Apply(Read(Three), key => key, out var refused));
        Assert.Empty(refused);
    }

    /// <summary>Said of the file's address, as a key that names nothing is, rather than of what the host calls the row.</summary>
    [Fact]
    public void A_Step_Naming_What_Was_Removed_Is_Said_Of_The_File()
    {
        var plan = Plan(new SvgSourceEdit.Remove("row"), new SvgSourceEdit.SetAttributes("child", [("r", "4")]));

        Assert.Equal("There is no element at 1/0 in this drawing.", plan.Apply(Read(Three), key => key == "row" ? "1" : "1/0"));
    }

    [Fact]
    public void Retag_Keeps_Text_In_A_Run_Of_Text()
    {
        var source = Read("""<svg xmlns="http://www.w3.org/2000/svg"><text><tspan><tspan>a</tspan></tspan></text></svg>""");

        Assert.Null(Apply(source, Plan(new SvgSourceEdit.Retag("0/0", "textPath"))));
        Assert.Equal("""<svg xmlns="http://www.w3.org/2000/svg"><text><textPath><tspan>a</tspan></textPath></text></svg>""", source.ToText());
    }

    [Fact]
    public void Set_Content_Writes_The_Text_As_The_Editor_Does()
    {
        const string text = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <text>old<!-- kept --></text>
              <text><tspan>a</tspan></text>
            </svg>
            """;
        var source = Read(text);

        Assert.Null(Apply(source, Plan(new SvgSourceEdit.SetContent("0", "new"))));
        Assert.Equal(text.Replace("old", "new", StringComparison.Ordinal), source.ToText());

        Assert.Equal(SvgAttributeEditor.SetContent(Read(text), "1", "b"), Apply(Read(text), Plan(new SvgSourceEdit.SetContent("1", "b"))));
    }

    [Fact]
    public void Retag_Renames_The_Element_Keeping_What_It_Is_Written_With()
    {
        const string text = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <rect id="r" x="1"><title>t</title></rect>
              <linearGradient><stop offset="0" /></linearGradient>
              <g><circle r="1" /></g>
            </svg>
            """;
        var source = Read(text);

        Assert.Null(Apply(source, Plan(new SvgSourceEdit.Retag("0", "path"))));
        Assert.Equal(text.Replace("<rect id=\"r\" x=\"1\"><title>t</title></rect>", "<path id=\"r\" x=\"1\"><title>t</title></path>", StringComparison.Ordinal), source.ToText());

        Assert.Equal("The drawing itself cannot be turned into another element.", Apply(Read(text), Plan(new SvgSourceEdit.Retag("", "g"))));
        Assert.Equal("'1a' cannot be the name of an element.", Apply(Read(text), Plan(new SvgSourceEdit.Retag("0", "1a"))));
        Assert.Equal("A <linearGradient> cannot hold a <rect>.", Apply(Read(text), Plan(new SvgSourceEdit.Retag("1/0", "rect"))));
        Assert.Equal("A <rect> cannot hold a <circle>.", Apply(Read(text), Plan(new SvgSourceEdit.Retag("2", "rect"))));
    }
}
