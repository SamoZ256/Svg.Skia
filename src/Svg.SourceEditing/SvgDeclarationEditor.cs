// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Svg.Expressions;

namespace Svg.SourceEditing;

/// <summary>
/// Adds and changes <c>&lt;e:code&gt;</c> declarations by replacing spans of a document's own text.
/// </summary>
/// <remarks>
/// A splice, not a rewrite. Parsing a drawing and writing it back drops every comment — the SVG
/// reader's node switch has no case for them — turns <c>fill="{{ primary }}"</c> into a placeholder
/// plus a foreign attribute, and adds a doctype and two namespaces nobody asked for. Nothing here
/// decides what is legal: a proposal goes through <see cref="SvgExpressionDeclarations.Builder"/>,
/// and the result is read back before it is handed over.
/// </remarks>
public static class SvgDeclarationEditor
{
    private static readonly XNamespace Ns = SvgExpressionDeclarations.Namespace;

    private const string SvgNamespace = "http://www.w3.org/2000/svg";

    /// <summary>Declares a parameter, creating the block and the namespace if the document has none.</summary>
    public static SvgSourceEditResult Add(string svgText, SvgExpressionParameter parameter)
    {
        if (svgText is null)
        {
            throw new ArgumentNullException(nameof(svgText));
        }

        if (parameter is null)
        {
            throw new ArgumentNullException(nameof(parameter));
        }

        if (!Open(svgText, out var document, out var positions, out var refusal))
        {
            return SvgSourceEditResult.Refuse(refusal!);
        }

        var declarations = SvgExpressionDeclarations.Parse(svgText, out _);

        if (Rejected(declarations, parameter) is { } bad)
        {
            return SvgSourceEditResult.Refuse(bad);
        }

        var root = document!.Root;

        if (root is null)
        {
            return SvgSourceEditResult.Refuse("The document has no root element to add a parameter to.");
        }

        var prefix = SvgExpressionDeclarations.NamespacePrefixFor(root, out var declared);
        var newline = Newline(svgText);
        var indent = IndentUnit(svgText);

        var edits = new List<SvgTextEdit>();

        if (!declared && DeclareNamespace(svgText, root, positions, prefix) is { } declaration)
        {
            edits.Add(declaration);
        }

        var block = document.Descendants(Ns + "code").FirstOrDefault();

        var written = block is null
            ? CreateBlock(svgText, root, positions, prefix, parameter, newline, indent)
            : AppendToBlock(svgText, block, positions, prefix, parameter, newline, indent);

        if (written is null)
        {
            return SvgSourceEditResult.Refuse("This drawing has nothing in it to give a parameter to.");
        }

        edits.Add(written.Value);

        return Verify(svgText, edits, parameter.Name);
    }

    /// <summary>Rewrites a declaration, carrying its uses with it when the name changes.</summary>
    /// <remarks>
    /// A rename is an edit in as many places as the drawing names it, and every one has to land or
    /// none should.
    /// </remarks>
    /// <param name="name">The declaration as it currently stands.</param>
    /// <param name="replacement">What it should say. Its type must be the one it already has.</param>
    public static SvgSourceEditResult Update(string svgText, string name, SvgExpressionParameter replacement)
    {
        if (svgText is null)
        {
            throw new ArgumentNullException(nameof(svgText));
        }

        if (replacement is null)
        {
            throw new ArgumentNullException(nameof(replacement));
        }

        if (!Open(svgText, out var document, out var positions, out var refusal))
        {
            return SvgSourceEditResult.Refuse(refusal!);
        }

        var element = document!
            .Descendants(Ns + "code")
            .SelectMany(block => block.Elements(Ns + "param"))
            .FirstOrDefault(candidate => string.Equals((string?)candidate.Attribute("name"), name, StringComparison.Ordinal));

        if (element is null)
        {
            return SvgSourceEditResult.Refuse($"This drawing declares no parameter called '{name}'.");
        }

        var declarations = SvgExpressionDeclarations.Parse(svgText, out _);

        if (Rejected(declarations, replacement, replacing: name) is { } bad)
        {
            return SvgSourceEditResult.Refuse(bad);
        }

        var current = declarations.Parameters.First(p => string.Equals(p.Name, name, StringComparison.Ordinal));

        if (current.Type != replacement.Type)
        {
            // Everything naming it was checked against the type it had, so changing one is a change
            // to every expression that uses it rather than to the declaration alone.
            return SvgSourceEditResult.Refuse(
                $"'{name}' is a {ExprFunctions.Describe(current.Type)} and cannot become a "
                + $"{ExprFunctions.Describe(replacement.Type)}. Remove it and declare it again.");
        }

        var edits = new List<SvgTextEdit>();

        Write(svgText, element, positions, "default", replacement.DefaultExpression, edits);
        Write(svgText, element, positions, "min", replacement.MinExpression, edits);
        Write(svgText, element, positions, "max", replacement.MaxExpression, edits);
        Write(svgText, element, positions, "step", replacement.StepExpression, edits);

        var renamed = !string.Equals(name, replacement.Name, StringComparison.Ordinal);

        if (renamed)
        {
            Write(svgText, element, positions, "name", replacement.Name, edits);

            if (SvgDeclarationReferences.Rename(svgText, document, positions, name, replacement.Name, edits) is { } trouble)
            {
                return SvgSourceEditResult.Refuse(trouble);
            }
        }

        edits.Sort((left, right) => left.Position.CompareTo(right.Position));

        return Verify(svgText, edits, renamed ? replacement.Name : null);
    }

    /// <summary>Writes one attribute of a declaration, adding or removing it as needed.</summary>
    /// <param name="expression">What it should say, or null to take the attribute away.</param>
    public static SvgSourceEditResult Set(
        string svgText,
        string name,
        SvgDeclarationPart part,
        string? expression)
        => SetAll(svgText, new Dictionary<string, string?>(StringComparer.Ordinal) { [name] = expression }, part);

    /// <summary>Writes the <c>default</c> of several declarations at once.</summary>
    /// <remarks>One call rather than a loop, so the whole commit is one thing to take back.</remarks>
    public static SvgSourceEditResult SetDefaults(string svgText, IReadOnlyDictionary<string, string> byName)
    {
        if (byName is null)
        {
            throw new ArgumentNullException(nameof(byName));
        }

        var wanted = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var pair in byName)
        {
            wanted[pair.Key] = pair.Value;
        }

        return SetAll(svgText, wanted, SvgDeclarationPart.Default);
    }

    private static SvgSourceEditResult SetAll(
        string svgText,
        IReadOnlyDictionary<string, string?> wanted,
        SvgDeclarationPart part)
    {
        if (svgText is null)
        {
            throw new ArgumentNullException(nameof(svgText));
        }

        if (part == SvgDeclarationPart.Name)
        {
            return SvgSourceEditResult.Refuse("Renaming moves every use of the name too, which Update does.");
        }

        if (Attribute(part) is not { } attributeName)
        {
            return SvgSourceEditResult.Refuse($"{part} is not an attribute a declaration can be given.");
        }

        if (!Open(svgText, out var document, out var positions, out var refusal))
        {
            return SvgSourceEditResult.Refuse(refusal!);
        }

        var declarations = document!
            .Descendants(Ns + "code")
            .SelectMany(block => block.Elements(Ns + "param"))
            .ToList();

        var edits = new List<SvgTextEdit>();

        foreach (var pair in wanted)
        {
            var element = declarations.FirstOrDefault(
                candidate => string.Equals((string?)candidate.Attribute("name"), pair.Key, StringComparison.Ordinal));

            if (element is null)
            {
                return SvgSourceEditResult.Refuse($"This drawing declares no parameter called '{pair.Key}'.");
            }

            if (Write(svgText, element, positions, attributeName, pair.Value) is { } edit)
            {
                edits.Add(edit);
            }
        }

        // A caller may hand these over in any order; a document reads in one.
        edits.Sort((left, right) => left.Position.CompareTo(right.Position));

        return Verify(svgText, edits, null);
    }

    /// <summary>Reads the document, refusing anything an edit cannot be aimed at.</summary>
    /// <remarks>
    /// Both refusals are what a document looks like mid-typing. Neither is worth a mode: the action
    /// declines and works again as soon as the text does.
    /// </remarks>
    private static bool Open(
        string svgText,
        out XDocument? document,
        out SvgExpressionDeclarations.Positions positions,
        out string? refusal)
    {
        positions = new SvgExpressionDeclarations.Positions(svgText);

        document = SvgExpressionDeclarations.TryLoad(svgText, positions, out var malformed);

        if (document is null)
        {
            refusal = $"This drawing cannot be read as XML yet, so there is nowhere to write: {malformed!.Value.Message}";

            return false;
        }

        SvgExpressionDeclarations.Parse(svgText, out var diagnostics);

        if (diagnostics.Count > 0)
        {
            refusal = $"Fix what the declarations already say first: {diagnostics[0].Message}";

            return false;
        }

        refusal = null;

        return true;
    }

    /// <summary>Why the language would not accept this parameter beside the ones already there.</summary>
    /// <param name="replacing">A declaration this one stands in for, which is not a name it clashes with.</param>
    private static string? Rejected(
        SvgExpressionDeclarations declarations,
        SvgExpressionParameter parameter,
        string? replacing = null)
    {
        var builder = new SvgExpressionDeclarations.Builder();

        try
        {
            foreach (var existing in declarations.Parameters)
            {
                if (string.Equals(existing.Name, replacing, StringComparison.Ordinal))
                {
                    continue;
                }

                builder.AddParameter(
                    existing.Name,
                    ExprFunctions.NameOf(existing.Type),
                    existing.DefaultExpression,
                    existing.MinExpression,
                    existing.MaxExpression,
                    existing.StepExpression);
            }

            foreach (var let in declarations.Lets)
            {
                builder.AddLet(let.Name, let.Expression);
            }

            builder.AddParameter(
                parameter.Name,
                ExprFunctions.NameOf(parameter.Type),
                parameter.DefaultExpression,
                parameter.MinExpression,
                parameter.MaxExpression,
                parameter.StepExpression);
        }
        catch (ExprException bad)
        {
            return bad.Message;
        }

        return null;
    }

    /// <summary>Applies the edits and reads the result, so a bad splice cannot be handed over.</summary>
    /// <remarks>
    /// Spans produced by hand go wrong in ways that still look like text — a quote landed on, a tag
    /// left open — and only the reader can say so. Two reads at 3ms each is the whole cost.
    /// </remarks>
    private static SvgSourceEditResult Verify(string svgText, List<SvgTextEdit> edits, string? expected)
    {
        if (edits.Count == 0)
        {
            return SvgSourceEditResult.Nothing;
        }

        var rewritten = SvgTextEdit.ApplyAll(svgText, edits);

        var declarations = SvgExpressionDeclarations.Parse(rewritten, out var diagnostics);

        if (diagnostics.Count > 0)
        {
            return SvgSourceEditResult.Refuse(diagnostics[0].Message);
        }

        if (expected is { } name && !declarations.Parameters.Any(p => string.Equals(p.Name, name, StringComparison.Ordinal)))
        {
            return SvgSourceEditResult.Refuse($"'{name}' was written but the document does not read it back.");
        }

        return SvgSourceEditResult.From(edits);
    }

    /// <summary>Adds <c>xmlns:e</c> to the root, after whatever it already declares.</summary>
    private static SvgTextEdit? DeclareNamespace(
        string svgText,
        XElement root,
        SvgExpressionDeclarations.Positions positions,
        string prefix)
    {
        var last = root.Attributes().LastOrDefault();

        var at = last is { } ? positions.EndOfValue(last) : -1;

        if (at < 0)
        {
            // No attributes, or none this can find the end of: go just inside the open tag instead.
            at = positions.ContentStart(root) - 1;

            if (at < 1)
            {
                return null;
            }
        }
        else
        {
            // EndOfValue lands on the closing quote; the declaration goes after it.
            at++;
        }

        return new SvgTextEdit(at, 0, $" xmlns:{prefix}=\"{SvgExpressionDeclarations.Namespace}\"");
    }

    /// <summary>Writes a parameter into a block that already exists.</summary>
    private static SvgTextEdit? AppendToBlock(
        string svgText,
        XElement block,
        SvgExpressionDeclarations.Positions positions,
        string prefix,
        SvgExpressionParameter parameter,
        string newline,
        string indent)
    {
        var element = Render(prefix, parameter);

        // A block that closes itself has nothing to append to, so it becomes a pair holding the one
        // declaration. Its own indentation is what the new lines line up with.
        var contentStart = positions.ContentStart(block);

        if (contentStart < 0)
        {
            var (start, length) = positions.Span(block);
            var own = LeadingWhitespace(svgText, start);

            return new SvgTextEdit(
                start,
                length,
                $"<{prefix}:code>{newline}{own}{indent}{element}{newline}{own}</{prefix}:code>");
        }

        // Among the parameters, not at the end. The reader takes them in any order, but a parameter
        // written below the lets that use it reads backwards.
        var lastParameter = block.Elements(Ns + "param").LastOrDefault();

        if (lastParameter is { })
        {
            var (start, length) = positions.Span(lastParameter);

            if (start >= 0)
            {
                return new SvgTextEdit(
                    start + length,
                    0,
                    $"{newline}{LeadingWhitespace(svgText, start)}{element}");
            }
        }

        var first = block.Elements().FirstOrDefault();

        if (first is { } && positions.Span(first).Start is var firstStart && firstStart >= 0)
        {
            // Before everything, so it lands above the lets rather than between two of them, where
            // it would split a group whose order is the one thing about them that matters.
            return new SvgTextEdit(
                contentStart,
                0,
                $"{newline}{LeadingWhitespace(svgText, firstStart)}{element}");
        }

        // An empty block, so there is nothing to line up with but the block itself.
        return new SvgTextEdit(
            contentStart,
            0,
            $"{newline}{LeadingWhitespace(svgText, positions.Span(block).Start)}{indent}{element}");
    }

    /// <summary>Writes the block, and the &lt;defs&gt; to hold it if the drawing has none.</summary>
    /// <remarks>
    /// Where SvgRecipeRewriter.InjectDeclarations puts it, so a drawing that has been through a
    /// recipe and one that has been through this keep it in the same place.
    /// </remarks>
    private static SvgTextEdit? CreateBlock(
        string svgText,
        XElement root,
        SvgExpressionDeclarations.Positions positions,
        string prefix,
        SvgExpressionParameter parameter,
        string newline,
        string indent)
    {
        var element = Render(prefix, parameter);

        XNamespace svg = root.Name.Namespace.NamespaceName.Length > 0 ? root.Name.Namespace : SvgNamespace;

        var defs = root.Elements(svg + "defs").FirstOrDefault();

        if (defs is { } && positions.ContentStart(defs) is var contentStart && contentStart >= 0)
        {
            var own = LeadingWhitespace(svgText, positions.Span(defs).Start);

            return new SvgTextEdit(
                contentStart,
                0,
                $"{newline}{own}{indent}<{prefix}:code>" +
                $"{newline}{own}{indent}{indent}{element}" +
                $"{newline}{own}{indent}</{prefix}:code>");
        }

        var at = positions.ContentStart(root);

        if (at < 0)
        {
            // <svg /> has nothing in it to parameterise, and rewriting the root into a pair would
            // change the document's shape rather than add to it.
            return null;
        }

        // <defs> belongs to SVG, so it is written the way this document writes SVG: unprefixed under
        // a default namespace, and prefixed where the drawing prefixes its own elements.
        var svgPrefix = root.GetPrefixOfNamespace(svg);
        var defsName = string.IsNullOrEmpty(svgPrefix) ? "defs" : svgPrefix + ":defs";

        var rootIndent = LeadingWhitespace(svgText, positions.Span(root).Start);

        return new SvgTextEdit(
            at,
            0,
            $"{newline}{rootIndent}{indent}<{defsName}>" +
            $"{newline}{rootIndent}{indent}{indent}<{prefix}:code>" +
            $"{newline}{rootIndent}{indent}{indent}{indent}{element}" +
            $"{newline}{rootIndent}{indent}{indent}</{prefix}:code>" +
            $"{newline}{rootIndent}{indent}</{defsName}>");
    }

    private static void Write(
        string svgText,
        XElement element,
        SvgExpressionDeclarations.Positions positions,
        string attributeName,
        string? expression,
        List<SvgTextEdit> edits)
    {
        if (Write(svgText, element, positions, attributeName, expression) is { } edit)
        {
            edits.Add(edit);
        }
    }

    /// <summary>Writes one attribute of a declaration, or takes it away.</summary>
    private static SvgTextEdit? Write(
        string svgText,
        XElement element,
        SvgExpressionDeclarations.Positions positions,
        string attributeName,
        string? expression)
    {
        var attribute = element.Attribute(attributeName);

        if (attribute is { })
        {
            var start = positions.Value(attribute);
            var end = positions.EndOfValue(attribute);

            if (start < 0 || end < 0)
            {
                return null;
            }

            if (expression is null)
            {
                // The attribute and the space in front of it, so removing one does not leave a gap
                // where it used to be.
                var name = positions.NameStart(attribute);

                if (name < 0)
                {
                    return null;
                }

                var from = name;

                while (from > 0 && (svgText[from - 1] == ' ' || svgText[from - 1] == '\t'))
                {
                    from--;
                }

                return new SvgTextEdit(from, end + 1 - from, string.Empty);
            }

            var current = svgText.Substring(start, end - start);

            return string.Equals(current, Escape(expression), StringComparison.Ordinal)
                ? null
                : new SvgTextEdit(start, end - start, Escape(expression));
        }

        if (expression is null)
        {
            return null;
        }

        // Nothing to replace, so it joins the attributes already there.
        var last = element.Attributes().LastOrDefault();

        var at = last is { } ? positions.EndOfValue(last) : -1;

        if (at < 0)
        {
            return null;
        }

        return new SvgTextEdit(at + 1, 0, $" {attributeName}=\"{Escape(expression)}\"");
    }

    private static string Render(string prefix, SvgExpressionParameter parameter)
    {
        var builder = new StringBuilder();

        builder.Append('<').Append(prefix).Append(":param name=\"").Append(Escape(parameter.Name)).Append('"');
        builder.Append(" type=\"").Append(ExprFunctions.NameOf(parameter.Type)).Append('"');

        Attribute(builder, "default", parameter.DefaultExpression);
        Attribute(builder, "min", parameter.MinExpression);
        Attribute(builder, "max", parameter.MaxExpression);
        Attribute(builder, "step", parameter.StepExpression);

        return builder.Append(" />").ToString();
    }

    private static void Attribute(StringBuilder builder, string name, string? value)
    {
        if (value is { })
        {
            builder.Append(' ').Append(name).Append("=\"").Append(Escape(value)).Append('"');
        }
    }

    /// <summary>What a declaration's attribute is called, or null where the part is not one.</summary>
    private static string? Attribute(SvgDeclarationPart part) => part switch
    {
        SvgDeclarationPart.Name => "name",
        SvgDeclarationPart.Type => "type",
        SvgDeclarationPart.Default => "default",
        SvgDeclarationPart.Min => "min",
        SvgDeclarationPart.Max => "max",
        SvgDeclarationPart.Step => "step",
        _ => null,
    };

    /// <summary>
    /// An expression as it can sit inside a double-quoted attribute.
    /// </summary>
    /// <remarks>
    /// Written out because this produces spans and never has an XML writer to hand. The set is what
    /// matters inside a double-quoted value.
    /// </remarks>
    private static string Escape(string value)
        => value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");

    /// <summary>What the document ends its lines with.</summary>
    /// <remarks>
    /// Off the document, not the platform: editing a file written elsewhere must not leave it with
    /// two kinds of line ending.
    /// </remarks>
    private static string Newline(string svgText)
    {
        var at = svgText.IndexOf('\n');

        return at > 0 && svgText[at - 1] == '\r' ? "\r\n" : "\n";
    }

    /// <summary>One level of indentation, as this document writes it.</summary>
    /// <remarks>
    /// From the first indented line, so tabs or four spaces keep being written that way.
    /// </remarks>
    private static string IndentUnit(string svgText)
    {
        var lines = svgText.Split('\n');

        foreach (var line in lines)
        {
            var width = 0;

            while (width < line.Length && (line[width] == ' ' || line[width] == '\t'))
            {
                width++;
            }

            if (width > 0 && width < line.Length)
            {
                return line.Substring(0, width);
            }
        }

        return "  ";
    }

    /// <summary>The whitespace in front of whatever begins at <paramref name="at"/>.</summary>
    private static string LeadingWhitespace(string svgText, int at)
    {
        if (at < 0)
        {
            return string.Empty;
        }

        var from = at;

        while (from > 0 && (svgText[from - 1] == ' ' || svgText[from - 1] == '\t'))
        {
            from--;
        }

        return svgText.Substring(from, at - from);
    }
}
