// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Svg.PaintCode;

/// <summary>
/// The numeric type an expression comes out as, as far as the target language cares.
/// </summary>
/// <remarks>
/// The language never converts between an integer and a number, and PaintCode has only the one
/// type, so a translation has to say where the two meet. Whole is a literal, which the checker
/// settles to whichever type stands beside it; Other is anything arithmetic does not mix with.
/// </remarks>
internal enum PaintCodeSort
{
    Other,
    Number,
    Whole,
    Integer
}

/// <summary>
/// Rewrites a PaintCode expression as one the Svg expression extension reads.
/// </summary>
/// <remarks>
/// The two grammars agree on shape and precedence, so this parses and prints rather than building a
/// tree: what comes out keeps the author's own parentheses and reads like what went in. Three things
/// do not survive a substitution and are why a parser is needed at all — a remainder is a function
/// rather than an operator, trigonometry is in radians rather than degrees, and a colour is made from
/// bytes rather than from fractions.
///
/// Anything it cannot say refuses by name. Nothing is approximated here: the caller writes the value
/// the drawing had instead, and says so.
/// </remarks>
internal sealed class PaintCodeExpressionTranslator
{
    /// <summary>A translated expression and the sort it came out as.</summary>
    private readonly struct Typed
    {
        internal Typed(string text, PaintCodeSort sort)
        {
            Text = text;
            Sort = sort;
        }

        internal string Text { get; }

        internal PaintCodeSort Sort { get; }
    }

    private readonly string _source;
    private readonly PaintCodeDeclarations _declarations;
    private readonly IReadOnlyDictionary<string, string>? _overrides;
    private int _at;
    private string? _refusal;

    private PaintCodeExpressionTranslator(string source, PaintCodeDeclarations declarations, IReadOnlyDictionary<string, string>? overrides)
    {
        _source = source;
        _declarations = declarations;
        _overrides = overrides;
    }

    /// <summary>The words the target language reserves, which a PaintCode name may not collide with.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "and", "or", "not", "lt", "le", "gt", "ge", "eq", "ne", "true", "false", "pi", "tau"
    };

    private static readonly Dictionary<string, string> Functions = new(StringComparer.Ordinal)
    {
        ["abs"] = "abs",
        ["floor"] = "floor",
        ["ceil"] = "ceil",
        ["round"] = "round",
        ["sqrt"] = "sqrt",
        ["min"] = "min",
        ["max"] = "max",
        ["pow"] = "pow",

        // PaintCode's own name for the only crossing from a number to the words of a label.
        ["stringFromNumber"] = "str"
    };

    internal static bool TryTranslate(
        string source,
        PaintCodeDeclarations declarations,
        out string expression,
        out string refusal,
        IReadOnlyDictionary<string, string>? overrides = null)
        => TryTranslate(source, declarations, out expression, out _, out refusal, overrides);

    internal static bool TryTranslate(
        string source,
        PaintCodeDeclarations declarations,
        out string expression,
        out PaintCodeSort sort,
        out string refusal,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        var translator = new PaintCodeExpressionTranslator(source ?? string.Empty, declarations, overrides);
        var typed = translator.Conditional();

        translator.SkipSpace();

        if (translator._refusal is null && translator._at < translator._source.Length)
        {
            translator.Refuse($"'{translator._source.Substring(translator._at)}' is left over");
        }

        expression = typed?.Text ?? string.Empty;
        sort = typed?.Sort ?? PaintCodeSort.Other;
        refusal = translator._refusal ?? string.Empty;

        if (translator._refusal is { } || expression.Length == 0)
        {
            return false;
        }

        declarations.Remember(expression, sort);

        return true;
    }

    private Typed? Conditional()
    {
        var condition = Binary(0);

        if (condition is null || !Take('?'))
        {
            return condition;
        }

        var whenTrue = Conditional();

        if (!Take(':'))
        {
            return Refuse("a conditional has no ':'");
        }

        var whenFalse = Conditional();

        if (whenTrue is null || whenFalse is null)
        {
            return null;
        }

        var (yes, no, sort) = Met(whenTrue.Value, whenFalse.Value);

        return new Typed($"{condition.Value.Text} ? {yes} : {no}", sort);
    }

    /// <summary>
    /// Two operands as the language will take them side by side.
    /// </summary>
    /// <remarks>
    /// PaintCode has one numeric type and the target has two it never converts between, so an
    /// integer that meets a number goes through num(). A whole literal needs nothing: the checker
    /// settles it to whichever type is beside it.
    /// </remarks>
    private static (string Left, string Right, PaintCodeSort Sort) Met(Typed left, Typed right)
    {
        if (left.Sort is PaintCodeSort.Other || right.Sort is PaintCodeSort.Other)
        {
            return (left.Text, right.Text, PaintCodeSort.Other);
        }

        if (left.Sort is PaintCodeSort.Integer && right.Sort is PaintCodeSort.Number)
        {
            return (Num(left), right.Text, PaintCodeSort.Number);
        }

        if (left.Sort is PaintCodeSort.Number && right.Sort is PaintCodeSort.Integer)
        {
            return (left.Text, Num(right), PaintCodeSort.Number);
        }

        return (left.Text, right.Text, left.Sort is PaintCodeSort.Whole ? right.Sort : left.Sort);
    }

    private static string Num(Typed value) => value.Sort is PaintCodeSort.Integer ? $"num({value.Text})" : value.Text;

    // One table rather than one method per level: the two languages agree on every precedence, so
    // the only thing that varies is how each operator is spelled on the way out.
    private static readonly (string Source, string Target)[][] Levels =
    {
        new[] { ("||", "or") },
        new[] { ("&&", "and") },
        new[] { ("==", "=="), ("!=", "!=") },
        new[] { ("<=", "le"), (">=", ">="), ("<", "lt"), (">", ">") },
        new[] { ("+", "+"), ("-", "-") },
        new[] { ("*", "*"), ("/", "/"), ("%", "mod") }
    };

    private Typed? Binary(int level)
    {
        if (level >= Levels.Length)
        {
            return Unary();
        }

        var left = Binary(level + 1);

        while (left is { })
        {
            SkipSpace();

            var matched = false;

            foreach (var (source, target) in Levels[level])
            {
                if (!Peek(source))
                {
                    continue;
                }

                _at += source.Length;

                var right = Binary(level + 1);

                if (right is null)
                {
                    return null;
                }

                left = Combine(left.Value, target, right.Value);
                matched = true;

                break;
            }

            if (!matched)
            {
                break;
            }
        }

        return left;
    }

    /// <summary>
    /// Two operands and what joins them, with the joins that do nothing left out.
    /// </summary>
    /// <remarks>
    /// PaintCode writes "angle + 0" where it means "angle" -- it is how its editor spells a property
    /// that simply follows a variable. Keeping the addition would make 54 symbol instances look like
    /// they rebind something, and each would be copied instead of shared.
    /// </remarks>
    private static Typed Combine(Typed left, string target, Typed right)
    {
        switch (target)
        {
            case "+" or "-" when right.Text == "0":
            case "*" or "/" when right.Text == "1":
                return left;
        }

        // PaintCode divides reals, so two integers divide as numbers rather than to a whole.
        if (target == "/" && left.Sort is PaintCodeSort.Integer or PaintCodeSort.Whole && right.Sort is PaintCodeSort.Integer or PaintCodeSort.Whole
            && (left.Sort is PaintCodeSort.Integer || right.Sort is PaintCodeSort.Integer))
        {
            return new Typed($"{Num(left)} / {Num(right)}", PaintCodeSort.Number);
        }

        var (l, r, sort) = Met(left, right);

        return target switch
        {
            "mod" => new Typed($"mod({l}, {r})", sort),
            "and" or "or" or "==" or "!=" or "lt" or "le" or ">" or ">=" => new Typed($"{l} {target} {r}", PaintCodeSort.Other),
            _ => new Typed($"{l} {target} {r}", sort)
        };
    }

    private Typed? Unary()
    {
        SkipSpace();

        if (Take('!'))
        {
            var operand = Unary();

            return operand is null ? null : new Typed($"not {operand.Value.Text}", PaintCodeSort.Other);
        }

        if (Take('-'))
        {
            var operand = Unary();

            return operand is null ? null : new Typed($"-{operand.Value.Text}", operand.Value.Sort);
        }

        if (Take('+'))
        {
            return Unary();
        }

        return Primary();
    }

    private Typed? Primary()
    {
        SkipSpace();

        if (_at >= _source.Length)
        {
            return Refuse("it ends where a value was expected");
        }

        var character = _source[_at];

        if (character == '(')
        {
            _at++;

            var inner = Conditional();

            if (!Take(')'))
            {
                return Refuse("a bracket is not closed");
            }

            return inner is null ? null : new Typed($"({inner.Value.Text})", inner.Value.Sort);
        }

        if (character is '\'' or '"')
        {
            return Text(character);
        }

        if (char.IsDigit(character) || character == '.')
        {
            return Number();
        }

        return char.IsLetter(character) || character == '_'
            ? Name()
            : Refuse($"'{character}' has no meaning here");
    }

    private Typed? Text(char quote)
    {
        var start = ++_at;

        while (_at < _source.Length && _source[_at] != quote)
        {
            _at++;
        }

        if (_at >= _source.Length)
        {
            return Refuse("a string is not closed");
        }

        var text = _source.Substring(start, _at - start);
        _at++;

        // Single quotes, so the expression can sit in a double-quoted attribute without escaping.
        return new Typed("'" + text.Replace("\\", "\\\\").Replace("'", "\\'") + "'", PaintCodeSort.Other);
    }

    private Typed? Number()
    {
        var start = _at;

        while (_at < _source.Length && (char.IsDigit(_source[_at]) || _source[_at] == '.'))
        {
            _at++;
        }

        var text = _source.Substring(start, _at - start);

        // The target grammar has no exponent form, so a number that only prints with one cannot go.
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Literal(PaintCodeDeclarations.Number(value))
            : Refuse($"'{text}' is not a number this can write");
    }

    /// <summary>A literal as written, sorted by its spelling: whole digits settle either way, a point makes a number, the rest is not numeric.</summary>
    private static Typed Literal(string text)
    {
        var digits = text.Length > 0;

        foreach (var character in text)
        {
            if (character == '.')
            {
                return new Typed(text, PaintCodeSort.Number);
            }

            digits &= char.IsDigit(character) || character == '-';
        }

        return new Typed(text, digits ? PaintCodeSort.Whole : PaintCodeSort.Other);
    }

    private Typed? Name()
    {
        var start = _at;

        while (_at < _source.Length && (char.IsLetterOrDigit(_source[_at]) || _source[_at] == '_'))
        {
            _at++;
        }

        var name = _source.Substring(start, _at - start);

        SkipSpace();

        if (Peek("("))
        {
            return Call(name);
        }

        if (Peek("."))
        {
            return Member(name);
        }

        return Reference(name);
    }

    private Typed? Reference(string name)
    {
        if (name is "true" or "false")
        {
            return new Typed(name, PaintCodeSort.Other);
        }

        if (_overrides is { } overrides && overrides.TryGetValue(name, out var substituted))
        {
            return new Typed(Bracketed(substituted), SortOf(substituted));
        }

        if (!_declarations.ByName.TryGetValue(name, out var declaration))
        {
            return Refuse($"'{name}' is not a name this document declares");
        }

        return declaration.Kind switch
        {
            PaintCodeDeclarationKind.Constant when declaration.Body is { } literal => Literal(literal),
            PaintCodeDeclarationKind.Unusable => Refuse($"'{name}' cannot be declared: {declaration.Refusal}"),
            _ when Reserved.Contains(name) => Refuse($"'{name}' is a word the expression language reserves"),
            _ => new Typed(name, Sort(declaration))
        };
    }

    internal static PaintCodeSort Sort(PaintCodeDeclaration declaration)
        => declaration.Type switch
        {
            "integer" => PaintCodeSort.Integer,
            "number" => PaintCodeSort.Number,
            _ => PaintCodeSort.Other
        };

    /// <summary>
    /// What a substituted expression is.
    /// </summary>
    /// <remarks>
    /// A scope holds text, and the text is one of three things: a name this document declares, a
    /// literal an instance pinned, or a translation this document already produced -- whose sort is
    /// what it came out as, which the declarations kept.
    /// </remarks>
    private PaintCodeSort SortOf(string substituted)
    {
        if (_declarations.ByName.TryGetValue(substituted, out var declaration))
        {
            return Sort(declaration);
        }

        return _declarations.SortOf(substituted) ?? Literal(substituted).Sort;
    }

    /// <summary>
    /// A substituted expression, in brackets unless it is a single thing.
    /// </summary>
    /// <remarks>
    /// What is put in stands where a name stood, and a name binds tighter than anything. Splicing
    /// "state ? not isLight : isLight" in raw where "isLight" was re-associates the conditional it
    /// lands in: "isLight ? white : black" becomes "state ? not isLight : (isLight ? white : black)",
    /// which is a different drawing and, here, one that does not even type check.
    /// </remarks>
    private static string Bracketed(string expression)
    {
        foreach (var character in expression)
        {
            if (!char.IsLetterOrDigit(character) && character != '_' && character != '.' && character != '#')
            {
                return "(" + expression + ")";
            }
        }

        return expression;
    }

    private Typed? Member(string name)
    {
        var start = _at;

        while (_at < _source.Length && (_source[_at] == '.' || char.IsLetterOrDigit(_source[_at]) || _source[_at] == '_'))
        {
            _at++;
        }

        var path = name + _source.Substring(start, _at - start);

        return _declarations.TryMember(path, out var value)
            ? Literal(PaintCodeDeclarations.Number(value))
            : Refuse($"'{path}' reads part of a value, which the expression language cannot do");
    }

    private Typed? Call(string name)
    {
        _at++;

        var arguments = new List<Typed>();

        SkipSpace();

        if (!Take(')'))
        {
            while (true)
            {
                var argument = Conditional();

                if (argument is null)
                {
                    return null;
                }

                arguments.Add(argument.Value);

                if (Take(','))
                {
                    continue;
                }

                if (!Take(')'))
                {
                    return Refuse($"the arguments of '{name}' are not closed");
                }

                break;
            }
        }

        return Apply(name, arguments);
    }

    /// <summary>The functions that take integers as well as numbers, and answer in kind.</summary>
    private static readonly HashSet<string> Whole = new(StringComparer.Ordinal) { "abs", "min", "max" };

    private Typed? Apply(string name, IReadOnlyList<Typed> arguments)
    {
        // PaintCode's trigonometry is in degrees and the target's is in radians.
        if (name is "sin" or "cos" or "tan" && arguments.Count == 1)
        {
            return new Typed($"{name}(({Num(arguments[0])}) * pi / 180)", PaintCodeSort.Number);
        }

        // Its channels are fractions of one, and rgba's are bytes with an alpha that is not.
        if (name == "makeColor" && arguments.Count == 4)
        {
            return new Typed(
                $"rgba(({Num(arguments[0])}) * 255, ({Num(arguments[1])}) * 255, ({Num(arguments[2])}) * 255, {Num(arguments[3])})",
                PaintCodeSort.Other);
        }

        if (!Functions.TryGetValue(name, out var target))
        {
            return Refuse($"'{name}' is a function the expression language does not have");
        }

        if (target == "str")
        {
            return new Typed($"str({string.Join(", ", arguments.Select(argument => argument.Text))})", PaintCodeSort.Other);
        }

        // Kept whole where the function has an integer form and every argument is whole; otherwise
        // each argument is a number, and an integer among them goes through num().
        var integers = Whole.Contains(target)
            && arguments.All(argument => argument.Sort is PaintCodeSort.Integer or PaintCodeSort.Whole)
            && arguments.Any(argument => argument.Sort is PaintCodeSort.Integer);

        var texts = arguments.Select(argument => integers ? argument.Text : Num(argument));

        return new Typed($"{target}({string.Join(", ", texts)})", integers ? PaintCodeSort.Integer : PaintCodeSort.Number);
    }

    private Typed? Refuse(string reason)
    {
        _refusal ??= reason;

        return null;
    }

    private bool Peek(string text)
    {
        SkipSpace();

        if (_at + text.Length > _source.Length)
        {
            return false;
        }

        for (var index = 0; index < text.Length; index++)
        {
            if (_source[_at + index] != text[index])
            {
                return false;
            }
        }

        return true;
    }

    private bool Take(char character)
    {
        SkipSpace();

        if (_at >= _source.Length || _source[_at] != character)
        {
            return false;
        }

        _at++;

        return true;
    }

    private void SkipSpace()
    {
        while (_at < _source.Length && char.IsWhiteSpace(_source[_at]))
        {
            _at++;
        }
    }
}
