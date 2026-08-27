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
/// <para>
/// The alternative is to parse a drawing, change the tree and write it back, and that is measurably
/// worse for a file somebody is looking at: the SVG reader keeps no comments — its node switch has
/// no case for them — and writing a document out again renames <c>fill="{{ primary }}"</c> to a
/// placeholder plus a foreign attribute, reorders what it kept, and adds a doctype and two
/// namespaces nobody asked for. The drawing survives all of that; the file does not.
/// </para>
/// <para>
/// So an edit here is a splice. Everything outside the spans it returns is untouched, byte for byte,
/// which is the only version of this a source view can show without apologising for it.
/// </para>
/// <para>
/// Nothing here decides what is legal. A proposed declaration is put through
/// <see cref="SvgExpressionDeclarations.Builder"/>, the same rules the two readers enforce, and its
/// refusal is the message. The result is then read back with
/// <see cref="SvgExpressionDeclarations.Parse(string, out IReadOnlyList{SvgDeclarationDiagnostic})"/>
/// before it is handed over, so a splice that would leave the document saying something different
/// from what was asked for is refused rather than applied.
/// </para>
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

    /// <summary>Writes one attribute of a declaration, adding or removing it as needed.</summary>
    /// <param name="expression">What it should say, or null to take the attribute away.</param>
    public static SvgSourceEditResult Set(
        string svgText,
        string name,
        SvgDeclarationPart part,
        string? expression)
        => SetAll(svgText, new Dictionary<string, string?>(StringComparer.Ordinal) { [name] = expression }, part);

    /// <summary>Writes the <c>default</c> of several declarations at once.</summary>
    /// <remarks>
    /// One call rather than a loop, because the whole commit is one thing a reader did and should be
    /// one thing they can take back. A caller applying these in a text editor gets that by grouping
    /// them into a single undo step.
    /// </remarks>
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
    /// Both refusals are what a document looks like in the middle of being typed, which is exactly
    /// when a panel beside the text might be asked to change it. Neither is worth a mode: the action
    /// declines and says why, and works again as soon as the text does.
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
    private static string? Rejected(SvgExpressionDeclarations declarations, SvgExpressionParameter parameter)
    {
        var builder = new SvgExpressionDeclarations.Builder();

        try
        {
            foreach (var existing in declarations.Parameters)
            {
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
    /// The cheap half of correctness. Producing spans by hand can go wrong in ways that still look
    /// like text — a quote landed on, a tag left open — and the reader is the one thing that can say
    /// so. Two extra reads of a document measured at 3ms each is the whole cost.
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

        var last = block.Elements().LastOrDefault();

        var at = positions.InsertionPoint(block);

        // Line up with the declaration above where there is one, and otherwise one level in from the
        // block itself — copying what the document does rather than assuming two spaces.
        var alignment = last is { }
            ? LeadingWhitespace(svgText, positions.Span(last).Start)
            : LeadingWhitespace(svgText, positions.Span(block).Start) + indent;

        return new SvgTextEdit(at, 0, $"{newline}{alignment}{element}");
    }

    /// <summary>Writes the block, and the &lt;defs&gt; to hold it if the drawing has none.</summary>
    /// <remarks>
    /// Where SvgRecipeRewriter.InjectDeclarations puts it, and for the reason it gives: first in
    /// &lt;defs&gt;, where the declarations read as the document's preamble rather than as one more
    /// definition among the gradients. A drawing that has been through a recipe and one that has
    /// been through this should not differ in where they keep it.
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
            // A drawing written as <svg /> has nothing in it to parameterise. Rewriting the root into
            // a pair to hold a block would be a change to the shape of the document rather than an
            // addition to it, and it is not what anyone reaching for this meant.
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
    /// Written out rather than left to an XML writer, because this produces spans of text and never
    /// has a writer to hand. The set is the one that matters inside a double-quoted value: an
    /// apostrophe needs nothing there, and a newline in an expression is not something the language
    /// produces.
    /// </remarks>
    private static string Escape(string value)
        => value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");

    /// <summary>What the document ends its lines with.</summary>
    /// <remarks>
    /// Read off the document rather than taken from the platform, so that editing a file written on
    /// one machine on a different one does not leave it with two kinds of line ending.
    /// </remarks>
    private static string Newline(string svgText)
    {
        var at = svgText.IndexOf('\n');

        return at > 0 && svgText[at - 1] == '\r' ? "\r\n" : "\n";
    }

    /// <summary>One level of indentation, as this document writes it.</summary>
    /// <remarks>
    /// Measured from the first line that is indented at all, so a document written with tabs or with
    /// four spaces keeps being written that way. Two spaces only where a document has said nothing.
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
