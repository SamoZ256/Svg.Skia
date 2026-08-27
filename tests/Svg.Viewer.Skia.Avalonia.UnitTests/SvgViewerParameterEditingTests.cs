// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Svg.Expressions;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>
/// Declaring a parameter from the panel, and committing values into the drawing as defaults.
/// </summary>
/// <remarks>
/// <para>
/// What these are really about is that the panel and the pane are one document and not two. A
/// parameter added from the panel is a change to the drawing's own text, so it makes the document
/// modified, it saves, it undoes in one step, and it shows up in the pane spelled the way somebody
/// would have typed it.
/// </para>
/// <para>
/// The splice itself is pinned in Svg.SourceEditing.UnitTests, against documents far more awkward
/// than these. Here it only has to arrive.
/// </para>
/// </remarks>
public class SvgViewerParameterEditingTests
{
    private const string Parametric = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs>
            <e:code>
              <e:param name="tint" type="color" default="#ff0000" />
              <e:param name="fade" type="number" default="1" min="0" max="1" />
            </e:code>
          </defs>
          <!-- the rectangle everything above is for -->
          <rect x="0" y="0" width="24" height="24" fill="{{ tint }}" opacity="{{ fade }}" />
        </svg>
        """;

    private const string Grouped = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs>
            <e:code>
              <e:param name="tint" type="color" default="#ff0000" />

              <e:let name="deep">mix(tint, #000000, 0.4)</e:let>
            </e:code>
          </defs>
          <rect x="0" y="0" width="24" height="24" fill="{{ deep }}" />
        </svg>
        """;

    private const string Plain = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <rect x="0" y="0" width="24" height="24" fill="#ff0000" />
        </svg>
        """;

    private static (Window Window, SvgViewer Viewer) Host(SvgExpressionParameter? answer = null)
    {
        var viewer = new SvgViewer
        {
            ParameterDialogService = new StubParameterDialogService(answer),
        };

        var window = new Window
        {
            Width = 500,
            Height = 300,
            Background = Brushes.White,
            Content = viewer
        };

        window.Show();

        return (window, viewer);
    }

    private static async Task<(Window, SvgViewer)> HostLoaded(string markup, SvgExpressionParameter? answer = null)
    {
        var (window, viewer) = Host(answer);

        Assert.True(await viewer.LoadTextAsync(markup));
        Dispatcher.UIThread.RunJobs();

        return (window, viewer);
    }

    /// <summary>Waits for the rebuild the debounce holds back.</summary>
    private static async Task Settle()
    {
        Dispatcher.UIThread.RunJobs();

        // Real time, because the debounce is a real timer: the point of it is that it waits.
        await Task.Delay(400).ConfigureAwait(true);
        Dispatcher.UIThread.RunJobs();
    }

    private static TextEditor Pane(SvgViewer viewer)
        => viewer.GetVisualDescendants().OfType<TextEditor>().First(c => c.Name == "SourceEditor");

    private static TextBlock CommitLabel(SvgViewer viewer)
        => viewer.GetVisualDescendants().OfType<TextBlock>().First(c => c.Name == "CommitLabel");

    private static SvgExpressionParameter Radius(string name = "radius")
        => new(name, ExprType.Number, "8", "0", "12", null);

    [AvaloniaFact]
    public async Task A_Parameter_Added_From_The_Panel_Reaches_The_Drawing()
    {
        var (window, viewer) = await HostLoaded(Parametric, Radius());

        Assert.True(await viewer.AddParameterAsync());
        await Settle();

        Assert.Contains(viewer.Parameters, row => row.Name == "radius");
        Assert.Equal(3, viewer.Parameters.Count);

        window.Close();
    }

    [AvaloniaFact]
    public async Task It_Works_With_The_Source_Pane_Closed_And_Leaves_The_Document_Saveable()
    {
        var file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".svg");
        await File.WriteAllTextAsync(file, Parametric).ConfigureAwait(true);

        try
        {
            var (window, viewer) = Host(Radius());

            Assert.True(await viewer.LoadAsync(file));
            Dispatcher.UIThread.RunJobs();

            // The pane has never been opened, which is the state a viewer spends most of its life in.
            Assert.False(viewer.ShowSource);
            Assert.False(viewer.IsSourceModified);

            Assert.True(await viewer.AddParameterAsync());
            await Settle();

            Assert.True(viewer.IsSourceModified);
            Assert.True(await viewer.SaveSourceAsync());

            var written = await File.ReadAllTextAsync(file).ConfigureAwait(true);

            Assert.Contains("""<e:param name="radius" type="number" default="8" min="0" max="12" />""", written);
            Assert.False(viewer.IsSourceModified);

            window.Close();
        }
        finally
        {
            File.Delete(file);
        }
    }

    [AvaloniaFact]
    public async Task Everything_Else_In_The_File_Is_Left_Alone()
    {
        var (window, viewer) = await HostLoaded(Parametric, Radius());

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(await viewer.AddParameterAsync());
        await Settle();

        var text = Pane(viewer).Document.Text;

        // The comment and the placeholders are what a regenerated document would have lost.
        Assert.Contains("<!-- the rectangle everything above is for -->", text);
        Assert.Contains("fill=\"{{ tint }}\" opacity=\"{{ fade }}\"", text);
        Assert.Equal(Parametric.Split('\n').Length + 1, text.Split('\n').Length);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Drawing_That_Declared_Nothing_Gets_A_Block_And_The_Namespace()
    {
        var (window, viewer) = await HostLoaded(Plain, Radius());

        Assert.Empty(viewer.Parameters);

        Assert.True(await viewer.AddParameterAsync());
        await Settle();

        Assert.Single(viewer.Parameters);
        Assert.Equal("radius", viewer.Parameters[0].Name);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Parameter_Is_Written_With_The_Parameters_And_Not_Below_The_Lets()
    {
        var (window, viewer) = await HostLoaded(Grouped, Radius());

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(await viewer.AddParameterAsync());
        await Settle();

        var text = Pane(viewer).Document.Text;

        Assert.True(
            text.IndexOf("name=\"radius\"", StringComparison.Ordinal) < text.IndexOf("<e:let", StringComparison.Ordinal),
            "A parameter added from the panel should join the parameters, above the lets.");

        window.Close();
    }

    [AvaloniaFact]
    public async Task Adding_One_Is_A_Single_Thing_To_Take_Back()
    {
        var (window, viewer) = await HostLoaded(Plain, Radius());

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var before = Pane(viewer).Document.Text;

        Assert.True(await viewer.AddParameterAsync());
        await Settle();

        // Three spans went in — the namespace, the block and the declaration — and one undo takes
        // all of them out again.
        Pane(viewer).Document.UndoStack.Undo();
        await Settle();

        Assert.Equal(before, Pane(viewer).Document.Text);
        Assert.Empty(viewer.Parameters);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Value_Somebody_Chose_Survives_The_Addition()
    {
        var (window, viewer) = await HostLoaded(Parametric, Radius());

        var fade = viewer.Parameters.OfType<SvgViewerNumberParameter>().Single(row => row.Name == "fade");

        fade.Value = 0.25d;
        Dispatcher.UIThread.RunJobs();

        Assert.True(await viewer.AddParameterAsync());
        await Settle();

        // Adding a parameter rewrites the block, so every row is rebuilt. A value nobody typed a
        // default for should still be where it was left.
        Assert.Equal(
            0.25d,
            viewer.Parameters.OfType<SvgViewerNumberParameter>().Single(row => row.Name == "fade").Value,
            3);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Nothing_Happens_When_Nobody_Names_A_Parameter()
    {
        var (window, viewer) = await HostLoaded(Parametric);

        Assert.False(await viewer.AddParameterAsync());
        Assert.False(viewer.IsSourceModified);
        Assert.Equal(2, viewer.Parameters.Count);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Name_Already_Taken_Is_Refused_Rather_Than_Written()
    {
        var (window, viewer) = await HostLoaded(Parametric, Radius("tint"));

        var said = string.Empty;
        viewer.ErrorRaised += (_, message) => said = message;

        Assert.False(await viewer.AddParameterAsync());

        Assert.False(viewer.IsSourceModified);
        Assert.Equal(2, viewer.Parameters.Count);
        Assert.Contains("tint", said);

        window.Close();
    }

    [AvaloniaFact]
    public async Task An_Edit_Leaves_A_View_Somebody_Adjusted_Where_It_Was()
    {
        var (window, viewer) = await HostLoaded(Parametric, Radius());

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        viewer.Canvas.ZoomIn();
        viewer.Canvas.ZoomIn();
        Dispatcher.UIThread.RunJobs();

        var scale = viewer.Canvas.Scale;
        var offsetX = viewer.Canvas.OffsetX;

        Assert.True(await viewer.AddParameterAsync());
        await Settle();

        Assert.Equal(scale, viewer.Canvas.Scale, 6);
        Assert.Equal(offsetX, viewer.Canvas.OffsetX, 6);

        window.Close();
    }

    [AvaloniaFact]
    public async Task An_Edit_Keeps_A_View_Nobody_Adjusted_Fitted()
    {
        var (window, viewer) = await HostLoaded(Parametric, Radius());

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var fitted = viewer.Canvas.Scale;

        Assert.True(await viewer.AddParameterAsync());
        await Settle();

        Assert.Equal(fitted, viewer.Canvas.Scale, 6);

        window.Close();
    }

    // ---- editing one ----

    [AvaloniaFact]
    public async Task Editing_A_Parameter_Writes_Its_New_Range()
    {
        var (window, viewer) = await HostLoaded(
            Parametric,
            new SvgExpressionParameter("fade", ExprType.Number, "1", "0", "4", "0.5"));

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var row = viewer.Parameters.OfType<SvgViewerNumberParameter>().Single();

        Assert.True(await viewer.EditParameterAsync(row));
        await Settle();

        var edited = viewer.Parameters.OfType<SvgViewerNumberParameter>().Single();

        Assert.Equal(4d, edited.Maximum, 6);
        Assert.Equal(0.5d, edited.Step, 6);
        Assert.Contains("max=\"4\"", Pane(viewer).Document.Text);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Renaming_A_Parameter_Carries_The_Places_That_Use_It()
    {
        var (window, viewer) = await HostLoaded(
            Parametric,
            new SvgExpressionParameter("opacity", ExprType.Number, "1", "0", "1", null));

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var row = viewer.Parameters.OfType<SvgViewerNumberParameter>().Single();

        Assert.True(await viewer.EditParameterAsync(row));
        await Settle();

        var text = Pane(viewer).Document.Text;

        Assert.Contains("{{ opacity }}", text);
        Assert.DoesNotContain("fade", text);
        Assert.Contains(viewer.Parameters, p => p.Name == "opacity");

        window.Close();
    }

    [AvaloniaFact]
    public async Task The_Form_Is_Shown_What_The_Parameter_Says_And_Not_Its_Own_Name_As_Taken()
    {
        var service = new StubParameterDialogService(null);

        var (window, viewer) = await HostLoaded(Parametric);
        viewer.ParameterDialogService = service;

        var row = viewer.Parameters.OfType<SvgViewerNumberParameter>().Single();

        Assert.False(await viewer.EditParameterAsync(row));

        Assert.Equal("fade", service.Asked?.Name);
        Assert.Equal("1", service.Asked?.DefaultExpression);
        Assert.DoesNotContain("fade", service.Taken!);
        Assert.Contains("tint", service.Taken!);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Editing_Is_A_Single_Thing_To_Take_Back()
    {
        var (window, viewer) = await HostLoaded(
            Parametric,
            new SvgExpressionParameter("opacity", ExprType.Number, "1", "0", "1", null));

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var before = Pane(viewer).Document.Text;

        Assert.True(await viewer.EditParameterAsync(viewer.Parameters.OfType<SvgViewerNumberParameter>().Single()));
        await Settle();

        // The declaration and the placeholder that names it moved together, and come back together.
        Pane(viewer).Document.UndoStack.Undo();
        await Settle();

        Assert.Equal(before, Pane(viewer).Document.Text);

        window.Close();
    }

    [AvaloniaFact]
    public void The_Form_Will_Not_Change_A_Type_It_Was_Opened_On()
    {
        var form = new SvgParameterFormView();
        var window = new Window { Width = 400, Height = 400, Content = form };

        window.Show();

        form.Initialize(new SvgExpressionParameter("tint", ExprType.Color, "#3fb5b5"));
        Dispatcher.UIThread.RunJobs();

        var type = form.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "TypeBox");

        Assert.False(type.IsEnabled);
        Assert.Equal(ExprType.Color, form.TryBuild(out _)?.Type);

        window.Close();
    }

    // ---- committing values as defaults ----

    [AvaloniaFact]
    public async Task Committing_Writes_Every_Changed_Value_As_The_Declared_Default()
    {
        var (window, viewer) = await HostLoaded(Parametric);

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        viewer.Parameters.OfType<SvgViewerNumberParameter>().Single().Value = 0.5d;
        viewer.Parameters.OfType<SvgViewerColorParameter>().Single().Color = Color.FromRgb(0, 0, 255);
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewer.CommitParameterDefaults());
        await Settle();

        var text = Pane(viewer).Document.Text;

        Assert.Contains("default=\"0.5\"", text);
        Assert.Contains("default=\"#0000ff\"", text);

        // Committed, so nothing differs from the declared defaults any more.
        Assert.DoesNotContain(viewer.Parameters, row => row.IsModified);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Committing_A_Value_That_Is_Not_A_Round_Binary_Fraction_Still_Settles()
    {
        // 0.5 is exactly representable and 0.37 is not, which is the whole of the difference: the
        // seed and a restored value have to reach the same double or the row stays modified for
        // ever and the panel keeps offering to commit what it just committed.
        var (window, viewer) = await HostLoaded(Parametric);

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        viewer.Parameters.OfType<SvgViewerNumberParameter>().Single().Value = 0.37d;
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewer.CommitParameterDefaults());
        await Settle();

        Assert.DoesNotContain(viewer.Parameters, row => row.IsModified);
        Assert.False(CommitLabel(viewer).IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public async Task The_Panel_Stops_Offering_To_Commit_Once_It_Has()
    {
        var (window, viewer) = await HostLoaded(Parametric);

        Assert.False(CommitLabel(viewer).IsVisible);

        viewer.Parameters.OfType<SvgViewerColorParameter>().Single().Color = Color.FromRgb(0, 0, 255);
        Dispatcher.UIThread.RunJobs();

        Assert.True(CommitLabel(viewer).IsVisible);

        Assert.True(viewer.CommitParameterDefaults());
        await Settle();

        Assert.False(CommitLabel(viewer).IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Committing_A_Whole_Panel_Is_A_Single_Thing_To_Take_Back()
    {
        var (window, viewer) = await HostLoaded(Parametric);

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var before = Pane(viewer).Document.Text;

        viewer.Parameters.OfType<SvgViewerNumberParameter>().Single().Value = 0.5d;
        viewer.Parameters.OfType<SvgViewerColorParameter>().Single().Color = Color.FromRgb(0, 0, 255);
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewer.CommitParameterDefaults());
        await Settle();

        Pane(viewer).Document.UndoStack.Undo();
        await Settle();

        Assert.Equal(before, Pane(viewer).Document.Text);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Committing_With_Nothing_Changed_Does_Nothing()
    {
        var (window, viewer) = await HostLoaded(Parametric);

        Assert.False(viewer.CommitParameterDefaults());
        Assert.False(viewer.IsSourceModified);

        window.Close();
    }

    [AvaloniaFact]
    public async Task An_Edit_Is_Refused_While_The_Text_Will_Not_Parse()
    {
        var (window, viewer) = await HostLoaded(Parametric, Radius());

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        // What a drawing looks like halfway through being typed.
        Pane(viewer).Document.Text = Parametric.Replace("</svg>", string.Empty);
        await Settle();

        var said = string.Empty;
        viewer.ErrorRaised += (_, message) => said = message;

        Assert.False(await viewer.AddParameterAsync());
        Assert.NotEmpty(said);

        window.Close();
    }

    /// <summary>Answers with whatever the test decided, because a modal cannot be driven headlessly.</summary>
    private sealed class StubParameterDialogService : ISvgViewerParameterDialogService
    {
        private readonly SvgExpressionParameter? _answer;

        public StubParameterDialogService(SvgExpressionParameter? answer) => _answer = answer;

        /// <summary>What the form was shown, so a test can assert on what it was offered.</summary>
        public SvgExpressionParameter? Asked { get; private set; }

        public IReadOnlyCollection<string>? Taken { get; private set; }

        public Task<SvgExpressionParameter?> AskAsync(TopLevel? owner, IReadOnlyCollection<string> taken)
        {
            Taken = taken;

            return Task.FromResult(_answer);
        }

        public Task<SvgExpressionParameter?> EditAsync(
            TopLevel? owner,
            IReadOnlyCollection<string> taken,
            SvgExpressionParameter existing)
        {
            Asked = existing;
            Taken = taken;

            return Task.FromResult(_answer);
        }
    }

    // ---- taking one away ----

    [AvaloniaFact]
    public async Task A_Parameter_Nothing_Names_Is_Removed_From_The_Drawing()
    {
        var (window, viewer) = await HostLoaded(Parametric, Radius());

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        // `fade` is on the rect's opacity, `tint` on its fill, so neither can go. One that nothing
        // names has to be declared first.
        Assert.True(await viewer.AddParameterAsync());
        await Settle();

        var spare = viewer.Parameters.Single(row => row.Name == "radius");

        Assert.True(viewer.RemoveParameter(spare));
        await Settle();

        Assert.DoesNotContain(viewer.Parameters, row => row.Name == "radius");
        Assert.DoesNotContain("radius", Pane(viewer).Text);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Parameter_The_Drawing_Still_Uses_Is_Refused_And_Said_So()
    {
        var (window, viewer) = await HostLoaded(Parametric);

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var used = viewer.Parameters.Single(row => row.Name == "tint");

        Assert.False(viewer.RemoveParameter(used));
        await Settle();

        // Still there, and the pane still holds the placeholder that kept it.
        Assert.Contains(viewer.Parameters, row => row.Name == "tint");
        Assert.Contains("{{ tint }}", Pane(viewer).Text);
        Assert.False(viewer.IsSourceModified);

        window.Close();
    }

    [AvaloniaFact]
    public async Task The_Row_Button_Is_What_Asks_For_It()
    {
        var (window, viewer) = await HostLoaded(Parametric, Radius());

        Assert.True(await viewer.AddParameterAsync());
        await Settle();

        var spare = viewer.Parameters.Single(row => row.Name == "radius");

        viewer.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => ReferenceEquals(button.DataContext, spare) && button.Content as string == "✕")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        await Settle();

        Assert.DoesNotContain(viewer.Parameters, row => row.Name == "radius");

        window.Close();
    }
}
