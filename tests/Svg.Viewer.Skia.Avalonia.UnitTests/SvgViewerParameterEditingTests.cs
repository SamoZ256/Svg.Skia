// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
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

    private const string Stepped = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs>
            <e:code>
              <e:param name="steps" type="integer" default="4" min="0" max="9" step="1" />
            </e:code>
          </defs>
          <rect x="0" y="0" width="24" height="24" fill="#ff0000" opacity="{{ num(steps) / 10 }}" />
        </svg>
        """;

    [AvaloniaFact]
    public async Task An_Integer_Parameter_Gets_A_Row_Of_Its_Own()
    {
        var (window, viewer) = await HostLoaded(Stepped);

        var row = Assert.IsType<SvgViewerIntegerParameter>(Assert.Single(viewer.Parameters));

        Assert.Equal("steps", row.Name);
        Assert.Equal(4, row.Value);
        Assert.Equal(0, row.Minimum);
        Assert.Equal(9, row.Maximum);
        Assert.Equal(1, row.Step);
        Assert.False(row.IsModified);

        // The value a host puts back, and the one a commit would write into the document.
        Assert.True(viewer.TrySetParameterValue("steps", ExprValue.Integer(7)));
        Assert.Equal(7, row.Value);
        Assert.True(row.IsModified);
        Assert.Equal("7", row.ToExpression());
        Assert.Equal(ExprType.Integer, row.ToExprValue().Type);

        // A number is not an integer, and the row says so rather than rounding one in.
        Assert.False(viewer.TrySetParameterValue("steps", ExprValue.Number(2f)));
        Assert.Equal(7, row.Value);

        row.ResetToDefault();
        Assert.Equal(4, row.Value);

        window.Close();
    }

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

    private static TextBlock CommitLabel(SvgViewer viewer)
        => viewer.GetVisualDescendants().OfType<TextBlock>().First(c => c.Name == "CommitLabel");

    private static SvgExpressionParameter Radius(string name = "radius")
        => new(name, ExprType.Number, "8", "0", "12", null);

    [AvaloniaFact]
    public async Task A_Parameter_Added_From_The_Panel_Reaches_The_Drawing()
    {
        var (window, viewer) = await HostLoaded(Parametric, Radius());

        Assert.True(await viewer.AddParameterAsync());
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(viewer.Parameters, row => row.Name == "radius");
        Assert.Equal(3, viewer.Parameters.Count);

        window.Close();
    }

    [AvaloniaFact]
    public async Task An_Added_Parameter_Leaves_The_Document_Saveable()
    {
        var file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".svg");
        await File.WriteAllTextAsync(file, Parametric).ConfigureAwait(true);

        try
        {
            var (window, viewer) = Host(Radius());

            Assert.True(await viewer.LoadAsync(file));
            Dispatcher.UIThread.RunJobs();

            Assert.False(viewer.IsSourceModified);

            Assert.True(await viewer.AddParameterAsync());
            Dispatcher.UIThread.RunJobs();

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

        Dispatcher.UIThread.RunJobs();

        Assert.True(await viewer.AddParameterAsync());
        Dispatcher.UIThread.RunJobs();

        var text = viewer.Source;

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
        Dispatcher.UIThread.RunJobs();

        Assert.Single(viewer.Parameters);
        Assert.Equal("radius", viewer.Parameters[0].Name);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Parameter_Is_Written_With_The_Parameters_And_Not_Below_The_Lets()
    {
        var (window, viewer) = await HostLoaded(Grouped, Radius());

        Dispatcher.UIThread.RunJobs();

        Assert.True(await viewer.AddParameterAsync());
        Dispatcher.UIThread.RunJobs();

        var text = viewer.Source;

        Assert.True(
            text.IndexOf("name=\"radius\"", StringComparison.Ordinal) < text.IndexOf("<e:let", StringComparison.Ordinal),
            "A parameter added from the panel should join the parameters, above the lets.");

        window.Close();
    }

    [AvaloniaFact]
    public async Task Adding_One_Is_A_Single_Thing_To_Take_Back()
    {
        var (window, viewer) = await HostLoaded(Plain, Radius());

        Dispatcher.UIThread.RunJobs();

        var before = viewer.Source;

        Assert.True(await viewer.AddParameterAsync());
        Dispatcher.UIThread.RunJobs();

        // Three spans went in — the namespace, the block and the declaration — and one undo takes
        // all of them out again.
        viewer.Undo();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before, viewer.Source);
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
        Dispatcher.UIThread.RunJobs();

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

        Dispatcher.UIThread.RunJobs();

        viewer.Canvas.ZoomIn();
        viewer.Canvas.ZoomIn();
        Dispatcher.UIThread.RunJobs();

        var scale = viewer.Canvas.Scale;
        var offsetX = viewer.Canvas.OffsetX;

        Assert.True(await viewer.AddParameterAsync());
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(scale, viewer.Canvas.Scale, 6);
        Assert.Equal(offsetX, viewer.Canvas.OffsetX, 6);

        window.Close();
    }

    [AvaloniaFact]
    public async Task An_Edit_Keeps_A_View_Nobody_Adjusted_Fitted()
    {
        var (window, viewer) = await HostLoaded(Parametric, Radius());

        Dispatcher.UIThread.RunJobs();

        var fitted = viewer.Canvas.Scale;

        Assert.True(await viewer.AddParameterAsync());
        Dispatcher.UIThread.RunJobs();

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

        Dispatcher.UIThread.RunJobs();

        var row = viewer.Parameters.OfType<SvgViewerNumberParameter>().Single();

        Assert.True(await viewer.EditParameterAsync(row));
        Dispatcher.UIThread.RunJobs();

        var edited = viewer.Parameters.OfType<SvgViewerNumberParameter>().Single();

        Assert.Equal(4m, edited.Maximum);
        Assert.Equal(0.5d, edited.Step, 6);
        Assert.Contains("max=\"4\"", viewer.Source);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Renaming_A_Parameter_Carries_The_Places_That_Use_It()
    {
        var (window, viewer) = await HostLoaded(
            Parametric,
            new SvgExpressionParameter("opacity", ExprType.Number, "1", "0", "1", null));

        Dispatcher.UIThread.RunJobs();

        var row = viewer.Parameters.OfType<SvgViewerNumberParameter>().Single();

        Assert.True(await viewer.EditParameterAsync(row));
        Dispatcher.UIThread.RunJobs();

        var text = viewer.Source;

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

        Dispatcher.UIThread.RunJobs();

        var before = viewer.Source;

        Assert.True(await viewer.EditParameterAsync(viewer.Parameters.OfType<SvgViewerNumberParameter>().Single()));
        Dispatcher.UIThread.RunJobs();

        // The declaration and the placeholder that names it moved together, and come back together.
        viewer.Undo();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before, viewer.Source);

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

    [AvaloniaFact]
    public void The_Form_Opens_On_The_Type_The_Last_Added_Parameter_Chose()
    {
        // The memory is a static, so this ends where it started: nothing else here asks what type a
        // fresh form opens on, but a test that leaves the default moved is one waiting to be one.
        Assert.Equal(ExprType.Number, Added("hue", ExprType.Number).Type);
        Assert.Equal(ExprType.Color, Added("tint", ExprType.Color).Type);

        var form = new SvgParameterFormView();
        var window = new Window { Width = 400, Height = 400, Content = form };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        // A name only because a nameless form builds nothing; the type is what is being asked about.
        form.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == "NameBox").Text = "shade";

        Assert.Equal(ExprType.Color, form.TryBuild(out _)?.Type);

        window.Close();

        Assert.Equal(ExprType.Number, Added("fade", ExprType.Number).Type);
    }

    /// <summary>Fills a form in and presses its button, as somebody adding a parameter would.</summary>
    private static SvgExpressionParameter Added(string name, ExprType type, string? @default = null)
    {
        var form = new SvgParameterFormView();
        var window = new Window { Width = 400, Height = 400, Content = form };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        form.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == "NameBox").Text = name;
        form.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == "DefaultBox").Text = @default;
        form.GetVisualDescendants().OfType<ComboBox>().First(box => box.Name == "TypeBox").SelectedIndex = type switch
        {
            // The picker's order, which is the enum's. Named rather than counted from the end, so a
            // type added between two of these moves the indices here and not silently the meaning.
            ExprType.Number => 0,
            ExprType.Integer => 1,
            ExprType.Color => 2,
            ExprType.Boolean => 3,
            ExprType.String => 4,
            ExprType.NumberList => 5,
            ExprType.ColorList => 6,
            _ => throw new NotSupportedException($"Unsupported {nameof(ExprType)}: {type}."),
        };

        SvgExpressionParameter? accepted = null;

        form.Accepted += (_, parameter) => accepted = parameter;

        form.GetVisualDescendants()
            .OfType<Button>()
            .First(button => button.Name == "AddButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.Close();

        return accepted!;
    }

    /// <summary>Both lists are offered by name, take a list literal for a default, and have no range.</summary>
    [AvaloniaFact]
    public void The_Form_Declares_Either_List_With_A_Literal_For_Its_Default()
    {
        var numbers = Added("positions", ExprType.NumberList, "[0, 0.5, 1]");
        var colours = Added("colors", ExprType.ColorList, "[#ff0000, #0000ff]");

        Assert.Equal((ExprType.NumberList, "[0, 0.5, 1]"), (numbers.Type, numbers.DefaultExpression));
        Assert.Equal((ExprType.ColorList, "[#ff0000, #0000ff]"), (colours.Type, colours.DefaultExpression));
        Assert.Null(numbers.MinExpression);

        var form = new SvgParameterFormView();
        var window = new Window { Width = 400, Height = 400, Content = form };

        window.Show();

        // And opened on one, the type is the list it was declared as and the range is not there.
        form.Initialize(colours);
        Dispatcher.UIThread.RunJobs();

        var type = form.GetVisualDescendants().OfType<ComboBox>().First(box => box.Name == "TypeBox");

        Assert.Equal("color list", (type.SelectedItem as ComboBoxItem)?.Content);
        Assert.False(form.GetVisualDescendants().OfType<StackPanel>().First(panel => panel.Name == "RangeRows").IsVisible);
        Assert.Equal(colours, form.TryBuild(out _));

        // A literal of the other kind is refused by what the language says of it.
        form.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == "DefaultBox").Text = "[0, 1]";

        Assert.Null(form.TryBuild(out var trouble));
        Assert.Contains("list of colours", trouble);

        window.Close();
        Assert.Equal(ExprType.Number, Added("fade", ExprType.Number).Type);
    }

    // ---- lists ----

    private const string Listed = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs>
            <e:code>
              <e:param name="colors" type="color[]" default="[#ff0000, #0000ff]" />
              <e:param name="positions" type="number[]" default="[0, 1]" />
            </e:code>
            <linearGradient id="g" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2="24" y2="0"
                            e:stop-colors="{{ colors }}" e:stop-offsets="{{ positions }}">
              <stop offset="0" stop-color="#ff0000" />
              <stop offset="1" stop-color="#0000ff" />
            </linearGradient>
          </defs>
          <rect x="0" y="0" width="24" height="24" fill="url(#g)" />
        </svg>
        """;

    /// <summary>A button in a list row, by the name it gives assistive technology.</summary>
    private static Button Named(SvgViewer viewer, string name) => Editor<Button>(viewer, name);

    private static T Editor<T>(SvgViewer viewer, string name)
        where T : Control
        => viewer.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetName(control) == name);

    /// <summary>What the drawing renders at one pixel, from its own picture.</summary>
    private static SKColor Pixel(SvgViewer viewer, int x, int y)
    {
        using var bitmap = new SKBitmap(24, 24);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawPicture(viewer.Svg!.Picture);
        }

        return bitmap.GetPixel(x, y);
    }

    /// <summary>
    /// A list row's own buttons and editors change the list, and the drawing follows each change.
    /// </summary>
    [AvaloniaFact]
    public async Task A_List_Is_Edited_Element_By_Element_And_The_Drawing_Follows()
    {
        var (window, viewer) = await HostLoaded(Listed);

        var colours = Assert.IsType<SvgViewerColorListParameter>(viewer.Parameters[0]);
        var positions = Assert.IsType<SvgViewerNumberListParameter>(viewer.Parameters[1]);
        var changes = 0;

        colours.ValueChanged += (_, _) => changes++;
        positions.ValueChanged += (_, _) => changes++;

        Assert.True(Pixel(viewer, 0, 12).Red > 200);

        // The first and the last cannot go further, and say so by being disabled.
        Assert.False(Named(viewer, "Move colors[0] up").IsEnabled);
        Assert.False(Named(viewer, "Move colors[1] down").IsEnabled);

        Named(viewer, "Move colors[1] up").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, changes);
        Assert.Equal(
            ExprValue.ColorList(new[] { ExprValue.Color(0, 0, 255, 255), ExprValue.Color(255, 0, 0, 255) }),
            viewer.Svg!.ExpressionValues!["colors"]);
        Assert.True(Pixel(viewer, 0, 12).Blue > 200);

        Named(viewer, "Add to colors").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, viewer.Svg.ExpressionValues!["colors"].AsColorList.Count);

        Named(viewer, "Remove colors[0]").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            ExprValue.ColorList(new[] { ExprValue.Color(255, 0, 0, 255), ExprValue.Color(255, 0, 0, 255) }),
            viewer.Svg.ExpressionValues!["colors"]);

        // Through the editors themselves, each found by the name it gives assistive technology.
        Editor<ColorPicker>(viewer, "colors[1]").Color = Color.FromRgb(0, 255, 0);
        Editor<NumericUpDown>(viewer, "positions[0]").Value = 0.5m;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Color.FromRgb(0, 255, 0), colours.Items[1].Value);

        Assert.Equal(5, changes);
        Assert.Equal(ExprValue.NumberList(new[] { 0.5f, 1f }), viewer.Svg.ExpressionValues!["positions"]);

        // Red up to the first stop, halfway across, then towards green.
        Assert.True(Pixel(viewer, 6, 12).Red > 200);
        Assert.True(Pixel(viewer, 23, 12).Green > 200);
        Assert.True(CommitLabel(viewer).IsVisible);

        viewer.ResetParameters();
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(viewer.Parameters, row => row.IsModified);
        Assert.Equal(ExprValue.NumberList(new[] { 0f, 1f }), viewer.Svg.ExpressionValues!["positions"]);
        Assert.True(Pixel(viewer, 0, 12).Red > 200);

        window.Close();
    }

    /// <summary>An empty list says so, and the button under it is how it starts again.</summary>
    [AvaloniaFact]
    public async Task An_Emptied_List_Says_So_And_Can_Be_Filled_Again()
    {
        var (window, viewer) = await HostLoaded(Listed);

        var positions = Assert.IsType<SvgViewerNumberListParameter>(viewer.Parameters[1]);
        var empty = viewer.GetVisualDescendants()
            .OfType<TextBlock>()
            .Single(text => ReferenceEquals(text.DataContext, positions) && text.Text == "empty");

        Assert.False(empty.IsVisible);

        Named(viewer, "Remove positions[1]").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Named(viewer, "Remove positions[0]").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.True(empty.IsVisible);

        // With no offsets the colours are spaced evenly, which is what an empty list gives.
        Assert.Equal(ExprValue.NumberList(Array.Empty<float>()), viewer.Svg!.ExpressionValues!["positions"]);

        Named(viewer, "Add to positions").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.False(empty.IsVisible);
        Assert.Equal(ExprValue.NumberList(new[] { 0f }), viewer.Svg.ExpressionValues!["positions"]);

        window.Close();
    }

    /// <summary>
    /// An element's buttons, hidden until its row is pointed at, show to the keyboard too: tabbing
    /// reaches them without pointing at anything, and they are the only way to move or remove one.
    /// </summary>
    [AvaloniaFact]
    public async Task A_Lists_Buttons_Show_Where_The_Keyboard_Is()
    {
        var (window, viewer) = await HostLoaded(Listed);

        var remove = Named(viewer, "Remove colors[0]");

        Assert.Equal(0d, remove.Opacity);

        Assert.True(remove.Focus(NavigationMethod.Tab));
        Dispatcher.UIThread.RunJobs();

        Assert.True(remove.Opacity > 0d);
        Assert.Equal(0d, Named(viewer, "Remove positions[0]").Opacity);

        window.Close();
    }

    // ---- committing values as defaults ----

    /// <summary>A list is committed as its literal, which reads back as the list it was.</summary>
    [AvaloniaFact]
    public async Task Committing_A_List_Writes_The_Literal_That_Declares_It()
    {
        var (window, viewer) = await HostLoaded(Listed);

        var colours = Assert.IsType<SvgViewerColorListParameter>(viewer.Parameters[0]);
        var positions = Assert.IsType<SvgViewerNumberListParameter>(viewer.Parameters[1]);

        colours.Items[1].MoveUp();
        positions.Add();
        positions.Items[1].Value = 0.00001d;
        Dispatcher.UIThread.RunJobs();

        var values = viewer.Svg!.ExpressionValues!;

        Assert.True(viewer.CommitParameterDefaults());
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("name=\"colors\" type=\"color[]\" default=\"[#0000ff, #ff0000]\"", viewer.Source);
        Assert.Contains("name=\"positions\" type=\"number[]\" default=\"[0, 0.00001, 1]\"", viewer.Source);

        // Read back as the default, it is the value that was showing, so nothing differs any more.
        Assert.DoesNotContain(viewer.Parameters, row => row.IsModified);
        Assert.Equal(values["colors"], viewer.Svg!.ExpressionValues!["colors"]);
        Assert.Equal(values["positions"], viewer.Svg.ExpressionValues!["positions"]);

        window.Close();
    }

    /// <summary>
    /// A list is committed whole whenever one element changed, so the others have to be written as
    /// they were declared and drawn, the floats that need eight or nine digits among them.
    /// </summary>
    [AvaloniaFact]
    public async Task Committing_A_List_Leaves_The_Elements_Nobody_Edited_As_They_Were()
    {
        var (window, viewer) = await HostLoaded(Listed.Replace("default=\"[0, 1]\"", "default=\"[1 / 3, 123456789]\""));

        var positions = Assert.IsType<SvgViewerNumberListParameter>(viewer.Parameters[1]);

        Assert.Equal(ExprValue.NumberList(new[] { 1f / 3f, 123456789f }), viewer.Svg!.ExpressionValues!["positions"]);

        positions.Add();
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewer.CommitParameterDefaults());
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("name=\"positions\" type=\"number[]\" default=\"[0.33333334, 123456790, 123456790]\"", viewer.Source);
        Assert.Equal(ExprValue.NumberList(new[] { 1f / 3f, 123456789f, 123456789f }), viewer.Svg!.ExpressionValues!["positions"]);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Committing_Writes_Every_Changed_Value_As_The_Declared_Default()
    {
        var (window, viewer) = await HostLoaded(Parametric);

        Dispatcher.UIThread.RunJobs();

        viewer.Parameters.OfType<SvgViewerNumberParameter>().Single().Value = 0.5d;
        viewer.Parameters.OfType<SvgViewerColorParameter>().Single().Color = Color.FromRgb(0, 0, 255);
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewer.CommitParameterDefaults());
        Dispatcher.UIThread.RunJobs();

        var text = viewer.Source;

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

        Dispatcher.UIThread.RunJobs();

        viewer.Parameters.OfType<SvgViewerNumberParameter>().Single().Value = 0.37d;
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewer.CommitParameterDefaults());
        Dispatcher.UIThread.RunJobs();

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
        Dispatcher.UIThread.RunJobs();

        Assert.False(CommitLabel(viewer).IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Committing_A_Whole_Panel_Is_A_Single_Thing_To_Take_Back()
    {
        var (window, viewer) = await HostLoaded(Parametric);

        Dispatcher.UIThread.RunJobs();

        var before = viewer.Source;

        viewer.Parameters.OfType<SvgViewerNumberParameter>().Single().Value = 0.5d;
        viewer.Parameters.OfType<SvgViewerColorParameter>().Single().Color = Color.FromRgb(0, 0, 255);
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewer.CommitParameterDefaults());
        Dispatcher.UIThread.RunJobs();

        viewer.Undo();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before, viewer.Source);

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
    public async Task Text_That_Will_Not_Read_Back_Never_Becomes_The_Drawing()
    {
        // What this used to assert -- that an edit is refused while the document will not parse --
        // has no state to be in any more: the drawing cannot be put into one. So it asserts that.
        var (window, viewer) = await HostLoaded(Parametric, Radius());

        Dispatcher.UIThread.RunJobs();

        Assert.False(viewer.SetSource(Parametric.Replace("</svg>", string.Empty)));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Parametric, viewer.Source);
        Assert.False(viewer.IsSourceModified);

        // And an edit still works, because nothing was broken by the attempt.
        Assert.True(await viewer.AddParameterAsync());

        window.Close();
    }

    /// <summary>Answers with whatever the test decided, because a modal cannot be driven headlessly.</summary>
    // ---- reordering ----

    private const string Required = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs>
            <e:code>
              <e:param name="size" type="number" />
              <e:param name="fade" type="number" default="1" />
            </e:code>
          </defs>
          <rect x="0" y="0" width="{{ size }}" height="24" opacity="{{ fade }}" />
        </svg>
        """;

    [AvaloniaFact]
    public async Task A_Parameter_Moved_From_The_Panel_Reaches_The_Drawing()
    {
        var (window, viewer) = await HostLoaded(Parametric);

        Assert.Equal(new[] { "tint", "fade" }, viewer.Parameters.Select(row => row.Name).ToArray());

        Assert.True(viewer.MoveParameter(viewer.Parameters[1], 0));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "fade", "tint" }, viewer.Parameters.Select(row => row.Name).ToArray());

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Parameter_With_No_Default_Can_Be_Moved_After_One_That_Has_One()
    {
        var (window, viewer) = await HostLoaded(Required);

        // Every order renders the same, since a default may not name another parameter. What the
        // C# generator wants of them is the generator's to say, when somebody runs it.
        Assert.True(viewer.MoveParameter(viewer.Parameters.Single(row => row.Name == "size"), 1));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "fade", "size" }, viewer.Parameters.Select(row => row.Name).ToArray());

        window.Close();
    }

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

        Dispatcher.UIThread.RunJobs();

        // `fade` is on the rect's opacity, `tint` on its fill, so neither can go. One that nothing
        // names has to be declared first.
        Assert.True(await viewer.AddParameterAsync());
        Dispatcher.UIThread.RunJobs();

        var spare = viewer.Parameters.Single(row => row.Name == "radius");

        Assert.True(viewer.RemoveParameter(spare));
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(viewer.Parameters, row => row.Name == "radius");
        Assert.DoesNotContain("radius", viewer.Source);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Parameter_The_Drawing_Still_Uses_Is_Refused_And_Said_So()
    {
        var (window, viewer) = await HostLoaded(Parametric);

        Dispatcher.UIThread.RunJobs();

        var used = viewer.Parameters.Single(row => row.Name == "tint");

        Assert.False(viewer.RemoveParameter(used));
        Dispatcher.UIThread.RunJobs();

        // Still there, and the pane still holds the placeholder that kept it.
        Assert.Contains(viewer.Parameters, row => row.Name == "tint");
        Assert.Contains("{{ tint }}", viewer.Source);
        Assert.False(viewer.IsSourceModified);

        window.Close();
    }

    [AvaloniaFact]
    public async Task The_Row_Button_Is_What_Asks_For_It()
    {
        var (window, viewer) = await HostLoaded(Parametric, Radius());

        Assert.True(await viewer.AddParameterAsync());
        Dispatcher.UIThread.RunJobs();

        var spare = viewer.Parameters.Single(row => row.Name == "radius");

        viewer.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => ReferenceEquals(button.DataContext, spare) && button.Content as string == "✕")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(viewer.Parameters, row => row.Name == "radius");

        window.Close();
    }
    /// <summary>
    /// A row has two drags, and the one that carries the name does not reorder the list.
    /// </summary>
    /// <remarks>
    /// The grip moves a row up and down, because where a let sits decides what it can name; the name
    /// itself is carried onto an attribute. Two grab points on one row, so a press on one must not
    /// do the other's work — which is the thing a shared handle would have risked.
    /// </remarks>
    [AvaloniaFact]
    public async Task Dragging_A_Name_Does_Not_Reorder_The_List()
    {
        var (window, viewer) = await HostLoaded(Parametric);

        var asked = 0;

        var panel = viewer.GetVisualDescendants().OfType<SvgViewerDeclarationPanel>().Single();

        panel.ParameterMoveRequested = (_, _) =>
        {
            asked++;

            return true;
        };

        var names = viewer.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("name"))
            .ToList();

        Assert.Equal(2, names.Count);

        var from = names[0].TranslatePoint(new Point(4d, 4d), window);

        Assert.NotNull(from);

        window.MouseDown(from!.Value, MouseButton.Left);
        window.MouseMove(new Point(from.Value.X, from.Value.Y + 60d), RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(new Point(from.Value.X, from.Value.Y + 60d), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, asked);
        Assert.Equal(new[] { "tint", "fade" }, viewer.Parameters!.Select(row => row.Name));

        window.Close();
    }
}
