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

/// <summary>Which part of a declaration a rule is complaining about.</summary>
/// <remarks>
/// A rule knows what it is about; only a reader knows where that was written. Naming the part is
/// what lets the two stay apart, so the rules keep living in one place and a reader that has
/// positions can turn them into one. <see cref="Element"/> is for a rule about something the
/// document left out, which has nothing of its own to point at.
/// </remarks>
public enum SvgDeclarationPart
{
    Element,
    Name,
    Type,
    Default,
    Min,
    Max,
    Step,
    Body,
}

/// <summary>Something wrong with a declaration, and where in the document it was written.</summary>
/// <remarks>
/// An offset rather than a line and column, because everything else here — a token, an underline,
/// <see cref="ExprException.Position"/> — is an offset, and either is one scan from the other.
/// </remarks>
public readonly record struct SvgDeclarationDiagnostic(int Position, string Message);

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
        var minimum = Bound(MinExpression, "min", SvgExpressionRange.Default.Minimum, SvgDeclarationPart.Min);
        var maximum = Bound(MaxExpression, "max", SvgExpressionRange.Default.Maximum, SvgDeclarationPart.Max);
        var step = Bound(StepExpression, "step", 0f, SvgDeclarationPart.Step);

        if (minimum > maximum)
        {
            throw new ExprException($"The min for '{Name}' is greater than its max.", 0, part: SvgDeclarationPart.Min);
        }

        if (StepExpression is { } && step <= 0f)
        {
            throw new ExprException($"The step for '{Name}' must be greater than zero.", 0, part: SvgDeclarationPart.Step);
        }

        return new SvgExpressionRange(minimum, maximum, step);
    }

    private float Bound(string? expression, string what, float fallback, SvgDeclarationPart part)
    {
        if (expression is null)
        {
            return fallback;
        }

        try
        {
            return ExprEvaluator.Isolated
                .EvaluateTo(expression, ExprType.Number, $"The {what} for '{Name}'")
                .AsNumber;
        }
        catch (ExprException failure)
        {
            // The language reported where in the bound it stopped; which bound that was is only
            // knowable here, and a caller needs both to underline it.
            throw new ExprException(failure.Message, failure.Position, failure.ExpressionText, part);
        }
    }
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

    /// <summary>Reads the declarations, stopping at the first thing wrong with them.</summary>
    /// <exception cref="ExprException">A declaration is malformed.</exception>
    public static SvgExpressionDeclarations Parse(string? svgText)
    {
        var declarations = Read(svgText, out _, out var failure);

        return failure is null ? declarations : throw failure;
    }

    /// <summary>
    /// Reads the declarations, reporting everything wrong with them and where it was written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a caller showing someone the file: a source view, a compiler diagnostic. Every
    /// declaration is read, so a document with three mistakes reports three rather than hiding two
    /// behind the first, and what could be read is still returned — the parameters after a bad one
    /// are not lost with it.
    /// </para>
    /// <para>
    /// A document that is not well-formed XML is reported here too, where <see cref="Parse(string?)"/>
    /// contributes no declarations and says nothing: the SVG parser is the authority on that and
    /// will report it, but this is the one place holding the text that can say where.
    /// </para>
    /// </remarks>
    public static SvgExpressionDeclarations Parse(
        string? svgText,
        out IReadOnlyList<SvgDeclarationDiagnostic> diagnostics)
        => Read(svgText, out diagnostics, out _);

    private static SvgExpressionDeclarations Read(
        string? svgText,
        out IReadOnlyList<SvgDeclarationDiagnostic> diagnostics,
        out ExprException? failure)
    {
        var found = new List<SvgDeclarationDiagnostic>();

        diagnostics = found;
        failure = null;

        if (string.IsNullOrWhiteSpace(svgText) || svgText!.IndexOf(Namespace, StringComparison.Ordinal) < 0)
        {
            return Empty;
        }

        var positions = new Positions(svgText);

        XDocument document;
        try
        {
            // Entities are read, because a drawing may declare its shapes as them and a block of
            // expressions is no reason to stop reading one. The resolver stays null so nothing
            // external is fetched, which is what SvgDocument's own resolver does by default; this
            // assembly holds the language and deliberately depends on nothing to reuse that one.
            using var reader = XmlReader.Create(
                new StringReader(svgText),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, XmlResolver = null });

            // Line info is what turns a rule's verdict into somewhere to point. Measured at 3ms on a
            // 212KB drawing, against the 7ms it takes merely to split one into tokens.
            document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException malformed)
        {
            found.Add(new SvgDeclarationDiagnostic(
                positions.At(malformed.LineNumber, malformed.LinePosition),
                malformed.Message));

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

            try
            {
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
            catch (ExprException bad)
            {
                // The first is kept whole rather than as a message, because it is what the throwing
                // reader rethrows — the two cannot report differently if only one of them decides.
                failure ??= bad;

                found.Add(new SvgDeclarationDiagnostic(positions.Of(element, bad.Part), bad.Message));
            }
        }

        return builder.Build();
    }

    /// <summary>
    /// Turns the line and column an XML reader reports into an offset into the document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both readers of a <c>&lt;e:code&gt;</c> block enforce the same rules, and only this one has a
    /// document to point into: the other walks a parsed tree that never held a position. So the
    /// mapping from a rule's <see cref="SvgDeclarationPart"/> to somewhere in the text lives here,
    /// and the rules stay in <see cref="Builder"/> where both readers reach them.
    /// </para>
    /// <para>
    /// Internal rather than private because the same problem turns up once more: an SVG document
    /// keeps no source position either, so anything reporting what is wrong with a drawing has to
    /// read the text a second time and turn what the reader says back into an offset. A second copy
    /// of this would be a second set of answers about where a quote is.
    /// </para>
    /// </remarks>
    internal sealed class Positions
    {
        private readonly string _text;
        private readonly List<int> _lines = new() { 0 };

        public Positions(string text)
        {
            _text = text;

            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] == '\n')
                {
                    _lines.Add(index + 1);
                }
            }
        }

        /// <summary>Where a one-based line and column is, clamped to the document.</summary>
        public int At(int line, int column)
            => line < 1 || line > _lines.Count
                ? 0
                : Math.Min(_lines[line - 1] + Math.Max(0, column - 1), Math.Max(0, _text.Length - 1));

        /// <summary>Where the part a rule complained about was written.</summary>
        /// <remarks>
        /// A rule about something the document left out — a missing type, a let with no expression —
        /// has nothing of its own to point at, so it points at the declaration that is missing it.
        /// </remarks>
        public int Of(XElement element, SvgDeclarationPart? part)
        {
            var at = part switch
            {
                SvgDeclarationPart.Name => Value(element.Attribute("name")),
                SvgDeclarationPart.Type => Value(element.Attribute("type")),
                SvgDeclarationPart.Default => Value(element.Attribute("default")),
                SvgDeclarationPart.Min => Value(element.Attribute("min")),
                SvgDeclarationPart.Max => Value(element.Attribute("max")),
                SvgDeclarationPart.Step => Value(element.Attribute("step")),
                SvgDeclarationPart.Body => Body(element),
                _ => -1,
            };

            return at >= 0 ? at : At(element);
        }

        /// <summary>Where an attribute's value starts, past the equals sign and the quote.</summary>
        /// <remarks>
        /// The reader points at the attribute's name, and the name is not the mistake — what it says
        /// is. Pointing past the quote also lands inside the expression a <c>default</c> or a bound
        /// holds, so a view that colours those marks the piece rather than the string.
        /// </remarks>
        public int Value(XAttribute? attribute)
        {
            if (attribute is null || !((IXmlLineInfo)attribute).HasLineInfo())
            {
                return -1;
            }

            var name = At((IXmlLineInfo)attribute);
            var equals = _text.IndexOf('=', name);

            if (equals < 0)
            {
                return name;
            }

            var quote = equals + 1;

            while (quote < _text.Length && char.IsWhiteSpace(_text[quote]))
            {
                quote++;
            }

            return quote < _text.Length && _text[quote] is '"' or '\'' ? quote + 1 : name;
        }

        /// <summary>Where a let's expression starts, or nowhere when it has none.</summary>
        private int Body(XElement element)
        {
            foreach (var node in element.Nodes())
            {
                if (node is XText text
                    && text.Value.Trim().Length > 0
                    && ((IXmlLineInfo)text).HasLineInfo())
                {
                    return At((IXmlLineInfo)text);
                }
            }

            return -1;
        }

        private int At(IXmlLineInfo info) => info.HasLineInfo() ? At(info.LineNumber, info.LinePosition) : 0;
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
                0,
                SvgDeclarationPart.Type);

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
                    0,
                    // The one to delete, which is the first one written.
                    part: minimum is { } ? SvgDeclarationPart.Min : maximum is { } ? SvgDeclarationPart.Max : SvgDeclarationPart.Step);
            }

            if (minimum is { } && maximum is null)
            {
                throw new ExprException(
                    $"<e:param name=\"{declared}\"> has a min but no max. A range needs both ends, or neither.",
                    0,
                    part: SvgDeclarationPart.Min);
            }

            if (maximum is { } && minimum is null)
            {
                throw new ExprException(
                    $"<e:param name=\"{declared}\"> has a max but no min. A range needs both ends, or neither.",
                    0,
                    part: SvgDeclarationPart.Max);
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
                Trim(expression)
                ?? throw new ExprException($"<e:let name=\"{declared}\"> has no expression.", 0, part: SvgDeclarationPart.Body)));
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
                ?? throw new ExprException($"<e:{what}> is missing a name.", 0, part: SvgDeclarationPart.Element);

            if (!IsIdentifier(name))
            {
                throw new ExprException($"'{name}' is not a valid name: use letters, digits and underscore, not starting with a digit.", 0, part: SvgDeclarationPart.Name);
            }

            if (ExprFunctions.IsReservedName(name))
            {
                throw new ExprException($"'{name}' is a built-in name and cannot be redeclared.", 0, part: SvgDeclarationPart.Name);
            }

            // One set across params and lets, so a let cannot shadow a parameter.
            if (!_declared.Add(name))
            {
                throw new ExprException($"'{name}' is declared more than once.", 0, part: SvgDeclarationPart.Name);
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
