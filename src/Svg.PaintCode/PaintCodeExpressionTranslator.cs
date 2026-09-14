// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Svg.PaintCode;

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
        ["pow"] = "pow"
    };

    internal static bool TryTranslate(
        string source,
        PaintCodeDeclarations declarations,
        out string expression,
        out string refusal,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        var translator = new PaintCodeExpressionTranslator(source ?? string.Empty, declarations, overrides);
        var text = translator.Conditional();

        translator.SkipSpace();

        if (translator._refusal is null && translator._at < translator._source.Length)
        {
            translator.Refuse($"'{translator._source.Substring(translator._at)}' is left over");
        }

        expression = text ?? string.Empty;
        refusal = translator._refusal ?? string.Empty;

        return translator._refusal is null && expression.Length > 0;
    }

    private string? Conditional()
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

        return whenTrue is null || whenFalse is null ? null : $"{condition} ? {whenTrue} : {whenFalse}";
    }

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

    private string? Binary(int level)
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

                left = target == "mod" ? $"mod({left}, {right})" : $"{left} {target} {right}";
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

    private string? Unary()
    {
        SkipSpace();

        if (Take('!'))
        {
            var operand = Unary();

            return operand is null ? null : $"not {operand}";
        }

        if (Take('-'))
        {
            var operand = Unary();

            return operand is null ? null : $"-{operand}";
        }

        if (Take('+'))
        {
            return Unary();
        }

        return Primary();
    }

    private string? Primary()
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

            return inner is null ? null : $"({inner})";
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

    private string? Text(char quote)
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
        return "'" + text.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
    }

    private string? Number()
    {
        var start = _at;

        while (_at < _source.Length && (char.IsDigit(_source[_at]) || _source[_at] == '.'))
        {
            _at++;
        }

        var text = _source.Substring(start, _at - start);

        // The target grammar has no exponent form, so a number that only prints with one cannot go.
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? PaintCodeDeclarations.Number(value)
            : Refuse($"'{text}' is not a number this can write");
    }

    private string? Name()
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

    private string? Reference(string name)
    {
        if (name is "true" or "false")
        {
            return name;
        }

        if (_overrides is { } overrides && overrides.TryGetValue(name, out var substituted))
        {
            return substituted;
        }

        if (!_declarations.ByName.TryGetValue(name, out var declaration))
        {
            return Refuse($"'{name}' is not a name this document declares");
        }

        return declaration.Kind switch
        {
            PaintCodeDeclarationKind.Constant when declaration.Body is { } literal => literal,
            PaintCodeDeclarationKind.Unusable => Refuse($"'{name}' cannot be declared: {declaration.Refusal}"),
            _ when Reserved.Contains(name) => Refuse($"'{name}' is a word the expression language reserves"),
            _ => name
        };
    }

    private string? Member(string name)
    {
        var start = _at;

        while (_at < _source.Length && (_source[_at] == '.' || char.IsLetterOrDigit(_source[_at]) || _source[_at] == '_'))
        {
            _at++;
        }

        var path = name + _source.Substring(start, _at - start);

        return _declarations.TryMember(path, out var value)
            ? PaintCodeDeclarations.Number(value)
            : Refuse($"'{path}' reads part of a value, which the expression language cannot do");
    }

    private string? Call(string name)
    {
        _at++;

        var arguments = new List<string>();

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

                arguments.Add(argument);

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

    private string? Apply(string name, IReadOnlyList<string> arguments)
    {
        // PaintCode's trigonometry is in degrees and the target's is in radians.
        if (name is "sin" or "cos" or "tan" && arguments.Count == 1)
        {
            return $"{name}(({arguments[0]}) * pi / 180)";
        }

        // Its channels are fractions of one, and rgba's are bytes with an alpha that is not.
        if (name == "makeColor" && arguments.Count == 4)
        {
            return $"rgba(({arguments[0]}) * 255, ({arguments[1]}) * 255, ({arguments[2]}) * 255, {arguments[3]})";
        }

        if (Functions.TryGetValue(name, out var target))
        {
            return $"{target}({string.Join(", ", arguments)})";
        }

        return Refuse($"'{name}' is a function the expression language does not have");
    }

    private string? Refuse(string reason)
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
