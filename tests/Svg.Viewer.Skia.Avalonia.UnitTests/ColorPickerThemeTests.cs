using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>
/// A colour parameter is only configurable if its picker actually renders. The ColorPicker theme
/// lives in its own assembly, so a host that includes only FluentTheme gets a control with no
/// template — present in the tree, and invisible on screen.
/// </summary>
public class ColorPickerThemeTests
{
    private const string ColorDocument = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs><e:code><e:param name="tint" type="color" default="#ff0000" /></e:code></defs>
          <rect x="0" y="0" width="24" height="24" fill="{{ tint }}" />
        </svg>
        """;

    [AvaloniaFact]
    public async Task A_Colour_Parameter_Renders_A_Usable_Picker()
    {
        var viewer = new SvgViewer();
        var window = new Window { Width = 700, Height = 460, Background = Brushes.White, Content = viewer };
        window.Show();

        Assert.True(await viewer.LoadTextAsync(ColorDocument));
        Dispatcher.UIThread.RunJobs();
        viewer.Measure(new global::Avalonia.Size(700, 460));
        viewer.Arrange(new global::Avalonia.Rect(0, 0, 700, 460));
        Dispatcher.UIThread.RunJobs();

        var picker = viewer.GetVisualDescendants().OfType<ColorPicker>().FirstOrDefault();
        Assert.True(picker is not null, "No ColorPicker was created for the colour parameter.");

        // A templated control whose theme is missing resolves no template at all, so it occupies the
        // tree while drawing nothing. That is exactly what "there is no colour picker" looks like.
        Assert.True(
            picker!.GetVisualChildren().Any(),
            "The ColorPicker has no visual children, so its control theme was never applied. "
            + "Avalonia.Controls.ColorPicker ships its theme separately from FluentTheme.");

        window.Close();
    }
}
