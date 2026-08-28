// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Svg.Skia;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>
/// Resizing a drawing from the viewer: the arithmetic is the sizing model's and the splice is
/// Svg.SourceEditing's, so what these ask is that the drawing itself changed — the text says the new
/// size, the tab is unsaved, and taking it back is an undo away.
/// </summary>
public class SvgViewerResizeCommandTests
{
    private const string Drawing = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <rect x="0" y="0" width="24" height="24" fill="#3366cc" />
        </svg>
        """;

    private const string WithoutViewBox = """
        <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
          <rect x="0" y="0" width="24" height="24" fill="#3366cc" />
        </svg>
        """;

    private static async Task<SvgViewer> Host(string markup)
    {
        var viewer = new SvgViewer();

        var window = new Window { Width = 400, Height = 300, Background = Brushes.White, Content = viewer };

        window.Show();

        Assert.True(await viewer.LoadTextAsync(markup));
        Dispatcher.UIThread.RunJobs();

        return viewer;
    }

    [AvaloniaFact]
    public async Task Resizing_Rewrites_The_Frame_The_Document_Declares()
    {
        var viewer = await Host(Drawing);

        Assert.True(viewer.Resize(new SvgSizeRequest(48f, null, null)));

        Assert.Contains("width=\"48\"", viewer.Source);
        Assert.Contains("height=\"48\"", viewer.Source);

        // The author's own viewBox is what the drawing is scaled against, so it stays as written.
        Assert.Contains("viewBox=\"0 0 24 24\"", viewer.Source);

        // It is the drawing that changed, so the pane is holding something the file does not.
        Assert.True(viewer.IsSourceModified);
    }

    [AvaloniaFact]
    public async Task A_Drawing_Without_A_ViewBox_Is_Given_One()
    {
        // Width and height alone are a viewport: a larger one would reframe the drawing rather than
        // resize it, so the size it had becomes the viewBox on the way past.
        var viewer = await Host(WithoutViewBox);

        Assert.True(viewer.Resize(new SvgSizeRequest(48f, null, null)));

        Assert.Contains("viewBox=\"0 0 24 24\"", viewer.Source);
        Assert.Contains("width=\"48\"", viewer.Source);
    }

    [AvaloniaFact]
    public async Task A_Scale_And_A_Width_Agree_On_The_Same_Drawing()
    {
        var byWidth = await Host(Drawing);
        var byScale = await Host(Drawing);

        Assert.True(byWidth.Resize(new SvgSizeRequest(48f, null, null)));
        Assert.True(byScale.Resize(new SvgSizeRequest(null, null, 2f)));

        Assert.Equal(byWidth.Source, byScale.Source);
    }

    [AvaloniaFact]
    public async Task Resizing_To_The_Size_It_Already_Is_Changes_Nothing()
    {
        var viewer = await Host(Drawing);

        Assert.False(viewer.Resize(SvgSizeRequest.None));
        Assert.False(viewer.IsSourceModified);
    }

    [AvaloniaFact]
    public async Task Padding_Reframes_The_Drawing_Inside_The_Size_It_Has()
    {
        var viewer = await Host(Drawing);

        Assert.True(viewer.Resize(new SvgSizeRequest(null, null, null, SvgPadding.Parse("10%"))));

        // The canvas is the size it was; the drawing shrinks inside it, which is a viewBox wider
        // than the drawing's own coordinates by the room left on each side.
        Assert.Contains("width=\"24\"", viewer.Source);
        Assert.Contains("viewBox=\"-3 -3 30 30\"", viewer.Source);
    }

    [AvaloniaFact]
    public async Task A_Padding_That_Leaves_No_Room_Is_Refused_Rather_Than_Written()
    {
        var viewer = await Host(Drawing);

        // The model's own rule, and its own words: four sides that add up to the whole canvas.
        var refusal = Assert.Throws<ArgumentException>(() => SvgPadding.Parse("60%"));

        Assert.Contains("no room", refusal.Message);
        Assert.False(viewer.IsSourceModified);
    }

    [AvaloniaFact]
    public async Task An_Unlocked_Resize_Writes_The_Box_It_Was_Given()
    {
        var viewer = await Host(Drawing);

        Assert.True(viewer.Resize(new SvgSizeRequest(48f, 24f, null)));

        Assert.Contains("width=\"48\"", viewer.Source);
        Assert.Contains("height=\"24\"", viewer.Source);
    }
}
