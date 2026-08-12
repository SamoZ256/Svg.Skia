using System;
using System.IO;

namespace SvgRecipeDemo;

/// <summary>
/// The two documents the demo starts from. They are copied next to the executable, with the
/// contents inlined as a fallback so the window still opens from a bare output directory.
/// </summary>
internal static class DemoFiles
{
    public static string Svg => Read(Path.Combine("Svg", "demo.svg"), FallbackSvg);

    public static string Recipe => Read(Path.Combine("Recipe", "demo.recipe"), FallbackRecipe);

    private static string Read(string relativePath, string fallback)
    {
        var path = Path.Combine(AppContext.BaseDirectory, relativePath);

        return File.Exists(path) ? File.ReadAllText(path) : fallback;
    }

    private const string FallbackSvg = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" height="24" width="24">
          <circle cx="12" cy="12" r="10" fill="#000000" />
        </svg>
        """;

    // Not an interpolated literal: '{{' is the expression delimiter and would be read as an
    // escaped brace in a raw interpolated string (CS9006).
    private const string FallbackRecipe = """
        <recipe xmlns="https://svg.skia/expr/1.0">
          <code>
            <param name="hue" type="number" default="0.58" />
            <let name="body">hsl(hue * 360, 72%, 58%)</let>
          </code>

          <replace color="#000000">body</replace>
        </recipe>
        """;
}
