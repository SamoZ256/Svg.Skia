// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Svg.Expressions;

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
    private readonly Dictionary<string, PaintCodeGradient> _gradients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _gradientExpressions = new(StringComparer.Ordinal);

    private SvgExpressionDeclarations? _resolved;
    private ExprEvaluator? _evaluator;

    private PaintCodeDeclarations()
    {
    }

    /// <summary>
    /// What <paramref name="expression"/> comes to with every parameter left at its default.
    /// </summary>
    /// <remarks>
    /// A driven transform needs this. PaintCode stores, beside the expression, the number the
    /// property was set to -- not the number the expression produced -- and writes the difference
    /// between the two into its own generated code as a constant. Working that difference out means
    /// evaluating the expression the way the document would on opening.
    /// </remarks>
    internal bool TryValue(string expression, out double value)
    {
        value = 0;

        try
        {
            _evaluator ??= ExprEvaluator.Create(Resolved());
            value = _evaluator.Evaluate(expression).AsNumber;

            return true;
        }
        catch (Exception failure) when (failure is ExprException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private SvgExpressionDeclarations Resolved()
    {
        if (_resolved is { })
        {
            return _resolved;
        }

        var builder = new SvgExpressionDeclarations.Builder();

        foreach (var declaration in _byName.Values)
        {
            if (declaration.Kind is PaintCodeDeclarationKind.Parameter)
            {
                builder.AddParameter(declaration.Name, declaration.Type, declaration.Body);
            }
        }

        // In dependency order, since a let may only name what is declared above it.
        foreach (var name in Locals())
        {
            if (_byName[name].Body is { } body)
            {
                builder.AddLet(name, body);
            }
        }

        return _resolved = builder.Build();
    }

    internal IReadOnlyDictionary<string, PaintCodeDeclaration> ByName => _byName;

    internal static PaintCodeDeclarations Of(PaintCodeDocument document, bool integers = false)
    {
        var declarations = new PaintCodeDeclarations();

        foreach (var gradient in document.Gradients)
        {
            declarations._gradients[PaintCodeSlug.Identifier(gradient.Name)] = gradient;
        }

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

            // A gradient-valued variable cannot be declared, since the format has no gradient type,
            // but its expression is still what chooses between the gradients a stop reads.
            if (variable.Kind is PaintCodeValueKind.Gradient)
            {
                var name = PaintCodeSlug.Identifier(variable.Name);

                if (variable.Expression is { } chooses)
                {
                    declarations._gradientExpressions[name] = chooses;
                }
                else if (variable.Value.Gradient is { } value)
                {
                    declarations._gradients[name] = value;
                }
            }

            declarations.Add(Variable(variable, integers));
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
                    _byName[name] = PaintCodeDeclaration.Local(name, expression, declaration.Type ?? "number")
                        .From(declaration.Body);
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

    /// <summary>
    /// The locals, each after everything it is built from.
    /// </summary>
    /// <remarks>
    /// The order a symbol's scope has to be built in: a local that reads a rebound variable is itself
    /// rebound, and one built from that local is rebound in turn.
    /// </remarks>
    internal IEnumerable<string> Locals()
    {
        var written = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();

        void Write(string name)
        {
            if (!_byName.TryGetValue(name, out var declaration) ||
                declaration.Kind is not PaintCodeDeclarationKind.Local ||
                !written.Add(name))
            {
                return;
            }

            foreach (var needed in Names(declaration.Body ?? string.Empty))
            {
                Write(needed);
            }

            order.Add(name);
        }

        foreach (var name in new List<string>(_byName.Keys))
        {
            Write(name);
        }

        return order;
    }

    /// <summary>The gradient a name stands for, where the library names one.</summary>
    internal PaintCodeGradient? Gradient(string name)
        => _gradients.TryGetValue(name, out var gradient) ? gradient : null;

    /// <summary>
    /// The colour each stop of <paramref name="source"/> takes, where the expression chooses between
    /// gradients.
    /// </summary>
    /// <remarks>
    /// The expression format has no gradient, and says so: a gradient is parameterised through its
    /// own stop colours instead. So the one expression is translated once per stop, in a scope where
    /// every gradient name stands for that stop's colour — which only works where the gradients being
    /// chosen between have the same number of stops, since no expression can vary that.
    /// </remarks>
    internal bool TryStops(string source, IReadOnlyDictionary<string, string>? overrides, out IReadOnlyList<string> stops, out string refusal)
    {
        stops = System.Array.Empty<string>();
        refusal = string.Empty;

        var reachable = new Dictionary<string, PaintCodeGradient>(StringComparer.Ordinal);

        if (!Reach(source, reachable, new HashSet<string>(StringComparer.Ordinal), ref refusal))
        {
            return false;
        }

        if (reachable.Count == 0)
        {
            refusal = "it names no gradient this document holds";

            return false;
        }

        var count = -1;

        foreach (var gradient in reachable.Values)
        {
            if (count < 0)
            {
                count = gradient.Stops.Count;

                continue;
            }

            if (gradient.Stops.Count != count)
            {
                refusal = $"'{gradient.Name}' has {gradient.Stops.Count} stops where another has {count}, and no expression can vary that";

                return false;
            }
        }

        var written = new List<string>(count);

        for (var index = 0; index < count; index++)
        {
            var scope = new Dictionary<string, string>(StringComparer.Ordinal);

            if (overrides is { })
            {
                foreach (var given in overrides)
                {
                    scope[given.Key] = given.Value;
                }
            }

            foreach (var gradient in reachable)
            {
                scope[gradient.Key] = Stop(gradient.Value.Stops[index].Color);
            }

            // Deepest first, so a variable built from another has what it names already in the scope.
            foreach (var name in Ordered())
            {
                if (scope.ContainsKey(name) ||
                    !PaintCodeExpressionTranslator.TryTranslate(_gradientExpressions[name], this, out var chosen, out refusal, scope))
                {
                    continue;
                }

                scope[name] = chosen;
            }

            if (!PaintCodeExpressionTranslator.TryTranslate(source, this, out var expression, out refusal, scope))
            {
                return false;
            }

            written.Add(expression);
        }

        stops = written;

        return true;
    }

    /// <summary>Every gradient an expression can end up reading, through as many variables as it takes.</summary>
    private bool Reach(string source, Dictionary<string, PaintCodeGradient> found, HashSet<string> seen, ref string refusal)
    {
        foreach (var name in Names(source))
        {
            if (_gradients.TryGetValue(name, out var gradient))
            {
                found[name] = gradient;

                continue;
            }

            if (!_gradientExpressions.TryGetValue(name, out var chooses) || !seen.Add(name))
            {
                continue;
            }

            if (!Reach(chooses, found, seen, ref refusal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Gradient variables, the ones built from others last.</summary>
    private IEnumerable<string> Ordered()
    {
        var names = new List<string>(_gradientExpressions.Keys);
        var written = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();

        void Write(string name)
        {
            if (!_gradientExpressions.TryGetValue(name, out var body) || !written.Add(name))
            {
                return;
            }

            foreach (var needed in Names(body))
            {
                Write(needed);
            }

            order.Add(name);
        }

        foreach (var name in names)
        {
            Write(name);
        }

        return order;
    }

    /// <summary>A stop's colour as the name a drawing can bind, or as the bytes it simply is.</summary>
    internal string Stop(PaintCodeColor color)
    {
        var name = PaintCodeSlug.Identifier(color.Name);

        return color.Name.Length > 0 &&
               _byName.TryGetValue(name, out var declaration) &&
               declaration.Kind is PaintCodeDeclarationKind.Parameter or PaintCodeDeclarationKind.Local
            ? name
            : Literal(color);
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

            // A colour literal's digits start with a letter often enough to read as a name.
            if (expression[at] == '#')
            {
                at++;

                while (at < expression.Length && Uri.IsHexDigit(expression[at]))
                {
                    at++;
                }

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

    /// <summary>
    /// The same expression with every name the scope rebinds replaced by what it is now bound to.
    /// </summary>
    /// <remarks>
    /// For a local already written in this format's own syntax rather than PaintCode's, which the
    /// translator cannot be asked to read again. Tokenised exactly as <see cref="Names"/> does, so a
    /// name inside a string or the letters of a colour literal are left where they are, and each
    /// replacement is bracketed: splicing a conditional in raw would re-associate it against
    /// whatever sits beside it.
    /// </remarks>
    internal static string Rebound(string expression, IReadOnlyDictionary<string, string> scope)
    {
        var rebuilt = new StringBuilder(expression.Length);

        for (var at = 0; at < expression.Length;)
        {
            if (expression[at] == '\'' || expression[at] == '"')
            {
                var quote = expression[at++];
                var opened = at - 1;

                while (at < expression.Length && expression[at] != quote)
                {
                    at += expression[at] == '\\' ? 2 : 1;
                }

                at++;
                rebuilt.Append(expression, opened, Math.Min(at, expression.Length) - opened);

                continue;
            }

            if (expression[at] == '#')
            {
                var opened = at++;

                while (at < expression.Length && Uri.IsHexDigit(expression[at]))
                {
                    at++;
                }

                rebuilt.Append(expression, opened, at - opened);

                continue;
            }

            if (!char.IsLetter(expression[at]) && expression[at] != '_')
            {
                rebuilt.Append(expression[at++]);

                continue;
            }

            var start = at;

            while (at < expression.Length && (char.IsLetterOrDigit(expression[at]) || expression[at] == '_'))
            {
                at++;
            }

            var name = expression.Substring(start, at - start);

            rebuilt.Append(scope.TryGetValue(name, out var bound) ? "(" + bound + ")" : name);
        }

        return rebuilt.ToString();
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

    private static PaintCodeDeclaration Variable(PaintCodeVariable variable, bool integers)
    {
        var name = PaintCodeSlug.Identifier(variable.Name);
        var type = integers && IsWhole(variable) ? "integer" : Type(variable.Kind);

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

    /// <summary>Whether a number variable is one this would write as an integer.</summary>
    /// <remarks>
    /// An input rather than a derived expression, whose value is whole, and whose ends are whole
    /// where it has any. A derived one is left out because its body is PaintCode's arithmetic in
    /// PaintCode's one numeric type, and retyping the answer without retyping the working would
    /// refuse the document rather than improve it.
    /// </remarks>
    private static bool IsWhole(PaintCodeVariable variable)
        => variable.Kind is PaintCodeValueKind.Number
           && !variable.IsDerived
           && variable.Value.Number is { } value
           && Whole(value)
           && (variable.Minimum is not { } minimum || Whole(minimum))
           && (variable.Maximum is not { } maximum || Whole(maximum));

    // In range as well as whole: an integer is 32 bits and a PaintCode number is a double.
    private static bool Whole(double value)
        => value == Math.Floor(value) && value >= int.MinValue && value <= int.MaxValue;

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

            // Written out rather than through Number, whose "0.####" would be right for every value
            // that reaches here and wrong the moment one did not.
            "integer" => value.Number is { } whole && Whole(whole)
                ? ((int)whole).ToString(CultureInfo.InvariantCulture)
                : null,
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

    /// <summary>PaintCode's own text for a local, kept so it can be translated again in a symbol's scope.</summary>
    internal string? Source { get; private set; }

    internal PaintCodeDeclaration From(string? source)
    {
        Source = source;

        return this;
    }

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
