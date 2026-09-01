// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Svg.Expressions.Recipes;

/// <summary>
/// Applies a <see cref="SvgRecipe"/> to a plain SVG document, producing one in the expression
/// extension format: every occurrence of a rule's colour becomes <c>{{ expression }}</c>, and
/// the recipe's declarations are injected as an <c>&lt;e:code&gt;</c> block.
/// </summary>
public static class SvgRecipeRewriter
{
    private const string SvgNamespace = "http://www.w3.org/2000/svg";

    /// <summary>
    /// The paint attributes an expression can drive. Kept in step with the placeholder table in
    /// <see cref="SvgExpressionAttributes"/>, minus the two that are not colours.
    /// </summary>
    private static readonly string[] s_paintAttributes = { "fill", "stroke", "stop-color" };

    public static SvgRecipeResult Apply(string svgText, SvgRecipe recipe)
    {
        var (document, root) = Read(svgText);

        // Running the tool over its own output would declare the parameters twice. It almost
        // always means the output path was passed as the input, so it is worth stopping for.
        // Only here: a document already in the expression format is exactly one worth surveying,
        // to see which of its colours are still literal.
        if (document.Descendants(SvgRecipe.Ns + "code").Any())
        {
            throw new SvgRecipeException(
                "The document already has an <e:code> block, so it is already in the expression format. Apply the recipe to the original SVG instead.");
        }

        var counts = new int[recipe.ColorRules.Count];

        Walk(root, value => TryMatch(value, recipe, counts, out var expression) ? expression : null);

        if (recipe.Declarations.Count > 0)
        {
            InjectDeclarations(root, recipe.Declarations);
        }

        var matches = recipe.ColorRules
            .Select((rule, index) => new SvgRecipeRuleMatch(rule, counts[index]))
            .ToList();

        return new SvgRecipeResult(Save(document), matches);
    }

    /// <summary>
    /// The literal colours <paramref name="svgText"/> paints with, in the order they are met.
    /// </summary>
    /// <remarks>
    /// What an editor offers to write a rule against. Through the rewrite's own walk, so what is
    /// listed is exactly what a rule could reach: a colour hidden under a <c>style</c> declaration,
    /// or already written as an expression, is not one, and a second traversal that decided that
    /// differently would offer colours the rewrite then refused to replace.
    /// </remarks>
    public static IReadOnlyList<SvgRecipeSurveyColor> Survey(string svgText)
    {
        var (_, root) = Read(svgText);

        var counts = new Dictionary<int, int>();

        // The order they are met in, which is the order they are read in the file. A dictionary's
        // own is arbitrary, and a list that reordered itself between two drawings of one set would
        // make the panel look like it was showing something different.
        var order = new List<int>();

        Walk(root, value =>
        {
            if (!SvgRecipeColor.TryParse(value, out var argb))
            {
                return null;
            }

            if (!counts.TryGetValue(argb, out var count))
            {
                order.Add(argb);
            }

            counts[argb] = count + 1;

            // Nothing taken back, so nothing is written: every edit in Visit is gated on a value.
            return null;
        });

        return order.Select(argb => new SvgRecipeSurveyColor(argb, counts[argb])).ToList();
    }

    /// <summary>The document and its root, checked as far as both reading and rewriting need.</summary>
    private static (XDocument Document, XElement Root) Read(string svgText)
    {
        var document = Load(svgText);

        var root = document.Root
            ?? throw new SvgRecipeException("The document is empty.");

        if (root.Name.LocalName != "svg")
        {
            throw new SvgRecipeException($"The document root is <{root.Name.LocalName}>, not <svg>.");
        }

        return (document, root);
    }

    /// <summary>
    /// Hands every literal paint value in the document to <paramref name="visit"/>.
    /// </summary>
    /// <remarks>
    /// One traversal for the rewrite and the survey both, because the rule that a <c>style</c>
    /// declaration beats the presentation attribute under it lives here — and a survey that had
    /// its own copy of that rule would offer a colour the rewrite then refused to replace.
    ///
    /// The visitor answers with what should take the value's place, or null to leave it alone.
    /// Every write below is gated on an answer, so a visitor that only looks cannot change the
    /// document it is looking at.
    /// </remarks>
    private static void Walk(XElement root, Func<string, string?> visit)
    {
        // Colours only mean anything on SVG elements. Foreign content keeps whatever its own
        // vocabulary gives 'fill', and is left alone.
        foreach (var element in root.DescendantsAndSelf().Where(e => e.Name.Namespace == root.Name.Namespace))
        {
            Visit(element, visit);
        }
    }

    private static void Visit(XElement element, Func<string, string?> visit)
    {
        var styleAttribute = element.Attribute("style");
        var style = SvgRecipeStyle.Parse(styleAttribute?.Value);
        var styleChanged = false;

        foreach (var name in s_paintAttributes)
        {
            // A 'style' declaration beats the presentation attribute, so rewriting the dead one
            // underneath would emit an expression that never paints.
            if (style.TryGetValue(name, out var styleValue))
            {
                if (Literal(styleValue) && visit(styleValue) is { } styleExpression)
                {
                    // Written where it was found. A style declaration used to be promoted to a
                    // presentation attribute because only an attribute was lifted; a declaration
                    // is lifted too now, and moving one changes a document more than a recipe
                    // needs to.
                    styleChanged = style.TrySetValue(name, styleExpression);
                }

                continue;
            }

            var attribute = element.Attribute(name);

            if (attribute is { } && Literal(attribute.Value) && visit(attribute.Value) is { } expression)
            {
                attribute.Value = expression;
            }
        }

        if (styleChanged)
        {
            var text = style.ToText();
            element.SetAttributeValue("style", text.Length == 0 ? null : text);
        }
    }

    /// <summary>Whether a paint value is a colour the document still names, rather than an expression.</summary>
    /// <remarks>
    /// Already an expression means the document has been converted before, or hand edited. Either
    /// way the author's text wins over a recipe's — and it is not a colour to offer a rule for
    /// either, so the walk keeps it from both rather than each remembering.
    /// </remarks>
    private static bool Literal(string? value) => !SvgExpressionAttributes.TryUnwrap(value, out _);

    private static bool TryMatch(string? value, SvgRecipe recipe, int[] counts, out string expression)
    {
        expression = string.Empty;

        if (!SvgRecipeColor.TryParse(value, out var argb))
        {
            return false;
        }

        for (var i = 0; i < recipe.ColorRules.Count; i++)
        {
            if (recipe.ColorRules[i].Argb != argb)
            {
                continue;
            }

            expression = "{{ " + recipe.ColorRules[i].Expression + " }}";
            counts[i]++;
            return true;
        }

        return false;
    }

    private static void InjectDeclarations(XElement root, IReadOnlyList<XElement> declarations)
    {
        // XLinq resolves the prefix from this declaration when it serialises, so the block comes
        // out as <e:code> instead of carrying a default namespace of its own.
        DeclareNamespace(root);

        XNamespace svg = root.Name.Namespace;

        var defs = root.Elements(svg + "defs").FirstOrDefault();
        var created = defs is null;

        if (defs is null)
        {
            defs = new XElement(svg + "defs");
            root.AddFirst(new XText("\n" + Indent(1)), defs);
        }

        var depth = defs.Ancestors().Count() + 1;

        var code = new XElement(SvgRecipe.Ns + "code");

        foreach (var declaration in declarations)
        {
            code.Add(new XText("\n" + Indent(depth + 1)), new XElement(declaration));
        }

        code.Add(new XText("\n" + Indent(depth)));

        // First in <defs>, where the declarations read as the document's preamble rather than
        // as one more definition among the gradients.
        defs.AddFirst(new XText("\n" + Indent(depth)), code);

        if (created)
        {
            defs.Add(new XText("\n" + Indent(depth - 1)));
        }
    }

    /// <summary>Declares the extension namespace on the root, reusing an existing prefix for it.</summary>
    /// <remarks>
    /// The choice belongs to the language, because a source editor splicing the same block has to
    /// reach the same answer.
    /// </remarks>
    private static void DeclareNamespace(XElement root)
    {
        var prefix = SvgExpressionDeclarations.NamespacePrefixFor(root, out var declared);

        if (declared)
        {
            return;
        }

        root.Add(new XAttribute(XNamespace.Xmlns + prefix, SvgRecipe.Namespace));
    }

    private static string Indent(int depth) => new(' ', depth * 2);

    private static XDocument Load(string svgText)
    {
        try
        {
            using var reader = XmlReader.Create(
                new StringReader(svgText),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });

            // The source layout is preserved so that re-running the recipe after the drawing is
            // exported again produces a diff of the colours that changed, and nothing else.
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new SvgRecipeException($"The document is not well formed XML: {ex.Message}", ex);
        }
    }

    private static string Save(XDocument document)
    {
        var builder = new StringBuilder();

        using (var writer = new Utf8StringWriter(builder))
        {
            document.Save(writer, SaveOptions.DisableFormatting);
        }

        // Whitespace after the root element survives the round trip, and the writer contributes
        // its own line break, so the tail is normalised to exactly one newline.
        return builder.ToString().TrimEnd('\r', '\n', ' ', '\t') + "\n";
    }

    // XDocument stamps the declaration with the encoding of the writer it is saving to, and a
    // StringWriter reports UTF-16 — which would be a lie about a file written out as UTF-8.
    private sealed class Utf8StringWriter : StringWriter
    {
        public Utf8StringWriter(StringBuilder builder)
            : base(builder, System.Globalization.CultureInfo.InvariantCulture)
        {
        }

        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
