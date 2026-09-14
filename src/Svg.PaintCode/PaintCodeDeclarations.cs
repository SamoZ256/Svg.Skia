// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Svg.PaintCode;

/// <summary>What a drawing's expressions may name: parameters, locals, and folded constants.</summary>
/// <remarks>
/// PaintCode keeps three kinds of name in one library. A variable or colour marked as used is asked
/// for by the drawing, so it becomes a parameter; one derived from others becomes a local; the rest
/// are values nobody can change, and are written into the expressions that name them.
/// </remarks>
internal sealed class PaintCodeDeclarations
{
    private readonly Dictionary<string, PaintCodeDeclaration> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PaintCodeRect> _rects = new(StringComparer.Ordinal);

    private PaintCodeDeclarations()
    {
    }

    internal IReadOnlyDictionary<string, PaintCodeDeclaration> ByName => _byName;

    internal static PaintCodeDeclarations Of(PaintCodeDocument document)
    {
        var declarations = new PaintCodeDeclarations();

        foreach (var color in document.Colors)
        {
            declarations.Add(declarations.Color(color));
        }

        foreach (var variable in document.Variables)
        {
            if (variable.Kind is PaintCodeValueKind.Rect && variable.Value.Rect is { } rect)
            {
                declarations._rects[PaintCodeSlug.Identifier(variable.Name)] = rect;

                continue;
            }

            declarations.Add(Variable(variable));
        }

        declarations.Resolve();

        return declarations;
    }

    /// <summary>
    /// Translates every local once, and keeps doing it until nothing more changes.
    /// </summary>
    /// <remarks>
    /// More than one pass because a local can be built from another: the first pass refuses one whose
    /// dependency has not been translated yet, and a local built from one that refuses has to refuse
    /// too rather than name something that will not be written.
    /// </remarks>
    private void Resolve()
    {
        for (var pass = 0; pass < _byName.Count + 1; pass++)
        {
            var changed = false;

            foreach (var name in new List<string>(_byName.Keys))
            {
                var declaration = _byName[name];

                if (declaration.Kind is not PaintCodeDeclarationKind.Local || !declaration.NeedsTranslation)
                {
                    continue;
                }

                if (PaintCodeExpressionTranslator.TryTranslate(declaration.Body ?? string.Empty, this, out var expression, out var refusal))
                {
                    _byName[name] = PaintCodeDeclaration.Local(name, expression, declaration.Type ?? "number");
                    changed = true;

                    continue;
                }

                declaration.Refuse(refusal);
            }

            if (!changed)
            {
                break;
            }
        }

        foreach (var name in new List<string>(_byName.Keys))
        {
            var declaration = _byName[name];

            if (declaration.Kind is PaintCodeDeclarationKind.Local && declaration.NeedsTranslation)
            {
                _byName[name] = PaintCodeDeclaration.Unusable(name, declaration.Refusal ?? "it could not be translated");
            }
        }
    }

    /// <summary>The names a translated expression reads, so what it needs can be declared with it.</summary>
    internal static IEnumerable<string> Names(string expression)
    {
        for (var at = 0; at < expression.Length;)
        {
            if (expression[at] == '\'' || expression[at] == '"')
            {
                var quote = expression[at++];

                while (at < expression.Length && expression[at] != quote)
                {
                    at += expression[at] == '\\' ? 2 : 1;
                }

                at++;

                continue;
            }

            if (!char.IsLetter(expression[at]) && expression[at] != '_')
            {
                at++;

                continue;
            }

            var start = at;

            while (at < expression.Length && (char.IsLetterOrDigit(expression[at]) || expression[at] == '_'))
            {
                at++;
            }

            yield return expression.Substring(start, at - start);
        }
    }

    /// <summary>The number a dotted path names, where every part of it is a constant.</summary>
    /// <remarks>
    /// The expression language has neither a rectangle nor member access, so <c>bound.size.height</c>
    /// can only survive as the number it already is — which it is, whenever the rectangle is one
    /// nothing drives.
    /// </remarks>
    internal bool TryMember(string path, out double value)
    {
        value = 0;

        var parts = path.Split('.');

        if (parts.Length < 2 || !_rects.TryGetValue(parts[0], out var rect))
        {
            return false;
        }

        var tail = string.Join(".", parts, 1, parts.Length - 1);

        switch (tail)
        {
            case "size.width" or "width":
                value = rect.Width;

                return true;

            case "size.height" or "height":
                value = rect.Height;

                return true;

            case "origin.x" or "x":
                value = rect.X;

                return true;

            case "origin.y" or "y":
                value = rect.Y;

                return true;

            default:
                return false;
        }
    }

    private void Add(PaintCodeDeclaration declaration)
    {
        if (declaration.Name.Length > 0)
        {
            _byName[declaration.Name] = declaration;
        }
    }

    private PaintCodeDeclaration Color(PaintCodeLibraryColor color)
    {
        var name = PaintCodeSlug.Identifier(color.Name);

        if (color.IsParameter)
        {
            return PaintCodeDeclaration.Parameter(name, "color", Literal(color.Value));
        }

        // A derived colour stays derived, so binding the parent it came from carries through to it
        // the way it does in PaintCode -- but only where that parent is something a caller can
        // change. Derived from a value nothing drives, it is itself a value nothing drives, and the
        // reader has already worked out what it is.
        if (color.ParentName is { } parent &&
            color.Alpha is { } alpha &&
            _byName.TryGetValue(PaintCodeSlug.Identifier(parent), out var declaration) &&
            declaration.Kind is PaintCodeDeclarationKind.Parameter or PaintCodeDeclarationKind.Local)
        {
            return PaintCodeDeclaration.Local(
                name,
                $"withAlpha({declaration.Name}, {Number(alpha)})",
                "color");
        }

        return PaintCodeDeclaration.Constant(name, "color", Literal(color.Value), color.Value.IsApproximate);
    }

    private static PaintCodeDeclaration Variable(PaintCodeVariable variable)
    {
        var name = PaintCodeSlug.Identifier(variable.Name);
        var type = Type(variable.Kind);

        if (type is null)
        {
            return PaintCodeDeclaration.Unusable(name, $"a {variable.Kind.ToString().ToLowerInvariant()} has no type in the expression format");
        }

        if (variable.Expression is { } expression)
        {
            return PaintCodeDeclaration.Local(name, expression, type, translate: true);
        }

        var literal = Literal(variable.Value, type);

        if (literal is null)
        {
            return PaintCodeDeclaration.Unusable(name, "its value could not be written as a literal");
        }

        return variable.IsParameter
            ? PaintCodeDeclaration.Parameter(name, type, literal, variable.Minimum, variable.Maximum)
            : PaintCodeDeclaration.Constant(name, type, literal);
    }

    private static string? Type(PaintCodeValueKind kind)
        => kind switch
        {
            PaintCodeValueKind.Number => "number",
            PaintCodeValueKind.Boolean => "boolean",
            PaintCodeValueKind.String => "string",
            PaintCodeValueKind.Color => "color",
            _ => null
        };

    internal static string? Literal(PaintCodeBinding value, string type)
        => type switch
        {
            "number" => value.Number is { } number ? Number(number) : null,
            "boolean" => value.Flag is { } flag ? (flag ? "true" : "false") : null,
            "string" => value.Text is { } text ? "'" + text.Replace("\\", "\\\\").Replace("'", "\\'") + "'" : null,
            "color" => value.Color is { } color ? Literal(color) : null,
            _ => null
        };

    /// <summary>A colour as the eight hex digits the expression language reads, alpha included.</summary>
    internal static string Literal(PaintCodeColor color)
    {
        var alpha = (int)Math.Round(Math.Max(0, Math.Min(1, color.Alpha)) * 255, MidpointRounding.AwayFromZero);

        return string.Format(CultureInfo.InvariantCulture, "#{0:x2}{1:x2}{2:x2}{3:x2}", color.Red, color.Green, color.Blue, alpha);
    }

    internal static string Number(double value) => PaintCodePathData.Number(value);
}

internal enum PaintCodeDeclarationKind
{
    Parameter,
    Local,
    Constant,
    Unusable
}

internal sealed class PaintCodeDeclaration
{
    private PaintCodeDeclaration(PaintCodeDeclarationKind kind, string name, string? type, string? body)
    {
        Kind = kind;
        Name = name;
        Type = type;
        Body = body;
    }

    internal PaintCodeDeclarationKind Kind { get; }

    internal string Name { get; }

    internal string? Type { get; }

    /// <summary>A parameter's default, a local's expression, or a constant's literal.</summary>
    internal string? Body { get; }

    /// <summary>Whether <see cref="Body"/> is PaintCode's own text and still needs translating.</summary>
    internal bool NeedsTranslation { get; private set; }

    internal double? Minimum { get; private set; }

    internal double? Maximum { get; private set; }

    internal string? Refusal { get; private set; }

    internal void Refuse(string reason) => Refusal = reason;

    internal bool IsApproximate { get; private set; }

    internal static PaintCodeDeclaration Parameter(string name, string type, string? @default, double? minimum = null, double? maximum = null)
        => new(PaintCodeDeclarationKind.Parameter, name, type, @default) { Minimum = minimum, Maximum = maximum };

    internal static PaintCodeDeclaration Local(string name, string body, string type, bool translate = false)
        => new(PaintCodeDeclarationKind.Local, name, type, body) { NeedsTranslation = translate };

    internal static PaintCodeDeclaration Constant(string name, string type, string? literal, bool approximate = false)
        => new(PaintCodeDeclarationKind.Constant, name, type, literal) { IsApproximate = approximate };

    internal static PaintCodeDeclaration Unusable(string name, string refusal)
        => new(PaintCodeDeclarationKind.Unusable, name, null, null) { Refusal = refusal };
}
