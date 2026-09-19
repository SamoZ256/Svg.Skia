using System;
using System.Linq;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// The order the project pane reads names in.
/// </summary>
/// <remarks>
/// Asserted on the comparer rather than through a window: what is being settled here is an
/// ordering, and a tree with a row per case would say the same thing far more slowly.
/// </remarks>
public class NaturalOrderTests
{
    private static string[] Sorted(params string[] names)
        => names.OrderBy(name => name, NaturalOrder.Instance).ToArray();

    [Fact]
    public void A_Number_Is_Read_As_A_Number()
    {
        Assert.Equal(
            new[] { "icon1", "icon2", "icon9", "icon10", "icon100" },
            Sorted("icon10", "icon100", "icon2", "icon9", "icon1"));
    }

    /// <summary>Digits are compared as digits, so a name may carry more than any integer holds.</summary>
    [Fact]
    public void A_Number_Too_Long_To_Parse_Still_Sorts()
    {
        var huge = new string('9', 40);
        var larger = "1" + new string('0', 40);

        Assert.Equal(new[] { "n" + huge, "n" + larger }, Sorted("n" + larger, "n" + huge));
    }

    [Fact]
    public void Numbers_Are_Compared_Where_They_Sit()
    {
        Assert.Equal(
            new[] { "a2b1", "a2b10", "a10b1" },
            Sorted("a10b1", "a2b10", "a2b1"));
    }

    /// <summary>
    /// Leading zeros are not part of the number, and do not leave the order unsettled.
    /// </summary>
    /// <remarks>
    /// Two names spelling one number have to land in some order and the same one every time, or
    /// rows swap about between rebuilds for no reason a reader can see.
    /// </remarks>
    [Fact]
    public void Leading_Zeros_Do_Not_Make_A_Larger_Number()
    {
        Assert.Equal(new[] { "icon02", "icon2", "icon10" }, Sorted("icon10", "icon2", "icon02"));

        Assert.True(NaturalOrder.Instance.Compare("icon02", "icon2") < 0);
        Assert.True(NaturalOrder.Instance.Compare("icon2", "icon02") > 0);

        // A run of nothing but zeros is still the number zero, below every other.
        Assert.Equal(new[] { "icon00", "icon1" }, Sorted("icon1", "icon00"));
    }

    [Fact]
    public void Case_Is_Not_A_Section_Of_Its_Own()
    {
        Assert.Equal(new[] { "Ant", "apple", "Bee" }, Sorted("apple", "Bee", "Ant"));
    }

    /// <summary>A name that is only a number, and one that has none at all.</summary>
    [Fact]
    public void A_Name_Need_Not_Have_Both_Kinds()
    {
        Assert.Equal(new[] { "2", "10", "apple" }, Sorted("apple", "10", "2"));
        Assert.Equal(new[] { "icon", "icon1" }, Sorted("icon1", "icon"));
    }

    [Fact]
    public void The_Same_Name_Is_The_Same_Place()
    {
        Assert.Equal(0, NaturalOrder.Instance.Compare("icon2", "icon2"));
        Assert.Equal(0, NaturalOrder.Instance.Compare(null, null));
        Assert.True(NaturalOrder.Instance.Compare(null, "a") < 0);
        Assert.True(NaturalOrder.Instance.Compare("a", null) > 0);
    }

    /// <summary>
    /// The order is total, so sorting is stable whatever order it started in.
    /// </summary>
    /// <remarks>
    /// A comparer that answers zero for two names that are not equal lets the sort put them either
    /// way round, which reads as rows moving on their own.
    /// </remarks>
    [Fact]
    public void Every_Pair_Of_Unequal_Names_Has_An_Order()
    {
        var names = new[] { "icon2", "icon02", "Icon2", "icon10", "icon", "2", "a2b1", "ICON2" };

        foreach (var one in names)
        {
            foreach (var other in names)
            {
                var compared = NaturalOrder.Instance.Compare(one, other);

                if (string.Equals(one, other, StringComparison.Ordinal))
                {
                    Assert.Equal(0, compared);
                }
                else
                {
                    Assert.NotEqual(0, compared);

                    // And the answer is the same read from either side.
                    Assert.Equal(Math.Sign(compared), -Math.Sign(NaturalOrder.Instance.Compare(other, one)));
                }
            }
        }
    }
}
