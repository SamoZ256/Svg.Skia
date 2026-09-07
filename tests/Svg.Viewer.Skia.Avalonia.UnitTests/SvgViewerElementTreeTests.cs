using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>
/// The pane listing what the open drawing is made of.
/// </summary>
public class SvgViewerElementTreeTests
{
    private const string Markup = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs>
            <e:code><e:param name="tint" type="color" default="#ff0000" /></e:code>
          </defs>
          <g id="wrap">
            <rect x="0" y="0" width="24" height="24" fill="{{ tint }}" />
            <text x="2" y="20">hi</text>
          </g>
        </svg>
        """;

    private static async Task<(Window Window, SvgViewer Viewer)> Host(string markup = Markup)
    {
        var viewer = new SvgViewer();
        var window = new Window { Width = 700, Height = 500, Background = Brushes.White, Content = viewer };

        window.Show();

        Assert.True(await viewer.LoadTextAsync(markup));
        Dispatcher.UIThread.RunJobs();

        return (window, viewer);
    }

    /// <summary>Every row, in document order, as it reads.</summary>
    private static string[] Rows(SvgViewer viewer)
        => viewer.Elements.Root is { } root
            ? root.Flatten().Select(node => node.ToString()).ToArray()
            : System.Array.Empty<string>();

    private static TextEditor Editor(SvgViewer viewer)
        => viewer.GetVisualDescendants().OfType<TextEditor>().First(c => c.Name == "SourceEditor");

    [AvaloniaFact]
    public async Task Every_Element_Is_Listed_In_Document_Order()
    {
        // The whole document and not only what is drawn. Half of what a reader wants to find --
        // the declarations block, a gradient, a clip path -- never reaches the canvas at all.
        var (_, viewer) = await Host();

        Assert.Equal(
            new[] { "svg", "defs", "code", "param", "g #wrap", "rect", "text" },
            Rows(viewer));
    }

    [AvaloniaFact]
    public async Task The_Root_And_Its_Children_Start_Open()
    {
        var (_, viewer) = await Host();

        var root = viewer.Elements.Root!;

        Assert.True(root.IsExpanded);
        Assert.All(root.Children, child => Assert.True(child.IsExpanded));

        // And no further: a drawing of any size has more rows than the pane is tall.
        Assert.False(root.Children.Single(node => node.Label == "g").Children[0].IsExpanded);
    }

    [AvaloniaFact]
    public async Task A_Rebuild_From_Edited_Text_Updates_The_Tree()
    {
        // The trap. Rebuilding from the pane raises no DocumentOpened -- only opening a file does --
        // so a tree following that event alone would show the document as it was before the last
        // keystroke, for as long as somebody kept typing.
        var (_, viewer) = await Host();

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        Editor(viewer).Text = Markup.Replace("</g>", "  <circle cx=\"12\" cy=\"12\" r=\"4\" />\n  </g>");

        Assert.True(viewer.Rebuild());
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("circle", Rows(viewer));
    }

    [AvaloniaFact]
    public async Task The_Selection_Survives_A_Rebuild()
    {
        // By address and not by element: a rebuilt drawing shares no element with the one it
        // replaced, so anything holding a reference would lose the selection on every keystroke.
        var (_, viewer) = await Host();

        Assert.True(viewer.Elements.TrySelect("1/0"));
        Assert.Equal("rect", viewer.Elements.SelectedNode!.Label);

        var before = viewer.Elements.SelectedNode.Element;

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        Editor(viewer).Text = Markup.Replace("width=\"24\" height=\"24\" fill", "width=\"20\" height=\"20\" fill");

        Assert.True(viewer.Rebuild());
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("rect", viewer.Elements.SelectedNode!.Label);
        Assert.NotSame(before, viewer.Elements.SelectedNode.Element);
    }

    [AvaloniaFact]
    public async Task A_Selection_Whose_Element_Has_Gone_Is_Dropped()
    {
        var (_, viewer) = await Host();

        Assert.True(viewer.Elements.TrySelect("1/1"));
        Assert.Equal("text", viewer.Elements.SelectedNode!.Label);

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        Editor(viewer).Text = Markup.Replace("<text x=\"2\" y=\"20\">hi</text>", "");

        Assert.True(viewer.Rebuild());
        Dispatcher.UIThread.RunJobs();

        Assert.Null(viewer.Elements.SelectedNode);
    }

    [AvaloniaFact]
    public async Task Closing_The_Viewer_Empties_The_Tree()
    {
        var (_, viewer) = await Host();

        viewer.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(viewer.Elements.Root);
    }

    [AvaloniaFact]
    public async Task Hiding_The_Tree_Gives_Its_Height_Back()
    {
        // The row carries the height, so hiding the border alone would leave the parameters paying
        // for a strip of nothing -- the same trap the source pane has.
        var (window, viewer) = await Host();

        window.Measure(new global::Avalonia.Size(700, 500));
        window.Arrange(new global::Avalonia.Rect(0, 0, 700, 500));
        Dispatcher.UIThread.RunJobs();

        var panel = viewer.GetVisualDescendants().OfType<SvgViewerDeclarationPanel>().First();
        var before = panel.Bounds.Height;

        viewer.ShowElementTree = false;

        window.Measure(new global::Avalonia.Size(700, 500));
        window.Arrange(new global::Avalonia.Rect(0, 0, 700, 500));
        Dispatcher.UIThread.RunJobs();

        Assert.True(panel.Bounds.Height > before, $"{panel.Bounds.Height} is not more than {before}");
    }

    [AvaloniaFact]
    public async Task The_Tree_Is_Shown_Unless_A_Host_Says_Otherwise()
    {
        var (_, viewer) = await Host();

        Assert.True(viewer.ShowElementTree);

        viewer.ShowElementTree = false;

        Assert.False(viewer.ShowElementTree);
    }
}
