#if PAINTCODE_ORACLE
using System;
using System.IO;
using Xunit;

namespace Svg.PaintCode.UnitTests.Oracle;

/// <summary>
/// A fact that runs only when <c>SVG_PAINTCODE_PROJECT</c> names the Studio project to compare
/// against PaintCode, and reports itself skipped otherwise.
/// </summary>
/// <remarks>
/// A project rather than the PaintCode document, because the project is what gets edited: a drawing
/// fixed there and not in the document would otherwise stay listed as a fault forever.
/// </remarks>
public sealed class OracleFactAttribute : FactAttribute
{
    public OracleFactAttribute()
    {
        if (Path is null)
        {
            Skip = "Set SVG_PAINTCODE_PROJECT to the .svgstudio to compare against PaintCode to run this.";
        }
    }

    internal static string? Path
    {
        get
        {
            var path = Environment.GetEnvironmentVariable("SVG_PAINTCODE_PROJECT");

            return string.IsNullOrEmpty(path) || !Directory.Exists(path) ? null : path;
        }
    }
}
#endif
