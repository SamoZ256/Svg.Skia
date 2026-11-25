using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>A paint, shown as the Attributes panel and a host's own bar show one.</summary>
public class SvgPaintChipTests
{
    [AvaloniaTheory]
    [InlineData(null, SvgPaintChipState.Unset)]
    [InlineData("#ff0000", SvgPaintChipState.Solid)]
    [InlineData("none", SvgPaintChipState.None)]
    [InlineData("url(#fade)", SvgPaintChipState.Reference)]
    [InlineData("{{ tint }}", SvgPaintChipState.Driven)]
    [InlineData("currentColor", SvgPaintChipState.Unset)]
    public void The_State_Is_Read_From_The_Paint_As_Written(string? paint, SvgPaintChipState state)
        => Assert.Equal(state, new SvgPaintChip { Paint = paint }.State);

    [AvaloniaFact]
    public void Mixed_Wins_Over_What_The_First_Paint_Is()
        => Assert.Equal(SvgPaintChipState.Mixed, new SvgPaintChip { Paint = "#ff0000", IsMixed = true }.State);

    /// <summary>A paint that is itself an expression changes only by typing over it; one a group drives can be set here.</summary>
    [AvaloniaFact]
    public void A_Bound_Chip_Is_Locked_And_An_Inherited_One_Is_Not()
    {
        Assert.False(new SvgPaintChip { Paint = "{{ tint }}" }.IsEnabled);

        var inherited = new SvgPaintChip { IsDriven = true };

        Assert.Equal(SvgPaintChipState.Driven, inherited.State);
        Assert.True(inherited.IsEnabled);
    }

    [AvaloniaFact]
    public void A_Driven_Chip_Shows_What_The_Expression_Comes_To()
    {
        var chip = new SvgPaintChip { Paint = "{{ tint }}", DrivenColor = Colors.Red };
        var shown = Assert.IsType<global::Avalonia.Controls.Border>(chip.Content);

        Assert.Equal(Colors.Red, Assert.IsAssignableFrom<ISolidColorBrush>(shown.Background).Color);
    }
}
