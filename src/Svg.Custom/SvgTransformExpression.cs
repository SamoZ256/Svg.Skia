// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace Svg;

/// <summary>One argument of one transform function, as the attribute writes it.</summary>
public readonly struct SvgTransformExpressionArgument
{
    public SvgTransformExpressionArgument(string function, int index, int start, int length, string? expression, string text)
    {
        Function = function;
        Index = index;
        Start = start;
        Length = length;
        Expression = expression;
        Text = text;
    }

    /// <summary>The function this argument belongs to, named as SVG spells it.</summary>
    public string Function { get; }

    /// <summary>Which argument of that function this is, counting from zero.</summary>
    public int Index { get; }

    /// <summary>Where the argument sits in the attribute value, so a source view can mark it.</summary>
    public int Start { get; }

    public int Length { get; }

    /// <summary>The code between the braces, or null where the argument is a literal.</summary>
    public string? Expression { get; }

    /// <summary>The argument as written, braces included.</summary>
    public string Text { get; }

    public bool IsExpression => Expression is { };
}

/// <summary>One transform function and the arguments it was written with.</summary>
public sealed class SvgTransformExpressionFunction
{
    public SvgTransformExpressionFunction(string name, IReadOnlyList<SvgTransformExpressionArgument> arguments)
    {
        Name = name;
        Arguments = arguments;
    }

    public string Name { get; }

    public IReadOnlyList<SvgTransformExpressionArgument> Arguments { get; }
}

/// <summary>A <c>transform</c> attribute split into its functions, their arguments, and any stray braces.</summary>
public sealed class SvgTransformExpressionValue
{
    private readonly string _value;

    internal SvgTransformExpressionValue(
        string value,
        IReadOnlyList<SvgTransformExpressionFunction> functions,
        IReadOnlyList<SvgTransformExpressionArgument> stray)
    {
        _value = value;
        Functions = functions;
        Stray = stray;
    }

    public IReadOnlyList<SvgTransformExpressionFunction> Functions { get; }

    /// <summary>
    /// The <c>{{ … }}</c> spans that are not an argument of their own, and so drive nothing.
    /// </summary>
    /// <remarks>
    /// The whole-value rule of the extension, one level down: an argument is wholly an expression or
    /// wholly a literal, since <c>translate(1{{ dx }}, 0)</c> would otherwise have to be concatenated
    /// before it could be parsed, and the recorded model holds a number per slot rather than text.
    /// </remarks>
    public IReadOnlyList<SvgTransformExpressionArgument> Stray { get; }

    /// <summary>Whether any argument is driven by an expression.</summary>
    public bool Any
    {
        get
        {
            foreach (var function in Functions)
            {
                foreach (var argument in function.Arguments)
                {
                    if (argument.IsExpression)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

    /// <summary>The value with every expression replaced by the value <paramref name="value"/> gives it.</summary>
    /// <remarks>
    /// Written back rather than rebuilt from the functions, so the literals the author wrote and the
    /// number of arguments they wrote both survive: <c>SvgTransformConverter</c> refuses a rotate with
    /// two arguments, and a rewrite that changed the count would refuse a document written correctly.
    /// </remarks>
    public string With(Func<SvgTransformExpressionArgument, string> value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        var builder = new StringBuilder(_value.Length);
        var written = 0;

        foreach (var function in Functions)
        {
            foreach (var argument in function.Arguments)
            {
                if (!argument.IsExpression)
                {
                    continue;
                }

                builder.Append(_value, written, argument.Start - written);
                builder.Append(value(argument));
                written = argument.Start + argument.Length;
            }
        }

        builder.Append(_value, written, _value.Length - written);

        return builder.ToString();
    }

    /// <summary>The value as it reads with nothing bound.</summary>
    public string Placeholder => With(argument => SvgTransformExpression.Identity(argument.Function, argument.Index));
}

/// <summary>
/// Expressions written inside the arguments of a transform:
///
///   &lt;rect transform="translate({{ dx }}, 0) rotate({{ angle }} 32 32)" /&gt;
/// </summary>
/// <remarks>
/// The only attribute holding more than one expression, so it is split here rather than lifted whole
/// by <see cref="SvgExpressionAttributes.TryUnwrap"/> — which still decides each argument, one level
/// down. The split runs before <c>SvgTransformConverter</c> sees the value: that parser finds its
/// arguments by searching for parentheses and splitting on commas and spaces, all three of which
/// <c>rotate({{ min(a, b) }})</c> writes inside the braces.
/// </remarks>
public static class SvgTransformExpression
{
    private const string Open = "{{";

    private const string Close = "}}";

    /// <summary>The attribute this splits. Named here so the three lift sites cannot spell it apart.</summary>
    public const string Name = "transform";

    /// <summary>
    /// The other attribute split per item: a dash array is read as the arguments of one function
    /// with no name and no parentheses, which is all a list is.
    /// </summary>
    public const string DashArrayName = "stroke-dasharray";

    /// <summary>
    /// The third: path data is read as one function per segment, named by its command letter, whose
    /// arguments are the segment's numbers.
    /// </summary>
    public const string PathDataName = "d";

    /// <summary>Whether <paramref name="value"/> is worth splitting at all.</summary>
    public static bool Holds(string? value)
        => value is { } && value.IndexOf(Open, StringComparison.Ordinal) >= 0;

    /// <summary>
    /// Splits <paramref name="value"/> into its functions and arguments.
    /// </summary>
    /// <remarks>
    /// Never throws, and never judges: what is malformed comes back as functions it could read and
    /// braces it could not place, because the callers are a parser that must not fail a document and
    /// a source view whose job is to say where the fault is.
    /// </remarks>
    public static SvgTransformExpressionValue Parse(string localName, string? value)
    {
        if (localName == PathDataName)
        {
            return ParsePathData(value ?? string.Empty);
        }

        if (localName != DashArrayName)
        {
            return Parse(value);
        }

        var text = value ?? string.Empty;
        var functions = new List<SvgTransformExpressionFunction>();
        var stray = new List<SvgTransformExpressionArgument>();

        ReadArguments(text, DashArrayName, 0, functions, stray, endsList: true);

        return new SvgTransformExpressionValue(text, functions, stray);
    }

    /// <summary>Splits a <c>transform</c> value into its functions and arguments.</summary>
    public static SvgTransformExpressionValue Parse(string? value)
    {
        var text = value ?? string.Empty;
        var functions = new List<SvgTransformExpressionFunction>();
        var stray = new List<SvgTransformExpressionArgument>();
        var index = 0;

        while (index < text.Length)
        {
            while (index < text.Length && (char.IsWhiteSpace(text[index]) || text[index] == ','))
            {
                index++;
            }

            if (index >= text.Length)
            {
                break;
            }

            var open = text.IndexOf('(', index);

            if (open < 0)
            {
                Stray(text, index, text.Length, stray);
                break;
            }

            var name = text.Substring(index, open - index).Trim();

            if (name.Length == 0 || name.IndexOf(Open, StringComparison.Ordinal) >= 0)
            {
                // A name is not a place an expression can go, so braces reaching here drive nothing.
                Stray(text, index, open, stray);
            }

            index = ReadArguments(text, name, open + 1, functions, stray);
        }

        return new SvgTransformExpressionValue(text, functions, stray);
    }

    /// <summary>
    /// The value that leaves an argument doing nothing.
    /// </summary>
    /// <remarks>
    /// One where the slot scales and zero everywhere else, so an unbound <c>scale</c> draws the shape
    /// at the size it was authored rather than collapsing it to a point. A dash item has no value
    /// that does nothing, and one rather than nought because an array summing to nought is dropped
    /// while it is recorded, taking the expressions that were to ride on it with it.
    /// </remarks>
    internal static string Identity(string function, int index)
        => function == DashArrayName || (function == "scale" && index < 2) || (function == "matrix" && (index == 0 || index == 3))
            ? "1"
            : "0";

    /// <summary>Reads one function's arguments, returning where the scan carries on.</summary>
    private static int ReadArguments(
        string text,
        string name,
        int start,
        List<SvgTransformExpressionFunction> functions,
        List<SvgTransformExpressionArgument> stray,
        bool endsList = false)
    {
        var arguments = new List<SvgTransformExpressionArgument>();
        var index = start;
        var token = -1;

        while (index < text.Length)
        {
            if (Fenced(text, index))
            {
                var close = text.IndexOf(Close, index + Open.Length, StringComparison.Ordinal);

                if (close < 0)
                {
                    // Unterminated, so nothing after it can be read as anything.
                    Flush(text, name, token, text.Length, arguments, stray);

                    return text.Length;
                }

                if (token < 0)
                {
                    token = index;
                }

                index = close + Close.Length;
                continue;
            }

            var character = text[index];

            if (character == ')' && !endsList)
            {
                Flush(text, name, token, index, arguments, stray);
                functions.Add(new SvgTransformExpressionFunction(name, arguments));

                return index + 1;
            }

            if (char.IsWhiteSpace(character) || character == ',')
            {
                Flush(text, name, token, index, arguments, stray);
                token = -1;
                index++;
                continue;
            }

            if (token < 0)
            {
                token = index;
            }

            index++;
        }

        Flush(text, name, token, text.Length, arguments, stray);

        if (endsList)
        {
            functions.Add(new SvgTransformExpressionFunction(name, arguments));

            return text.Length;
        }

        // Unclosed, so the arguments read so far belong to nothing that can be applied.

        foreach (var argument in arguments)
        {
            if (argument.IsExpression)
            {
                stray.Add(argument);
            }
        }

        return text.Length;
    }

    /// <summary>Splits path data into one function per segment, as <c>SvgPathBuilder</c> segments it.</summary>
    /// <remarks>
    /// The numbers are scanned as SVG writes them, so <c>10-5</c> is two and an arc's flags are one
    /// character each, and a segment the builder would draw is a function here with the same
    /// arguments in the same slots. An expression has to stand apart — a separator, a command letter
    /// or the end on either side — since <c>5{{ x }}</c> is no number the builder could read once the
    /// stand-in is written into it.
    /// </remarks>
    private static SvgTransformExpressionValue ParsePathData(string text)
    {
        var functions = new List<SvgTransformExpressionFunction>();
        var stray = new List<SvgTransformExpressionArgument>();
        var group = new List<SvgTransformExpressionArgument>();
        var command = '\0';
        var arity = -1;
        var repeated = false;
        var index = 0;

        while (index < text.Length)
        {
            var character = text[index];

            if (IsSeparator(character))
            {
                index++;
                continue;
            }

            if (Fenced(text, index))
            {
                var close = text.IndexOf(Close, index + Open.Length, StringComparison.Ordinal);
                var end = close < 0 ? text.Length : close + Close.Length;
                var written = text.Substring(index, end - index);
                var apart = (index == 0 || Separates(text[index - 1])) && (end == text.Length || Separates(text[end]));

                if (close >= 0 && apart && arity > 0 && SvgExpressionAttributes.TryUnwrap(written, out var expression))
                {
                    group.Add(new SvgTransformExpressionArgument(Segment(command, repeated), group.Count, index, written.Length, expression, written));
                }
                else
                {
                    stray.Add(new SvgTransformExpressionArgument(string.Empty, -1, index, written.Length, null, written));
                }

                index = end;
                Complete();
                continue;
            }

            if (IsCommand(character))
            {
                Abandon();
                command = character;
                arity = Arity(character);
                repeated = false;
                index++;

                if (arity == 0)
                {
                    functions.Add(new SvgTransformExpressionFunction(character.ToString(), Array.Empty<SvgTransformExpressionArgument>()));
                }

                continue;
            }

            // An arc's flag is one character, and only these two: anything else ends the command, as
            // it ends CoordinateParser's.
            var flag = arity == 7 && group.Count is 3 or 4;
            var length = flag ? (character is '0' or '1' ? 1 : 0) : ScanNumber(text, index);

            // CoordinateParser stops a command at anything it cannot read, so nothing after it joins
            // the command until the next letter.
            if (length == 0)
            {
                Abandon();
                arity = -1;
                index++;
                continue;
            }

            if (arity > 0)
            {
                group.Add(new SvgTransformExpressionArgument(Segment(command, repeated), group.Count, index, length, null, text.Substring(index, length)));
            }

            index += length;
            Complete();
        }

        Abandon();

        return new SvgTransformExpressionValue(text, functions, stray);

        void Complete()
        {
            if (arity <= 0 || group.Count < arity)
            {
                return;
            }

            functions.Add(new SvgTransformExpressionFunction(Segment(command, repeated), group));
            group = new List<SvgTransformExpressionArgument>();
            repeated = true;
        }

        // A segment left short of its numbers draws nothing, so what was written in it drives nothing.
        void Abandon()
        {
            foreach (var argument in group)
            {
                if (argument.IsExpression)
                {
                    stray.Add(argument);
                }
            }

            group.Clear();
        }
    }

    // After its first segment a move goes on as lines, as SvgPathBuilder reads it.
    private static string Segment(char command, bool repeated)
        => (repeated ? command switch { 'M' => 'L', 'm' => 'l', _ => command } : command).ToString();

    // Any letter but an exponent's, as SvgPathBuilder decides where a command starts.
    private static bool IsCommand(char character) => char.IsLetter(character) && character != 'e' && character != 'E';

    // CoordinateParser's separators, which are fewer than char.IsWhiteSpace's.
    private static bool IsSeparator(char character) => character is ' ' or '\t' or '\n' or '\r' or ',';

    private static bool Separates(char character) => IsSeparator(character) || IsCommand(character);

    private static int Arity(char command) => char.ToUpperInvariant(command) switch
    {
        'M' or 'L' or 'T' => 2,
        'H' or 'V' or 'B' => 1,
        'Q' or 'S' => 4,
        'C' => 6,
        'A' => 7,
        'Z' => 0,
        _ => -1
    };

    // A sign starts a number, and so does a second point: 10-5 is two numbers and so is .5.5.
    private static int ScanNumber(string text, int start)
    {
        var index = start;

        if (index < text.Length && text[index] is '+' or '-')
        {
            index++;
        }

        var digits = Digits(text, ref index);

        if (index < text.Length && text[index] == '.')
        {
            index++;
            digits += Digits(text, ref index);
        }

        if (digits == 0)
        {
            return 0;
        }

        if (index < text.Length && text[index] is 'e' or 'E')
        {
            var exponent = index + 1;

            if (exponent < text.Length && text[exponent] is '+' or '-')
            {
                exponent++;
            }

            if (Digits(text, ref exponent) > 0)
            {
                index = exponent;
            }
        }

        return index - start;
    }

    private static int Digits(string text, ref int index)
    {
        var start = index;

        while (index < text.Length && char.IsDigit(text[index]))
        {
            index++;
        }

        return index - start;
    }

    private static bool Fenced(string text, int index)
        => text[index] == '{' && index + 1 < text.Length && text[index + 1] == '{';

    private static void Flush(
        string text,
        string name,
        int start,
        int end,
        List<SvgTransformExpressionArgument> arguments,
        List<SvgTransformExpressionArgument> stray)
    {
        if (start < 0 || end <= start)
        {
            return;
        }

        var written = text.Substring(start, end - start);
        var index = arguments.Count;

        if (SvgExpressionAttributes.TryUnwrap(written, out var expression))
        {
            arguments.Add(new SvgTransformExpressionArgument(name, index, start, written.Length, expression, written));

            return;
        }

        arguments.Add(new SvgTransformExpressionArgument(name, index, start, written.Length, null, written));

        if (written.IndexOf(Open, StringComparison.Ordinal) >= 0)
        {
            stray.Add(arguments[index]);
        }
    }

    private static void Stray(string text, int start, int end, List<SvgTransformExpressionArgument> stray)
    {
        if (end <= start)
        {
            return;
        }

        var written = text.Substring(start, end - start);

        if (written.IndexOf(Open, StringComparison.Ordinal) < 0)
        {
            return;
        }

        stray.Add(new SvgTransformExpressionArgument(string.Empty, -1, start, written.Length, null, written));
    }
}
