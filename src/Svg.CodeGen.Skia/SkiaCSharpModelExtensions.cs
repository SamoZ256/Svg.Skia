// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
//#define USE_DISPOSE_TYPEFACE
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ShimSkiaSharp;
using ShimSkiaSharp.Editing;
using Svg.CodeGen.Skia.Expressions;
using Svg.Expressions;

namespace Svg.CodeGen.Skia;

public static class SkiaCSharpModelExtensions
{
    private static string s_srgbLinear = "SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Linear, SKColorSpaceXyz.Srgb)";

    private static string s_srgb = "SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.Srgb)";

    private static readonly CultureInfo s_ci = CultureInfo.InvariantCulture;

    private static readonly char[] s_fontFamilyTrim = { '\'' };

    public static string ToBoolString(this bool value) => value ? "true" : "false";

    public static string ToByteString(this byte value)
    {
        return value.ToString(s_ci);
    }

    public static string ToIntString(this int value)
    {
        return value.ToString(s_ci);
    }

    public static string ToFloatString(this float value)
    {
        if (float.IsNaN(value))
        {
            return "float.NaN";
        }
        if (float.IsNegativeInfinity(value))
        {
            return "float.NegativeInfinity";
        }
        if (float.IsPositiveInfinity(value))
        {
            return "float.PositiveInfinity";
        }
        return string.Concat(value.ToString(s_ci), "f");
    }

    public static StringBuilder ToByteArray(this byte[] array)
    {
        // Each byte is 1 to 3 chars. Add trailing comma and space, then 5 char is expected for each byte in the array.
        // Around the bytes are 15 constant chars, plus an unknown array size - let's do 7 for good measure.
        var sb = new StringBuilder("new byte[", array.Length * 5 + 22).AppendFormat(s_ci, "{0}] {{ ", array.Length);

        for (int i = 0; i < array.Length; i++)
        {
            sb.AppendFormat(s_ci, "{0}, ", array[i]); // C# allows trailing , on end element
        }

        sb.Append(" }");

        return sb;
    }

    public static StringBuilder ToFloatArray(this float[] array)
    {
        var sb = new StringBuilder("new float[").AppendFormat(s_ci, "{0}] {{ ", array.Length);

        for (int i = 0; i < array.Length; i++)
        {
            sb.Append(array[i].ToFloatString()).Append(", "); // C# allows trailing , on end element
        }

        sb.Append(" }");

        return sb;
    }

    public static StringBuilder ToStringArray(this string[] array)
    {
        var sb = new StringBuilder("new string[").AppendFormat(s_ci, "{0}] {{ ", array.Length);

        for (int i = 0; i < array.Length; i++)
        {
            sb.AppendFormat(s_ci, "@\"{0}\", ", array[i].Replace("\"", "\"\"")); // C# allows trailing , on end element
        }

        sb.Append(" }");

        return sb;
    }

    private static string ToGradientColorPositions(float[]? array)
    {
        if (array is null || array.Any(static value => float.IsNaN(value) || float.IsNegativeInfinity(value) || float.IsPositiveInfinity(value)))
        {
            return "null";
        }

        return array.ToFloatArray().ToString();
    }

    public static string ToSKPoint(this SKPoint point)
    {
        return $"new SKPoint({point.X.ToFloatString()}, {point.Y.ToFloatString()})";
    }

    public static string ToSKPoint3(this SKPoint3 point3)
    {
        return $"new SKPoint3({point3.X.ToFloatString()}, {point3.Y.ToFloatString()}, {point3.Z.ToFloatString()})";
    }

    public static string ToSKPoints(this IList<SKPoint> points)
    {
        var result = $"new SKPoint[{points.Count}] {{ ";

        for (int i = 0; i < points.Count; i++)
        {
            result += points[i].ToSKPoint();

            if (points.Count > 0 && i < points.Count - 1)
            {
                result += $", ";
            }
        }

        result += $" }}";

        return result;
    }

    public static string ToSKPointI(this SKPointI pointI)
    {
        return $"new SKPointI({pointI.X.ToIntString()}, {pointI.Y.ToIntString()})";
    }

    public static string ToSKSize(this SKSize size)
    {
        return $"new SKSize({size.Width.ToFloatString()}, {size.Height.ToFloatString()})";
    }

    public static string ToSKSizeI(this SKSizeI sizeI)
    {
        return $"new SKSizeI({sizeI.Width.ToIntString()}, {sizeI.Height.ToIntString()})";
    }

    public static string ToSKRect(this SKRect rect)
    {
        return $"new SKRect({rect.Left.ToFloatString()}, {rect.Top.ToFloatString()}, {rect.Right.ToFloatString()}, {rect.Bottom.ToFloatString()})";
    }

    public static string ToSKMatrix(this SKMatrix matrix)
    {
        return $"new SKMatrix({matrix.ScaleX.ToFloatString()}, {matrix.SkewX.ToFloatString()}, {matrix.TransX.ToFloatString()}, {matrix.SkewY.ToFloatString()}, {matrix.ScaleY.ToFloatString()}, {matrix.TransY.ToFloatString()}, {matrix.Persp0.ToFloatString()}, {matrix.Persp1.ToFloatString()}, {matrix.Persp2.ToFloatString()})";
    }

    /// <summary>The functions a transform was built from, as the calls that fold them again.</summary>
    /// <remarks>
    /// Continuation lines are indented relative to column zero; the caller shifts them to where the
    /// statement sits. Every argument goes through <see cref="SymCSharpEmitter"/> as a number, so a
    /// driven one becomes the compiled expression and a literal one the float it always was.
    /// </remarks>
    public static string ToSKMatrix(this SymMatrix symbolic)
    {
        if (symbolic.Transforms.Count == 0)
        {
            return "SKMatrix.CreateIdentity()";
        }

        var sb = new StringBuilder(ToSKMatrix(symbolic.Transforms[0]));

        for (var i = 1; i < symbolic.Transforms.Count; i++)
        {
            sb.AppendLine().Append("    .PreConcat(").Append(ToSKMatrix(symbolic.Transforms[i])).Append(')');
        }

        return sb.ToString();
    }

    private static string ToSKMatrix(SymTransform transform)
    {
        var arguments = transform.Arguments;

        return transform.Op switch
        {
            // SVG writes a matrix down the columns, so b and c swap — the same reordering the
            // SKMatrix overload above does when it prints an already folded matrix.
            SymTransformOp.Matrix =>
                $"new SKMatrix({ToNumber(arguments[0])}, {ToNumber(arguments[2])}, {ToNumber(arguments[4])}, {ToNumber(arguments[1])}, {ToNumber(arguments[3])}, {ToNumber(arguments[5])}, 0f, 0f, 1f)",
            SymTransformOp.Translate => $"SKMatrix.CreateTranslation({ToNumber(arguments[0])}, {ToNumber(arguments[1])})",
            SymTransformOp.Scale => $"SKMatrix.CreateScale({ToNumber(arguments[0])}, {ToNumber(arguments[1])})",
            SymTransformOp.Rotate => $"SKMatrix.CreateRotationDegrees({ToNumber(arguments[0])}, {ToNumber(arguments[1])}, {ToNumber(arguments[2])})",
            SymTransformOp.Skew =>
                $"SKMatrix.CreateSkew({ExprHelpers.Tangent}({ToNumber(arguments[0])}), {ExprHelpers.Tangent}({ToNumber(arguments[1])}))",
            _ => "SKMatrix.CreateIdentity()"
        };
    }

    private static string ToNumber(SymNode node) => SymCSharpEmitter.Emit(node, ExprType.Number);

    public static void ToSKImage(this SKImage image, SkiaCSharpCodeGenCounter counter, StringBuilder sb, string indent)
    {
        var counterImage = counter.Image;

        if (image.Data is null)
        {
            sb.AppendLine($"{indent}var {counter.ImageVarName}{counterImage} = default(SKImage);");
            return;
        }

        sb.Append($"{indent}var {counter.ImageVarName}{counterImage} = ");
        sb.AppendLine($"SKImage.FromEncodedData({image.Data.ToByteArray()});");
    }

    public static string ToSKPaintStyle(this SKPaintStyle paintStyle)
    {
        switch (paintStyle)
        {
            default:
            case SKPaintStyle.Fill:
                return "SKPaintStyle.Fill";
            case SKPaintStyle.Stroke:
                return "SKPaintStyle.Stroke";
            case SKPaintStyle.StrokeAndFill:
                return "SKPaintStyle.StrokeAndFill";
        }
    }

    public static string ToSKStrokeCap(this SKStrokeCap strokeCap)
    {
        switch (strokeCap)
        {
            default:
            case SKStrokeCap.Butt:
                return "SKStrokeCap.Butt";
            case SKStrokeCap.Round:
                return "SKStrokeCap.Round";
            case SKStrokeCap.Square:
                return "SKStrokeCap.Square";
        }
    }

    public static string ToSKStrokeJoin(this SKStrokeJoin strokeJoin)
    {
        switch (strokeJoin)
        {
            default:
            case SKStrokeJoin.Miter:
                return "SKStrokeJoin.Miter";
            case SKStrokeJoin.Round:
                return "SKStrokeJoin.Round";
            case SKStrokeJoin.Bevel:
                return "SKStrokeJoin.Bevel";
        }
    }

    public static string ToSKTextAlign(this SKTextAlign textAlign)
    {
        switch (textAlign)
        {
            default:
            case SKTextAlign.Left:
                return "SKTextAlign.Left";
            case SKTextAlign.Center:
                return "SKTextAlign.Center";
            case SKTextAlign.Right:
                return "SKTextAlign.Right";
        }
    }

    public static string ToSKTextEncoding(this SKTextEncoding textEncoding)
    {
        switch (textEncoding)
        {
            default:
            case SKTextEncoding.Utf8:
                return "SKTextEncoding.Utf8";
            case SKTextEncoding.Utf16:
                return "SKTextEncoding.Utf16";
            case SKTextEncoding.Utf32:
                return "SKTextEncoding.Utf32";
            case SKTextEncoding.GlyphId:
                return "SKTextEncoding.GlyphId";
        }
    }

    public static string ToSKFontEdging(this SKFontEdging edging)
    {
        switch (edging)
        {
            default:
            case SKFontEdging.Antialias:
                return "SKFontEdging.Antialias";
            case SKFontEdging.Alias:
                return "SKFontEdging.Alias";
            case SKFontEdging.SubpixelAntialias:
                return "SKFontEdging.SubpixelAntialias";
        }
    }

    public static string ToSKFontStyleWeight(this SKFontStyleWeight fontStyleWeight)
    {
        switch (fontStyleWeight)
        {
            default:
            case SKFontStyleWeight.Invisible:
                return "SKFontStyleWeight.Invisible";
            case SKFontStyleWeight.Thin:
                return "SKFontStyleWeight.Thin";
            case SKFontStyleWeight.ExtraLight:
                return "SKFontStyleWeight.ExtraLight";
            case SKFontStyleWeight.Light:
                return "SKFontStyleWeight.Light";
            case SKFontStyleWeight.Normal:
                return "SKFontStyleWeight.Normal";
            case SKFontStyleWeight.Medium:
                return "SKFontStyleWeight.Medium";
            case SKFontStyleWeight.SemiBold:
                return "SKFontStyleWeight.SemiBold";
            case SKFontStyleWeight.Bold:
                return "SKFontStyleWeight.Bold";
            case SKFontStyleWeight.ExtraBold:
                return "SKFontStyleWeight.ExtraBold";
            case SKFontStyleWeight.Black:
                return "SKFontStyleWeight.Black";
            case SKFontStyleWeight.ExtraBlack:
                return "SKFontStyleWeight.ExtraBlack";
        }
    }

    public static string ToSKFontStyleWidth(this SKFontStyleWidth fontStyleWidth)
    {
        switch (fontStyleWidth)
        {
            default:
            case SKFontStyleWidth.UltraCondensed:
                return "SKFontStyleWidth.UltraCondensed";
            case SKFontStyleWidth.ExtraCondensed:
                return "SKFontStyleWidth.ExtraCondensed";
            case SKFontStyleWidth.Condensed:
                return "SKFontStyleWidth.Condensed";
            case SKFontStyleWidth.SemiCondensed:
                return "SKFontStyleWidth.SemiCondensed";
            case SKFontStyleWidth.Normal:
                return "SKFontStyleWidth.Normal";
            case SKFontStyleWidth.SemiExpanded:
                return "SKFontStyleWidth.SemiExpanded";
            case SKFontStyleWidth.Expanded:
                return "SKFontStyleWidth.Expanded";
            case SKFontStyleWidth.ExtraExpanded:
                return "SKFontStyleWidth.ExtraExpanded";
            case SKFontStyleWidth.UltraExpanded:
                return "SKFontStyleWidth.UltraExpanded";
        }
    }

    public static string ToSKFontStyleSlant(this SKFontStyleSlant fontStyleSlant)
    {
        switch (fontStyleSlant)
        {
            default:
            case SKFontStyleSlant.Upright:
                return "SKFontStyleSlant.Upright";
            case SKFontStyleSlant.Italic:
                return "SKFontStyleSlant.Italic";
            case SKFontStyleSlant.Oblique:
                return "SKFontStyleSlant.Oblique";
        }
    }

    /// <param name="requested">What the document asked for, which a typeface template names in place of what was found.</param>
    public static void ToSKTypeface(this SKTypeface? typeface, SkiaCSharpCodeGenCounter counter, StringBuilder sb, string indent, SKTypeface? requested = null)
    {
        var counterTypeface = counter.Typeface;

        // A document naming no family asked for the default one, which is what was found.
        if (counter.TypefaceTemplate is { } template && (requested?.FamilyName ?? typeface?.FamilyName) is { } family)
        {
            var style = requested ?? typeface!;
            var named = SKTypeface.FromFamilyName(family, style.FontWeight, style.FontWidth, style.FontSlant);

            sb.AppendLine($"{indent}var {counter.TypefaceVarName}{counterTypeface} = {Filled(template, named)};");
            return;
        }

        if (typeface is null || typeface.FamilyName is null)
        {
            sb.AppendLine($"{indent}var {counter.TypefaceVarName}{counterTypeface} = SKTypeface.Default;");
            return;
        }

        var fontFamily = typeface.FamilyName;
        var fontWeight = typeface.FontWeight.ToSKFontStyleWeight();
        var fontWidth = typeface.FontWidth.ToSKFontStyleWidth();
        var fontStyle = typeface.FontSlant.ToSKFontStyleSlant();

        var fontFamilyNames = fontFamily?.Split(',')?.Select(x => x.Trim().Trim(s_fontFamilyTrim))?.ToArray();
        if (fontFamilyNames is null || fontFamilyNames.Length == 0)
        {
            sb.AppendLine($"{indent}var {counter.TypefaceVarName}{counterTypeface} = SKTypeface.Default;");
            return;
        }

        sb.AppendLine($"{indent}var {counter.TypefaceVarName}{counterTypeface} = default(SKTypeface);");
        sb.AppendLine($"{indent}var fontFamilyNames{counterTypeface} = {fontFamilyNames?.ToStringArray()};");
        sb.AppendLine($"{indent}var defaultName{counterTypeface} = SKTypeface.Default.FamilyName;");
        sb.AppendLine($"{indent}var {counter.FontManagerVarName}{counterTypeface} = SKFontManager.Default;");
        sb.AppendLine($"{indent}var {counter.FontStyleVarName}{counterTypeface} = new SKFontStyle({fontWeight}, {fontWidth}, {fontStyle});");
        sb.AppendLine($"{indent}foreach (var fontFamilyName{counterTypeface} in fontFamilyNames{counterTypeface})");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    var {counter.FontStyleSetVarName}{counterTypeface} = {counter.FontManagerVarName}{counterTypeface}.GetFontStyles(fontFamilyName{counterTypeface});");
        sb.AppendLine($"{indent}    if ({counter.FontStyleSetVarName}{counterTypeface}.Count > 0)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        {counter.TypefaceVarName}{counterTypeface} = {counter.FontManagerVarName}{counterTypeface}.MatchFamily(fontFamilyName{counterTypeface}, {counter.FontStyleVarName}{counterTypeface});");
        sb.AppendLine($"{indent}        if ({counter.TypefaceVarName}{counterTypeface} is {{ }})");
        sb.AppendLine($"{indent}        {{");
        sb.AppendLine($"{indent}            if (!defaultName{counterTypeface}.Equals(fontFamilyName{counterTypeface}, StringComparison.Ordinal)");
        sb.AppendLine($"{indent}                && defaultName{counterTypeface}.Equals({counter.TypefaceVarName}{counterTypeface}.FamilyName, StringComparison.Ordinal))");
        sb.AppendLine($"{indent}            {{");
#if USE_DISPOSE_TYPEFACE
            sb.AppendLine($"{indent}                {counter.TypefaceVarName}{counterTypeface}?.Dispose();");  
#endif
        sb.AppendLine($"{indent}                {counter.TypefaceVarName}{counterTypeface} = null;");
        sb.AppendLine($"{indent}                continue;");
        sb.AppendLine($"{indent}            }}");
        sb.AppendLine($"{indent}            break;");
        sb.AppendLine($"{indent}        }}");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}}}");
    }

    /// <summary>A typeface template with what it names of <paramref name="typeface"/> written in.</summary>
    private static string Filled(string template, SKTypeface typeface)
    {
        var family = typeface.FamilyName!.Split(',')[0].Trim().Trim('\'', '"');
        var weight = typeface.FontWeight switch
        {
            SKFontStyleWeight.Normal => "Regular",
            var named when Enum.IsDefined(typeof(SKFontStyleWeight), named) => named.ToString(),
            var other => ((int)other).ToString(s_ci)
        };

        // Most templates put these inside a string literal.
        static string Escaped(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        return template
            .Replace("{family}", Escaped(family))
            .Replace("{family-dashed}", Escaped(family.Replace(' ', '-')))
            .Replace("{weight}", weight)
            .Replace("{weightValue}", ((int)typeface.FontWeight).ToString(s_ci))
            .Replace("{slant}", typeface.FontSlant.ToString());
    }

    public static string ToSKColor(this SKColor color)
    {
        if (color.Expression is { } expression)
        {
            return SymCSharpEmitter.Emit(expression);
        }

        return $"new SKColor({color.Red}, {color.Green}, {color.Blue}, {color.Alpha})";
    }

    public static string ToSKColor(this SKColorF color)
    {
        if (color.Expression is { } expression)
        {
            // Authored expressions produce an SKColor; gradient stops need SKColorF.
            return SymCSharpEmitter.EmitAsColorF(expression);
        }

        return $"new SKColorF({color.Red.ToFloatString()}, {color.Green.ToFloatString()}, {color.Blue.ToFloatString()}, {color.Alpha.ToFloatString()})";
    }

    public static string ToSKColors(this SKColor[] colors)
    {
        var skColors = $"new SKColor[{colors.Length}] {{ ";

        for (int i = 0; i < colors.Length; i++)
        {
            skColors += colors[i].ToSKColor();

            if (colors.Length > 0 && i < colors.Length - 1)
            {
                skColors += $", ";
            }
        }

        skColors += $" }}";

        return skColors;
    }

    public static string ToSKColorF(this SKColorF color)
    {
        if (color.Expression is { } expression)
        {
            // Authored expressions produce an SKColor; gradient stops need SKColorF.
            return SymCSharpEmitter.EmitAsColorF(expression);
        }

        return $"new SKColorF({color.Red.ToFloatString()}, {color.Green.ToFloatString()}, {color.Blue.ToFloatString()}, {color.Alpha.ToFloatString()})";
    }

    public static string ToSKColors(this SKColorF[] colors)
    {
        var skColors = $"new SKColorF[{colors.Length}] {{ ";

        for (int i = 0; i < colors.Length; i++)
        {
            skColors += colors[i].ToSKColorF();

            if (colors.Length > 0 && i < colors.Length - 1)
            {
                skColors += $", ";
            }
        }

        skColors += $" }}";

        return skColors;
    }

    public static string ToSKShaderTileMode(this SKShaderTileMode shaderTileMode)
    {
        switch (shaderTileMode)
        {
            default:
            case SKShaderTileMode.Clamp:
                return "SKShaderTileMode.Clamp";
            case SKShaderTileMode.Repeat:
                return "SKShaderTileMode.Repeat";
            case SKShaderTileMode.Mirror:
                return "SKShaderTileMode.Mirror";
            case SKShaderTileMode.Decal:
                return "SKShaderTileMode.Decal";
        }
    }

    public static void ToSKShader(this SKShader? shader, SkiaCSharpCodeGenCounter counter, StringBuilder sb, string indent)
    {
        var counterShader = counter.Shader;

        switch (shader)
        {
            case ColorShader colorShader:
                {
                    sb.Append($"{indent}var {counter.ShaderVarName}{counterShader} = ");
                    sb.AppendLine($"SKShader.CreateColor(");
                    sb.AppendLine($"{indent}    {colorShader.Color.ToSKColor()},");
                    sb.AppendLine($"{indent}    {(colorShader.ColorSpace == SKColorSpace.Srgb ? s_srgb : s_srgbLinear)});");
                    return;
                }
            case LinearGradientShader linearGradientShader:
                {
                    if (linearGradientShader.Colors is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ShaderVarName}{counterShader} = default(SKShader);");
                        return;
                    }

                    var colorPos = ToGradientColorPositions(linearGradientShader.ColorPos);
                    if (linearGradientShader.LocalMatrix is { })
                    {
                        sb.Append($"{indent}var {counter.ShaderVarName}{counterShader} = ");
                        sb.AppendLine($"SKShader.CreateLinearGradient(");
                        sb.AppendLine($"{indent}    {linearGradientShader.Start.ToSKPoint()},");
                        sb.AppendLine($"{indent}    {linearGradientShader.End.ToSKPoint()},");
                        sb.AppendLine($"{indent}    {linearGradientShader.Colors.ToSKColors()},");
                        sb.AppendLine($"{indent}    {(linearGradientShader.ColorSpace == SKColorSpace.Srgb ? s_srgb : s_srgbLinear)},");
                        sb.AppendLine($"{indent}    {colorPos},");
                        sb.AppendLine($"{indent}    {linearGradientShader.Mode.ToSKShaderTileMode()},");
                        sb.AppendLine($"{indent}    {linearGradientShader.LocalMatrix.Value.ToSKMatrix()});");
                        return;
                    }
                    else
                    {
                        sb.Append($"{indent}var {counter.ShaderVarName}{counterShader} = ");
                        sb.AppendLine($"SKShader.CreateLinearGradient(");
                        sb.AppendLine($"{indent}    {linearGradientShader.Start.ToSKPoint()},");
                        sb.AppendLine($"{indent}    {linearGradientShader.End.ToSKPoint()},");
                        sb.AppendLine($"{indent}    {linearGradientShader.Colors.ToSKColors()},");
                        sb.AppendLine($"{indent}    {(linearGradientShader.ColorSpace == SKColorSpace.Srgb ? s_srgb : s_srgbLinear)},");
                        sb.AppendLine($"{indent}    {colorPos},");
                        sb.AppendLine($"{indent}    {linearGradientShader.Mode.ToSKShaderTileMode()});");
                        return;
                    }
                }
            case RadialGradientShader radialGradientShader:
                {
                    if (radialGradientShader.Colors is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ShaderVarName}{counterShader} = default(SKShader);");
                        return;
                    }

                    var colorPos = ToGradientColorPositions(radialGradientShader.ColorPos);
                    if (radialGradientShader.LocalMatrix is { })
                    {
                        sb.Append($"{indent}var {counter.ShaderVarName}{counterShader} = ");
                        sb.AppendLine($"SKShader.CreateRadialGradient(");
                        sb.AppendLine($"{indent}    {radialGradientShader.Center.ToSKPoint()},");
                        sb.AppendLine($"{indent}    {radialGradientShader.Radius.ToFloatString()},");
                        sb.AppendLine($"{indent}    {radialGradientShader.Colors.ToSKColors()},");
                        sb.AppendLine($"{indent}    {(radialGradientShader.ColorSpace == SKColorSpace.Srgb ? s_srgb : s_srgbLinear)},");
                        sb.AppendLine($"{indent}    {colorPos},");
                        sb.AppendLine($"{indent}    {radialGradientShader.Mode.ToSKShaderTileMode()},");
                        sb.AppendLine($"{indent}    {radialGradientShader.LocalMatrix.Value.ToSKMatrix()});");
                        return;
                    }
                    else
                    {
                        sb.Append($"{indent}var {counter.ShaderVarName}{counterShader} = ");
                        sb.AppendLine($"SKShader.CreateRadialGradient(");
                        sb.AppendLine($"{indent}    {radialGradientShader.Center.ToSKPoint()},");
                        sb.AppendLine($"{indent}    {radialGradientShader.Radius.ToFloatString()},");
                        sb.AppendLine($"{indent}    {radialGradientShader.Colors.ToSKColors()},");
                        sb.AppendLine($"{indent}    {(radialGradientShader.ColorSpace == SKColorSpace.Srgb ? s_srgb : s_srgbLinear)},");
                        sb.AppendLine($"{indent}    {colorPos},");
                        sb.AppendLine($"{indent}    {radialGradientShader.Mode.ToSKShaderTileMode()});");
                        return;
                    }
                }
            case TwoPointConicalGradientShader twoPointConicalGradientShader:
                {
                    if (twoPointConicalGradientShader.Colors is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ShaderVarName}{counterShader} = default(SKShader);");
                        return;
                    }

                    var colorPos = ToGradientColorPositions(twoPointConicalGradientShader.ColorPos);
                    if (twoPointConicalGradientShader.LocalMatrix is { })
                    {
                        sb.Append($"{indent}var {counter.ShaderVarName}{counterShader} = ");
                        sb.AppendLine($"SKShader.CreateTwoPointConicalGradient(");
                        sb.AppendLine($"{indent}    {twoPointConicalGradientShader.Start.ToSKPoint()},");
                        sb.AppendLine($"{indent}    {twoPointConicalGradientShader.StartRadius.ToFloatString()},");
                        sb.AppendLine($"{indent}    {twoPointConicalGradientShader.End.ToSKPoint()},");
                        sb.AppendLine($"{indent}    {twoPointConicalGradientShader.EndRadius.ToFloatString()},");
                        sb.AppendLine($"{indent}    {twoPointConicalGradientShader.Colors.ToSKColors()},");
                        sb.AppendLine($"{indent}    {(twoPointConicalGradientShader.ColorSpace == SKColorSpace.Srgb ? s_srgb : s_srgbLinear)},");
                        sb.AppendLine($"{indent}    {colorPos},");
                        sb.AppendLine($"{indent}    {twoPointConicalGradientShader.Mode.ToSKShaderTileMode()},");
                        sb.AppendLine($"{indent}    {twoPointConicalGradientShader.LocalMatrix.Value.ToSKMatrix()});");
                        return;
                    }
                    else
                    {
                        sb.Append($"{indent}var {counter.ShaderVarName}{counterShader} = ");
                        sb.AppendLine($"SKShader.CreateTwoPointConicalGradient(");
                        sb.AppendLine($"{indent}    {twoPointConicalGradientShader.Start.ToSKPoint()},");
                        sb.AppendLine($"{indent}    {twoPointConicalGradientShader.StartRadius.ToFloatString()},");
                        sb.AppendLine($"{indent}    {twoPointConicalGradientShader.End.ToSKPoint()},");
                        sb.AppendLine($"{indent}    {twoPointConicalGradientShader.EndRadius.ToFloatString()},");
                        sb.AppendLine($"{indent}    {twoPointConicalGradientShader.Colors.ToSKColors()},");
                        sb.AppendLine($"{indent}    {(twoPointConicalGradientShader.ColorSpace == SKColorSpace.Srgb ? s_srgb : s_srgbLinear)},");
                        sb.AppendLine($"{indent}    {colorPos},");
                        sb.AppendLine($"{indent}    {twoPointConicalGradientShader.Mode.ToSKShaderTileMode()});");
                        return;
                    }
                }
            case PictureShader pictureShader:
                {
                    if (pictureShader.Src is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ShaderVarName}{counterShader} = default(SKShader);");
                        return;
                    }

                    var counterPicture = ++counter.Picture;
                    pictureShader.Src.ToSKPicture(counter, sb, indent);

                    sb.Append($"{indent}var {counter.ShaderVarName}{counterShader} = ");
                    sb.AppendLine($"SKShader.CreatePicture(");
                    sb.AppendLine($"{indent}    {counter.PictureVarName}{counterPicture},");
                    sb.AppendLine($"{indent}    SKShaderTileMode.Repeat,");
                    sb.AppendLine($"{indent}    SKShaderTileMode.Repeat,");
                    sb.AppendLine($"{indent}    {pictureShader.LocalMatrix.ToSKMatrix()},");
                    sb.AppendLine($"{indent}    {pictureShader.Tile.ToSKRect()});");
                    sb.AppendLine($"{indent}{counter.PictureVarName}{counterPicture}?.Dispose();");
                    return;
                }
            case PerlinNoiseFractalNoiseShader perlinNoiseFractalNoiseShader:
                {
                    sb.Append($"{indent}var {counter.ShaderVarName}{counterShader} = ");
                    sb.AppendLine($"SKShader.CreatePerlinNoiseFractalNoise(");
                    sb.AppendLine($"{indent}    {perlinNoiseFractalNoiseShader.BaseFrequencyX.ToFloatString()},");
                    sb.AppendLine($"{indent}    {perlinNoiseFractalNoiseShader.BaseFrequencyY.ToFloatString()},");
                    sb.AppendLine($"{indent}    {perlinNoiseFractalNoiseShader.NumOctaves.ToIntString()},");
                    sb.AppendLine($"{indent}    {perlinNoiseFractalNoiseShader.Seed.ToFloatString()},");
                    sb.AppendLine($"{indent}    {perlinNoiseFractalNoiseShader.TileSize.ToSKPointI()});");
                    return;
                }
            case PerlinNoiseTurbulenceShader perlinNoiseTurbulenceShader:
                {
                    sb.Append($"{indent}var {counter.ShaderVarName}{counterShader} = ");
                    sb.AppendLine($"SKShader.CreatePerlinNoiseTurbulence(");
                    sb.AppendLine($"{indent}    {perlinNoiseTurbulenceShader.BaseFrequencyX.ToFloatString()},");
                    sb.AppendLine($"{indent}    {perlinNoiseTurbulenceShader.BaseFrequencyY.ToFloatString()},");
                    sb.AppendLine($"{indent}    {perlinNoiseTurbulenceShader.NumOctaves.ToIntString()},");
                    sb.AppendLine($"{indent}    {perlinNoiseTurbulenceShader.Seed.ToFloatString()},");
                    sb.AppendLine($"{indent}    {perlinNoiseTurbulenceShader.TileSize.ToSKPointI()});");
                    return;
                }
            default:
                {
                    sb.AppendLine($"{indent}var {counter.ShaderVarName}{counterShader} = default(SKShader);");
                    return;
                }
        }
    }

    public static void ToSKColorFilter(this SKColorFilter? colorFilter, SkiaCSharpCodeGenCounter counter, StringBuilder sb, string indent)
    {
        var counterColorFilter = counter.ColorFilter;

        switch (colorFilter)
        {
            case BlendModeColorFilter blendModeColorFilter:
                {
                    sb.Append($"{indent}var {counter.ColorFilterVarName}{counterColorFilter} = ");
                    sb.AppendLine($"SKColorFilter.CreateBlendMode(");
                    sb.AppendLine($"{indent}    {blendModeColorFilter.Color.ToSKColor()},");
                    sb.AppendLine($"{indent}    {blendModeColorFilter.Mode.ToSKBlendMode()});");
                    return;
                }
            case ColorMatrixColorFilter colorMatrixColorFilter:
                {
                    if (colorMatrixColorFilter.Matrix is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ColorFilterVarName}{counterColorFilter} = default(SKColorFilter);");
                        return;
                    }

                    sb.Append($"{indent}var {counter.ColorFilterVarName}{counterColorFilter} = ");
                    sb.AppendLine($"SKColorFilter.CreateColorMatrix(");
                    sb.AppendLine($"{indent}    {colorMatrixColorFilter.Matrix.ToFloatArray()});");
                    return;
                }
            case LumaColorColorFilter _:
                {
                    sb.Append($"{indent}var {counter.ColorFilterVarName}{counterColorFilter} = ");
                    sb.AppendLine($"SKColorFilter.CreateLumaColor();");
                    return;
                }
            case TableColorFilter tableColorFilter:
                {
                    if (tableColorFilter.TableA is null
                        || tableColorFilter.TableR is null
                        || tableColorFilter.TableG is null
                        || tableColorFilter.TableB is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ColorFilterVarName}{counterColorFilter} = default(SKColorFilter);");
                        return;
                    }

                    sb.Append($"{indent}var {counter.ColorFilterVarName}{counterColorFilter} = ");
                    sb.AppendLine($"SKColorFilter.CreateTable(");
                    sb.AppendLine($"{indent}    {tableColorFilter.TableA.ToByteArray()},");
                    sb.AppendLine($"{indent}    {tableColorFilter.TableR.ToByteArray()},");
                    sb.AppendLine($"{indent}    {tableColorFilter.TableG.ToByteArray()},");
                    sb.AppendLine($"{indent}    {tableColorFilter.TableB.ToByteArray()});");
                    return;
                }
            default:
                {
                    sb.AppendLine($"{indent}var {counter.ColorFilterVarName}{counterColorFilter} = default(SKColorFilter);");
                    return;
                }
        }
    }

    public static string ToSKColorChannel(this SKColorChannel colorChannel)
    {
        switch (colorChannel)
        {
            default:
            case SKColorChannel.R:
                return "SKColorChannel.R";
            case SKColorChannel.G:
                return "SKColorChannel.G";
            case SKColorChannel.B:
                return "SKColorChannel.B";
            case SKColorChannel.A:
                return "SKColorChannel.A";
        }
    }

    public static void ToSKImageFilter(this SKImageFilter? imageFilter, SkiaCSharpCodeGenCounter counter, StringBuilder sb, string indent)
    {
        var counterImageFilter = counter.ImageFilter;

        switch (imageFilter)
        {
            case ArithmeticImageFilter arithmeticImageFilter:
                {
                    if (arithmeticImageFilter.Background is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = default(SKImageFilter);");
                        return;
                    }

                    var counterImageFilterBackground = ++counter.ImageFilter;
                    if (arithmeticImageFilter.Background is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilterBackground} = default(SKImageFilter);");
                    }
                    else
                    {
                        arithmeticImageFilter.Background.ToSKImageFilter(counter, sb, indent);
                    }

                    var counterImageFilterForeground = ++counter.ImageFilter;
                    if (arithmeticImageFilter.Foreground is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilterBackground} = default(SKImageFilter);");
                    }
                    else
                    {
                        arithmeticImageFilter.Foreground.ToSKImageFilter(counter, sb, indent);
                    }

                    sb.Append($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = ");
                    sb.AppendLine($"SKImageFilter.CreateArithmetic(");
                    sb.AppendLine($"{indent}    {arithmeticImageFilter.K1.ToFloatString()},");
                    sb.AppendLine($"{indent}    {arithmeticImageFilter.K2.ToFloatString()},");
                    sb.AppendLine($"{indent}    {arithmeticImageFilter.K3.ToFloatString()},");
                    sb.AppendLine($"{indent}    {arithmeticImageFilter.K4.ToFloatString()},");
                    sb.AppendLine($"{indent}    {arithmeticImageFilter.EforcePMColor.ToBoolString()},");
                    sb.AppendLine($"{indent}    {counter.ImageFilterVarName}{counterImageFilterBackground},");
                    sb.AppendLine($"{indent}    {counter.ImageFilterVarName}{counterImageFilterForeground},");
                    sb.AppendLine($"{indent}    {arithmeticImageFilter.Clip?.ToSKRect() ?? "null"});");
                    return;
                }
            case BlendModeImageFilter blendModeImageFilter:
                {
                    if (blendModeImageFilter.Background is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = default(SKImageFilter);");
                        return;
                    }

                    var counterImageFilterBackground = ++counter.ImageFilter;
                    if (blendModeImageFilter.Background is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilterBackground} = default(SKImageFilter);");
                    }
                    else
                    {
                        blendModeImageFilter.Background.ToSKImageFilter(counter, sb, indent);
                    }

                    var counterImageFilterForeground = ++counter.ImageFilter;
                    if (blendModeImageFilter.Foreground is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilterBackground} = default(SKImageFilter);");
                    }
                    else
                    {
                        blendModeImageFilter.Foreground.ToSKImageFilter(counter, sb, indent);
                    }

                    sb.Append($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = ");
                    sb.AppendLine($"SKImageFilter.CreateBlendMode(");
                    sb.AppendLine($"{indent}    {blendModeImageFilter.Mode.ToSKBlendMode()},");
                    sb.AppendLine($"{indent}    {counter.ImageFilterVarName}{counterImageFilterBackground},");
                    sb.AppendLine($"{indent}    {counter.ImageFilterVarName}{counterImageFilterForeground},");
                    sb.AppendLine($"{indent}    {blendModeImageFilter.Clip?.ToSKRect() ?? "null"});");
                    return;
                }
            case BlurImageFilter blurImageFilter:
                {
                    var counterImageFilterInput = ++counter.ImageFilter;
                    if (blurImageFilter.Input is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilterInput} = default(SKImageFilter);");
                    }
                    else
                    {
                        blurImageFilter.Input.ToSKImageFilter(counter, sb, indent);
                    }

                    sb.Append($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = ");
                    sb.AppendLine($"SKImageFilter.CreateBlur(");
                    sb.AppendLine($"{indent}    {blurImageFilter.SigmaX.ToFloatString()},");
                    sb.AppendLine($"{indent}    {blurImageFilter.SigmaY.ToFloatString()},");
                    sb.AppendLine($"{indent}    {counter.ImageFilterVarName}{counterImageFilterInput},");
                    sb.AppendLine($"{indent}    {blurImageFilter.Clip?.ToSKRect() ?? "null"});");
                    return;
                }
            case ColorFilterImageFilter colorFilterImageFilter:
                {
                    if (colorFilterImageFilter.ColorFilter is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = default(SKImageFilter);");
                        return;
                    }

                    var counterColorFilter = ++counter.ColorFilter;
                    colorFilterImageFilter.ColorFilter.ToSKColorFilter(counter, sb, indent);

                    var counterImageFilterInput = ++counter.ImageFilter;
                    if (colorFilterImageFilter.Input is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilterInput} = default(SKImageFilter);");
                    }
                    else
                    {
                        colorFilterImageFilter.Input.ToSKImageFilter(counter, sb, indent);
                    }

                    sb.Append($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = ");
                    sb.AppendLine($"SKImageFilter.CreateColorFilter(");
                    sb.AppendLine($"{indent}    {counter.ColorFilterVarName}{counterColorFilter},");
                    sb.AppendLine($"{indent}    {counter.ImageFilterVarName}{counterImageFilterInput},");
                    sb.AppendLine($"{indent}    {colorFilterImageFilter.Clip?.ToSKRect() ?? "null"});");
                    return;
                }
            case DilateImageFilter dilateImageFilter:
                {
                    var counterImageFilterInput = ++counter.ImageFilter;
                    if (dilateImageFilter.Input is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilterInput} = default(SKImageFilter);");
                    }
                    else
                    {
                        dilateImageFilter.Input.ToSKImageFilter(counter, sb, indent);
                    }

                    sb.Append($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = ");
                    sb.AppendLine($"SKImageFilter.CreateDilate(");
                    sb.AppendLine($"{indent}    {dilateImageFilter.RadiusX.ToIntString()},");
                    sb.AppendLine($"{indent}    {dilateImageFilter.RadiusY.ToIntString()},");
                    sb.AppendLine($"{indent}    {counter.ImageFilterVarName}{counterImageFilterInput},");
                    sb.AppendLine($"{indent}    {dilateImageFilter.Clip?.ToSKRect() ?? "null"});");
                    return;
                }
            case DisplacementMapEffectImageFilter displacementMapEffectImageFilter:
                {
                    if (displacementMapEffectImageFilter.Displacement is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = default(SKImageFilter);");
                        return;
                    }

                    var counterImageFilterDisplacement = ++counter.ImageFilter;
                    displacementMapEffectImageFilter.Displacement.ToSKImageFilter(counter, sb, indent);

                    var counterImageFilterInput = ++counter.ImageFilter;
                    if (displacementMapEffectImageFilter.Input is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilterInput} = default(SKImageFilter);");
                    }
                    else
                    {
                        displacementMapEffectImageFilter.Input.ToSKImageFilter(counter, sb, indent);
                    }

                    sb.Append($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = ");
                    sb.AppendLine($"SKImageFilter.CreateDisplacementMapEffect(");
                    sb.AppendLine($"{indent}    {displacementMapEffectImageFilter.XChannelSelector.ToSKColorChannel()},");
                    sb.AppendLine($"{indent}    {displacementMapEffectImageFilter.YChannelSelector.ToSKColorChannel()},");
                    sb.AppendLine($"{indent}    {displacementMapEffectImageFilter.Scale.ToFloatString()},");
                    sb.AppendLine($"{indent}    {counter.ImageFilterVarName}{counterImageFilterDisplacement},");
                    sb.AppendLine($"{indent}    {counter.ImageFilterVarName}{counterImageFilterInput},");
                    sb.AppendLine($"{indent}    {displacementMapEffectImageFilter.Clip?.ToSKRect() ?? "null"});");
                    return;
                }
            case DistantLitDiffuseImageFilter distantLitDiffuseImageFilter:
                {
                    var counterImageFilterInput = ++counter.ImageFilter;
                    if (distantLitDiffuseImageFilter.Input is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilterInput} = default(SKImageFilter);");
                    }
                    else
                    {
                        distantLitDiffuseImageFilter.Input.ToSKImageFilter(counter, sb, indent);
                    }

                    sb.Append($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = ");
                    sb.AppendLine($"SKImageFilter.CreateDistantLitDiffuse(");
                    sb.AppendLine($"{indent}    {distantLitDiffuseImageFilter.Direction.ToSKPoint3()},");
                    sb.AppendLine($"{indent}    {distantLitDiffuseImageFilter.LightColor.ToSKColor()},");
                    sb.AppendLine($"{indent}    {distantLitDiffuseImageFilter.SurfaceScale.ToFloatString()},");
                    sb.AppendLine($"{indent}    {distantLitDiffuseImageFilter.Kd.ToFloatString()},");
                    sb.AppendLine($"{indent}    {counter.ImageFilterVarName}{counterImageFilterInput},");
                    sb.AppendLine($"{indent}    {distantLitDiffuseImageFilter.Clip?.ToSKRect() ?? "null"});");
                    return;
                }
            case DistantLitSpecularImageFilter distantLitSpecularImageFilter:
                {
                    var counterImageFilterInput = ++counter.ImageFilter;
                    if (distantLitSpecularImageFilter.Input is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilterInput} = default(SKImageFilter);");
                    }
                    else
                    {
                        distantLitSpecularImageFilter.Input.ToSKImageFilter(counter, sb, indent);
                    }

                    sb.Append($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = ");
                    sb.AppendLine($"SKImageFilter.CreateDistantLitSpecular(");
                    sb.AppendLine($"{indent}    {distantLitSpecularImageFilter.Direction.ToSKPoint3()},");
                    sb.AppendLine($"{indent}    {distantLitSpecularImageFilter.LightColor.ToSKColor()},");
                    sb.AppendLine($"{indent}    {distantLitSpecularImageFilter.SurfaceScale.ToFloatString()},");
                    sb.AppendLine($"{indent}    {distantLitSpecularImageFilter.Ks.ToFloatString()},");
                    sb.AppendLine($"{indent}    {distantLitSpecularImageFilter.Shininess.ToFloatString()},");
                    sb.AppendLine($"{indent}    {counter.ImageFilterVarName}{counterImageFilterInput},");
                    sb.AppendLine($"{indent}    {distantLitSpecularImageFilter.Clip?.ToSKRect() ?? "null"});");
                    return;
                }
            case ErodeImageFilter erodeImageFilter:
                {
                    var counterImageFilterInput = ++counter.ImageFilter;
                    if (erodeImageFilter.Input is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilterInput} = default(SKImageFilter);");
                    }
                    else
                    {
                        erodeImageFilter.Input.ToSKImageFilter(counter, sb, indent);
                    }

                    sb.Append($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = ");
                    sb.AppendLine($"SKImageFilter.CreateErode(");
                    sb.AppendLine($"{indent}    {erodeImageFilter.RadiusX.ToIntString()},");
                    sb.AppendLine($"{indent}    {erodeImageFilter.RadiusY.ToIntString()},");
                    sb.AppendLine($"{indent}    {counter.ImageFilterVarName}{counterImageFilterInput},");
                    sb.AppendLine($"{indent}    {erodeImageFilter.Clip?.ToSKRect() ?? "null"});");
                    return;
                }
            case ImageImageFilter imageImageFilter:
                {
                    if (imageImageFilter.Image is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = default(SKImageFilter);");
                        return;
                    }

                    var counterImage = ++counter.Image;
                    imageImageFilter.Image.ToSKImage(counter, sb, indent);

                    sb.Append($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = ");
                    sb.AppendLine($"SKImageFilter.CreateImage(");
                    sb.AppendLine($"{indent}    {counter.ImageVarName}{counterImage},");
                    sb.AppendLine($"{indent}    {imageImageFilter.Src.ToSKRect()},");
                    sb.AppendLine($"{indent}    {imageImageFilter.Dst.ToSKRect()},");
                    sb.AppendLine($"{indent}    {imageImageFilter.FilterQuality.ToSKSamplingOptions()});");
                    return;
                }
            case MatrixConvolutionImageFilter matrixConvolutionImageFilter:
                {
                    if (matrixConvolutionImageFilter.Kernel is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = default(SKImageFilter);");
                        return;
                    }

                    var counterImageFilterInput = ++counter.ImageFilter;
                    if (matrixConvolutionImageFilter.Input is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilterInput} = default(SKImageFilter);");
                    }
                    else
                    {
                        matrixConvolutionImageFilter.Input.ToSKImageFilter(counter, sb, indent);
                    }

                    sb.Append($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = ");
                    sb.AppendLine($"SKImageFilter.CreateMatrixConvolution(");
                    sb.AppendLine($"{indent}    {matrixConvolutionImageFilter.KernelSize.ToSKSizeI()},");
                    sb.AppendLine($"{indent}    {matrixConvolutionImageFilter.Kernel.ToFloatArray()},");
                    sb.AppendLine($"{indent}    {matrixConvolutionImageFilter.Gain.ToFloatString()},");
                    sb.AppendLine($"{indent}    {matrixConvolutionImageFilter.Bias.ToFloatString()},");
                    sb.AppendLine($"{indent}    {matrixConvolutionImageFilter.KernelOffset.ToSKPointI()},");
                    sb.AppendLine($"{indent}    {matrixConvolutionImageFilter.TileMode.ToSKShaderTileMode()},");
                    sb.AppendLine($"{indent}    {matrixConvolutionImageFilter.ConvolveAlpha.ToBoolString()},");
                    sb.AppendLine($"{indent}    {counter.ImageFilterVarName}{counterImageFilterInput},");
                    sb.AppendLine($"{indent}    {matrixConvolutionImageFilter.Clip?.ToSKRect() ?? "null"});");
                    return;
                }
            case MergeImageFilter mergeImageFilter:
                {
                    if (mergeImageFilter.Filters is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = default(SKImageFilter);");
                        return;
                    }

                    var imageFilters = mergeImageFilter.Filters;

                    sb.AppendLine($"{indent}var {counter.ImageFilterVarName}s{counterImageFilter} = new SKImageFilter[{imageFilters.Length}];");

                    for (int i = 0; i < imageFilters.Length; i++)
                    {
                        var imageFilterItem = imageFilters[i];
                        var counterImageFilterItem = ++counter.ImageFilter;
                        if (imageFilterItem is null)
                        {
                            sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilterItem} = default(SKImageFilter);");
                        }
                        else
                        {
                            imageFilterItem.ToSKImageFilter(counter, sb, indent);
                        }
                        sb.AppendLine($"{indent}{counter.ImageFilterVarName}s{counterImageFilter}[{i}] = {counter.ImageFilterVarName}{counterImageFilterItem};");
                    }

                    sb.Append($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = ");
                    sb.AppendLine($"SKImageFilter.CreateMerge(");
                    sb.AppendLine($"{indent}    {counter.ImageFilterVarName}s{counterImageFilter},");
                    sb.AppendLine($"{indent}    {mergeImageFilter.Clip?.ToSKRect() ?? "null"});");
                    return;
                }
            case OffsetImageFilter offsetImageFilter:
                {
                    var counterImageFilterInput = ++counter.ImageFilter;
                    if (offsetImageFilter.Input is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilterInput} = default(SKImageFilter);");
                    }
                    else
                    {
                        offsetImageFilter.Input.ToSKImageFilter(counter, sb, indent);
                    }

                    sb.Append($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = ");
                    sb.AppendLine($"SKImageFilter.CreateOffset(");
                    sb.AppendLine($"{indent}    {offsetImageFilter.Dx.ToFloatString()},");
                    sb.AppendLine($"{indent}    {offsetImageFilter.Dy.ToFloatString()},");
                    sb.AppendLine($"{indent}    {counter.ImageFilterVarName}{counterImageFilterInput},");
                    sb.AppendLine($"{indent}    {offsetImageFilter.Clip?.ToSKRect() ?? "null"});");
                    return;
                }
            case PaintImageFilter paintImageFilter:
                {
                    if (paintImageFilter.Paint is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = default(SKImageFilter);");
                        return;
                    }

                    var counterPaint = ++counter.Paint;
                    paintImageFilter.Paint.ToSKPaint(counter, sb, indent);

                    sb.Append($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = ");
                    sb.AppendLine($"SKImageFilter.CreatePaint(");
                    sb.AppendLine($"{indent}    {counter.PaintVarName}{counterPaint},");
                    sb.AppendLine($"{indent}    {paintImageFilter.Clip?.ToSKRect() ?? "null"});");

                    // NOTE: Do not dispose created SKTypeface by font manager.
#if USE_DISPOSE_TYPEFACE
                        if (paintImageFilter.Paint.Typeface is { })
                        {
                            sb.AppendLine($"{indent}if ({counter.PaintVarName}{counterPaint}.Typeface != SKTypeface.Default)");
                            sb.AppendLine($"{indent}{{");
                            sb.AppendLine($"{indent}    {counter.PaintVarName}{counterPaint}.Typeface?.Dispose();");
                            sb.AppendLine($"{indent}}}");
                        } 
#endif
                    if (paintImageFilter.Paint.Shader is { })
                    {
                        sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.Shader?.Dispose();");
                    }
                    if (paintImageFilter.Paint.ColorFilter is { })
                    {
                        sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.ColorFilter.Dispose()");
                    }
                    if (paintImageFilter.Paint.ImageFilter is { })
                    {
                        sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.ImageFilter?.Dispose();");
                    }
                    if (paintImageFilter.Paint.PathEffect is { })
                    {
                        sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.PathEffect?.Dispose();");
                    }

                    sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.Dispose();");
                    return;
                }
            case ShaderImageFilter shaderImageFilter:
                {
                    if (shaderImageFilter.Shader is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = default(SKImageFilter);");
                        return;
                    }

                    var counterShader = ++counter.Shader;
                    shaderImageFilter.Shader.ToSKShader(counter, sb, indent);

                    sb.Append($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = ");
                    sb.AppendLine($"SKImageFilter.CreateShader(");
                    sb.AppendLine($"{indent}    {counter.ShaderVarName}{counterShader},");
                    sb.AppendLine($"{indent}    {shaderImageFilter.Dither.ToBoolString()},");
                    sb.AppendLine($"{indent}    {shaderImageFilter.Clip?.ToSKRect() ?? "null"});");

                    sb.AppendLine($"{indent}{counter.ShaderVarName}{counterShader}?.Dispose();");
                    return;
                }
            case PictureImageFilter pictureImageFilter:
                {
                    if (pictureImageFilter.Picture is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = default(SKImageFilter);");
                        return;
                    }

                    var counterPicture = ++counter.Picture;
                    pictureImageFilter.Picture.ToSKPicture(counter, sb, indent);

                    sb.Append($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = ");
                    sb.AppendLine($"SKImageFilter.CreatePicture(");
                    sb.AppendLine($"{indent}    {counter.PictureVarName}{counterPicture},");
                    sb.AppendLine($"{indent}    {pictureImageFilter.Picture.CullRect.ToSKRect()});");
                    return;
                }
            case PointLitDiffuseImageFilter pointLitDiffuseImageFilter:
                {
                    var counterImageFilterInput = ++counter.ImageFilter;
                    if (pointLitDiffuseImageFilter.Input is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilterInput} = default(SKImageFilter);");
                    }
                    else
                    {
                        pointLitDiffuseImageFilter.Input.ToSKImageFilter(counter, sb, indent);
                    }

                    sb.Append($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = ");
                    sb.AppendLine($"SKImageFilter.CreatePointLitDiffuse(");
                    sb.AppendLine($"{indent}    {pointLitDiffuseImageFilter.Location.ToSKPoint3()},");
                    sb.AppendLine($"{indent}    {pointLitDiffuseImageFilter.LightColor.ToSKColor()},");
                    sb.AppendLine($"{indent}    {pointLitDiffuseImageFilter.SurfaceScale.ToFloatString()},");
                    sb.AppendLine($"{indent}    {pointLitDiffuseImageFilter.Kd.ToFloatString()},");
                    sb.AppendLine($"{indent}    {counter.ImageFilterVarName}{counterImageFilterInput},");
                    sb.AppendLine($"{indent}    {pointLitDiffuseImageFilter.Clip?.ToSKRect() ?? "null"});");
                    return;
                }
            case PointLitSpecularImageFilter pointLitSpecularImageFilter:
                {
                    var counterImageFilterInput = ++counter.ImageFilter;
                    if (pointLitSpecularImageFilter.Input is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilterInput} = default(SKImageFilter);");
                    }
                    else
                    {
                        pointLitSpecularImageFilter.Input.ToSKImageFilter(counter, sb, indent);
                    }

                    sb.Append($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = ");
                    sb.AppendLine($"SKImageFilter.CreatePointLitSpecular(");
                    sb.AppendLine($"{indent}    {pointLitSpecularImageFilter.Location.ToSKPoint3()},");
                    sb.AppendLine($"{indent}    {pointLitSpecularImageFilter.LightColor.ToSKColor()},");
                    sb.AppendLine($"{indent}    {pointLitSpecularImageFilter.SurfaceScale.ToFloatString()},");
                    sb.AppendLine($"{indent}    {pointLitSpecularImageFilter.Ks.ToFloatString()},");
                    sb.AppendLine($"{indent}    {pointLitSpecularImageFilter.Shininess.ToFloatString()},");
                    sb.AppendLine($"{indent}    {counter.ImageFilterVarName}{counterImageFilterInput},");
                    sb.AppendLine($"{indent}    {pointLitSpecularImageFilter.Clip?.ToSKRect() ?? "null"});");
                    return;
                }
            case SpotLitDiffuseImageFilter spotLitDiffuseImageFilter:
                {
                    var counterImageFilterInput = ++counter.ImageFilter;
                    if (spotLitDiffuseImageFilter.Input is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilterInput} = default(SKImageFilter);");
                    }
                    else
                    {
                        spotLitDiffuseImageFilter.Input.ToSKImageFilter(counter, sb, indent);
                    }

                    sb.Append($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = ");
                    sb.AppendLine($"SKImageFilter.CreateSpotLitDiffuse(");
                    sb.AppendLine($"{indent}    {spotLitDiffuseImageFilter.Location.ToSKPoint3()},");
                    sb.AppendLine($"{indent}    {spotLitDiffuseImageFilter.Target.ToSKPoint3()},");
                    sb.AppendLine($"{indent}    {spotLitDiffuseImageFilter.SpecularExponent.ToFloatString()},");
                    sb.AppendLine($"{indent}    {spotLitDiffuseImageFilter.CutoffAngle.ToFloatString()},");
                    sb.AppendLine($"{indent}    {spotLitDiffuseImageFilter.LightColor.ToSKColor()},");
                    sb.AppendLine($"{indent}    {spotLitDiffuseImageFilter.SurfaceScale.ToFloatString()},");
                    sb.AppendLine($"{indent}    {spotLitDiffuseImageFilter.Kd.ToFloatString()},");
                    sb.AppendLine($"{indent}    {counter.ImageFilterVarName}{counterImageFilterInput},");
                    sb.AppendLine($"{indent}    {spotLitDiffuseImageFilter.Clip?.ToSKRect() ?? "null"});");
                    return;
                }
            case SpotLitSpecularImageFilter spotLitSpecularImageFilter:
                {
                    var counterImageFilterInput = ++counter.ImageFilter;
                    if (spotLitSpecularImageFilter.Input is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilterInput} = default(SKImageFilter);");
                    }
                    else
                    {
                        spotLitSpecularImageFilter.Input.ToSKImageFilter(counter, sb, indent);
                    }

                    sb.Append($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = ");
                    sb.AppendLine($"SKImageFilter.CreateSpotLitSpecular(");
                    sb.AppendLine($"{indent}    {spotLitSpecularImageFilter.Location.ToSKPoint3()},");
                    sb.AppendLine($"{indent}    {spotLitSpecularImageFilter.Target.ToSKPoint3()},");
                    sb.AppendLine($"{indent}    {spotLitSpecularImageFilter.SpecularExponent.ToFloatString()},");
                    sb.AppendLine($"{indent}    {spotLitSpecularImageFilter.CutoffAngle.ToFloatString()},");
                    sb.AppendLine($"{indent}    {spotLitSpecularImageFilter.LightColor.ToSKColor()},");
                    sb.AppendLine($"{indent}    {spotLitSpecularImageFilter.SurfaceScale.ToFloatString()},");
                    sb.AppendLine($"{indent}    {spotLitSpecularImageFilter.Ks.ToFloatString()},");
                    sb.AppendLine($"{indent}    {spotLitSpecularImageFilter.Shininess.ToFloatString()},");
                    sb.AppendLine($"{indent}    {counter.ImageFilterVarName}{counterImageFilterInput},");
                    sb.AppendLine($"{indent}    {spotLitSpecularImageFilter.Clip?.ToSKRect() ?? "null"});");
                    return;
                }
            case TileImageFilter tileImageFilter:
                {
                    var counterImageFilterInput = ++counter.ImageFilter;
                    if (tileImageFilter.Input is null)
                    {
                        sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilterInput} = default(SKImageFilter);");
                    }
                    else
                    {
                        tileImageFilter.Input.ToSKImageFilter(counter, sb, indent);
                    }

                    sb.Append($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = ");
                    sb.AppendLine($"SKImageFilter.CreateTile(");
                    sb.AppendLine($"{indent}    {tileImageFilter.Src.ToSKRect()},");
                    sb.AppendLine($"{indent}    {tileImageFilter.Dst.ToSKRect()},");
                    sb.AppendLine($"{indent}    {counter.ImageFilterVarName}{counterImageFilterInput});");
                    return;
                }
            default:
                {
                    sb.AppendLine($"{indent}var {counter.ImageFilterVarName}{counterImageFilter} = default(SKImageFilter);");
                    return;
                }
        }
    }

    public static void ToSKPathEffect(this SKPathEffect? pathEffect, SkiaCSharpCodeGenCounter counter, StringBuilder sb, string indent)
    {
        var counterPathEffect = counter.PathEffect;

        switch (pathEffect)
        {
            case DashPathEffect dashPathEffect:
                {
                    if (dashPathEffect.Intervals is null)
                    {
                        sb.AppendLine($"{indent}var {counter.PathEffectVarName}{counterPathEffect} = default(SKPathEffect);");
                        return;
                    }

                    if (dashPathEffect.PhaseExpression is { } || dashPathEffect.IntervalExpressions is { })
                    {
                        // Through the helper rather than CreateDash, because what the dash binds to
                        // is not known here and the renderer answers a nonsense one: a phase with
                        // nought, an interval with a solid stroke.
                        var intervals = new StringBuilder("new float[] { ");

                        for (var i = 0; i < dashPathEffect.Intervals.Length; i++)
                        {
                            intervals.Append(dashPathEffect.IntervalExpressions?[i] is { } length
                                ? SymCSharpEmitter.Emit(length, ExprType.Number)
                                : dashPathEffect.Intervals[i].ToFloatString()).Append(", ");
                        }

                        var phase = dashPathEffect.PhaseExpression is { } shift
                            ? SymCSharpEmitter.Emit(shift, ExprType.Number)
                            : dashPathEffect.Phase.ToFloatString();

                        sb.Append($"{indent}var {counter.PathEffectVarName}{counterPathEffect} = ");
                        sb.AppendLine($"{ExprHelpers.Dash}(");
                        sb.AppendLine($"{indent}    {intervals}}},");
                        sb.AppendLine($"{indent}    {phase});");

                        return;
                    }

                    sb.Append($"{indent}var {counter.PathEffectVarName}{counterPathEffect} = ");
                    sb.AppendLine($"SKPathEffect.CreateDash(");
                    sb.AppendLine($"{indent}    {dashPathEffect.Intervals.ToFloatArray()},");
                    sb.AppendLine($"{indent}    {dashPathEffect.Phase.ToFloatString()});");
                    return;
                }
            default:
                {
                    sb.AppendLine($"{indent}var {counter.PathEffectVarName}{counterPathEffect} = default(SKPathEffect);");
                    return;
                }
        }
    }

    public static string ToSKBlendMode(this SKBlendMode blendMode)
    {
        switch (blendMode)
        {
            default:
            case SKBlendMode.Clear:
                return "SKBlendMode.Clear";
            case SKBlendMode.Src:
                return "SKBlendMode.Src";
            case SKBlendMode.Dst:
                return "SKBlendMode.Dst";
            case SKBlendMode.SrcOver:
                return "SKBlendMode.SrcOver";
            case SKBlendMode.DstOver:
                return "SKBlendMode.DstOver";
            case SKBlendMode.SrcIn:
                return "SKBlendMode.SrcIn";
            case SKBlendMode.DstIn:
                return "SKBlendMode.DstIn";
            case SKBlendMode.SrcOut:
                return "SKBlendMode.SrcOut";
            case SKBlendMode.DstOut:
                return "SKBlendMode.DstOut";
            case SKBlendMode.SrcATop:
                return "SKBlendMode.SrcATop";
            case SKBlendMode.DstATop:
                return "SKBlendMode.DstATop";
            case SKBlendMode.Xor:
                return "SKBlendMode.Xor";
            case SKBlendMode.Plus:
                return "SKBlendMode.Plus";
            case SKBlendMode.Modulate:
                return "SKBlendMode.Modulate";
            case SKBlendMode.Screen:
                return "SKBlendMode.Screen";
            case SKBlendMode.Overlay:
                return "SKBlendMode.Overlay";
            case SKBlendMode.Darken:
                return "SKBlendMode.Darken";
            case SKBlendMode.Lighten:
                return "SKBlendMode.Lighten";
            case SKBlendMode.ColorDodge:
                return "SKBlendMode.ColorDodge";
            case SKBlendMode.ColorBurn:
                return "SKBlendMode.ColorBurn";
            case SKBlendMode.HardLight:
                return "SKBlendMode.HardLight";
            case SKBlendMode.SoftLight:
                return "SKBlendMode.SoftLight";
            case SKBlendMode.Difference:
                return "SKBlendMode.Difference";
            case SKBlendMode.Exclusion:
                return "SKBlendMode.Exclusion";
            case SKBlendMode.Multiply:
                return "SKBlendMode.Multiply";
            case SKBlendMode.Hue:
                return "SKBlendMode.Hue";
            case SKBlendMode.Saturation:
                return "SKBlendMode.Saturation";
            case SKBlendMode.Color:
                return "SKBlendMode.Color";
            case SKBlendMode.Luminosity:
                return "SKBlendMode.Luminosity";
        }
    }

    public static string ToSKSamplingOptions(this SKFilterQuality filterQuality)
    {
        switch (filterQuality)
        {
            default:
            case SKFilterQuality.None:
                return "new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None)";
            case SKFilterQuality.Low:
                return "new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)";
            case SKFilterQuality.Medium:
                return "new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)";
            case SKFilterQuality.High:
                return "new SKSamplingOptions(SKCubicResampler.Mitchell)";
        }
    }

    public static string ToSKFilterMode(this SKFilterMode filterMode)
    {
        switch (filterMode)
        {
            default:
            case SKFilterMode.Nearest:
                return "SKFilterMode.Nearest";
            case SKFilterMode.Linear:
                return "SKFilterMode.Linear";
        }
    }

    public static string ToSKMipmapMode(this SKMipmapMode mipmapMode)
    {
        switch (mipmapMode)
        {
            default:
            case SKMipmapMode.None:
                return "SKMipmapMode.None";
            case SKMipmapMode.Nearest:
                return "SKMipmapMode.Nearest";
            case SKMipmapMode.Linear:
                return "SKMipmapMode.Linear";
        }
    }

    public static string ToSKCubicResampler(this SKCubicResampler cubic)
    {
        if (cubic.Equals(SKCubicResampler.Mitchell))
        {
            return "SKCubicResampler.Mitchell";
        }

        if (cubic.Equals(SKCubicResampler.CatmullRom))
        {
            return "SKCubicResampler.CatmullRom";
        }

        return $"new SKCubicResampler({cubic.B.ToFloatString()}, {cubic.C.ToFloatString()})";
    }

    public static string ToSKSamplingOptions(this SKSamplingOptions samplingOptions)
    {
        return samplingOptions.UseCubic
            ? $"new SKSamplingOptions({samplingOptions.Cubic.ToSKCubicResampler()})"
            : $"new SKSamplingOptions({samplingOptions.Filter.ToSKFilterMode()}, {samplingOptions.Mipmap.ToSKMipmapMode()})";
    }

    public static void ToSKPaint(this SKPaint paint, SkiaCSharpCodeGenCounter counter, StringBuilder sb, string indent)
        => ToSKPaint(paint, counter, sb, indent, fade: null);

    /// <param name="fade">The colours of the layers folded into this paint, whose alpha multiplies its own.</param>
    /// <param name="shared">Where a paint that follows an argument and holds no shader or filter is set up instead of built.</param>
    private static void ToSKPaint(SKPaint paint, SkiaCSharpCodeGenCounter counter, StringBuilder sb, string indent, IReadOnlyList<SKColor>? fade, CanvasState? shared = null)
    {
        if (counter.Statics is { })
        {
            var name = $"{counter.PaintVarName}{counter.Paint}";
            Action<StringBuilder>? local = shared?.Paint is { } && paint is { Shader: null, ColorFilter: null, ImageFilter: null, PathEffect: null }
                ? text => shared.Assign(name, Settings(paint, fade), text, indent)
                : null;

            // Inside the hoisted text, so a constant fade is applied once and a driven one keeps it local.
            Hoist(counter, sb, indent, "SKPaint", name, text => ToSKPaint(paint, counter, text, indent, fade), local: local);
            return;
        }

        var counterPaint = counter.Paint;

        sb.AppendLine($"{indent}var {counter.PaintVarName}{counterPaint} = new SKPaint();");

        foreach (var (property, value) in Settings(paint, fade))
        {
            sb.AppendLine($"{indent}{Assignment($"{counter.PaintVarName}{counterPaint}", property, value)}");
        }

        if (paint.Shader is { })
        {
            var counterShader = ++counter.Shader;
            paint.Shader.ToSKShader(counter, sb, indent);
            sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}.Shader = {counter.ShaderVarName}{counterShader};");
        }

        if (paint.ColorFilter is { })
        {
            var counterColorFilter = ++counter.ColorFilter;
            paint.ColorFilter.ToSKColorFilter(counter, sb, indent);
            sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}.ColorFilter = {counter.ColorFilterVarName}{counterColorFilter};");
        }

        if (paint.ImageFilter is { })
        {
            var counterImageFilter = ++counter.ImageFilter;
            paint.ImageFilter.ToSKImageFilter(counter, sb, indent);
            sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}.ImageFilter = {counter.ImageFilterVarName}{counterImageFilter};");
        }

        if (paint.PathEffect is { })
        {
            var counterPathEffect = ++counter.PathEffect;
            paint.PathEffect.ToSKPathEffect(counter, sb, indent);
            sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}.PathEffect = {counter.PathEffectVarName}{counterPathEffect};");
        }
    }

    /// <summary>
    /// What a paint sets beyond a new one's defaults, in the order it is set: each property's name and
    /// the C# of its value. <c>ColorF</c> stands for the folded layers' alpha, multiplied into the colour.
    /// </summary>
    private static List<(string Property, string Value)> Settings(SKPaint paint, IReadOnlyList<SKColor>? fade)
    {
        var settings = new List<(string, string)>();

        if (paint.Style != SKPaintStyle.Fill)
        {
            settings.Add(("Style", paint.Style.ToSKPaintStyle()));
        }

        if (paint.IsAntialias)
        {
            settings.Add(("IsAntialias", paint.IsAntialias.ToBoolString()));
        }

        if (paint.StrokeWidthExpression is { } strokeWidth)
        {
            settings.Add(("StrokeWidth", SymCSharpEmitter.Emit(strokeWidth, ExprType.Number)));
        }
        else if (paint.StrokeWidth != 0f)
        {
            settings.Add(("StrokeWidth", paint.StrokeWidth.ToFloatString()));
        }

        if (paint.StrokeCap != SKStrokeCap.Butt)
        {
            settings.Add(("StrokeCap", paint.StrokeCap.ToSKStrokeCap()));
        }

        if (paint.StrokeJoin != SKStrokeJoin.Miter)
        {
            settings.Add(("StrokeJoin", paint.StrokeJoin.ToSKStrokeJoin()));
        }

        if (paint.StrokeMiter != 4f)
        {
            settings.Add(("StrokeMiter", paint.StrokeMiter.ToFloatString()));
        }

        // Opaque black is the default only as a constant: a driven fill-opacity of a black shape
        // has that as its placeholder, with the expression still to set.
        if (paint.Color is { } color and not { Expression: null, Alpha: 255, Red: 0, Green: 0, Blue: 0 })
        {
            settings.Add(("Color", ToSKColor(color)));
        }

        // In the float arithmetic SkiaModel applies to the same layers, so the two draw alike.
        var factors = fade?
            .Where(color => color is not { Expression: null, Alpha: 255 })
            .Select(color => color.Expression is null
                ? $"({color.Alpha} * (1 / 255f))"
                : $"(({ToSKColor(color)}).Alpha * (1 / 255f))")
            .ToList();
        if (factors is { Count: > 0 })
        {
            settings.Add(("ColorF", string.Join(" * ", factors)));
        }

        if (paint.BlendMode != SKBlendMode.SrcOver)
        {
            settings.Add(("BlendMode", paint.BlendMode.ToSKBlendMode()));
        }

        return settings;
    }

    /// <summary>A paint's defaults, as <see cref="Settings"/> would write each one.</summary>
    private static readonly (string Property, string Value)[] s_paintDefaults =
    {
        ("Style", SKPaintStyle.Fill.ToSKPaintStyle()),
        ("IsAntialias", false.ToBoolString()),
        ("StrokeWidth", 0f.ToFloatString()),
        ("StrokeCap", SKStrokeCap.Butt.ToSKStrokeCap()),
        ("StrokeJoin", SKStrokeJoin.Miter.ToSKStrokeJoin()),
        ("StrokeMiter", 4f.ToFloatString()),
        ("Color", "SKColors.Black"),
        ("ColorF", ""),
        ("BlendMode", SKBlendMode.SrcOver.ToSKBlendMode())
    };

    private static string Assignment(string paint, string property, string value)
        => property == "ColorF"
            ? $"{paint}.ColorF = {paint}.ColorF.WithAlpha({paint}.ColorF.Alpha * ({value}));"
            : $"{paint}.{property} = {value};";

    /// <summary>
    /// What the code emitted so far leaves on the canvas and in the shared paint, as far as generation
    /// can know, set against what the model asks of each draw.
    /// </summary>
    /// <remarks>
    /// Every matrix the model sets is its whole total, so a save that scopes nothing but a matrix need
    /// not reach the canvas: the next draw sets what it is drawn under. Skia records each save a matrix
    /// changes under: HueBulbBulb's nine strokes record 28 operations so, and recorded 45.
    /// </remarks>
    private sealed class CanvasState
    {
        // What the canvas holds where a range that may not have run changed it.
        private static readonly Placed s_unknown = new("?", Symbolic: true, default);

        private readonly string _canvas;
        private readonly int _counterCanvas;
        private readonly bool _direct;
        private readonly Stack<Placed?> _saved = new();
        private readonly Stack<Placed?> _kept = new();
        private Placed? _matrix;
        private Placed? _holds;

        /// <summary>
        /// Each setting the shared paint holds beyond its defaults; null when a range that may not have
        /// run changed it, and the next shape resets it. Replaced, never changed, so a mark can share it.
        /// </summary>
        private Dictionary<string, string>? _settings = new(StringComparer.Ordinal);

        /// <param name="paint">The shared paint's local, or null where every paint is built for itself.</param>
        /// <param name="direct">Whether the canvas is the caller's, whose matrix every one set is relative to.</param>
        public CanvasState(string? paint, string canvas, int counterCanvas, bool direct)
        {
            Paint = paint;
            _canvas = canvas;
            _counterCanvas = counterCanvas;
            _direct = direct;
        }

        public string? Paint { get; }

        public bool PaintUsed { get; private set; }

        /// <summary>Whether anything emitted reads the caller's matrix.</summary>
        public bool BaseUsed { get; set; }

        /// <summary>Whether anything emitted changes the canvas, which the caller then needs back.</summary>
        public bool Touched { get; set; }

        /// <summary>
        /// Set where the ranges do not nest with the saves, so what a draw is drawn under depends on which
        /// ran: every matrix is then set where the model sets it, as it always was.
        /// </summary>
        public bool Eager { get; set; }

        public void Save(bool kept)
        {
            _saved.Push(_matrix);
            if (kept)
            {
                _kept.Push(_holds);
                Touched = true;
            }
        }

        public void Restore(bool kept)
        {
            if (_saved.Count > 0)
            {
                _matrix = _saved.Pop();
            }

            if (kept && _kept.Count > 0)
            {
                _holds = _kept.Pop();
            }
        }

        public void SetMatrix(SetMatrixCanvasCommand command, SkiaCSharpCodeGenCounter counter, StringBuilder sb, string indent)
        {
            _matrix = command.SymbolicTotal is { } symbolic
                ? new Placed(symbolic.ToSKMatrix(), Symbolic: true, command.TotalMatrix)
                : command.TotalMatrix.IsIdentity ? null : new Placed(command.TotalMatrix.ToSKMatrix(), Symbolic: false, command.TotalMatrix);

            if (Eager)
            {
                Sync(counter, sb, indent, force: true);
            }
        }

        /// <summary>The translation the model has set, where that is all it has.</summary>
        public SKPoint? Translation => _matrix is { IsTranslation: true, Total: var total } ? new SKPoint(total.TransX, total.TransY) : null;

        /// <summary>
        /// Puts the canvas under the matrix the model has set, unless it already is; under the caller's
        /// alone for a shape <paramref name="baked"/> into its coordinates.
        /// </summary>
        public void Sync(SkiaCSharpCodeGenCounter counter, StringBuilder sb, string indent, bool force = false, bool baked = false)
        {
            var target = baked ? null : _matrix;
            if (!force && (Eager || Equals(_holds, target)))
            {
                return;
            }

            if (!_direct)
            {
                SetAbsolute(target, counter, sb, indent);
            }
            else
            {
                // Composed by the canvas, as a picture's playback composes it: the same product taken in
                // managed code differs from it in the last bits. Translate is that product too.
                if (force || _holds is { })
                {
                    sb.AppendLine($"{indent}{_canvas}.SetMatrix(in skBase{_counterCanvas});");
                    BaseUsed = true;
                }

                if (target is { IsTranslation: true, Total: var total })
                {
                    sb.AppendLine($"{indent}{_canvas}.Translate({total.TransX.ToFloatString()}, {total.TransY.ToFloatString()});");
                }
                else if (target is { })
                {
                    sb.AppendLine($"{indent}{_canvas}.Concat(in {Local(target, counter, sb, indent)});");
                }
            }

            _holds = target;
            Touched = true;
        }

        private void SetAbsolute(Placed? matrix, SkiaCSharpCodeGenCounter counter, StringBuilder sb, string indent)
            => sb.AppendLine(matrix is null
                ? $"{indent}{_canvas}.ResetMatrix();"
                : $"{indent}{_canvas}.SetMatrix(in {Local(matrix, counter, sb, indent)});");

        // SetMatrix(SKMatrix) is obsolete as an *error* in SkiaSharp 4, so passing the matrix by value
        // does not merely warn: it fails to compile. The in overloads exist in 3 as well, and need a
        // local because an expression would be ambiguous between the two.
        private static string Local(Placed matrix, SkiaCSharpCodeGenCounter counter, StringBuilder sb, string indent)
        {
            var name = $"{counter.MatrixVarName}{++counter.Matrix}";

            // The recorder composed the symbolic total off the same save/restore stack as the baked
            // one, so the functions printed here are the whole matrix and nothing tracks a running local.
            sb.AppendLine($"{indent}var {name} = {matrix.Code.Replace("\n", "\n" + indent)};");
            return name;
        }

        /// <summary>The shared paint, as <paramref name="name"/>, set to <paramref name="settings"/> by what it does not already hold.</summary>
        public void Assign(string name, List<(string Property, string Value)> settings, StringBuilder sb, string indent)
        {
            sb.AppendLine($"{indent}var {name} = {Paint};");

            var held = _settings;
            if (held is null)
            {
                sb.AppendLine($"{indent}{name}.Reset();");
                held = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var wanted = settings.ToDictionary(setting => setting.Property, setting => setting.Value, StringComparer.Ordinal);

            // The fade multiplies whatever colour the paint holds, so either changing sets both again.
            var recolor = !Holds(held, wanted, "Color") || !Holds(held, wanted, "ColorF");

            foreach (var (property, fallback) in s_paintDefaults)
            {
                var value = wanted.TryGetValue(property, out var set) ? set : null;
                var skip = property switch
                {
                    "Color" => !recolor,
                    "ColorF" => !recolor || value is null,
                    _ => Holds(held, wanted, property)
                };

                if (!skip)
                {
                    sb.AppendLine($"{indent}{Assignment(name, property, value ?? fallback)}");
                }
            }

            _settings = wanted;
            PaintUsed = true;
        }

        private static bool Holds(Dictionary<string, string> held, Dictionary<string, string> wanted, string property)
            => held.TryGetValue(property, out var was) == wanted.TryGetValue(property, out var value) && was == value;

        public Mark Marked() => new(_settings, PaintUsed, _holds, BaseUsed, Touched);

        /// <summary>After a range that may or may not have run: what holds either way.</summary>
        public void Join(Mark before)
        {
            if (_settings is null || before.Settings is null || _settings.Count != before.Settings.Count || _settings.Any(setting => !before.Settings.TryGetValue(setting.Key, out var value) || value != setting.Value))
            {
                _settings = null;
            }

            if (!Equals(_holds, before.Holds))
            {
                _holds = s_unknown;
            }
        }

        /// <summary>After a range that never runs: nothing it emitted happened.</summary>
        public void Restore(Mark before)
        {
            _settings = before.Settings;
            PaintUsed = before.PaintUsed;
            _holds = before.Holds;
            BaseUsed = before.BaseUsed;
            Touched = before.Touched;
        }

        /// <summary>What a range is weighed against once it ends.</summary>
        public sealed record Mark(Dictionary<string, string>? Settings, bool PaintUsed, Placed? Holds, bool BaseUsed, bool Touched);
    }

    /// <summary>A matrix the model sets, as the C# that builds it, relative to the caller's.</summary>
    private sealed record Placed(string Code, bool Symbolic, SKMatrix Total)
    {
        public bool IsTranslation => !Symbolic && Total is { ScaleX: 1f, SkewX: 0f, SkewY: 0f, ScaleY: 1f, Persp0: 0f, Persp1: 0f, Persp2: 1f };
    }

    /// <summary>
    /// Which saves, with the restores that match them, have to reach the canvas: a save that scopes a
    /// clip, and every layer. Null where a range does not nest with the saves, or sets a matrix that
    /// outlives it, and which draw is under what depends on which ranges ran.
    /// </summary>
    private static bool[]? KeptSaves(IList<CanvasCommand> commands, CommandPlan plan)
    {
        var kept = new bool[commands.Count];
        var open = new Stack<int>();
        var ranges = new Stack<int>();

        for (var index = 0; index < commands.Count; index++)
        {
            switch (commands[index])
            {
                case SaveCanvasCommand:
                    open.Push(index);
                    break;
                case SaveLayerCanvasCommand:
                    kept[index] = true;
                    open.Push(index);
                    break;
                case RestoreCanvasCommand when open.Count == 0:
                    return null;
                case RestoreCanvasCommand:
                    kept[index] = kept[open.Pop()];
                    break;
                case ClipPathCanvasCommand or ClipRectCanvasCommand when !plan.Skips(index) && open.Count > 0:
                    kept[open.Peek()] = true;
                    break;
                case SetMatrixCanvasCommand when ranges.Count > 0 && ranges.Peek() == open.Count:
                    return null;
                case BeginConditionalCanvasCommand:
                    ranges.Push(open.Count);
                    break;
                case EndConditionalCanvasCommand when ranges.Count == 0 || ranges.Pop() != open.Count:
                    return null;
            }
        }

        // A save left open is kept, and with it whatever it scopes past the end of the list.
        foreach (var index in open)
        {
            kept[index] = true;
        }

        return kept;
    }

    public static void ToSKFont(this SKPaint paint, SkiaCSharpCodeGenCounter counter, StringBuilder sb, string indent)
    {
        var counterFont = counter.Font;
        var typefaceExpression = "SKTypeface.Default";

        if (paint.Typeface is { })
        {
            var counterTypeface = ++counter.Typeface;
            paint.Typeface?.ToSKTypeface(counter, sb, indent, paint.RequestedTypeface);
            sb.AppendLine($"{indent}if ({counter.TypefaceVarName}{counterTypeface} is null)");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    {counter.TypefaceVarName}{counterTypeface} = SKTypeface.Default;");
            sb.AppendLine($"{indent}}}");
            typefaceExpression = $"{counter.TypefaceVarName}{counterTypeface}";
        }

        sb.AppendLine($"{indent}var {counter.FontVarName}{counterFont} = new SKFont({typefaceExpression}, {paint.TextSize.ToFloatString()});");
        LinearMetrics(counter, sb, indent, counterFont);

        if (!paint.IsAntialias)
        {
            sb.AppendLine($"{indent}{counter.FontVarName}{counterFont}.Edging = SKFontEdging.Alias;");
        }
        else if (paint.LcdRenderText != false)
        {
            sb.AppendLine($"{indent}{counter.FontVarName}{counterFont}.Edging = SKFontEdging.SubpixelAntialias;");
        }

        if (paint.SubpixelText != false)
        {
            sb.AppendLine($"{indent}{counter.FontVarName}{counterFont}.Subpixel = {paint.SubpixelText.ToBoolString()};");
        }
    }

    /// <param name="requested">The typeface the paint drawn with it asked for, which a font does not carry.</param>
    /// <remarks>
    /// The runtime shapes text through HarfBuzz, which places glyphs on unhinted advances; DrawText
    /// takes FreeType's hinted ones on Linux, measured 0.034 RMS apart there and identical on macOS
    /// and Windows.
    /// </remarks>
    private static void LinearMetrics(SkiaCSharpCodeGenCounter counter, StringBuilder sb, string indent, int counterFont)
        => sb.AppendLine($"{indent}{counter.FontVarName}{counterFont}.LinearMetrics = true;");

    public static void ToSKFont(this SKFont font, SkiaCSharpCodeGenCounter counter, StringBuilder sb, string indent, SKTypeface? requested = null)
    {
        var counterFont = counter.Font;
        var typefaceExpression = "SKTypeface.Default";

        if (font.Typeface is { })
        {
            var counterTypeface = ++counter.Typeface;
            font.Typeface?.ToSKTypeface(counter, sb, indent, requested);
            sb.AppendLine($"{indent}if ({counter.TypefaceVarName}{counterTypeface} is null)");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    {counter.TypefaceVarName}{counterTypeface} = SKTypeface.Default;");
            sb.AppendLine($"{indent}}}");
            typefaceExpression = $"{counter.TypefaceVarName}{counterTypeface}";
        }

        sb.AppendLine($"{indent}var {counter.FontVarName}{counterFont} = new SKFont({typefaceExpression}, {font.Size.ToFloatString()}, {font.ScaleX.ToFloatString()}, {font.SkewX.ToFloatString()});");
        LinearMetrics(counter, sb, indent, counterFont);

        if (font.Edging != SKFontEdging.Antialias)
        {
            sb.AppendLine($"{indent}{counter.FontVarName}{counterFont}.Edging = {font.Edging.ToSKFontEdging()};");
        }

        if (font.Subpixel != false)
        {
            sb.AppendLine($"{indent}{counter.FontVarName}{counterFont}.Subpixel = {font.Subpixel.ToBoolString()};");
        }

        if (font.Embolden != false)
        {
            sb.AppendLine($"{indent}{counter.FontVarName}{counterFont}.Embolden = {font.Embolden.ToBoolString()};");
        }
    }

    public static string ToSKClipOperation(this SKClipOperation clipOperation)
    {
        switch (clipOperation)
        {
            default:
            case SKClipOperation.Difference:
                return "SKClipOperation.Difference";
            case SKClipOperation.Intersect:
                return "SKClipOperation.Intersect";
        }
    }

    public static string ToSKPathFillType(this SKPathFillType pathFillType)
    {
        switch (pathFillType)
        {
            default:
            case SKPathFillType.Winding:
                return "SKPathFillType.Winding";
            case SKPathFillType.EvenOdd:
                return "SKPathFillType.EvenOdd";
        }
    }

    public static string ToSKPathArcSize(this SKPathArcSize pathArcSize)
    {
        switch (pathArcSize)
        {
            default:
            case SKPathArcSize.Small:
                return "SKPathArcSize.Small";
            case SKPathArcSize.Large:
                return "SKPathArcSize.Large";
        }
    }

    public static string ToSKPathDirection(this SKPathDirection pathDirection)
    {
        switch (pathDirection)
        {
            default:
            case SKPathDirection.Clockwise:
                return "SKPathDirection.Clockwise";
            case SKPathDirection.CounterClockwise:
                return "SKPathDirection.CounterClockwise";
        }
    }

    public static string ToSKPathOp(this SKPathOp pathOp)
    {
        switch (pathOp)
        {
            default:
            case SKPathOp.Difference:
                return "SKPathOp.Difference";
            case SKPathOp.Intersect:
                return "SKPathOp.Intersect";
            case SKPathOp.Union:
                return "SKPathOp.Union";
            case SKPathOp.Xor:
                return "SKPathOp.Xor";
            case SKPathOp.ReverseDifference:
                return "SKPathOp.ReverseDifference";
        }
    }

    public static void ToSKPath(this SKPath path, SkiaCSharpCodeGenCounter counter, StringBuilder sb, string indent)
    {
        if (counter.Statics is { })
        {
            Hoist(counter, sb, indent, "SKPath", $"{counter.PathVarName}{counter.Path}", text => path.ToSKPath(counter, text, indent));
            return;
        }

        var counterPath = counter.Path;

        // SkiaSharp 4 obsoleted every mutating method on SKPath in favour of SKPathBuilder, which
        // does not exist in 3 at all. The commands themselves are identical; only what they are
        // called on differs, and whether a path has to be detached at the end.
        var detached = counter.SkiaSharpTarget != SkiaSharpTarget.V3;

        var builder = detached
            ? $"{counter.PathBuilderVarName}{counterPath}"
            : $"{counter.PathVarName}{counterPath}";

        sb.AppendLine($"{indent}var {builder} = new {(detached ? "SKPathBuilder" : "SKPath")}();");
        if (path.FillType != SKPathFillType.Winding)
        {
            // Carried through Detach, so it belongs on whichever object is being built.
            sb.AppendLine($"{indent}{builder}.FillType = {path.FillType.ToSKPathFillType()};");
        }

        var run = new List<PathCommand>();
        foreach (var pathCommand in path.Commands ?? Enumerable.Empty<PathCommand>())
        {
            if (pathCommand is MoveToPathCommand or LineToPathCommand or QuadToPathCommand or CubicToPathCommand or ArcToPathCommand or ClosePathCommand or AddPolyPathCommand { Points: { } })
            {
                run.Add(pathCommand);
                continue;
            }

            PathRun(run, builder, counter.SkiaSharpTarget, sb, indent);
            run.Clear();

            if (PathCall(pathCommand) is { } call)
            {
                sb.AppendLine($"{indent}{builder}.{call};");
            }
        }

        PathRun(run, builder, counter.SkiaSharpTarget, sb, indent);

        if (detached)
        {
            sb.AppendLine($"{indent}var {counter.PathVarName}{counterPath} = {builder}.Detach();");
            sb.AppendLine($"{indent}{builder}?.Dispose();");
        }
    }

    /// <summary>The call that adds <paramref name="command"/> to a path or a builder, or null for one that adds nothing.</summary>
    private static string? PathCall(PathCommand command) => command switch
    {
        MoveToPathCommand move => $"MoveTo({move.X.ToFloatString()}, {move.Y.ToFloatString()})",
        LineToPathCommand line => $"LineTo({line.X.ToFloatString()}, {line.Y.ToFloatString()})",
        ArcToPathCommand arc => $"ArcTo({arc.Rx.ToFloatString()}, {arc.Ry.ToFloatString()}, {arc.XAxisRotate.ToFloatString()}, {arc.LargeArc.ToSKPathArcSize()}, {arc.Sweep.ToSKPathDirection()}, {arc.X.ToFloatString()}, {arc.Y.ToFloatString()})",
        QuadToPathCommand quad => $"QuadTo({Floats(quad.X0, quad.Y0, quad.X1, quad.Y1)})",
        CubicToPathCommand cubic => $"CubicTo({Floats(cubic.X0, cubic.Y0, cubic.X1, cubic.Y1, cubic.X2, cubic.Y2)})",
        ClosePathCommand => "Close()",
        AddRectPathCommand rect => $"AddRect({rect.Rect.ToSKRect()})",
        AddRoundRectPathCommand roundRect => $"AddRoundRect({roundRect.Rect.ToSKRect()}, {roundRect.Rx.ToFloatString()}, {roundRect.Ry.ToFloatString()})",
        AddOvalPathCommand oval => $"AddOval({oval.Rect.ToSKRect()})",
        AddCirclePathCommand circle => $"AddCircle({Floats(circle.X, circle.Y, circle.Radius)})",
        AddPolyPathCommand { Points: { } points } poly => $"AddPoly({points.ToSKPoints()}, {poly.Close.ToBoolString()})",
        _ => null
    };

    // Below this many verbs a run is smaller spelled out as calls than as two arrays and a call.
    // TapHome's icons are smallest at 4 to 6 (2.58 MB), at 2 and at 10 2.62 and 2.61 MB.
    private const int PathDataVerbs = 4;

    /// <summary>
    /// A run of commands onto <paramref name="builder"/>: as verbs and coordinates the path data helper
    /// replays where it is long enough, which a native image keeps as data rather than code.
    /// </summary>
    private static void PathRun(List<PathCommand> run, string builder, SkiaSharpTarget target, StringBuilder sb, string indent)
    {
        var verbs = new List<byte>();
        var coordinates = new List<string>();

        foreach (var command in run)
        {
            switch (command)
            {
                case MoveToPathCommand move:
                    verbs.Add(0);
                    coordinates.Add(Floats(move.X, move.Y));
                    break;
                case LineToPathCommand line:
                    verbs.Add(1);
                    coordinates.Add(Floats(line.X, line.Y));
                    break;
                case QuadToPathCommand quad:
                    verbs.Add(2);
                    coordinates.Add(Floats(quad.X0, quad.Y0, quad.X1, quad.Y1));
                    break;
                case CubicToPathCommand cubic:
                    verbs.Add(3);
                    coordinates.Add(Floats(cubic.X0, cubic.Y0, cubic.X1, cubic.Y1, cubic.X2, cubic.Y2));
                    break;
                case ClosePathCommand:
                    verbs.Add(4);
                    break;
                case ArcToPathCommand arc:
                    verbs.Add((byte)(5 + (int)arc.LargeArc + 2 * (int)arc.Sweep));
                    coordinates.Add(Floats(arc.Rx, arc.Ry, arc.XAxisRotate, arc.X, arc.Y));
                    break;
                case AddPolyPathCommand { Points: { Count: > 0 } points } poly:
                    // What SkPath::addPoly and SkPathBuilder::addPolygon add: a move, a line to each later point, a close.
                    verbs.Add(0);
                    verbs.AddRange(Enumerable.Repeat((byte)1, points.Count - 1));
                    if (poly.Close)
                    {
                        verbs.Add(4);
                    }

                    coordinates.Add(Floats(points.SelectMany(point => new[] { point.X, point.Y }).ToArray()));
                    break;
            }
        }

        if (verbs.Count < PathDataVerbs)
        {
            foreach (var command in run)
            {
                if (PathCall(command) is { } call)
                {
                    sb.AppendLine($"{indent}{builder}.{call};");
                }
            }

            return;
        }

        var helper = target == SkiaSharpTarget.V3 ? ExprHelpers.PathData : ExprHelpers.PathBuilderData;
        sb.AppendLine($"{indent}{helper}({builder}, new byte[] {{ {string.Join(", ", verbs)} }}, new float[]");
        sb.AppendLine($"{indent}{{");
        foreach (var line in coordinates)
        {
            sb.AppendLine($"{indent}    {line},");
        }

        sb.AppendLine($"{indent}}});");
    }

    private static string Floats(params float[] values) => string.Join(", ", values.Select(value => value.ToFloatString()));

    /// <summary><paramref name="path"/> with (<paramref name="dx"/>, <paramref name="dy"/>) added to every coordinate it is built from.</summary>
    private static SKPath Moved(SKPath path, float dx, float dy)
    {
        var moved = new SKPath { FillType = path.FillType };

        foreach (var command in path.Commands ?? Enumerable.Empty<PathCommand>())
        {
            switch (command)
            {
                case MoveToPathCommand move:
                    moved.MoveTo(move.X + dx, move.Y + dy);
                    break;
                case LineToPathCommand line:
                    moved.LineTo(line.X + dx, line.Y + dy);
                    break;
                case QuadToPathCommand quad:
                    moved.QuadTo(quad.X0 + dx, quad.Y0 + dy, quad.X1 + dx, quad.Y1 + dy);
                    break;
                case CubicToPathCommand cubic:
                    moved.CubicTo(cubic.X0 + dx, cubic.Y0 + dy, cubic.X1 + dx, cubic.Y1 + dy, cubic.X2 + dx, cubic.Y2 + dy);
                    break;
                case ArcToPathCommand arc:
                    moved.ArcTo(arc.Rx, arc.Ry, arc.XAxisRotate, arc.LargeArc, arc.Sweep, arc.X + dx, arc.Y + dy);
                    break;
                case ClosePathCommand:
                    moved.Close();
                    break;
                case AddRectPathCommand rect:
                    moved.AddRect(Moved(rect.Rect));
                    break;
                case AddRoundRectPathCommand roundRect:
                    moved.AddRoundRect(Moved(roundRect.Rect), roundRect.Rx, roundRect.Ry);
                    break;
                case AddOvalPathCommand oval:
                    moved.AddOval(Moved(oval.Rect));
                    break;
                case AddCirclePathCommand circle:
                    moved.AddCircle(circle.X + dx, circle.Y + dy, circle.Radius);
                    break;
                case AddPolyPathCommand { Points: { } points } poly:
                    moved.AddPoly(points.Select(point => new SKPoint(point.X + dx, point.Y + dy)).ToArray(), poly.Close);
                    break;
            }
        }

        return moved;

        SKRect Moved(SKRect rect) => new(rect.Left + dx, rect.Top + dy, rect.Right + dx, rect.Bottom + dy);
    }

    public static void ToSKPath(this ClipPath clipPath, SkiaCSharpCodeGenCounter counter, StringBuilder sb, string indent, out bool isDefault)
    {
        // Whole or not at all: the paths a clip is assembled from are transformed in place.
        if (counter.Statics is { })
        {
            var nothing = true;
            Hoist(counter, sb, indent, "SKPath", $"{counter.PathVarName}{counter.Path}", text => clipPath.ToSKPath(counter, text, indent, out nothing), () => !nothing);
            isDefault = nothing;
            return;
        }

        var counterPathResult = counter.Path;
        var isDefaultPathResult = true;

        if (clipPath.Clips is null)
        {
            isDefault = isDefaultPathResult;
            return;
        }

        foreach (var clip in clipPath.Clips)
        {
            var counterPath = ++counter.Path;

            if (clip.Path is null)
            {
                isDefault = true;
                return;
            }

            clip.Path.ToSKPath(counter, sb, indent);

            if (clip.Clip is { })
            {
                var counterPathClip = ++counter.Path;

                clip.Clip.ToSKPath(counter, sb, indent, out var isDefaultPathClip);

                if (!isDefaultPathClip)
                {
                    sb.AppendLine($"{indent}{counter.PathVarName}{counterPath} = {counter.PathVarName}{counterPath}.Op({counter.PathVarName}{counterPathClip}, SKPathOp.Intersect);");
                }
            }

            // SkPath::transform returns at once on the identity, which PaintCode's clips nearly all carry.
            if (clip.Transform is { IsIdentity: false })
            {
                // Transform(SKMatrix) is obsolete in favour of Transform(in SKMatrix). Passing an
                // expression would be ambiguous between the two, so the matrix needs a local.
                var counterMatrix = ++counter.Matrix;
                sb.AppendLine($"{indent}var {counter.MatrixVarName}{counterMatrix} = {clip.Transform.Value.ToSKMatrix()};");
                sb.AppendLine($"{indent}{counter.PathVarName}{counterPath}.Transform(in {counter.MatrixVarName}{counterMatrix});");
            }

            if (isDefaultPathResult)
            {
                sb.AppendLine($"{indent}var {counter.PathVarName}{counterPathResult} = {counter.PathVarName}{counterPath};");
                isDefaultPathResult = false;
            }
            else
            {
                sb.AppendLine($"{indent}{counter.PathVarName}{counterPathResult} = {counter.PathVarName}{counterPathResult}.Op({counter.PathVarName}{counterPath}, SKPathOp.Union);");
            }
        }

        if (clipPath.Clip is { } && clipPath.Clip.Clips is { })
        {
            var counterPathClip = ++counter.Path;

            clipPath.Clip.ToSKPath(counter, sb, indent, out var isDefaultPathClip);

            if (!isDefaultPathClip)
            {
                if (isDefaultPathResult)
                {
                    sb.AppendLine($"{indent}var {counter.PathVarName}{counterPathResult} = {counter.PathVarName}{counterPathClip};");
                    isDefaultPathResult = false;
                }
                else
                {
                    sb.AppendLine($"{indent}{counter.PathVarName}{counterPathResult} = {counter.PathVarName}{counterPathResult}.Op({counter.PathVarName}{counterPathClip}, SKPathOp.Intersect);");
                }
            }
        }

        if (!isDefaultPathResult && clipPath.Transform is { IsIdentity: false })
        {
            var counterMatrix = ++counter.Matrix;
            sb.AppendLine($"{indent}var {counter.MatrixVarName}{counterMatrix} = {clipPath.Transform.Value.ToSKMatrix()};");
            sb.AppendLine($"{indent}{counter.PathVarName}{counterPathResult}.Transform(in {counter.MatrixVarName}{counterMatrix});");
        }

        isDefault = isDefaultPathResult;
    }

    /// <summary>
    /// Emits an object into <see cref="SkiaCSharpCodeGenCounter.Statics"/> and reads it from there,
    /// unless emitting it took something symbolic, which the caller can change from one draw to the next.
    /// </summary>
    private static void Hoist(
        SkiaCSharpCodeGenCounter counter,
        StringBuilder sb,
        string indent,
        string type,
        string name,
        Action<StringBuilder> emit,
        Func<bool>? declared = null,
        Action<StringBuilder>? local = null)
    {
        var statics = counter.Statics!;
        var symbolic = SymCSharpEmitter.Emitted;
        var text = new StringBuilder();

        counter.Statics = null;

        try
        {
            emit(text);
        }
        finally
        {
            counter.Statics = statics;
        }

        if (SymCSharpEmitter.Emitted != symbolic || declared?.Invoke() == false)
        {
            if (local is null)
            {
                sb.Append(text);
                return;
            }

            counter.Borrowed.Add(name);
            local(sb);
            return;
        }

        counter.Borrowed.Add(name);

        // Locals renumbered in the order they first appear, so a copy built under other names matches.
        var locals = new Dictionary<string, int>(StringComparer.Ordinal);
        var key = type + " " + s_local.Replace(text.ToString(), local => "#" + (locals.TryGetValue(local.Value, out var n) ? n : locals[local.Value] = locals.Count));
        if (counter.StaticTexts.TryGetValue(key, out var first))
        {
            sb.AppendLine($"{indent}var {name} = s_{first};");
            return;
        }

        foreach (var line in text.ToString().Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0))
        {
            statics.AppendLine(StaticIndent + (line.StartsWith(indent, StringComparison.Ordinal) ? line.Substring(indent.Length) : line.TrimStart()));
        }

        statics.AppendLine($"{StaticIndent}s_{name} = {name};");
        counter.StaticFields.Add((type, name));
        counter.StaticTexts.Add(key, name);
        sb.AppendLine($"{indent}var {name} = s_{name};");
    }

    private const string StaticIndent = "            ";

    // A generated local under the counters' default names, the only ones a static's text holds.
    private static readonly Regex s_local = new(@"\bsk[A-Z][A-Za-z]*\d+\b", RegexOptions.CultureInvariant);

    /// <summary>The command without the disposal of what it reads from a static field, which outlives every draw.</summary>
    private static string Shared(string command, SkiaCSharpCodeGenCounter counter)
    {
        if (counter.Borrowed.Count == 0)
        {
            return command;
        }

        var kept = command
            .Split('\n')
            .Where(line =>
            {
                var trimmed = line.Trim();
                if (!trimmed.EndsWith("Dispose();", StringComparison.Ordinal))
                {
                    return true;
                }

                var end = trimmed.IndexOfAny(s_access);
                return end < 0 || !counter.Borrowed.Contains(trimmed.Substring(0, end));
            });

        return string.Join("\n", kept);
    }

    private static readonly char[] s_access = { '?', '.' };

    private enum FoldedRange
    {
        Emitted,
        Unwrapped,
        Dead
    }

    public static void ToSKPicture(this SKPicture? picture, SkiaCSharpCodeGenCounter counter, StringBuilder sb, string indent)
        => ToSKPicture(picture, counter, sb, indent, layers: null);

    /// <param name="layers">
    /// The layers open around a picture the drawing draws, which it replays under as the renderer
    /// does; a shader's or an image filter's picture is recorded on its own, under none.
    /// </param>
    private static void ToSKPicture(SKPicture? picture, SkiaCSharpCodeGenCounter counter, StringBuilder sb, string indent, SaveLayerTracker? layers)
    {
        // A picture inside the drawing is still recorded, and what it builds is disposed with it.
        var direct = counter.Direct;
        var statics = counter.Statics;
        counter.Direct = false;
        counter.Statics = null;

        try
        {
            if (direct)
            {
                ToCanvas(picture, counter, sb, indent, statics, layers);
            }
            else
            {
                ToRecording(picture, counter, sb, indent, layers);
            }
        }
        finally
        {
            counter.Statics = statics;
        }
    }

    /// <summary>The drawing's commands onto the canvas <c>Draw</c> was given, under whatever it already holds.</summary>
    private static void ToCanvas(SKPicture? picture, SkiaCSharpCodeGenCounter counter, StringBuilder sb, string indent, StringBuilder? statics, SaveLayerTracker? layers)
    {
        var counterCanvas = ++counter.Canvas;
        var canvas = $"{counter.CanvasVarName}{counterCanvas}";

        var state = new CanvasState($"skSharedPaint{counterCanvas}", canvas, counterCanvas, direct: true);
        var body = new StringBuilder();

        Commands(picture?.Commands, counter, body, indent, counterCanvas, statics ?? new StringBuilder(), layers ?? new SaveLayerTracker(), state);

        sb.AppendLine($"{indent}var {canvas} = {counter.CanvasVarName};");

        if (state.BaseUsed)
        {
            sb.AppendLine($"{indent}var skBase{counterCanvas} = {canvas}.TotalMatrix;");
        }

        if (state.Touched)
        {
            sb.AppendLine($"{indent}var skSave{counterCanvas} = {canvas}.Save();");
        }

        if (state.PaintUsed)
        {
            sb.AppendLine($"{indent}var {state.Paint} = {ExprHelpers.Paint}();");
        }

        sb.Append(body);

        if (state.Touched)
        {
            sb.AppendLine($"{indent}{canvas}.RestoreToCount(skSave{counterCanvas});");
        }
    }

    private static void ToRecording(SKPicture? picture, SkiaCSharpCodeGenCounter counter, StringBuilder sb, string indent, SaveLayerTracker? layers)
    {
        var counterPicture = counter.Picture;

        if (picture is null)
        {
            sb.AppendLine($"{indent}var {counter.PictureVarName}{counterPicture} = default(SKPicture);");
            return;
        }

        var counterPictureRecorder = ++counter.PictureRecorder;
        var counterCanvas = ++counter.Canvas;

        sb.AppendLine($"{indent}var {counter.PictureRecorderVarName}{counterPictureRecorder} = new SKPictureRecorder();");
        sb.AppendLine($"{indent}var {counter.CanvasVarName}{counterCanvas} = {counter.PictureRecorderVarName}{counterPictureRecorder}.BeginRecording({picture.CullRect.ToSKRect()});");

        var canvas = $"{counter.CanvasVarName}{counterCanvas}";
        Commands(picture.Commands, counter, sb, indent, counterCanvas, statics: null, layers ?? new SaveLayerTracker(), new CanvasState(paint: null, canvas, counterCanvas, direct: false));

        sb.AppendLine($"{indent}var {counter.PictureVarName}{counterPicture} = {counter.PictureRecorderVarName}{counterPictureRecorder}.EndRecording();");
        sb.AppendLine($"{indent}{counter.PictureRecorderVarName}{counterPictureRecorder}?.Dispose();");
        sb.AppendLine($"{indent}{counter.CanvasVarName}{counterCanvas}?.Dispose();");
    }

    /// <param name="statics">
    /// Set when the commands draw straight onto the caller's canvas, whose matrix is then the base
    /// every matrix the picture recorded is relative to.
    /// </param>
    private static void Commands(
        IList<CanvasCommand>? commands,
        SkiaCSharpCodeGenCounter counter,
        StringBuilder sb,
        string indent,
        int counterCanvas,
        StringBuilder? statics,
        SaveLayerTracker layers,
        CanvasState state)
    {
        if (commands is null)
        {
            return;
        }

        var direct = statics is { };

        // Planned for every value an expression may take: generated code learns them only as it draws.
        var plan = CommandPlan.For(commands, symbolic: true);
        var kept = KeptSaves(commands, plan);
        state.Eager = kept is null;

        // A range whose condition folds to false is still emitted, into text that is thrown away:
        // emitting is what type checks its expressions, and a typo hidden behind `{{ false }}` has to
        // fail the build as it did when the range was an `if (false)`.
        var live = sb;
        var discarded = new StringBuilder();
        var command = new StringBuilder();
        var ranges = new Stack<(FoldedRange Range, CanvasState.Mark Before)>();
        var dead = 0;

        for (var index = 0; index < commands.Count; index++)
        {
            var canvasCommand = commands[index];
            sb = dead > 0 ? discarded : direct ? command.Clear() : live;
            counter.Statics = dead > 0 ? null : statics;

            // A shape under nothing but a translation takes it into its own coordinates, unless its
            // paint has something in those coordinates too: a shader, a filter, a dash, a non-scaling stroke.
            var offset = counter.Bake && !state.Eager && canvasCommand is DrawPathCanvasCommand { Path: { }, Paint: { Shader: null, ImageFilter: null, PathEffect: null, IsStrokeNonScaling: false } }
                ? state.Translation
                : null;

            if (canvasCommand is not (SaveCanvasCommand or RestoreCanvasCommand or SetMatrixCanvasCommand or SaveLayerCanvasCommand or BeginConditionalCanvasCommand or EndConditionalCanvasCommand)
                && !plan.Skips(index))
            {
                state.Sync(counter, sb, indent, baked: offset is { });
            }

            switch (canvasCommand)
            {
                case ClipPathCanvasCommand or ClipRectCanvasCommand when plan.Skips(index):
                    break;
                case ClipPathCanvasCommand clipPathCanvasCommand:
                    {
                        if (clipPathCanvasCommand.ClipPath is { })
                        {
                            var counterPath = ++counter.Path;
                            clipPathCanvasCommand.ClipPath.ToSKPath(counter, sb, indent, out var isDefault);
                            if (!isDefault)
                            {
                                var operation = clipPathCanvasCommand.Operation.ToSKClipOperation();
                                var antialias = clipPathCanvasCommand.Antialias.ToBoolString();
                                state.Touched = true;
                                sb.AppendLine(
                                    $"{indent}{counter.CanvasVarName}{counterCanvas}.ClipPath({counter.PathVarName}{counterPath}, {operation}, {antialias});");
                            }
                        }
                        break;
                    }
                case ClipRectCanvasCommand clipRectCanvasCommand:
                    {
                        var rect = clipRectCanvasCommand.Rect.ToSKRect();
                        var operation = clipRectCanvasCommand.Operation.ToSKClipOperation();
                        var antialias = clipRectCanvasCommand.Antialias.ToBoolString();
                        state.Touched = true;
                        sb.AppendLine($"{indent}{counter.CanvasVarName}{counterCanvas}.ClipRect({rect}, {operation}, {antialias});");
                        break;
                    }
                case SaveCanvasCommand _:
                    {
                        // A save that scopes no clip only scopes a matrix, which every draw sets anyway.
                        var keep = kept?[index] ?? true;
                        if (keep)
                        {
                            sb.AppendLine($"{indent}{counter.CanvasVarName}{counterCanvas}.Save();");
                        }

                        state.Save(keep);
                        layers.Save(isLayer: false);
                        break;
                    }
                case RestoreCanvasCommand _:
                    {
                        var keep = kept?[index] ?? true;
                        if (keep)
                        {
                            sb.AppendLine($"{indent}{counter.CanvasVarName}{counterCanvas}.Restore();");
                        }

                        state.Restore(keep);
                        layers.Restore();
                        break;
                    }
                case SetMatrixCanvasCommand setMatrixCanvasCommand:
                    {
                        // Set on the canvas only once something is drawn under it.
                        state.SetMatrix(setMatrixCanvasCommand, counter, sb, indent);
                        break;
                    }
                case BeginConditionalCanvasCommand beginConditionalCanvasCommand:
                    {
                        // Dropping a false range whole is what `if (false)` did with it, Save, Restore
                        // and SetMatrix included: generated code restates the absolute matrix.
                        if (SymCSharpEmitter.TryConstant(beginConditionalCanvasCommand.Condition, ExprType.Boolean, out var known))
                        {
                            ranges.Push((known.AsBoolean ? FoldedRange.Unwrapped : FoldedRange.Dead, state.Marked()));
                            dead += known.AsBoolean ? 0 : 1;
                            break;
                        }

                        ranges.Push((FoldedRange.Emitted, state.Marked()));
                        sb.AppendLine($"{indent}if ({SymCSharpEmitter.Emit(beginConditionalCanvasCommand.Condition, ExprType.Boolean)})");
                        sb.AppendLine($"{indent}{{");

                        // Reassigning the parameter indents everything emitted until the
                        // matching end, including the nested ToSKPaint/ToSKPath helpers.
                        indent += "    ";
                        break;
                    }
                case EndConditionalCanvasCommand _:
                    {
                        var (range, before) = ranges.Count > 0 ? ranges.Pop() : (FoldedRange.Emitted, state.Marked());

                        if (range == FoldedRange.Dead)
                        {
                            state.Restore(before);
                        }
                        else if (range == FoldedRange.Emitted)
                        {
                            state.Join(before);
                        }

                        if (range != FoldedRange.Emitted)
                        {
                            dead -= range == FoldedRange.Dead ? 1 : 0;
                            break;
                        }

                        if (indent.Length >= 4)
                        {
                            indent = indent.Substring(4);
                        }

                        sb.AppendLine($"{indent}}}");
                        break;
                    }
                case SaveLayerCanvasCommand saveLayerCanvasCommand:
                    {
                        // A layer to the tracker even where it is drawn as a save, as the renderer counts it.
                        layers.Save(isLayer: true);

                        // Under an antialiased clip a layer that changes nothing still costs most of
                        // the drawing (blinds180ltr: 59.7 µs, 8.0 µs without).
                        var layerPlan = plan.Layer(index);
                        var inert = layerPlan == LayerPlan.SaveIfOpaque;
                        var color = saveLayerCanvasCommand.Paint?.Color;
                        if (layerPlan == LayerPlan.Fold || inert && (color is null || color.Value is { Expression: null, Alpha: 255 }))
                        {
                            sb.AppendLine($"{indent}{counter.CanvasVarName}{counterCanvas}.Save();");
                            state.Save(kept: true);
                            break;
                        }

                        // A layer's bounds and filters are in the space it is opened under, which is
                        // also what its restore puts back.
                        state.Sync(counter, sb, indent);
                        state.Save(kept: true);
                        var bounds = saveLayerCanvasCommand.Bounds?.ToSKRect();
                        if (saveLayerCanvasCommand.Paint is { })
                        {
                            var counterPaint = ++counter.Paint;
                            saveLayerCanvasCommand.Paint.ToSKPaint(counter, sb, indent);
                            var saveLayer = bounds is { }
                                ? $"{counter.CanvasVarName}{counterCanvas}.SaveLayer({bounds}, {counter.PaintVarName}{counterPaint});"
                                : $"{counter.CanvasVarName}{counterCanvas}.SaveLayer({counter.PaintVarName}{counterPaint});";
                            if (inert && color?.Expression is { })
                            {
                                sb.AppendLine($"{indent}if ({counter.PaintVarName}{counterPaint}.Color.Alpha == 255)");
                                sb.AppendLine($"{indent}{{");
                                sb.AppendLine($"{indent}    {counter.CanvasVarName}{counterCanvas}.Save();");
                                sb.AppendLine($"{indent}}}");
                                sb.AppendLine($"{indent}else");
                                sb.AppendLine($"{indent}{{");
                                sb.AppendLine($"{indent}    {saveLayer}");
                                sb.AppendLine($"{indent}}}");
                            }
                            else
                            {
                                sb.AppendLine($"{indent}{saveLayer}");
                            }

                            // NOTE: Do not dispose created SKTypeface by font manager.
#if USE_DISPOSE_TYPEFACE
                        if (saveLayerCanvasCommand.Paint.Typeface is { })
                        {
                            sb.AppendLine($"{indent}if ({counter.PaintVarName}{counterPaint}.Typeface != SKTypeface.Default)");
                            sb.AppendLine($"{indent}{{");
                            sb.AppendLine($"{indent}    {counter.PaintVarName}{counterPaint}.Typeface?.Dispose();");
                            sb.AppendLine($"{indent}}}");
                        } 
#endif
                            if (saveLayerCanvasCommand.Paint.Shader is { })
                            {
                                sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.Shader?.Dispose();");
                            }
                            if (saveLayerCanvasCommand.Paint.ColorFilter is { })
                            {
                                sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.ColorFilter?.Dispose();");
                            }
                            if (saveLayerCanvasCommand.Paint.ImageFilter is { })
                            {
                                sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.ImageFilter?.Dispose();");
                            }
                            if (saveLayerCanvasCommand.Paint.PathEffect is { })
                            {
                                sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.PathEffect?.Dispose();");
                            }

                            sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.Dispose();");
                        }
                        else if (bounds is { })
                        {
                            sb.AppendLine($"{indent}{counter.CanvasVarName}{counterCanvas}.SaveLayer({bounds}, null);");
                        }
                        else
                        {
                            sb.AppendLine($"{indent}{counter.CanvasVarName}{counterCanvas}.SaveLayer();");
                        }
                        break;
                    }
                case DrawImageCanvasCommand drawImageCanvasCommand:
                    {
                        if (drawImageCanvasCommand.Image is { })
                        {
                            var counterImage = ++counter.Image;
                            drawImageCanvasCommand.Image.ToSKImage(counter, sb, indent);
                            var source = drawImageCanvasCommand.Source.ToSKRect();
                            var dest = drawImageCanvasCommand.Dest.ToSKRect();
                            var paintExpression = "null";
                            var counterPaint = -1;
                            if (drawImageCanvasCommand.Paint is { })
                            {
                                counterPaint = ++counter.Paint;
                                drawImageCanvasCommand.Paint.ToSKPaint(counter, sb, indent);
                                paintExpression = $"{counter.PaintVarName}{counterPaint}";
                            }
                            var samplingOptions = drawImageCanvasCommand.Sampling?.ToSKSamplingOptions() ?? drawImageCanvasCommand.Paint?.FilterQuality.ToSKSamplingOptions() ?? "SKSamplingOptions.Default";
                            sb.AppendLine($"{indent}{counter.CanvasVarName}{counterCanvas}.DrawImage({counter.ImageVarName}{counterImage}, {source}, {dest}, {samplingOptions}, {paintExpression});");
                            sb.AppendLine($"{indent}{counter.ImageVarName}{counterImage}?.Dispose();");

                            // NOTE: Do not dispose created SKTypeface by font manager.
#if USE_DISPOSE_TYPEFACE
                        if (drawImageCanvasCommand.Paint?.Typeface is { })
                        {
                            sb.AppendLine($"{indent}if ({counter.PaintVarName}{counterPaint}.Typeface != SKTypeface.Default)");
                            sb.AppendLine($"{indent}{{");
                            sb.AppendLine($"{indent}    {counter.PaintVarName}{counterPaint}.Typeface?.Dispose();");
                            sb.AppendLine($"{indent}}}");
                        } 
#endif
                            if (drawImageCanvasCommand.Paint is { })
                            {
                                if (drawImageCanvasCommand.Paint.Shader is { })
                                {
                                    sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.Shader?.Dispose();");
                                }
                                if (drawImageCanvasCommand.Paint.ColorFilter is { })
                                {
                                    sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.ColorFilter?.Dispose();");
                                }
                                if (drawImageCanvasCommand.Paint.ImageFilter is { })
                                {
                                    sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.ImageFilter?.Dispose();");
                                }
                                if (drawImageCanvasCommand.Paint.PathEffect is { })
                                {
                                    sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.PathEffect?.Dispose();");
                                }

                                sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.Dispose();");
                            }
                        }
                        break;
                    }
                case DrawPictureCanvasCommand drawPictureCanvasCommand:
                    {
                        if (drawPictureCanvasCommand.Picture is { })
                        {
                            var counterChildPicture = ++counter.Picture;
                            ToSKPicture(drawPictureCanvasCommand.Picture, counter, sb, indent, layers);
                            sb.AppendLine($"{indent}{counter.CanvasVarName}{counterCanvas}.DrawPicture({counter.PictureVarName}{counterChildPicture});");
                            sb.AppendLine($"{indent}{counter.PictureVarName}{counterChildPicture}?.Dispose();");
                        }
                        break;
                    }
                case DrawPathCanvasCommand drawPathCanvasCommand:
                    {
                        // Not `with`: built for netstandard2.0, its init setter does not bind to the
                        // ShimSkiaSharp a newer runtime loads (MissingMethodException).
                        if (offset is { } moved)
                        {
                            drawPathCanvasCommand = new DrawPathCanvasCommand(Moved(drawPathCanvasCommand.Path!, moved.X, moved.Y), drawPathCanvasCommand.Paint);
                        }

                        if (drawPathCanvasCommand.Path is { } && drawPathCanvasCommand.Paint is { })
                        {
                            var primitive = layers.Primitive(drawPathCanvasCommand) switch
                            {
                                AddRectPathCommand rect => $"DrawRect({rect.Rect.ToSKRect()}, ",
                                AddRoundRectPathCommand roundRect => $"DrawRoundRect({roundRect.Rect.ToSKRect()}, {roundRect.Rx.ToFloatString()}, {roundRect.Ry.ToFloatString()}, ",
                                AddOvalPathCommand oval => $"DrawOval({oval.Rect.ToSKRect()}, ",
                                AddCirclePathCommand circle => $"DrawCircle({circle.X.ToFloatString()}, {circle.Y.ToFloatString()}, {circle.Radius.ToFloatString()}, ",
                                _ => null
                            };
                            var counterPath = primitive is null ? ++counter.Path : -1;
                            if (primitive is null)
                            {
                                drawPathCanvasCommand.Path.ToSKPath(counter, sb, indent);
                            }
                            var counterPaint = ++counter.Paint;
                            var fade = plan.FoldedInto(index)?
                                .Select(layer => ((SaveLayerCanvasCommand)commands[layer]).Paint?.Color)
                                .OfType<SKColor>()
                                .ToList();
                            ToSKPaint(drawPathCanvasCommand.Paint, counter, sb, indent, fade, state);
                            if (primitive is { })
                            {
                                sb.AppendLine($"{indent}{counter.CanvasVarName}{counterCanvas}.{primitive}{counter.PaintVarName}{counterPaint});");
                            }
                            else if (drawPathCanvasCommand.Paint.IsStrokeNonScaling &&
                                drawPathCanvasCommand.Paint.Style == SKPaintStyle.Stroke)
                            {
                                var counterNonScalingPath = ++counter.Path;
                                if (direct)
                                {
                                    // What the picture's own matrices add to the caller's, as a recording would see it.
                                    state.BaseUsed = true;
                                    sb.AppendLine($"{indent}skBase{counterCanvas}.TryInvert(out var inverse{counterNonScalingPath});");
                                    sb.AppendLine($"{indent}var matrix{counterNonScalingPath} = inverse{counterNonScalingPath}.PreConcat({counter.CanvasVarName}{counterCanvas}.TotalMatrix);");
                                }
                                else
                                {
                                    sb.AppendLine($"{indent}var matrix{counterNonScalingPath} = {counter.CanvasVarName}{counterCanvas}.TotalMatrix;");
                                }

                                sb.AppendLine($"{indent}if (matrix{counterNonScalingPath}.IsIdentity)");
                                sb.AppendLine($"{indent}{{");
                                sb.AppendLine($"{indent}    {counter.CanvasVarName}{counterCanvas}.DrawPath({counter.PathVarName}{counterPath}, {counter.PaintVarName}{counterPaint});");
                                sb.AppendLine($"{indent}}}");
                                sb.AppendLine($"{indent}else");
                                sb.AppendLine($"{indent}{{");
                                sb.AppendLine($"{indent}    using var {counter.PathVarName}{counterNonScalingPath} = new SKPath({counter.PathVarName}{counterPath});");
                                sb.AppendLine($"{indent}    {counter.PathVarName}{counterNonScalingPath}.Transform(in matrix{counterNonScalingPath});");
                                sb.AppendLine($"{indent}    {counter.CanvasVarName}{counterCanvas}.Save();");
                                sb.AppendLine(direct
                                    ? $"{indent}    {counter.CanvasVarName}{counterCanvas}.SetMatrix(in skBase{counterCanvas});"
                                    : $"{indent}    {counter.CanvasVarName}{counterCanvas}.ResetMatrix();");
                                sb.AppendLine($"{indent}    {counter.CanvasVarName}{counterCanvas}.DrawPath({counter.PathVarName}{counterNonScalingPath}, {counter.PaintVarName}{counterPaint});");
                                sb.AppendLine($"{indent}    {counter.CanvasVarName}{counterCanvas}.Restore();");
                                sb.AppendLine($"{indent}}}");
                            }
                            else
                            {
                                sb.AppendLine($"{indent}{counter.CanvasVarName}{counterCanvas}.DrawPath({counter.PathVarName}{counterPath}, {counter.PaintVarName}{counterPaint});");
                            }

                            // NOTE: Do not dispose created SKTypeface by font manager.
#if USE_DISPOSE_TYPEFACE
                        if (drawPathCanvasCommand.Paint.Typeface is { })
                        {
                            sb.AppendLine($"{indent}if ({counter.PaintVarName}{counterPaint}.Typeface != SKTypeface.Default)");
                            sb.AppendLine($"{indent}{{");
                            sb.AppendLine($"{indent}    {counter.PaintVarName}{counterPaint}.Typeface?.Dispose();");
                            sb.AppendLine($"{indent}}}");
                        } 
#endif
                            if (drawPathCanvasCommand.Paint.Shader is { })
                            {
                                sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.Shader?.Dispose();");
                            }
                            if (drawPathCanvasCommand.Paint.ColorFilter is { })
                            {
                                sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.ColorFilter?.Dispose();");
                            }
                            if (drawPathCanvasCommand.Paint.ImageFilter is { })
                            {
                                sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.ImageFilter?.Dispose();");
                            }
                            if (drawPathCanvasCommand.Paint.PathEffect is { })
                            {
                                sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.PathEffect?.Dispose();");
                            }

                            sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.Dispose();");
                            if (primitive is null)
                            {
                                sb.AppendLine($"{indent}{counter.PathVarName}{counterPath}?.Dispose();");
                            }
                        }
                        break;
                    }
                case DrawPositionedTextRunCanvasCommand drawPositionedTextRunCanvasCommand:
                    {
                        if (drawPositionedTextRunCanvasCommand.Fragments is { Count: > 0 } && drawPositionedTextRunCanvasCommand.Paint is { })
                        {
                            var counterPaint = ++counter.Paint;
                            drawPositionedTextRunCanvasCommand.Paint.ToSKPaint(counter, sb, indent);
                            var counterFont = ++counter.Font;
                            if (drawPositionedTextRunCanvasCommand.Font is { } textRunFont)
                            {
                                textRunFont.ToSKFont(counter, sb, indent, drawPositionedTextRunCanvasCommand.Paint.RequestedTypeface);
                            }
                            else
                            {
                                drawPositionedTextRunCanvasCommand.Paint.ToSKFont(counter, sb, indent);
                            }
                            var textAlign = (drawPositionedTextRunCanvasCommand.TextAlign ?? drawPositionedTextRunCanvasCommand.Paint.TextAlign).ToSKTextAlign();

                            foreach (var fragment in drawPositionedTextRunCanvasCommand.Fragments)
                            {
                                var text = ExprCSharpBackend.Literal(fragment.Text);
                                var x = fragment.Point.X;
                                var y = fragment.Point.Y;
                                if (fragment.RotationDegrees == 0f && fragment.ScaleX == 1f)
                                {
                                    sb.AppendLine($"{indent}{counter.CanvasVarName}{counterCanvas}.DrawText({text}, {x.ToFloatString()}, {y.ToFloatString()}, {textAlign}, {counter.FontVarName}{counterFont}, {counter.PaintVarName}{counterPaint});");
                                    continue;
                                }

                                sb.AppendLine($"{indent}{counter.CanvasVarName}{counterCanvas}.Save();");
                                if (fragment.RotationDegrees != 0f)
                                {
                                    var counterMatrix = ++counter.Matrix;
                                    sb.AppendLine($"{indent}var {counter.MatrixVarName}{counterMatrix} = SKMatrix.CreateRotationDegrees({fragment.RotationDegrees.ToFloatString()}, {x.ToFloatString()}, {y.ToFloatString()});");
                                    sb.AppendLine($"{indent}{counter.CanvasVarName}{counterCanvas}.Concat(in {counter.MatrixVarName}{counterMatrix});");
                                }

                                if (fragment.ScaleX != 1f)
                                {
                                    var counterMatrix = ++counter.Matrix;
                                    sb.AppendLine($"{indent}var {counter.MatrixVarName}{counterMatrix} = SKMatrix.CreateScale({fragment.ScaleX.ToFloatString()}, 1f, {fragment.ScaleOriginX.ToFloatString()}, {y.ToFloatString()});");
                                    sb.AppendLine($"{indent}{counter.CanvasVarName}{counterCanvas}.Concat(in {counter.MatrixVarName}{counterMatrix});");
                                }

                                sb.AppendLine($"{indent}{counter.CanvasVarName}{counterCanvas}.DrawText({text}, {x.ToFloatString()}, {y.ToFloatString()}, {textAlign}, {counter.FontVarName}{counterFont}, {counter.PaintVarName}{counterPaint});");
                                sb.AppendLine($"{indent}{counter.CanvasVarName}{counterCanvas}.Restore();");
                            }

                            // NOTE: Do not dispose created SKTypeface by font manager.
#if USE_DISPOSE_TYPEFACE
                        if (drawPositionedTextRunCanvasCommand.Paint.Typeface is { })
                        {
                            sb.AppendLine($"{indent}if ({counter.PaintVarName}{counterPaint}.Typeface != SKTypeface.Default)");
                            sb.AppendLine($"{indent}{{");
                            sb.AppendLine($"{indent}    {counter.PaintVarName}{counterPaint}.Typeface?.Dispose();");
                            sb.AppendLine($"{indent}}}");
                        }
#endif
                            if (drawPositionedTextRunCanvasCommand.Paint.Shader is { })
                            {
                                sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.Shader?.Dispose();");
                            }
                            if (drawPositionedTextRunCanvasCommand.Paint.ColorFilter is { })
                            {
                                sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.ColorFilter?.Dispose();");
                            }
                            if (drawPositionedTextRunCanvasCommand.Paint.ImageFilter is { })
                            {
                                sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.ImageFilter?.Dispose();");
                            }
                            if (drawPositionedTextRunCanvasCommand.Paint.PathEffect is { })
                            {
                                sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.PathEffect?.Dispose();");
                            }

                            sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.Dispose();");
                            sb.AppendLine($"{indent}{counter.FontVarName}{counterFont}?.Dispose();");
                        }
                        break;
                    }
                case DrawTextBlobCanvasCommand drawPositionedTextCanvasCommand:
                    {
                        if (drawPositionedTextCanvasCommand.TextBlob is { } && drawPositionedTextCanvasCommand.TextBlob.Points is { } && drawPositionedTextCanvasCommand.Paint is { })
                        {
                            var text = ExprCSharpBackend.Literal(drawPositionedTextCanvasCommand.TextBlob.Text);
                            var points = drawPositionedTextCanvasCommand.TextBlob.Points.ToSKPoints();
                            var counterPaint = ++counter.Paint;
                            drawPositionedTextCanvasCommand.Paint.ToSKPaint(counter, sb, indent);
                            var counterFont = ++counter.Font;
                            if (drawPositionedTextCanvasCommand.TextBlob.Font is { } textBlobFont)
                            {
                                textBlobFont.ToSKFont(counter, sb, indent, drawPositionedTextCanvasCommand.Paint.RequestedTypeface);
                            }
                            else
                            {
                                drawPositionedTextCanvasCommand.Paint.ToSKFont(counter, sb, indent);
                            }
                            var counterTextBlob = ++counter.TextBlob;
                            sb.AppendLine($"{indent}var {counter.TextBlobVarName}{counterTextBlob} = SKTextBlob.CreatePositioned({text}, {counter.FontVarName}{counterFont}, {points});");
                            var x = drawPositionedTextCanvasCommand.X;
                            var y = drawPositionedTextCanvasCommand.Y;
                            sb.AppendLine($"{indent}{counter.CanvasVarName}{counterCanvas}.DrawText({counter.TextBlobVarName}{counterTextBlob}, {x.ToFloatString()}, {y.ToFloatString()}, {counter.PaintVarName}{counterPaint});");

                            // NOTE: Do not dispose created SKTypeface by font manager.
#if USE_DISPOSE_TYPEFACE
                        if (drawPositionedTextCanvasCommand.Paint.Typeface is { })
                        {
                            sb.AppendLine($"{indent}if ({counter.PaintVarName}{counterPaint}.Typeface != SKTypeface.Default)");
                            sb.AppendLine($"{indent}{{");
                            sb.AppendLine($"{indent}    {counter.PaintVarName}{counterPaint}.Typeface?.Dispose();");
                            sb.AppendLine($"{indent}}}");
                        } 
#endif
                            if (drawPositionedTextCanvasCommand.Paint.Shader is { })
                            {
                                sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.Shader?.Dispose();");
                            }
                            if (drawPositionedTextCanvasCommand.Paint.ColorFilter is { })
                            {
                                sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.ColorFilter?.Dispose();");
                            }
                            if (drawPositionedTextCanvasCommand.Paint.ImageFilter is { })
                            {
                                sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.ImageFilter?.Dispose();");
                            }
                            if (drawPositionedTextCanvasCommand.Paint.PathEffect is { })
                            {
                                sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.PathEffect?.Dispose();");
                            }

                            sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.Dispose();");
                            sb.AppendLine($"{indent}{counter.FontVarName}{counterFont}?.Dispose();");
                            sb.AppendLine($"{indent}{counter.TextBlobVarName}{counterTextBlob}?.Dispose();");
                        }
                        break;
                    }
                case DrawTextCanvasCommand drawTextCanvasCommand:
                    {
                        if (drawTextCanvasCommand.Paint is { })
                        {
                            // The expression where the author drives the text, so the argument reaches the draw; the
                            // string it was recorded with otherwise.
                            var text = drawTextCanvasCommand.TextExpression is { } textExpression
                                ? SymCSharpEmitter.Emit(textExpression, ExprType.String)
                                : ExprCSharpBackend.Literal(drawTextCanvasCommand.Text);
                            var x = drawTextCanvasCommand.X;
                            var y = drawTextCanvasCommand.Y;
                            var counterPaint = ++counter.Paint;
                            drawTextCanvasCommand.Paint.ToSKPaint(counter, sb, indent);
                            var counterFont = ++counter.Font;
                            if (drawTextCanvasCommand.Font is { } textFont)
                            {
                                textFont.ToSKFont(counter, sb, indent, drawTextCanvasCommand.Paint.RequestedTypeface);
                            }
                            else
                            {
                                drawTextCanvasCommand.Paint.ToSKFont(counter, sb, indent);
                            }
                            var textAlign = (drawTextCanvasCommand.TextAlign ?? drawTextCanvasCommand.Paint.TextAlign).ToSKTextAlign();
                            sb.AppendLine($"{indent}{counter.CanvasVarName}{counterCanvas}.DrawText({text}, {x.ToFloatString()}, {y.ToFloatString()}, {textAlign}, {counter.FontVarName}{counterFont}, {counter.PaintVarName}{counterPaint});");

                            // NOTE: Do not dispose created SKTypeface by font manager.
#if USE_DISPOSE_TYPEFACE
                        if (drawTextCanvasCommand.Paint.Typeface is { })
                        {
                            sb.AppendLine($"{indent}if ({counter.PaintVarName}{counterPaint}.Typeface != SKTypeface.Default)");
                            sb.AppendLine($"{indent}{{");
                            sb.AppendLine($"{indent}    {counter.PaintVarName}{counterPaint}.Typeface?.Dispose();");
                            sb.AppendLine($"{indent}}}");
                        } 
#endif
                            if (drawTextCanvasCommand.Paint.Shader is { })
                            {
                                sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.Shader?.Dispose();");
                            }
                            if (drawTextCanvasCommand.Paint.ColorFilter is { })
                            {
                                sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.ColorFilter?.Dispose();");
                            }
                            if (drawTextCanvasCommand.Paint.ImageFilter is { })
                            {
                                sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.ImageFilter?.Dispose();");
                            }
                            if (drawTextCanvasCommand.Paint.PathEffect is { })
                            {
                                sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.PathEffect?.Dispose();");
                            }

                            sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.Dispose();");
                            sb.AppendLine($"{indent}{counter.FontVarName}{counterFont}?.Dispose();");
                        }
                        break;
                    }
                case DrawTextOnPathCanvasCommand drawTextOnPathCanvasCommand:
                    {
                        if (drawTextOnPathCanvasCommand.Path is { } && drawTextOnPathCanvasCommand.Paint is { })
                        {
                            var text = ExprCSharpBackend.Literal(drawTextOnPathCanvasCommand.Text);
                            var counterPath = ++counter.Path;
                            drawTextOnPathCanvasCommand.Path.ToSKPath(counter, sb, indent);
                            var hOffset = drawTextOnPathCanvasCommand.HOffset;
                            var vOffset = drawTextOnPathCanvasCommand.VOffset;
                            var counterPaint = ++counter.Paint;
                            drawTextOnPathCanvasCommand.Paint.ToSKPaint(counter, sb, indent);
                            var counterFont = ++counter.Font;
                            if (drawTextOnPathCanvasCommand.Font is { } textOnPathFont)
                            {
                                textOnPathFont.ToSKFont(counter, sb, indent, drawTextOnPathCanvasCommand.Paint.RequestedTypeface);
                            }
                            else
                            {
                                drawTextOnPathCanvasCommand.Paint.ToSKFont(counter, sb, indent);
                            }
                            var textAlign = (drawTextOnPathCanvasCommand.TextAlign ?? drawTextOnPathCanvasCommand.Paint.TextAlign).ToSKTextAlign();
                            sb.AppendLine($"{indent}{counter.CanvasVarName}{counterCanvas}.DrawTextOnPath({text}, {counter.PathVarName}{counterPath}, {hOffset.ToFloatString()}, {vOffset.ToFloatString()}, {textAlign}, {counter.FontVarName}{counterFont}, {counter.PaintVarName}{counterPaint});");

                            // NOTE: Do not dispose created SKTypeface by font manager.
#if USE_DISPOSE_TYPEFACE
                        if (drawTextOnPathCanvasCommand.Paint.Typeface is { })
                        {
                            sb.AppendLine($"{indent}if ({counter.PaintVarName}{counterPaint}.Typeface != SKTypeface.Default)");
                            sb.AppendLine($"{indent}{{");
                            sb.AppendLine($"{indent}    {counter.PaintVarName}{counterPaint}.Typeface?.Dispose();");
                            sb.AppendLine($"{indent}}}");
                        }
#endif
                            if (drawTextOnPathCanvasCommand.Paint.Shader is { })
                            {
                                sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.Shader?.Dispose();");
                            }
                            if (drawTextOnPathCanvasCommand.Paint.ColorFilter is { })
                            {
                                sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.ColorFilter?.Dispose();");
                            }
                            if (drawTextOnPathCanvasCommand.Paint.ImageFilter is { })
                            {
                                sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.ImageFilter?.Dispose();");
                            }
                            if (drawTextOnPathCanvasCommand.Paint.PathEffect is { })
                            {
                                sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.PathEffect?.Dispose();");
                            }

                            sb.AppendLine($"{indent}{counter.PaintVarName}{counterPaint}?.Dispose();");
                            sb.AppendLine($"{indent}{counter.FontVarName}{counterFont}?.Dispose();");
                            sb.AppendLine($"{indent}{counter.PathVarName}{counterPath}?.Dispose();");
                        }
                        break;
                    }
                default:
                    {
                        break;
                    }
            }

            if (ReferenceEquals(sb, command))
            {
                live.Append(Shared(command.ToString(), counter));
            }
        }

        counter.Statics = statics;
    }
}
