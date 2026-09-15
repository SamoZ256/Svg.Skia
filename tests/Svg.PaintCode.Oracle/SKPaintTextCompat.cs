using System.Runtime.CompilerServices;
using SkiaSharp;

namespace PaintCode;

/// <summary>
/// Restores <c>SKPaint.Typeface</c> and <c>SKPaint.TextSize</c>, which SkiaSharp 4 removed.
/// </summary>
/// <remarks>
/// PaintCode2Skia emitted against SkiaSharp 3, where a paint still carried the font; SkiaSharp 4
/// moved both onto <c>SKFont</c>, and the generated code sets them at 78 sites. The alternative was
/// to build the oracle against SkiaSharp 3 — but then two SkiaSharp versions meet in one test
/// process, NuGet unifies them upward, and the older assembly dies at the first call. Holding the
/// two values beside the paint keeps the whole comparison on the version this repository pins.
///
/// It is faithful rather than a stub because PaintCodeStaticLayout reads both back out to build its
/// SKFont when no font is passed, which is the path every one of those 78 sites takes.
/// </remarks>
public static class SKPaintTextCompat
{
    private sealed class Text
    {
        public SKTypeface? Typeface;

        // SkiaSharp 3's SKPaint.TextSize started at 12, and a site that sets only the typeface
        // inherits it.
        public float Size = 12f;
    }

    private static readonly ConditionalWeakTable<SKPaint, Text> s_text = new();

    extension(SKPaint paint)
    {
        public SKTypeface? Typeface
        {
            get => s_text.GetOrCreateValue(paint).Typeface;
            set => s_text.GetOrCreateValue(paint).Typeface = value;
        }

        public float TextSize
        {
            get => s_text.GetOrCreateValue(paint).Size;
            set => s_text.GetOrCreateValue(paint).Size = value;
        }
    }
}
