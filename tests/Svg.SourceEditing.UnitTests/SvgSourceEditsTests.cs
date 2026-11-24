using System;
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
        => new("move", Array.ConvertAll(members, member => (SvgSourceEdit)new SvgSourceEdit.SetAttributes(member.Key, new[] { ("x", member.X) })));

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
}
