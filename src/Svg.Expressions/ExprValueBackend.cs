// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;

namespace Svg.Expressions;

/// <summary>Evaluates a checked expression against values.</summary>
/// <remarks>
/// The sibling of the code generator's C# back end and pinned against it: the same document can go
/// through either. Nothing is folded or reordered — short-circuiting is what C# does, and doing
/// otherwise would change the answer for an expression whose unused half divides by zero.
/// </remarks>
internal static class ExprValueBackend
{
    public static ExprValue Evaluate(TypedExpr node, IReadOnlyDictionary<string, ExprValue> values)
        => node switch
        {
            // The double is narrowed exactly where the C# back end's Literal() narrows it.
            TypedNumber number => ExprValue.Number((float)number.Value),
            TypedInteger integer => ExprValue.Integer((int)integer.Value),
            TypedColor color => ExprValue.Color(color.R, color.G, color.B, color.A),
            TypedBoolean boolean => ExprValue.Boolean(boolean.Value),
            TypedString text => ExprValue.String(text.Value),
            TypedSymbol symbol => Lookup(symbol, values),
            TypedConstant constant => EvaluateConstant(constant),
            TypedUnary unary => EvaluateUnary(unary, values),
            TypedBinary binary => EvaluateBinary(binary, values),
            TypedConditional conditional => EvaluateConditional(conditional, values),
            TypedCall call => EvaluateCall(call, values),
            _ => throw new NotSupportedException($"Unsupported {nameof(TypedExpr)}: {node.GetType().Name}.")
        };

    private static ExprValue Lookup(TypedSymbol symbol, IReadOnlyDictionary<string, ExprValue> values)
    {
        if (!values.TryGetValue(symbol.Name, out var value))
        {
            // The checker resolved the name, so reaching here means the table and the values
            // disagree: a caller declared a type without binding a value.
            throw new ExprException($"No value was bound for '{symbol.Name}'.", symbol.Position);
        }

        return value;
    }

    private static ExprValue EvaluateConstant(TypedConstant constant)
        => constant.Constant switch
        {
            // Emitted as "(MathF.PI * 2f)", so the multiplication happens in float here too and
            // tau / (pi * 2) is exactly 1.
            ExprConstant.Pi => ExprValue.Number(ExprMath.Pi),
            _ => ExprValue.Number(ExprMath.Pi * 2f)
        };

    private static ExprValue EvaluateUnary(TypedUnary unary, IReadOnlyDictionary<string, ExprValue> values)
    {
        var operand = Evaluate(unary.Operand, values);

        if (unary.Op != ExprUnaryOp.Negate)
        {
            return ExprValue.Boolean(!operand.AsBoolean);
        }

        // Unchecked, so negating int.MinValue wraps back to itself rather than throwing, which is
        // what the generated C# does with the same int.
        return operand.Type == ExprType.Integer
            ? ExprValue.Integer(unchecked(-operand.AsInteger))
            : ExprValue.Number(-operand.AsNumber);
    }

    private static ExprValue EvaluateBinary(TypedBinary binary, IReadOnlyDictionary<string, ExprValue> values)
    {
        // && and || short-circuit in the generated C#, so the right operand is not evaluated here
        // either. It cannot have a side effect, but it can throw or produce NaN.
        if (binary.Op == ExprBinaryOp.And || binary.Op == ExprBinaryOp.Or)
        {
            var shortCircuit = Evaluate(binary.Left, values).AsBoolean;

            if (binary.Op == ExprBinaryOp.And)
            {
                return ExprValue.Boolean(shortCircuit && Evaluate(binary.Right, values).AsBoolean);
            }

            return ExprValue.Boolean(shortCircuit || Evaluate(binary.Right, values).AsBoolean);
        }

        var left = Evaluate(binary.Left, values);
        var right = Evaluate(binary.Right, values);

        // The checker has already settled that both sides are integers wherever one is, so the
        // whole of integer arithmetic branches once here rather than case by case below. Equality
        // is not among it: AreEqual already answers for every type, integers included.
        if (left.Type == ExprType.Integer
            && right.Type == ExprType.Integer
            && binary.Op is not (ExprBinaryOp.Equal or ExprBinaryOp.NotEqual))
        {
            return EvaluateIntegerBinary(binary.Op, left.AsInteger, right.AsInteger);
        }

        switch (binary.Op)
        {
            case ExprBinaryOp.Add:
                return left.Type == ExprType.String
                    ? ExprValue.String(left.AsString + right.AsString)
                    : ExprValue.Number(left.AsNumber + right.AsNumber);
            case ExprBinaryOp.Subtract:
                return ExprValue.Number(left.AsNumber - right.AsNumber);
            case ExprBinaryOp.Multiply:
                return ExprValue.Number(left.AsNumber * right.AsNumber);
            case ExprBinaryOp.Divide:
                return ExprValue.Number(left.AsNumber / right.AsNumber);
            case ExprBinaryOp.Less:
                return ExprValue.Boolean(left.AsNumber < right.AsNumber);
            case ExprBinaryOp.LessOrEqual:
                return ExprValue.Boolean(left.AsNumber <= right.AsNumber);
            case ExprBinaryOp.Greater:
                return ExprValue.Boolean(left.AsNumber > right.AsNumber);
            case ExprBinaryOp.GreaterOrEqual:
                return ExprValue.Boolean(left.AsNumber >= right.AsNumber);
            case ExprBinaryOp.Equal:
                return ExprValue.Boolean(AreEqual(left, right));
            case ExprBinaryOp.NotEqual:
                return ExprValue.Boolean(!AreEqual(left, right));
            default:
                throw new NotSupportedException($"Unsupported {nameof(ExprBinaryOp)}: {binary.Op}.");
        }
    }

    // Not ExprValue.Equals: the generated code emits C# ==, where NaN equals nothing. The checker
    // has already established that both sides have the same type.
    private static bool AreEqual(ExprValue left, ExprValue right)
        => left.Type switch
        {
            ExprType.Number => left.AsNumber == right.AsNumber,
            ExprType.Integer => left.AsInteger == right.AsInteger,
            ExprType.Color => left.Red == right.Red
                              && left.Green == right.Green
                              && left.Blue == right.Blue
                              && left.Alpha == right.Alpha,
            ExprType.Boolean => left.AsBoolean == right.AsBoolean,
            ExprType.String => string.Equals(left.AsString, right.AsString, StringComparison.Ordinal),
            _ => throw new NotSupportedException($"Unsupported {nameof(ExprType)}: {left.Type}.")
        };

    private static ExprValue EvaluateConditional(
        TypedConditional conditional,
        IReadOnlyDictionary<string, ExprValue> values)
        => Evaluate(conditional.Condition, values).AsBoolean
            ? Evaluate(conditional.WhenTrue, values)
            : Evaluate(conditional.WhenFalse, values);

    private static ExprValue EvaluateCall(TypedCall call, IReadOnlyDictionary<string, ExprValue> values)
    {
        var arguments = new ExprValue[call.Arguments.Count];

        for (var index = 0; index < call.Arguments.Count; index++)
        {
            arguments[index] = Evaluate(call.Arguments[index], values);
        }

        // The checker picked an overload, and every argument of an integer one is an integer, so
        // the first argument answers for the call.
        if (arguments.Length > 0 && arguments[0].Type == ExprType.Integer)
        {
            switch (call.Function)
            {
                case ExprFunction.Abs:
                    return ExprValue.Integer(Absolute(arguments[0].AsInteger));
                case ExprFunction.Min:
                    return ExprValue.Integer(Math.Min(arguments[0].AsInteger, arguments[1].AsInteger));
                case ExprFunction.Max:
                    return ExprValue.Integer(Math.Max(arguments[0].AsInteger, arguments[1].AsInteger));
                case ExprFunction.Mod:
                    return ExprValue.Integer(Remainder(arguments[0].AsInteger, arguments[1].AsInteger));
                case ExprFunction.Clamp:
                    return ExprValue.Integer(
                        ExprMath.Clamp(arguments[0].AsInteger, arguments[1].AsInteger, arguments[2].AsInteger));
            }
        }

        switch (call.Function)
        {
            case ExprFunction.Sin:
                return ExprValue.Number(ExprMath.Sin(arguments[0].AsNumber));
            case ExprFunction.Cos:
                return ExprValue.Number(ExprMath.Cos(arguments[0].AsNumber));
            case ExprFunction.Tan:
                return ExprValue.Number(ExprMath.Tan(arguments[0].AsNumber));
            case ExprFunction.Abs:
                return ExprValue.Number(ExprMath.Abs(arguments[0].AsNumber));
            case ExprFunction.Sqrt:
                return ExprValue.Number(ExprMath.Sqrt(arguments[0].AsNumber));
            case ExprFunction.Floor:
                return ExprValue.Number(ExprMath.Floor(arguments[0].AsNumber));
            case ExprFunction.Ceil:
                return ExprValue.Number(ExprMath.Ceiling(arguments[0].AsNumber));
            case ExprFunction.Round:
                return ExprValue.Number(ExprMath.Round(arguments[0].AsNumber));
            case ExprFunction.Pow:
                return ExprValue.Number(ExprMath.Pow(arguments[0].AsNumber, arguments[1].AsNumber));
            case ExprFunction.Min:
                return ExprValue.Number(ExprMath.Min(arguments[0].AsNumber, arguments[1].AsNumber));
            case ExprFunction.Max:
                return ExprValue.Number(ExprMath.Max(arguments[0].AsNumber, arguments[1].AsNumber));

            // Emitted inline as C# %, whose sign follows the left operand. Not IEEERemainder.
            case ExprFunction.Mod:
                return ExprValue.Number(arguments[0].AsNumber % arguments[1].AsNumber);

            case ExprFunction.Clamp:
                return ExprValue.Number(ExprMath.Clamp(
                    arguments[0].AsNumber,
                    arguments[1].AsNumber,
                    arguments[2].AsNumber));

            case ExprFunction.Lerp:
                return ExprValue.Number(Lerp(
                    arguments[0].AsNumber,
                    arguments[1].AsNumber,
                    arguments[2].AsNumber));

            case ExprFunction.Rgb:
                return Rgb(arguments[0].AsNumber, arguments[1].AsNumber, arguments[2].AsNumber);
            case ExprFunction.Rgba:
                return Rgba(
                    arguments[0].AsNumber,
                    arguments[1].AsNumber,
                    arguments[2].AsNumber,
                    arguments[3].AsNumber);
            case ExprFunction.Hsl:
                return Hsl(arguments[0].AsNumber, arguments[1].AsNumber, arguments[2].AsNumber);
            case ExprFunction.Hsla:
                return Hsl(arguments[0].AsNumber, arguments[1].AsNumber, arguments[2].AsNumber)
                    .WithAlpha(AlphaByte(arguments[3].AsNumber));
            case ExprFunction.Mix:
                return Mix(arguments[0], arguments[1], arguments[2].AsNumber);
            case ExprFunction.WithAlpha:
                return arguments[0].WithAlpha(AlphaByte(arguments[1].AsNumber));

            // Invariant, so the two back ends cannot disagree by the culture of the machine that
            // ran them. The generated code spells it the same way.
            case ExprFunction.Upper:
                return ExprValue.String(arguments[0].AsString.ToUpperInvariant());
            case ExprFunction.Lower:
                return ExprValue.String(arguments[0].AsString.ToLowerInvariant());
            case ExprFunction.Len:
                return ExprValue.Integer(arguments[0].AsString.Length);

            // Shortest round-trip, invariant: a whole number reads as one, with no point and no
            // trailing zero, which is what anyone writing a label expects to see.
            case ExprFunction.Str:
                return ExprValue.String(
                    arguments[0].Type == ExprType.Integer
                        ? arguments[0].AsInteger.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : arguments[0].AsNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));

            case ExprFunction.Int:
                return ExprValue.Integer(Truncate(arguments[0].AsNumber));
            case ExprFunction.Num:
                return ExprValue.Number(arguments[0].AsInteger);

            default:
                throw new NotSupportedException($"Unsupported {nameof(ExprFunction)}: {call.Function}.");
        }
    }

    private static ExprValue EvaluateIntegerBinary(ExprBinaryOp op, int left, int right)
    {
        switch (op)
        {
            // Unchecked, so an overflow wraps rather than throwing -- which is what the generated
            // C# does with the same two ints, and the two may not disagree.
            case ExprBinaryOp.Add:
                return ExprValue.Integer(unchecked(left + right));
            case ExprBinaryOp.Subtract:
                return ExprValue.Integer(unchecked(left - right));
            case ExprBinaryOp.Multiply:
                return ExprValue.Integer(unchecked(left * right));
            case ExprBinaryOp.Divide:
                return ExprValue.Integer(Divide(left, right));
            case ExprBinaryOp.Less:
                return ExprValue.Boolean(left < right);
            case ExprBinaryOp.LessOrEqual:
                return ExprValue.Boolean(left <= right);
            case ExprBinaryOp.Greater:
                return ExprValue.Boolean(left > right);
            case ExprBinaryOp.GreaterOrEqual:
                return ExprValue.Boolean(left >= right);
            default:
                throw new NotSupportedException($"Unsupported {nameof(ExprBinaryOp)}: {op}.");
        }
    }

    /// <summary>The magnitude, saturating where there is no room for it.</summary>
    /// <remarks>
    /// Character for character what ExprHelpers.SvgIAbs emits. Math.Abs(int.MinValue) throws, since
    /// its answer is one past the top; int.MaxValue is the nearest one that fits, and is where
    /// int() and integer division saturate too.
    /// </remarks>
    private static int Absolute(int value)
        => value == int.MinValue ? int.MaxValue : Math.Abs(value);

    /// <summary>The remainder, with the two cases C# throws on answered instead.</summary>
    /// <remarks>
    /// Character for character what ExprHelpers.SvgIMod emits. The number path answers a zero
    /// divisor with NaN, which int() reads as zero, so this does too.
    /// </remarks>
    private static int Remainder(int left, int right)
        => right == 0 || (left == int.MinValue && right == -1) ? 0 : left % right;

    /// <summary>Integer division, with the two cases C# throws on answered instead.</summary>
    /// <remarks>
    /// Character for character what ExprHelpers.SvgIDiv emits. A drawing that renders must not start
    /// throwing because a divisor reached zero, and the number path does not: it produces an
    /// infinity. The ends are that infinity's integer spelling, and the same ones int() saturates
    /// to, so int(1 / 0) and 1 / 0 agree. int.MinValue / -1 has no answer in range and throws even
    /// unchecked, so it saturates with them.
    /// </remarks>
    private static int Divide(int left, int right)
        => right != 0 && !(left == int.MinValue && right == -1)
            ? left / right
            : left == 0 && right == 0 ? 0
            : (left < 0) == (right < 0) ? int.MaxValue
            : int.MinValue;

    /// <summary>A number as an integer, toward zero, saturating at the ends.</summary>
    /// <remarks>
    /// Character for character what ExprHelpers.SvgInt emits. A bare C# cast is what we want in the
    /// middle of the range and undefined outside it -- .NET does not promise what (int)1e30f is --
    /// so the ends are named rather than left to whatever the two back ends happen to compile to.
    /// </remarks>
    private static int Truncate(float value)
        => float.IsNaN(value) ? 0
            : value >= 2147483647f ? int.MaxValue
            : value <= -2147483648f ? int.MinValue
            : (int)value;

    // ExprHelpers.SvgLerp: unclamped, so t outside [0, 1] extrapolates.
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    // ExprHelpers.SvgRgb / SvgRgba. Math.Round on a double, so ties go to even, and the channels
    // are clamped to 0..255 while alpha is a 0..1 fraction.
    private static ExprValue Rgb(float r, float g, float b)
        => ExprValue.Color(ChannelByte(r), ChannelByte(g), ChannelByte(b), 255);

    private static ExprValue Rgba(float r, float g, float b, float a)
        => ExprValue.Color(ChannelByte(r), ChannelByte(g), ChannelByte(b), AlphaByte(a));

    private static byte ChannelByte(float value) => (byte)Math.Round(ExprMath.Clamp(value, 0f, 255f));

    // ExprHelpers.SvgRgba / SvgHsla / SvgWithAlpha all spell the alpha conversion this way.
    private static byte AlphaByte(float value) => (byte)Math.Round(ExprMath.Clamp(value, 0f, 1f) * 255f);

    // The hue wrap is load-bearing, not defensive: SKColor.FromHsl folds it back into range only
    // once, so it alone would be wrong for something like 720.
    private static ExprValue Hsl(float h, float s, float l)
        => FromHsl(
            ((h % 360f) + 360f) % 360f,
            ExprMath.Clamp(s, 0f, 1f) * 100f,
            ExprMath.Clamp(l, 0f, 1f) * 100f);

    /// <summary>
    /// SkiaSharp's <c>SKColor.FromHsl</c>, reimplemented because the language must evaluate without
    /// a reference to SkiaSharp.
    /// </summary>
    /// <remarks>
    /// The final cast <b>truncates</b>, and that is the whole reason this is written out rather than
    /// derived from first principles: over 53,361 samples of the h/s/l grid a truncating cast agrees
    /// with SkiaSharp everywhere, while rounding disagrees on 40,747 of them. Pinned by
    /// <c>ExprEvaluatorHslTests</c>, which sweeps the domain against the real thing.
    /// </remarks>
    private static ExprValue FromHsl(float h, float s, float l)
    {
        h /= 360f;
        s /= 100f;
        l /= 100f;

        var r = l;
        var g = l;
        var b = l;

        if (Math.Abs(s) > float.Epsilon)
        {
            var v2 = l < 0.5f ? l * (1f + s) : (l + s) - (l * s);
            var v1 = 2f * l - v2;

            r = HueToRgb(v1, v2, h + 1f / 3f);
            g = HueToRgb(v1, v2, h);
            b = HueToRgb(v1, v2, h - 1f / 3f);
        }

        return ExprValue.Color((byte)(255f * r), (byte)(255f * g), (byte)(255f * b), 255);
    }

    private static float HueToRgb(float v1, float v2, float vh)
    {
        if (vh < 0f)
        {
            vh += 1f;
        }

        if (vh > 1f)
        {
            vh -= 1f;
        }

        if (6f * vh < 1f)
        {
            return v1 + (v2 - v1) * 6f * vh;
        }

        if (2f * vh < 1f)
        {
            return v2;
        }

        if (3f * vh < 2f)
        {
            return v1 + (v2 - v1) * (2f / 3f - vh) * 6f;
        }

        return v1;
    }

    // ExprHelpers.SvgMix. Every channel including alpha, each rounded on its own, with t clamped.
    private static ExprValue Mix(ExprValue a, ExprValue b, float t)
    {
        var k = ExprMath.Clamp(t, 0f, 1f);

        return ExprValue.Color(
            (byte)Math.Round(a.Red + (b.Red - a.Red) * k),
            (byte)Math.Round(a.Green + (b.Green - a.Green) * k),
            (byte)Math.Round(a.Blue + (b.Blue - a.Blue) * k),
            (byte)Math.Round(a.Alpha + (b.Alpha - a.Alpha) * k));
    }
}
