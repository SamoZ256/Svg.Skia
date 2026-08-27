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

/// <summary>A colour in the source document, and the expression that replaces every occurrence of it.</summary>
public sealed class SvgColorRule
{
    public SvgColorRule(string colorText, int argb, string expression)
    {
        ColorText = colorText;
        Argb = argb;
        Expression = expression;
    }

    /// <summary>The colour as the recipe spelled it, for diagnostics.</summary>
    public string ColorText { get; }

    /// <summary>Normalised colour key. Matching is by value, not by spelling.</summary>
    public int Argb { get; }

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
///   &lt;/recipe&gt;
/// </code>
///
/// The recipe is written in the extension's own namespace so that the declaration block is
/// exactly the block that ends up in the output — there is no second schema to learn, and the
/// text is copied rather than re-serialised.
/// </summary>
public sealed class SvgRecipe
{
    /// <remarks>
    /// The language's own constant rather than a second spelling of it: a recipe writes the very
    /// block the declarations reader reads back, and two literals could drift.
    /// </remarks>
    public const string Namespace = SvgExpressionDeclarations.Namespace;

    internal static readonly XNamespace Ns = Namespace;

    private SvgRecipe(IReadOnlyList<XElement> declarations, IReadOnlyList<SvgColorRule> colorRules)
    {
        Declarations = declarations;
        ColorRules = colorRules;
    }

    /// <summary>The <c>param</c> and <c>let</c> elements, in document order, ready to be copied out.</summary>
    public IReadOnlyList<XElement> Declarations { get; }

    public IReadOnlyList<SvgColorRule> ColorRules { get; }

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
        var rules = new List<SvgColorRule>();
        var byColor = new Dictionary<int, SvgColorRule>();

        foreach (var element in root.Elements())
        {
            RequireRecipeNamespace(element);

            switch (element.Name.LocalName)
            {
                case "code":
                    ReadDeclarations(element, declarations);
                    break;

                case "replace":
                    AddColorRule(element, rules, byColor);
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

            // Copied verbatim: the expression language is type checked by the code generator,
            // which owns the symbol table. Validating here would mean a second implementation
            // that could disagree with the first.
            declarations.Add(new XElement(element));
        }
    }

    private static void AddColorRule(XElement element, List<SvgColorRule> rules, Dictionary<int, SvgColorRule> byColor)
    {
        var colorText = ((string?)element.Attribute("color"))?.Trim();

        if (colorText is null || colorText.Length == 0)
        {
            throw new SvgRecipeException("<replace> is missing a color.");
        }

        var argb = SvgRecipeColor.Parse(colorText, $"The color of <replace color=\"{colorText}\">");

        var expression = NormalizeExpression(element.Value);

        if (expression.Length == 0)
        {
            throw new SvgRecipeException($"<replace color=\"{colorText}\"> has no expression.");
        }

        if (expression.IndexOf("}}", StringComparison.Ordinal) >= 0 ||
            expression.IndexOf("{{", StringComparison.Ordinal) >= 0)
        {
            throw new SvgRecipeException(
                $"The expression for <replace color=\"{colorText}\"> must not contain braces; they are added when it is written out.");
        }

        var rule = new SvgColorRule(colorText, argb, expression);

        // Two rules for one colour cannot both apply, and whichever silently lost would be a
        // painful thing to debug in the generated code.
        if (byColor.TryGetValue(argb, out var existing))
        {
            throw new SvgRecipeException(
                $"'{colorText}' and '{existing.ColorText}' are the same colour, so they cannot have different expressions.");
        }

        byColor.Add(argb, rule);
        rules.Add(rule);
    }

    private static void RequireRecipeNamespace(XElement element)
    {
        if (element.Name.Namespace != Ns)
        {
            throw new SvgRecipeException(
                $"<{element.Name.LocalName}> is in '{element.Name.NamespaceName}', but recipe elements must be in '{Namespace}'.");
        }
    }

    // An expression may be written across several lines, or in CDATA to avoid escaping. It ends
    // up in an XML attribute, where a newline would be normalised to a space by any reader, so
    // it is folded here instead — with the same result, but visible in the file.
    private static string NormalizeExpression(string? value)
        => value is null ? string.Empty : Regex.Replace(value.Trim(), @"\s+", " ");
}
