// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>
/// Colouring an expression inside a box that is still a text box.
/// </summary>
/// <remarks>
/// What the pieces are is pinned in Svg.Highlighting.UnitTests; here it only has to reach the
/// layout. The rest is what a text box must go on doing once the thing that paints it is replaced.
/// </remarks>
public class SvgExpressionPresenterTests
{
    private const string Grouped = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs>
            <e:code>
              <e:param name="tint" type="color" default="#ff0000" />
              <e:let name="deep">mix(tint, #000000, 0.5)</e:let>
            </e:code>
          </defs>
          <rect x="0" y="0" width="24" height="24" fill="{{ deep }}" />
        </svg>
        """;

    private static async Task<(Window Window, SvgViewer Viewer)> HostLoaded()
    {
        var viewer = new SvgViewer();

        var window = new Window { Width = 600, Height = 400, Background = global::Avalonia.Media.Brushes.White, Content = viewer };

        window.Show();

        Assert.True(await viewer.LoadTextAsync(Grouped));
        Dispatcher.UIThread.RunJobs();

        return (window, viewer);
    }

    /// <summary>The presenter behind the one let row's body box.</summary>
    private static SvgExpressionPresenter Presenter(SvgViewer viewer)
        => viewer.GetVisualDescendants().OfType<SvgExpressionPresenter>().Single();

    /// <summary>The distinct foregrounds the layout actually paints with, in order.</summary>
    private static IBrush?[] Painted(SvgExpressionPresenter presenter)
        => presenter.TextLayout.TextLines
            .SelectMany(line => line.TextRuns.OfType<ShapedTextRun>())
            .Select(run => run.Properties.ForegroundBrush)
            .ToArray();

    [AvaloniaFact]
    public async Task An_Expression_Is_Painted_In_More_Than_One_Colour()
    {
        var (window, viewer) = await HostLoaded();

        var painted = Painted(Presenter(viewer));

        // `mix` is a function, `tint` a name, `#000000` a colour and `0.5` a number: whatever the
        // palette is, one flat run would mean the tokens never reached the layout.
        Assert.True(painted.Length > 1, $"Expected several runs, found {painted.Length}.");
        Assert.True(painted.Distinct().Count() > 1, "Every run was painted the same.");

        window.Close();
    }

    [AvaloniaFact]
    public async Task Typing_Recolours_It()
    {
        var (window, viewer) = await HostLoaded();

        var presenter = Presenter(viewer);
        var before = Painted(presenter).Length;

        // A colour literal where there was none, so the run count has to move.
        viewer.Lets.Single().Expression = "mix(tint, #00ff00, 0.5) + 1";
        Dispatcher.UIThread.RunJobs();

        Assert.NotEqual(before, Painted(presenter).Length);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Selected_Text_Keeps_Its_Colours()
    {
        var (window, viewer) = await HostLoaded();

        var box = viewer.GetVisualDescendants().OfType<TextBox>()
            .Single(t => t.PlaceholderText == "expression");

        var painted = Painted(Presenter(viewer));

        box.SelectionStart = 0;
        box.SelectionEnd = box.Text!.Length;
        Dispatcher.UIThread.RunJobs();

        // The stock presenter repaints a selection in one brush. This one does not, because the
        // source pane does not either -- AvaloniaEdit leaves its selection foreground unset -- and
        // one expression shown in two places should not be two colours.
        Assert.Equal(painted, Painted(Presenter(viewer)));

        window.Close();
    }

    [AvaloniaFact]
    public async Task It_Is_Still_A_Text_Box()
    {
        var (window, viewer) = await HostLoaded();

        var box = viewer.GetVisualDescendants().OfType<TextBox>()
            .Single(t => t.PlaceholderText == "expression");

        // The point of replacing the presenter rather than the control: what a box does is still
        // the box's. If this ever needs its own answer, the approach has stopped paying for itself.
        box.Focus();
        box.CaretIndex = 3;
        box.SelectionStart = 0;
        box.SelectionEnd = 3;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("mix", box.SelectedText);
        Assert.Equal(3, box.CaretIndex);

        window.Close();
    }

    [AvaloniaFact]
    public async Task An_Empty_Box_Shows_What_It_Is_For()
    {
        var (window, viewer) = await HostLoaded();

        var row = viewer.Lets.Single();

        row.Expression = string.Empty;
        Dispatcher.UIThread.RunJobs();

        var watermark = viewer.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "PART_Watermark" && (string?)t.Text == "expression");

        // The template replaces the stock one, so the watermark is this template's to provide.
        Assert.True(watermark.IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void The_Parameter_Form_Colours_Its_Expressions_And_Not_Its_Name()
    {
        // Its own window, and that is the point of the test: the palette used to live inside
        // SvgViewer's resources, where nothing shown in a window of its own could reach it. Every
        // brush resolved to null and these boxes painted flat while the pane beside them coloured
        // the same text. Asserting the presenter is *present* did not catch it -- only asking what
        // it actually paints does.
        var form = new SvgParameterFormView();
        var window = new Window { Width = 400, Height = 400, Content = form };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var boxes = form.GetVisualDescendants().OfType<TextBox>().ToList();

        TextBox Box(string name) => boxes.Single(b => b.Name == name);

        SvgExpressionPresenter? PresenterOf(string name)
            => Box(name).GetVisualDescendants().OfType<SvgExpressionPresenter>().FirstOrDefault();

        // default, min, max and step hold expressions -- `tau / 4` is a legal default.
        foreach (var name in new[] { "DefaultBox", "MinBox", "MaxBox", "StepBox" })
        {
            Box(name).Text = "mix(#ff0000, #000000, 0.5)";
        }

        Dispatcher.UIThread.RunJobs();

        foreach (var name in new[] { "DefaultBox", "MinBox", "MaxBox", "StepBox" })
        {
            var presenter = PresenterOf(name);

            Assert.NotNull(presenter);
            Assert.True(
                Painted(presenter!).Distinct().Count() > 1,
                $"{name} painted every run the same, so its brushes did not resolve.");
        }

        // A name is an identifier, not an expression. Colouring it would say it was one.
        Assert.Null(PresenterOf("NameBox"));

        window.Close();
    }
}
