// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>
/// The resize form as a person meets it: three boxes over one size.
/// </summary>
/// <remarks>
/// The arithmetic is <see cref="SvgViewerResize"/>'s and tested there. What is only true on screen
/// is the wiring — that typing in one box moves the others, that a box being typed in is not
/// rewritten under the caret, and that the scale goes dead when a scale means nothing.
/// </remarks>
public class SvgResizeWindowTests
{
    private static (SvgResizeWindow Window, SvgViewerResize Resize) Host()
    {
        var resize = new SvgViewerResize(200f, 100f);
        var window = new SvgResizeWindow(resize);

        window.Show();
        Dispatcher.UIThread.RunJobs();

        return (window, resize);
    }

    private static TextBox Box(SvgResizeWindow window, string name)
        => window.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == name);

    private static CheckBox Lock(SvgResizeWindow window)
        => window.GetVisualDescendants().OfType<CheckBox>().First(box => box.Name == "LockBox");

    [AvaloniaFact]
    public void The_Form_Opens_On_The_Size_The_Drawing_Has()
    {
        var (window, _) = Host();

        Assert.Equal("200", Box(window, "WidthBox").Text);
        Assert.Equal("100", Box(window, "HeightBox").Text);
        Assert.Equal("1", Box(window, "ScaleBox").Text);
    }

    [AvaloniaFact]
    public void Typing_A_Width_Moves_The_Height_And_The_Scale()
    {
        var (window, _) = Host();

        Box(window, "WidthBox").Text = "400";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("200", Box(window, "HeightBox").Text);
        Assert.Equal("2", Box(window, "ScaleBox").Text);

        // The box being typed in is left as it was typed: rewriting it would move the caret.
        Assert.Equal("400", Box(window, "WidthBox").Text);
    }

    [AvaloniaFact]
    public void Typing_A_Scale_Moves_Both_Dimensions()
    {
        var (window, _) = Host();

        Box(window, "ScaleBox").Text = "0.5";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("100", Box(window, "WidthBox").Text);
        Assert.Equal("50", Box(window, "HeightBox").Text);
    }

    [AvaloniaFact]
    public void Unlocking_The_Ratio_Puts_The_Scale_Out_Of_Use()
    {
        var (window, _) = Host();

        Lock(window).IsChecked = false;
        Dispatcher.UIThread.RunJobs();

        var scale = Box(window, "ScaleBox");

        Assert.False(scale.IsEnabled);
        Assert.Equal(string.Empty, scale.Text);

        // And the two dimensions stop following each other.
        Box(window, "WidthBox").Text = "400";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("100", Box(window, "HeightBox").Text);
    }

    [AvaloniaFact]
    public void A_Half_Typed_Box_Is_Not_A_Refusal()
    {
        var (window, resize) = Host();

        Box(window, "WidthBox").Text = string.Empty;
        Dispatcher.UIThread.RunJobs();

        // Nothing has been said yet, so nothing is complained about and nothing has changed.
        Assert.Equal(200f, resize.Width);
        Assert.False(window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "NoteText").IsVisible);
    }
}
