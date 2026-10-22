#if PAINTCODE_ORACLE
using Xunit;

namespace Svg.PaintCode.UnitTests.Oracle;

/// <summary>The <see cref="OracleFactAttribute"/> gate, for a theory.</summary>
public sealed class OracleTheoryAttribute : TheoryAttribute
{
    public OracleTheoryAttribute()
    {
        if (OracleFactAttribute.Path is null)
        {
            Skip = "Set SVG_PAINTCODE_PROJECT to the .svgstudio to compare against PaintCode to run this.";
        }
    }
}
#endif
