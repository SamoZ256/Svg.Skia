// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Svg.CodeGen.Skia.Expressions;
using Svg.Expressions;

namespace Svg.CodeGen.Skia;

public sealed class SvgCodeParameter
{
    public SvgCodeParameter(string name, ExprType type, string? defaultExpression)
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

public sealed class SvgCodeLet
{
    public SvgCodeLet(string name, string expression)
    {
        Name = name;
        Expression = expression;
    }

    public string Name { get; }

    public string Expression { get; }
}

// Document level declarations for generated code, authored as a foreign-namespace block that
// conforming SVG renderers ignore:
//
//   <defs>
//     <e:code>
//       <e:param name="t" type="number" default="0" />
//       <e:let name="wave">(sin(t * tau) + 1) / 2</e:let>
//     </e:code>
//   </defs>
//
// Read straight from the source text rather than from the parsed DOM: the SVG object model
// exposes foreign element names but not their namespace, and matching on an unqualified name
// would claim <param> elements belonging to somebody else's namespace.
public sealed class SvgCodeDeclarations
{
    public const string Namespace = "https://svg.skia/expr/1.0";

    public static readonly SvgCodeDeclarations Empty = new(
        Array.Empty<SvgCodeParameter>(),
        Array.Empty<SvgCodeLet>());

    private SvgCodeDeclarations(
        IReadOnlyList<SvgCodeParameter> parameters,
        IReadOnlyList<SvgCodeLet> lets)
    {
        Parameters = parameters;
        Lets = lets;
    }

    public IReadOnlyList<SvgCodeParameter> Parameters { get; }

    public IReadOnlyList<SvgCodeLet> Lets { get; }

    public bool IsEmpty => Parameters.Count == 0 && Lets.Count == 0;

    public static SvgCodeDeclarations Parse(string? svgText)
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

        var parameters = new List<SvgCodeParameter>();
        var lets = new List<SvgCodeLet>();
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

                        parameters.Add(new SvgCodeParameter(
                            name,
                            ExprCompiler.ParseType(typeText, 0),
                            Trim((string?)element.Attribute("default"))));

                        break;
                    }

                case "let":
                    {
                        var name = RequireName(element, "let", declared);
                        var expression = Trim(element.Value)
                            ?? throw new ExprException($"<e:let name=\"{name}\"> has no expression.", 0);

                        lets.Add(new SvgCodeLet(name, expression));
                        break;
                    }
            }
        }

        if (parameters.Count == 0 && lets.Count == 0)
        {
            return Empty;
        }

        return new SvgCodeDeclarations(parameters, lets);
    }

    /// <summary>
    /// Type checks the declarations and returns the symbol table every paint expression in the
    /// document is compiled against, along with the C# for each let in declaration order.
    /// </summary>
    public (ExprCompiler Compiler, IReadOnlyList<(string Name, ExprType Type, string Code)> Lets) Resolve()
    {
        var symbols = new Dictionary<string, ExprType>(StringComparer.Ordinal);
        var compiled = new List<(string, ExprType, string)>();

        foreach (var parameter in Parameters)
        {
            symbols[parameter.Name] = parameter.Type;
        }

        var compiler = new ExprCompiler(symbols);

        // Lets resolve in order, so a let may use the ones declared above it but not below.
        foreach (var let in Lets)
        {
            var (type, code) = compiler.Compile(let.Expression);
            compiled.Add((let.Name, type, code));
            symbols[let.Name] = type;
        }

        return (compiler, compiled);
    }

    /// <summary>
    /// Compiles a parameter default, or returns null when the author declared none — in which
    /// case the generated parameter is required rather than carrying an invented value.
    /// </summary>
    public string? DefaultCodeFor(SvgCodeParameter parameter)
    {
        if (parameter.DefaultExpression is null)
        {
            return null;
        }

        // A colour cannot be a C# argument default: `new SKColor(...)` is not a compile time
        // constant, and emitting it produces a class that does not build (CS1736). This is the
        // same limit that stops a default referencing another parameter.
        if (parameter.Type == ExprType.Color)
        {
            throw new ExprException(
                $"The default for '{parameter.Name}' cannot be used: a colour is not a compile-time constant in C#. Drop the default and pass the value at the call site.",
                0);
        }

        // Defaults may not reference other parameters: argument defaults are compile time
        // constants in C#, and an ordering dependency between them would be invisible here.
        var compiler = new ExprCompiler(new Dictionary<string, ExprType>(StringComparer.Ordinal));

        return compiler.CompileTo(
            parameter.DefaultExpression,
            parameter.Type,
            $"The default for '{parameter.Name}'");
    }

    private static string RequireName(XElement element, string what, HashSet<string> declared)
    {
        var name = Trim((string?)element.Attribute("name"))
            ?? throw new ExprException($"<e:{what}> is missing a name.", 0);

        if (!IsIdentifier(name))
        {
            throw new ExprException($"'{name}' is not a valid name: use letters, digits and underscore, not starting with a digit.", 0);
        }

        if (ExprCompiler.IsReservedName(name))
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
