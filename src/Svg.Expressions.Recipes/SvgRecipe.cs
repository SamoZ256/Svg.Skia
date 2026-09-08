// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Svg.Expressions.Recipes;

/// <summary>A value in the source document, and the expression that replaces every occurrence of it.</summary>
public sealed class SvgReplaceRule
{
    public SvgReplaceRule(string name, string valueText, string key, ExprType type, string expression)
    {
        Name = name;
        ValueText = valueText;
        Key = key;
        Type = type;
        Expression = expression;
    }

    /// <summary>What the rule names: an attribute, or <see cref="SvgRecipeValue.ColorName"/>.</summary>
    public string Name { get; }

    /// <summary>The value as the recipe spelled it, for diagnostics and for finding the rule in text.</summary>
    public string ValueText { get; }

    /// <summary>Normalised value key. Matching is by value, not by spelling.</summary>
    public string Key { get; }

    /// <summary>What the expression has to come to, which is what the attribute holds.</summary>
    public ExprType Type { get; }

    /// <summary>Expression text, without the braces.</summary>
    public string Expression { get; }
}

/// <summary>
/// The description file that turns a plain SVG into the expression extension format:
///
/// <code>
///   &lt;recipe xmlns="https://svg.skia/expr/1.0"&gt;
///     &lt;code&gt;
///       &lt;param name="hue" type="number" default="200" /&gt;
///       &lt;let name="accent"&gt;hsl(hue, 74%, 55%)&lt;/let&gt;
///     &lt;/code&gt;
///     &lt;replace color="#3b82f6"&gt;accent&lt;/replace&gt;
///     &lt;replace opacity="0.5"&gt;fade&lt;/replace&gt;
///   &lt;/recipe&gt;
/// </code>
///
/// Written in the extension's own namespace, so the declaration block is exactly the block that
/// ends up in the output: no second schema, and the text is copied rather than re-serialised.
///
/// A rule names <c>color</c>, or one of the non-colour attributes an expression can drive. See
/// <see cref="SvgRecipeValue.ColorName"/> for why colours have a name of their own and the others
/// do not.
/// </summary>
public sealed class SvgRecipe
{
    /// <remarks>
    /// The language's own constant rather than a second spelling of it: a recipe writes the very
    /// block the declarations reader reads back, and two literals could drift.
    /// </remarks>
    public const string Namespace = SvgExpressionDeclarations.Namespace;

    internal static readonly XNamespace Ns = Namespace;

    private SvgRecipe(IReadOnlyList<XElement> declarations, IReadOnlyList<SvgReplaceRule> rules)
    {
        Declarations = declarations;
        Rules = rules;
    }

    /// <summary>The <c>param</c> and <c>let</c> elements, in document order, ready to be copied out.</summary>
    public IReadOnlyList<XElement> Declarations { get; }

    public IReadOnlyList<SvgReplaceRule> Rules { get; }

    public static SvgRecipe Load(string path) => Parse(File.ReadAllText(path));

    public static SvgRecipe Parse(string recipeXml)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(
                new StringReader(recipeXml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new SvgRecipeException($"The recipe is not well formed XML: {ex.Message}", ex);
        }

        var root = document.Root
            ?? throw new SvgRecipeException("The recipe is empty.");

        if (root.Name != Ns + "recipe")
        {
            throw new SvgRecipeException(
                $"The recipe root must be <recipe xmlns=\"{Namespace}\">, but was <{root.Name.LocalName}> in '{root.Name.NamespaceName}'.");
        }

        var declarations = new List<XElement>();
        var rules = new List<SvgReplaceRule>();
        var claimed = new Dictionary<(string Name, string Key), SvgReplaceRule>();

        foreach (var element in root.Elements())
        {
            RequireRecipeNamespace(element);

            switch (element.Name.LocalName)
            {
                case "code":
                    ReadDeclarations(element, declarations);
                    break;

                case "replace":
                    AddRule(element, rules, claimed);
                    break;

                default:
                    throw new SvgRecipeException(
                        $"<{element.Name.LocalName}> is not a recipe element. Expected <code> or <replace>.");
            }
        }

        return new SvgRecipe(declarations, rules);
    }

    // Several <code> blocks merge in document order, matching how the extension itself treats
    // several <e:code> blocks in one document.
    private static void ReadDeclarations(XElement code, List<XElement> declarations)
    {
        foreach (var element in code.Elements())
        {
            RequireRecipeNamespace(element);

            if (element.Name.LocalName is not ("param" or "let"))
            {
                throw new SvgRecipeException(
                    $"<{element.Name.LocalName}> is not a declaration. Expected <param> or <let> inside <code>.");
            }

            // Copied verbatim: the code generator owns the symbol table and type checks it, and a
            // second implementation here could disagree.
            declarations.Add(new XElement(element));
        }
    }

    private static void AddRule(
        XElement element,
        List<SvgReplaceRule> rules,
        Dictionary<(string Name, string Key), SvgReplaceRule> claimed)
    {
        var (name, valueText) = Named(element);

        var type = SvgRecipeValue.TypeFor(name)!.Value;
        var written = $"<replace {name}=\"{valueText}\">";

        var key = SvgRecipeValue.Key(type, valueText, $"The value of {written}");

        var expression = NormalizeExpression(element.Value);

        if (expression.Length == 0)
        {
            throw new SvgRecipeException($"{written} has no expression.");
        }

        if (expression.IndexOf("}}", StringComparison.Ordinal) >= 0 ||
            expression.IndexOf("{{", StringComparison.Ordinal) >= 0)
        {
            throw new SvgRecipeException(
                $"The expression for {written} must not contain braces; they are added when it is written out.");
        }

        var rule = new SvgReplaceRule(name, valueText, key, type, expression);

        // Two rules for one value cannot both apply, and whichever silently lost would be a
        // painful thing to debug in the generated code. One name per kind is what keeps this to a
        // single comparison: no rule can claim a value another rule also claims under a different
        // name, so there is no precedence to settle here.
        if (claimed.TryGetValue((name, key), out var existing))
        {
            throw new SvgRecipeException(
                $"'{valueText}' and '{existing.ValueText}' are the same {SvgRecipeValue.Describe(type)}, so they cannot have different expressions.");
        }

        claimed.Add((name, key), rule);
        rules.Add(rule);
    }

    /// <summary>What one <c>&lt;replace&gt;</c> names, refusing anything a rule cannot replace.</summary>
    /// <remarks>
    /// The message names what can be replaced rather than only what cannot, because the useful half
    /// of "stroke-width does not work here" is the list of what does — the same reasoning as
    /// <see cref="SvgExpressionAttributes.WhyUnsupported"/>, said for a recipe instead of the parser.
    /// </remarks>
    private static (string Name, string Value) Named(XElement element)
    {
        (string Name, string Value)? found = null;

        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration)
            {
                continue;
            }

            var name = attribute.Name.LocalName;

            if (SvgRecipeValue.TypeFor(name) is null)
            {
                // A colour attribute is the near miss worth answering directly: it is replaceable,
                // just not under its own name, and 'color' already covers every one of them.
                throw new SvgRecipeException(
                    SvgExpressionAttributes.TypeFor(name) == ExprType.Color
                        ? $"<replace> names '{name}' one attribute at a time. Use <replace {SvgRecipeValue.ColorName}=\"…\">, which claims every colour attribute at once."
                        : $"<replace> cannot replace '{name}'. A rule names {Expected()}.");
            }

            if (found is { } already)
            {
                throw new SvgRecipeException(
                    $"<replace> names both '{already.Name}' and '{name}', but a rule replaces one value.");
            }

            found = (name, attribute.Value.Trim());
        }

        if (found is not { } rule || rule.Value.Length == 0)
        {
            throw new SvgRecipeException($"<replace> is missing a value to replace. A rule names {Expected()}.");
        }

        return rule;
    }

    private static string Expected() => string.Join(", ", SvgRecipeValue.Names);

    private static void RequireRecipeNamespace(XElement element)
    {
        if (element.Name.Namespace != Ns)
        {
            throw new SvgRecipeException(
                $"<{element.Name.LocalName}> is in '{element.Name.NamespaceName}', but recipe elements must be in '{Namespace}'.");
        }
    }

    // It ends up in an attribute, where any reader normalises a newline to a space, so folding it
    // here gives the same result and is visible in the file.
    internal static string NormalizeExpression(string? value)
        => value is null ? string.Empty : Regex.Replace(value.Trim(), @"\s+", " ");
}
