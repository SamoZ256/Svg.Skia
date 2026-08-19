// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace Svg.Expressions;

public sealed class SvgExpressionParameter
{
    public SvgExpressionParameter(string name, ExprType type, string? defaultExpression)
        : this(name, type, defaultExpression, null, null, null)
    {
    }

    public SvgExpressionParameter(
        string name,
        ExprType type,
        string? defaultExpression,
        string? minExpression,
        string? maxExpression,
        string? stepExpression)
    {
        Name = name;
        Type = type;
        DefaultExpression = defaultExpression;
        MinExpression = minExpression;
        MaxExpression = maxExpression;
        StepExpression = stepExpression;
    }

    public string Name { get; }

    public ExprType Type { get; }

    /// <summary>Author supplied default, in the expression language.</summary>
    public string? DefaultExpression { get; }

    /// <summary>Author supplied lower bound, in the expression language.</summary>
    public string? MinExpression { get; }

    /// <summary>Author supplied upper bound, in the expression language.</summary>
    public string? MaxExpression { get; }

    /// <summary>Author supplied increment, in the expression language.</summary>
    public string? StepExpression { get; }

    /// <summary>Whether the author declared any of <c>min</c>, <c>max</c> or <c>step</c>.</summary>
    /// <remarks>
    /// Lets a host tell a deliberate 0 to 1 from a parameter that said nothing, which
    /// <see cref="ResolveRange"/> cannot, since it reports the same range for both.
    /// </remarks>
    public bool HasRange => MinExpression is { } || MaxExpression is { } || StepExpression is { };

    /// <summary>
    /// The range a host should offer for this parameter, or <see cref="SvgExpressionRange.Default"/>
    /// when it declares none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Total, including for a parameter that is not a number: the declarations reader refuses a range
    /// on one, so there is nothing to report, and a host looping over every parameter is not made to
    /// branch before it can ask.
    /// </para>
    /// <para>
    /// Evaluated here rather than while the declarations are read, because reading a document must
    /// not evaluate anything — <c>SKSvg.Load</c> is documented never to, so a malformed block cannot
    /// fail a load, and <c>SvgDocument.ExpressionDeclarations</c> is recomputed on every access. The
    /// bounds resolve against nothing at all, exactly as a <c>default</c> does.
    /// </para>
    /// </remarks>
    /// <exception cref="ExprException">
    /// A bound is not a number, <c>min</c> is greater than <c>max</c>, or <c>step</c> is not positive.
    /// </exception>
    public SvgExpressionRange ResolveRange()
    {
        var minimum = Bound(MinExpression, "min", SvgExpressionRange.Default.Minimum);
        var maximum = Bound(MaxExpression, "max", SvgExpressionRange.Default.Maximum);
        var step = Bound(StepExpression, "step", 0f);

        if (minimum > maximum)
        {
            throw new ExprException($"The min for '{Name}' is greater than its max.", 0);
        }

        if (StepExpression is { } && step <= 0f)
        {
            throw new ExprException($"The step for '{Name}' must be greater than zero.", 0);
        }

        return new SvgExpressionRange(minimum, maximum, step);
    }

    private float Bound(string? expression, string what, float fallback)
        => expression is null
            ? fallback
            : ExprEvaluator.Isolated
                .EvaluateTo(expression, ExprType.Number, $"The {what} for '{Name}'")
                .AsNumber;
}

public sealed class SvgExpressionLet
{
    public SvgExpressionLet(string name, string expression)
    {
        Name = name;
        Expression = expression;
    }

    public string Name { get; }

    public string Expression { get; }
}

// Document level declarations, authored as a foreign-namespace block that conforming SVG renderers
// ignore:
//
//   <defs>
//     <e:code>
//       <e:param name="t" type="number" default="0" />
//       <e:let name="wave">(sin(t * tau) + 1) / 2</e:let>
//     </e:code>
//   </defs>
//
// The declarations are the symbol table every expression in the document is checked against, so
// they sit beside the language rather than in a back end: the code generator turns them into a
// method signature, and a runtime evaluator binds values to them.
//
// Parse reads the source text rather than the parsed DOM, because the SVG object model exposes a
// foreign element's namespace only to code inside Svg.Custom: from out here, matching on an
// unqualified name would claim <param> elements belonging to somebody else's namespace. Svg.Custom
// itself can see the namespace and reads them straight off the DOM instead — see
// SvgDocument.ExpressionDeclarations. Both routes go through Builder, so they validate identically.
public sealed class SvgExpressionDeclarations
{
    public const string Namespace = "https://svg.skia/expr/1.0";

    public static readonly SvgExpressionDeclarations Empty = new(
        Array.Empty<SvgExpressionParameter>(),
        Array.Empty<SvgExpressionLet>());

    private SvgExpressionDeclarations(
        IReadOnlyList<SvgExpressionParameter> parameters,
        IReadOnlyList<SvgExpressionLet> lets)
    {
        Parameters = parameters;
        Lets = lets;
    }

    public IReadOnlyList<SvgExpressionParameter> Parameters { get; }

    public IReadOnlyList<SvgExpressionLet> Lets { get; }

    public bool IsEmpty => Parameters.Count == 0 && Lets.Count == 0;

    public static SvgExpressionDeclarations Parse(string? svgText)
    {
        if (string.IsNullOrWhiteSpace(svgText) || svgText!.IndexOf(Namespace, StringComparison.Ordinal) < 0)
        {
            return Empty;
        }

        XDocument document;
        try
        {
            using var reader = XmlReader.Create(
                new StringReader(svgText),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            // The SVG parser is the authority on whether the document is well formed. If it is
            // not, it will report that; silently contributing no declarations is enough here.
            return Empty;
        }

        XNamespace ns = Namespace;
        var blocks = document.Descendants(ns + "code").ToList();
        if (blocks.Count == 0)
        {
            return Empty;
        }

        var builder = new Builder();

        foreach (var element in blocks.SelectMany(block => block.Elements()))
        {
            if (element.Name.Namespace != ns)
            {
                continue;
            }

            switch (element.Name.LocalName)
            {
                case "param":
                    builder.AddParameter(
                        (string?)element.Attribute("name"),
                        (string?)element.Attribute("type"),
                        (string?)element.Attribute("default"),
                        minExpression: (string?)element.Attribute("min"),
                        maxExpression: (string?)element.Attribute("max"),
                        stepExpression: (string?)element.Attribute("step"));
                    break;

                case "let":
                    builder.AddLet((string?)element.Attribute("name"), element.Value);
                    break;
            }
        }

        return builder.Build();
    }

    /// <summary>
    /// Collects declarations one at a time, applying every rule about names, types and defaults.
    /// </summary>
    /// <remarks>
    /// Shared so that reading an <c>&lt;e:code&gt;</c> block out of source text and reading it out of
    /// a parsed document cannot drift apart. The rules — valid identifier, not a built-in name, not
    /// declared twice, a type on every parameter, and a range only on a number and only as a pair —
    /// belong to the format, not to whichever reader got there first.
    /// </remarks>
    public sealed class Builder
    {
        private readonly List<SvgExpressionParameter> _parameters = new();
        private readonly List<SvgExpressionLet> _lets = new();
        private readonly HashSet<string> _declared = new(StringComparer.Ordinal);

        public void AddParameter(string? name, string? typeText, string? defaultExpression)
            => AddParameter(name, typeText, defaultExpression, null, null, null);

        public void AddParameter(
            string? name,
            string? typeText,
            string? defaultExpression,
            string? minExpression,
            string? maxExpression,
            string? stepExpression)
        {
            var declared = RequireName(name, "param");

            var type = ExprFunctions.ParseType(
                Trim(typeText) ?? throw new ExprException($"<e:param name=\"{declared}\"> is missing a type.", 0),
                0);

            var minimum = Trim(minExpression);
            var maximum = Trim(maxExpression);
            var step = Trim(stepExpression);

            // Structural only: whether the bounds resolve to sensible numbers is settled by
            // ResolveRange, since reading a document may not evaluate anything. What is checked here
            // is what can be checked by looking, and catches the typo worth catching early — a range
            // on something that has no range.
            if (type != ExprType.Number && (minimum is { } || maximum is { } || step is { }))
            {
                throw new ExprException(
                    $"<e:param name=\"{declared}\"> is a {ExprFunctions.Describe(type)}, so it cannot carry min, max or step. Those describe the range of a number.",
                    0);
            }

            if (minimum is { } && maximum is null)
            {
                throw new ExprException(
                    $"<e:param name=\"{declared}\"> has a min but no max. A range needs both ends, or neither.",
                    0);
            }

            if (maximum is { } && minimum is null)
            {
                throw new ExprException(
                    $"<e:param name=\"{declared}\"> has a max but no min. A range needs both ends, or neither.",
                    0);
            }

            _parameters.Add(new SvgExpressionParameter(
                declared,
                type,
                Trim(defaultExpression),
                minimum,
                maximum,
                step));
        }

        public void AddLet(string? name, string? expression)
        {
            var declared = RequireName(name, "let");

            _lets.Add(new SvgExpressionLet(
                declared,
                Trim(expression) ?? throw new ExprException($"<e:let name=\"{declared}\"> has no expression.", 0)));
        }

        /// <summary>
        /// The declarations collected so far, or <see cref="Empty"/> when there were none.
        /// </summary>
        public SvgExpressionDeclarations Build()
            => _parameters.Count == 0 && _lets.Count == 0
                ? Empty
                : new SvgExpressionDeclarations(_parameters, _lets);

        private string RequireName(string? value, string what)
        {
            var name = Trim(value)
                ?? throw new ExprException($"<e:{what}> is missing a name.", 0);

            if (!IsIdentifier(name))
            {
                throw new ExprException($"'{name}' is not a valid name: use letters, digits and underscore, not starting with a digit.", 0);
            }

            if (ExprFunctions.IsReservedName(name))
            {
                throw new ExprException($"'{name}' is a built-in name and cannot be redeclared.", 0);
            }

            // One set across params and lets, so a let cannot shadow a parameter.
            if (!_declared.Add(name))
            {
                throw new ExprException($"'{name}' is declared more than once.", 0);
            }

            return name;
        }
    }

    /// <summary>
    /// A fresh symbol table holding the parameters, which every back end starts from.
    /// </summary>
    /// <remarks>
    /// Mutable, and deliberately handed out rather than copied: both back ends add each let to it
    /// as that let resolves, and the checker they hand it to keeps the reference. See
    /// <see cref="ExprChecker"/>.
    /// </remarks>
    public Dictionary<string, ExprType> CreateSymbolTable()
    {
        var symbols = new Dictionary<string, ExprType>(StringComparer.Ordinal);

        foreach (var parameter in Parameters)
        {
            symbols[parameter.Name] = parameter.Type;
        }

        return symbols;
    }

    private static bool IsIdentifier(string name)
    {
        if (name.Length == 0 || char.IsDigit(name[0]))
        {
            return false;
        }

        return name.All(c => c == '_' || char.IsLetterOrDigit(c));
    }

    private static string? Trim(string? value)
    {
        if (value is null)
        {
            return null;
        }

        value = value.Trim();

        return value.Length == 0 ? null : value;
    }
}
