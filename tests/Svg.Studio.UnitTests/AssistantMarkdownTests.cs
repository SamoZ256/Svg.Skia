using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>What a reply's Markdown is drawn as.</summary>
public class AssistantMarkdownTests
{
    [AvaloniaFact]
    public void Paragraphs_Lists_And_Code_Are_Drawn_As_Themselves()
    {
        var drawn = (StackPanel)AssistantMarkdown.Render("Set **fill** to `#ff0000`.\n\n- one\n- two\n\n```xml\n<rect />\n```");

        Assert.Equal(3, drawn.Children.Count);

        var paragraph = Assert.IsType<SelectableTextBlock>(drawn.Children[0]);
        var inlines = paragraph.Inlines!.ToList();

        Assert.Contains(inlines, inline => inline is Bold bold && bold.Inlines.OfType<Run>().Single().Text == "fill");
        Assert.Contains(inlines, inline => inline is Run { Text: "#ff0000" } code && code.FontFamily.Name.StartsWith("Menlo", System.StringComparison.Ordinal));

        var list = Assert.IsType<StackPanel>(drawn.Children[1]);

        Assert.Equal(2, list.Children.Count);
        Assert.Equal("•", ((DockPanel)list.Children[0]).Children.OfType<TextBlock>().First().Text);

        var code = Assert.IsType<Border>(drawn.Children[2]);

        Assert.Equal("<rect />", ((SelectableTextBlock)code.Child!).Text);
    }

    [AvaloniaFact]
    public void Plain_Text_Is_One_Paragraph_And_Empty_Text_Is_Nothing()
    {
        Assert.Single(((StackPanel)AssistantMarkdown.Render("It is red now.")).Children);
        Assert.Empty(((StackPanel)AssistantMarkdown.Render("")).Children);
    }

    [AvaloniaFact]
    public void A_Numbered_List_Counts_From_Where_It_Starts()
    {
        var list = (StackPanel)((StackPanel)AssistantMarkdown.Render("3. c\n4. d")).Children[0];

        Assert.Equal(new[] { "3.", "4." }, list.Children.Cast<DockPanel>().Select(row => row.Children.OfType<TextBlock>().First().Text));
    }
}
