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

    private static TextBlock Note(SvgResizeWindow window)
        => window.GetVisualDescendants().OfType<TextBlock>().First(text => text.Name == "NoteText");

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
    public void The_Window_Does_Not_Move_While_Somebody_Types()
    {
        // A TextBox measured against unbounded width answers with the width of its own text, so a
        // dialog that sizes to its content grows under the caret; a note that appears and goes takes
        // the buttons up and down with it. Both were real, and neither is visible to a test that
        // only reads values back.
        var (window, _) = Host();

        var before = window.Bounds.Size;

        Box(window, "WidthBox").Text = "1024";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before, window.Bounds.Size);

        // The longest refusal there is, arriving mid-word as a percentage is typed.
        Box(window, "TopBox").Text = "10";
        Dispatcher.UIThread.RunJobs();

        Assert.NotEmpty(Note(window).Text!);
        Assert.Equal(before, window.Bounds.Size);

        Box(window, "TopBox").Text = "10%";
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(Note(window).Text!);
        Assert.Equal(before, window.Bounds.Size);
    }

    [AvaloniaFact]
    public void The_Four_Padding_Boxes_Are_One_Padding()
    {
        var (window, resize) = Host();

        Box(window, "TopBox").Text = "10%";
        Box(window, "RightBox").Text = "0.05";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0.1f, resize.Padding.Top);
        Assert.Equal(0.05f, resize.Padding.Right);

        // An empty box is no padding on that side rather than a refusal.
        Assert.Equal(0f, resize.Padding.Bottom);
    }

    [AvaloniaFact]
    public void A_Padding_That_Leaves_No_Room_Says_So_In_The_Models_Own_Words()
    {
        var (window, _) = Host();

        Box(window, "LeftBox").Text = "60%";
        Box(window, "RightBox").Text = "60%";
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("no room", Note(window).Text);
    }

    [AvaloniaFact]
    public void A_Bare_Number_Is_A_Fraction_And_Ten_Of_Them_Is_Refused()
    {
        var (window, _) = Host();

        Box(window, "TopBox").Text = "10";
        Dispatcher.UIThread.RunJobs();

        Assert.NotEmpty(Note(window).Text!);
    }

    [AvaloniaFact]
    public void A_Half_Typed_Box_Is_Not_A_Refusal()
    {
        var (window, resize) = Host();

        Box(window, "WidthBox").Text = string.Empty;
        Dispatcher.UIThread.RunJobs();

        // Nothing has been said yet, so nothing is complained about and nothing has changed.
        Assert.Equal(200f, resize.Width);
        Assert.Empty(Note(window).Text!);
    }
}
