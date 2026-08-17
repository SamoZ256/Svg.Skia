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
// Read straight from the source text rather than from the parsed DOM: the SVG object model
// exposes foreign element names but not their namespace, and matching on an unqualified name
// would claim <param> elements belonging to somebody else's namespace.
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

        var parameters = new List<SvgExpressionParameter>();
        var lets = new List<SvgExpressionLet>();
        var declared = new HashSet<string>(StringComparer.Ordinal);

        foreach (var element in blocks.SelectMany(block => block.Elements()))
        {
            if (element.Name.Namespace != ns)
            {
                continue;
            }

            switch (element.Name.LocalName)
            {
                case "param":
                    {
                        var name = RequireName(element, "param", declared);
                        var typeText = Trim((string?)element.Attribute("type"))
                            ?? throw new ExprException($"<e:param name=\"{name}\"> is missing a type.", 0);
                        var type = ExprFunctions.ParseType(typeText, 0);
                        var defaultExpression = Trim((string?)element.Attribute("default"));

                        RejectColourDefault(name, type, defaultExpression);

                        parameters.Add(new SvgExpressionParameter(name, type, defaultExpression));

                        break;
                    }

                case "let":
                    {
                        var name = RequireName(element, "let", declared);
                        var expression = Trim(element.Value)
                            ?? throw new ExprException($"<e:let name=\"{name}\"> has no expression.", 0);

                        lets.Add(new SvgExpressionLet(name, expression));
                        break;
                    }
            }
        }

        if (parameters.Count == 0 && lets.Count == 0)
        {
            return Empty;
        }

        return new SvgExpressionDeclarations(parameters, lets);
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

    private static string RequireName(XElement element, string what, HashSet<string> declared)
    {
        var name = Trim((string?)element.Attribute("name"))
            ?? throw new ExprException($"<e:{what}> is missing a name.", 0);

        if (!IsIdentifier(name))
        {
            throw new ExprException($"'{name}' is not a valid name: use letters, digits and underscore, not starting with a digit.", 0);
        }

        if (ExprFunctions.IsReservedName(name))
        {
            throw new ExprException($"'{name}' is a built-in name and cannot be redeclared.", 0);
        }

        if (!declared.Add(name))
        {
            throw new ExprException($"'{name}' is declared more than once.", 0);
        }

        return name;
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
