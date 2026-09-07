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
    public async Task Filtering_Keeps_The_Matches_And_What_Is_Above_Them()
    {
        // A match with its ancestors cut off says where it is not.
        var (_, viewer) = await Host();

        viewer.Elements.Filter = "rect";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "svg", "g #wrap", "rect" }, Rows(viewer));
    }

    [AvaloniaFact]
    public async Task Filtering_Matches_An_Id_As_Well_As_A_Name()
    {
        var (_, viewer) = await Host();

        viewer.Elements.Filter = "wrap";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "svg", "g #wrap" }, Rows(viewer));
    }

    [AvaloniaFact]
    public async Task A_Filter_Opens_What_It_Keeps_And_Clearing_It_Folds_Back()
    {
        // A match three levels down behind a closed row makes the box look broken; and writing that
        // into what the reader had open would leave the tree unfolded once the box is empty again.
        var (_, viewer) = await Host();

        var group = viewer.Elements.Root!.Children.Single(node => node.Label == "g");

        group.IsExpanded = false;

        viewer.Elements.Filter = "rect";
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewer.Elements.Root!.Children.Single(node => node.Label == "g").IsExpanded);

        viewer.Elements.Filter = "";
        Dispatcher.UIThread.RunJobs();

        Assert.False(viewer.Elements.Root!.Children.Single(node => node.Label == "g").IsExpanded);
    }

    [AvaloniaFact]
    public async Task A_Filter_That_Keeps_Nothing_Says_So()
    {
        var (_, viewer) = await Host();

        viewer.Elements.Filter = "nothing-is-called-this";
        Dispatcher.UIThread.RunJobs();

        Assert.Null(viewer.Elements.Root);
    }

    [AvaloniaFact]
    public async Task A_Selection_The_Filter_Hides_Comes_Back()
    {
        // Hidden is not deleted. Only an element that has gone from the document stops being the
        // selected one.
        var (_, viewer) = await Host();

        Assert.True(viewer.Elements.TrySelect("1/0"));

        viewer.Elements.Filter = "text";
        Dispatcher.UIThread.RunJobs();

        Assert.Null(viewer.Elements.SelectedNode);

        viewer.Elements.Filter = "";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("rect", viewer.Elements.SelectedNode!.Label);
    }

    // ---- showing the element in the text -------------------------------------------------------

    [AvaloniaFact]
    public async Task Selecting_A_Row_Opens_The_Source_And_Selects_Its_Start_Tag()
    {
        var (_, viewer) = await Host();

        Assert.False(viewer.ShowSource);

        Assert.True(viewer.Elements.TrySelect("1/0"));
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewer.ShowSource);
        Assert.Equal(
            """<rect x="0" y="0" width="24" height="24" fill="{{ tint }}" />""",
            Editor(viewer).SelectedText);
    }

    [AvaloniaFact]
    public async Task The_Root_Row_Selects_The_Svg_Tag()
    {
        // The root's address is the empty string, which is easy to write off as "no address".
        var (_, viewer) = await Host();

        Assert.True(viewer.Elements.TrySelect(""));
        Dispatcher.UIThread.RunJobs();

        Assert.StartsWith("<svg xmlns=", Editor(viewer).SelectedText);
        Assert.EndsWith("""height="24">""", Editor(viewer).SelectedText);
    }

    [AvaloniaFact]
    public async Task An_Element_In_The_Declarations_Block_Is_Shown_Like_Any_Other()
    {
        var (_, viewer) = await Host();

        Assert.True(viewer.Elements.TrySelect("0/0/0"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            """<e:param name="tint" type="color" default="#ff0000" />""",
            Editor(viewer).SelectedText);
    }

    [AvaloniaFact]
    public async Task A_Row_That_Cannot_Be_Placed_Moves_Nothing()
    {
        // Half-typed markup does not parse, so the drawing and its tree are the last ones that did
        // while the text is something else entirely. Scrolling somebody confidently to the wrong
        // line is the failure worth engineering against; not moving is the second best.
        var (_, viewer) = await Host();

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        Editor(viewer).Text = "<svg><rect";
        Dispatcher.UIThread.RunJobs();

        Assert.False(viewer.RevealInSource(viewer.Elements.Root!.Children[1]));
        Assert.Equal(string.Empty, Editor(viewer).SelectedText);
    }

    [AvaloniaFact]
    public async Task A_Rebuild_Does_Not_Move_The_Source_View()
    {
        // Restoring the selection is not the reader picking something. A rebuild happens on every
        // keystroke, and one that scrolled the pane would fight whoever was typing in it.
        var (_, viewer) = await Host();

        Assert.True(viewer.Elements.TrySelect("1/1"));
        Dispatcher.UIThread.RunJobs();

        Editor(viewer).CaretOffset = 0;
        Editor(viewer).SelectionLength = 0;

        Assert.True(viewer.Rebuild());
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(string.Empty, Editor(viewer).SelectedText);
        Assert.Equal("text", viewer.Elements.SelectedNode!.Label);
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
