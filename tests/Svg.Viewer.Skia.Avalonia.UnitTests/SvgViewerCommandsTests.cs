using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>Flip and Rotate: worked out on the drawing as it is drawn, and written as a term after each element's transform.</summary>
public class SvgViewerCommandsTests
{
    private const string Markup = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 40 40" width="40" height="40">
          <defs>
            <e:code><e:param name="a" type="number" default="30" /></e:code>
            <rect id="kept" width="1" height="1" />
          </defs>
          <rect id="r" x="2" y="4" width="10" height="6" fill="#3366cc" />
          <g id="g" transform="translate(20 0)"><path id="p" d="M 0 0 L 10 0 L 0 8 Z" /></g>
          <rect id="d" x="20" y="20" width="10" height="10" transform="rotate({{ a }} 25 25)" />
          <rect id="s" x="30" y="4" width="6" height="6" />
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

    private static string[] Picked(SvgViewer viewer, params string[] keys)
    {
        Assert.True(viewer.Elements.TrySelect(keys));
        Dispatcher.UIThread.RunJobs();

        return viewer.Elements.SelectedAddresses.ToArray();
    }

    [AvaloniaFact]
    public async Task Flip_Horizontal_Mirrors_An_Element_Across_Its_Middle()
    {
        var (_, viewer) = await Host();
        var was = viewer.Source;

        Assert.True(viewer.Flip(Picked(viewer, "1"), horizontal: true));

        Assert.Equal(
            was.Replace("""fill="#3366cc" />""", """fill="#3366cc" transform="translate(14) scale(-1 1)" />""", StringComparison.Ordinal),
            viewer.Source);
        Assert.Equal("flip horizontally", viewer.UndoLabel);
        Assert.Equal(new[] { "1" }, viewer.Elements.SelectedAddresses);

        viewer.Undo();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(was, viewer.Source);
    }

    [AvaloniaFact]
    public async Task Flip_Vertical_Mirrors_Top_To_Bottom()
    {
        var (_, viewer) = await Host();

        Assert.True(viewer.Flip(Picked(viewer, "1"), horizontal: false));

        Assert.Contains("""fill="#3366cc" transform="translate(0 14) scale(1 -1)" />""", viewer.Source);
        Assert.Equal("flip vertically", viewer.UndoLabel);
    }

    [AvaloniaFact]
    public async Task A_Quarter_Turn_Is_A_Rotate_About_The_Middle()
    {
        var (_, viewer) = await Host();

        Assert.True(viewer.Rotate(Picked(viewer, "1"), 90f));

        Assert.Contains("""fill="#3366cc" transform="rotate(90 7 7)" />""", viewer.Source);
        Assert.Equal("rotate right", viewer.UndoLabel);

        Assert.True(viewer.Rotate(Picked(viewer, "1"), -90f));

        Assert.Contains("""transform="rotate(90 7 7) rotate(-90 7 7)" />""", viewer.Source);
        Assert.Equal("rotate left", viewer.UndoLabel);
    }

    /// <summary>The turn drawn is the turn meant: the rect turned reads the same, pixel for pixel, as one written turned.</summary>
    [AvaloniaFact]
    public async Task A_Turned_Element_Draws_Where_It_Was_Turned_To()
    {
        var (_, viewer) = await Host();

        Assert.True(viewer.Rotate(Picked(viewer, "1"), 90f));

        var turned = Pixels(viewer.Source);
        var written = Pixels(Markup.Replace("""<rect id="r" x="2" y="4" width="10" height="6" """, """<rect id="r" x="4" y="2" width="6" height="10" """, StringComparison.Ordinal));

        Assert.Equal(written, turned);
    }

    /// <summary>A quarter turn in the drawing's own units, which a stretched viewBox would make a shear on the page.</summary>
    [AvaloniaFact]
    public async Task A_Quarter_Turn_Is_One_In_User_Units_However_The_Page_Is_Stretched()
    {
        var (_, viewer) = await Host(Markup.Replace("""viewBox="0 0 40 40" width="40" height="40">""", """viewBox="0 0 40 20" width="40" height="40" preserveAspectRatio="none">""", StringComparison.Ordinal));

        Assert.True(viewer.Rotate(Picked(viewer, "1"), 90f));

        Assert.Contains("""fill="#3366cc" transform="rotate(90 7 7)" />""", viewer.Source);
    }

    /// <summary>Under a turned group, a mirror across the page is one across a slant in the element's space, written as a matrix.</summary>
    [AvaloniaFact]
    public async Task A_Mirror_Under_A_Turned_Group_Is_A_Matrix()
    {
        var (_, viewer) = await Host(Markup.Replace("""<g id="g" transform="translate(20 0)">""", """<g id="g" transform="rotate(45 25 4)">""", StringComparison.Ordinal));

        Assert.True(viewer.Flip(Picked(viewer, "2/0"), horizontal: true));

        Assert.Contains("""<path id="p" d="M 0 0 L 10 0 L 0 8 Z" transform="matrix(0 1 1 0 """, viewer.Source);
    }

    [AvaloniaFact]
    public async Task A_Shape_In_A_Clip_Path_Turns_With_What_Uses_It()
    {
        var (_, viewer) = await Host(Markup.Replace("""<rect id="kept" width="1" height="1" />""", """<clipPath id="c"><rect id="cr" width="9" height="9" /></clipPath>""", StringComparison.Ordinal)
            .Replace("""<rect id="s" x="30" y="4" width="6" height="6" />""", """<rect id="s" x="30" y="4" width="6" height="6" clip-path="url(#c)" />""", StringComparison.Ordinal));

        Assert.False(viewer.Flip(Picked(viewer, "0/1/0"), horizontal: true));

        Assert.StartsWith("The <rect> at 0/1/0 ", viewer.Note, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task An_Element_In_A_Moved_Group_Is_Turned_In_Its_Own_Space()
    {
        var (_, viewer) = await Host();

        Assert.True(viewer.Flip(Picked(viewer, "2/0"), horizontal: true));

        Assert.Contains("""<path id="p" d="M 0 0 L 10 0 L 0 8 Z" transform="translate(10) scale(-1 1)" />""", viewer.Source);
        Assert.Contains("""<g id="g" transform="translate(20 0)">""", viewer.Source);
    }

    [AvaloniaFact]
    public async Task Elements_Picked_Together_Turn_About_The_Middle_Of_Them_All()
    {
        var (_, viewer) = await Host();

        Assert.True(viewer.Flip(Picked(viewer, "1", "4"), horizontal: true));

        Assert.Contains("""fill="#3366cc" transform="translate(38) scale(-1 1)" />""", viewer.Source);
        Assert.Contains("""<rect id="s" x="30" y="4" width="6" height="6" transform="translate(38) scale(-1 1)" />""", viewer.Source);
        Assert.Equal("flip 2 elements horizontally", viewer.UndoLabel);
    }

    [AvaloniaFact]
    public async Task What_Is_Inside_A_Picked_Group_Turns_With_It()
    {
        var (_, viewer) = await Host();

        Assert.True(viewer.Flip(Picked(viewer, "2", "2/0"), horizontal: true));

        Assert.Contains("""<g id="g" transform="translate(20 0) translate(10) scale(-1 1)">""", viewer.Source);
        Assert.Contains("""<path id="p" d="M 0 0 L 10 0 L 0 8 Z" />""", viewer.Source);
    }

    /// <summary>The expression goes on applying first; the turn is written after it, about the point it leaves in place.</summary>
    [AvaloniaFact]
    public async Task A_Turn_Is_Written_After_A_Driven_Transform()
    {
        var (_, viewer) = await Host();

        Assert.True(viewer.Rotate(Picked(viewer, "3"), 90f));

        Assert.Contains("""transform="rotate({{ a }} 25 25) rotate(90 25 25)" />""", viewer.Source);
    }

    [AvaloniaFact]
    public async Task What_Draws_Nothing_Has_No_Middle_To_Turn_About()
    {
        var (_, viewer) = await Host();
        var was = viewer.Source;

        Assert.False(viewer.Flip(Picked(viewer, "0/1"), horizontal: true));

        Assert.Equal("The <rect> at 0/1 draws nothing, so it has no middle to turn about.", viewer.Note);
        Assert.Equal(was, viewer.Source);
    }

    [AvaloniaFact]
    public async Task What_Is_Not_Displayed_Has_No_Middle_To_Turn_About()
    {
        var (_, viewer) = await Host(Markup.Replace("""<rect id="s" x="30" y="4" width="6" height="6" />""", """<rect id="s" x="30" y="4" width="6" height="6" display="none" />""", StringComparison.Ordinal));

        Assert.False(viewer.Flip(Picked(viewer, "4"), horizontal: true));

        Assert.Equal("The <rect> at 4 draws nothing, so it has no middle to turn about.", viewer.Note);
    }

    [AvaloniaFact]
    public async Task The_Drawing_Itself_Is_Not_Turned()
    {
        var (_, viewer) = await Host();

        Assert.False(viewer.Rotate(Picked(viewer, string.Empty), 90f));

        Assert.Equal("The drawing itself has nothing around it to turn in.", viewer.Note);
    }

    [AvaloniaFact]
    public async Task Shift_H_And_Shift_V_Flip_The_Picked_Rows()
    {
        var (window, viewer) = await Host();

        Picked(viewer, "1");
        viewer.Canvas.Focus();
        Dispatcher.UIThread.RunJobs();

        window.KeyPressQwerty(PhysicalKey.H, RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("""transform="translate(14) scale(-1 1)" />""", viewer.Source);

        // The tree answers it too, as it does Shift+X.
        viewer.Elements.FindControl<TreeView>("Tree")!.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.V, KeyModifiers = KeyModifiers.Shift });
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("""transform="translate(14) scale(-1 1) translate(0 14) scale(1 -1)" />""", viewer.Source);
    }

    [AvaloniaFact]
    public async Task The_Rows_Menu_Offers_The_Turns()
    {
        var (_, viewer) = await Host();

        Picked(viewer, "1");

        var transform = Offered(viewer).Single(item => item.Header as string == "Transform");
        var items = transform.Items.OfType<MenuItem>().ToDictionary(item => (string)item.Header!);

        Assert.Equal(new[] { "Flip Horizontal", "Flip Vertical", "Rotate 90° Right", "Rotate 90° Left" }, items.Keys);
        Assert.Equal(new KeyGesture(Key.H, KeyModifiers.Shift), items["Flip Horizontal"].InputGesture);

        items["Rotate 90° Right"].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("""transform="rotate(90 7 7)" />""", viewer.Source);
    }

    /// <summary>Opens the rows' menu as a right click does, and reads what it offers.</summary>
    private static MenuItem[] Offered(SvgViewer viewer)
    {
        var tree = viewer.Elements.FindControl<TreeView>("Tree")!;

        tree.RaiseEvent(new ContextRequestedEventArgs());
        Dispatcher.UIThread.RunJobs();

        var offered = tree.ContextMenu!.Items.OfType<MenuItem>().Where(item => item.IsVisible).ToArray();

        tree.ContextMenu.Close();
        Dispatcher.UIThread.RunJobs();

        return offered;
    }

    private static uint[] Pixels(string markup)
    {
        var svg = new Svg.Skia.SKSvg();

        Assert.NotNull(svg.FromSvg(markup));

        using var bitmap = new SkiaSharp.SKBitmap(40, 40);

        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        {
            canvas.Clear(SkiaSharp.SKColors.Transparent);
            canvas.DrawPicture(svg.Picture);
        }

        return bitmap.Pixels.Select(pixel => (uint)pixel).ToArray();
    }
}
