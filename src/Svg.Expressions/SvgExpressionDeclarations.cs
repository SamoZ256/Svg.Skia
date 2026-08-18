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
    {
        Name = name;
        Type = type;
        DefaultExpression = defaultExpression;
    }

    public string Name { get; }

    public ExprType Type { get; }

    /// <summary>Author supplied default, in the expression language.</summary>
    public string? DefaultExpression { get; }
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
                        (string?)element.Attribute("default"));
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
    /// declared twice, a type on every parameter, no default on a colour — belong to the format, not
    /// to whichever reader got there first.
    /// </remarks>
    public sealed class Builder
    {
        private readonly List<SvgExpressionParameter> _parameters = new();
        private readonly List<SvgExpressionLet> _lets = new();
        private readonly HashSet<string> _declared = new(StringComparer.Ordinal);

        public void AddParameter(string? name, string? typeText, string? defaultExpression)
        {
            var declared = RequireName(name, "param");

            var type = ExprFunctions.ParseType(
                Trim(typeText) ?? throw new ExprException($"<e:param name=\"{declared}\"> is missing a type.", 0),
                0);

            var @default = Trim(defaultExpression);

            RejectColourDefault(declared, type, @default);

            _parameters.Add(new SvgExpressionParameter(declared, type, @default));
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

    // Rejected while reading the declarations rather than while emitting C#, so that a document
    // means the same thing to every back end. `new SKColor(...)` is not a compile-time constant,
    // so it cannot be a C# argument default (CS1736) — but a rule only the code generator
    // enforced would let a document evaluate happily at runtime and then refuse to generate,
    // which is a worse way to find out than being told here.
    private static void RejectColourDefault(string name, ExprType type, string? defaultExpression)
    {
        if (type != ExprType.Color || defaultExpression is null)
        {
            return;
        }

        throw new ExprException(
            $"The default for '{name}' cannot be used: a colour is not a compile-time constant in C#. Drop the default and pass the value at the call site.",
            0);
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
