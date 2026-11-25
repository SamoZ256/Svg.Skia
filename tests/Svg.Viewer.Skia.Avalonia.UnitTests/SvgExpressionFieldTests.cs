using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>The box the Attributes panel's rows, and a host's own bar, edit a value in.</summary>
public class SvgExpressionFieldTests
{
    private static bool Press(SvgExpressionField field, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        var e = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };

        field.RaiseEvent(e);

        return e.Handled;
    }

    [AvaloniaFact]
    public void Up_And_Down_Step_A_Number_Keeping_Its_Unit()
    {
        var field = new SvgExpressionField { Text = "12px", Step = 1 };

        Press(field, Key.Up);
        Assert.Equal("13px", field.Text);

        Press(field, Key.Down, KeyModifiers.Shift);
        Assert.Equal("3px", field.Text);
    }

    /// <summary>A percentage steps a hundred times as far, and stays inside the same range.</summary>
    [AvaloniaFact]
    public void A_Fraction_Steps_By_A_Tenth_Within_Nought_And_One()
    {
        var field = new SvgExpressionField { Text = "0.95", Step = 0.1, Minimum = 0, Maximum = 1, PercentIsFraction = true };

        Press(field, Key.Up);
        Assert.Equal("1", field.Text);

        field.Text = "45%";
        Press(field, Key.Down);
        Assert.Equal("35%", field.Text);

        field.Text = "5%";
        Press(field, Key.Down);
        Assert.Equal("0%", field.Text);
    }

    [AvaloniaFact]
    public void Escape_Restores_The_Value_And_Leaves_Nothing_Pending()
    {
        var field = new SvgExpressionField { Text = "red", Value = "red" };

        field.Text = "blue";
        Assert.True(field.IsPending);

        Press(field, Key.Escape);

        Assert.Equal("red", field.Text);
        Assert.False(field.IsPending);
    }

    /// <summary>A length written as a percentage steps by its own step: 50% is not a fraction of anything here.</summary>
    [AvaloniaFact]
    public void A_Percentage_That_Is_Not_A_Fraction_Steps_By_Its_Own_Step()
    {
        var field = new SvgExpressionField { Text = "50%", Step = 1 };

        Press(field, Key.Up);

        Assert.Equal("51%", field.Text);
    }

    /// <summary>Where it does not step, the arrows are the box's own, to move the caret.</summary>
    [AvaloniaFact]
    public void Arrows_Are_Left_To_The_Box_Where_The_Field_Does_Not_Step()
    {
        var field = new SvgExpressionField { Text = "12" };

        Assert.False(Press(field, Key.Up));
        Assert.Equal("12", field.Text);
    }

    [AvaloniaFact]
    public void A_Read_Only_Field_Neither_Steps_Nor_Puts_Back()
    {
        var field = new SvgExpressionField { Text = "12", Value = "10", Step = 1, IsReadOnly = true };

        Press(field, Key.Up);
        Assert.Equal("12", field.Text);

        Press(field, Key.Escape);
        Assert.Equal("12", field.Text);
    }
}
