using ShimSkiaSharp;
using Xunit;

namespace ShimSkiaSharp.UnitTests;

public class SymTests
{
    [Fact]
    public void Implicit_Conversion_Produces_A_Literal()
    {
        Sym<float> value = 3f;

        Assert.Equal(3f, value.Value);
        Assert.Null(value.Expression);
        Assert.False(value.IsSymbolic);
    }

    [Fact]
    public void WithExpression_Keeps_The_Concrete_Value()
    {
        var value = new Sym<float>(3f).WithExpression(SymNode.Source("t"));

        Assert.Equal(3f, value.Value);
        Assert.True(value.IsSymbolic);
        Assert.Equal(SymNode.Source("t"), value.Expression);
    }

    [Fact]
    public void Same_Value_And_Expression_Are_Equal()
    {
        var left = new Sym<float>(3f, SymNode.Source("t"));
        var right = new Sym<float>(3f, SymNode.Source("t"));

        Assert.True(left == right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void Same_Value_With_Different_Expressions_Is_Not_Equal()
    {
        // Caches key on this, so two shapes sharing a fallback must not collapse into one.
        var left = new Sym<float>(3f, SymNode.Source("t"));
        var right = new Sym<float>(3f, SymNode.Source("u"));

        Assert.True(left != right);
    }

    [Fact]
    public void Literal_Is_Not_Equal_To_Symbolic_With_Same_Value()
    {
        Sym<float> literal = 3f;
        var symbolic = new Sym<float>(3f, SymNode.Source("t"));

        Assert.NotEqual(literal, symbolic);
    }

    [Fact]
    public void Different_Values_Are_Not_Equal()
    {
        Assert.NotEqual(new Sym<float>(3f), new Sym<float>(4f));
    }
}
