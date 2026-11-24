// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Svg.Expressions;

namespace Svg.SourceEditing;

/// <summary>One attribute of an element, as the file writes it.</summary>
/// <remarks>
/// <see cref="Value"/> is the text between the quotes with entities resolved, which is what an
/// editor shows and what it compares against; the escaping back into markup is
/// <see cref="SvgAttributeEditor.SetAttribute"/>'s.
/// </remarks>
public readonly record struct SvgSourceAttribute(string Name, string Value);

/// <summary>
/// Writes one attribute of one element as a span into the text it was read from.
/// </summary>
/// <remarks>
/// The other half of what a drawing says. <see cref="SvgDeclarationEditor"/> writes what it declares
/// and <see cref="SvgFrameEditor"/> writes the frame of its root; this writes anything else, on any
/// element, which is what an editor showing one element's attributes needs.
///
/// A span and not a rewritten document, for the reason everything here is: a document regenerated
/// from a parsed tree comes back without the author's formatting, attribute order or comments, and
/// changing one value has no business touching any of them.
///
/// The element is named by the address <c>SvgElementAddress.Key</c> spells, as a string. That type
/// belongs to the SVG parser, and this assembly deliberately cannot see it — the same rule
/// <see cref="SvgRecipeRuleEditor"/> states about the recipe package. The key is the identity every
/// caller already holds, and both sides spell it the same way because the walk below counts what
/// <c>SvgSourceElements</c> counts.
/// </remarks>
public static class SvgAttributeEditor
{
    /// <summary>
    /// The name of the element <paramref name="addressKey"/> names, or null where it names nothing.
    /// </summary>
    /// <remarks>
    /// An SVG element is named bare however the file binds its namespace. Inkscape declares
    /// <c>xmlns:svg</c> beside the default, and asking for the prefix then answers <c>svg</c> for every
    /// element in the drawing. A foreign element keeps its prefix, which is what tells it apart.
    /// </remarks>
    public static string? ElementName(SvgSourceDocument source, string addressKey)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (addressKey is null)
        {
            throw new ArgumentNullException(nameof(addressKey));
        }

        return Resolve(source.Document, addressKey) is { } element ? ElementName(element) : null;
    }

    /// <summary>The name of <paramref name="element"/>, spelt as <see cref="ElementName(SvgSourceDocument, string)"/> spells it.</summary>
    /// <remarks>For a walk holding each element already, which resolving every key again from the root made quadratic in its siblings.</remarks>
    public static string ElementName(XElement element)
    {
        if (element is null)
        {
            throw new ArgumentNullException(nameof(element));
        }

        if (element.Name.Namespace == SvgNamespace || element.Name.Namespace == XNamespace.None)
        {
            return element.Name.LocalName;
        }

        var prefix = element.GetPrefixOfNamespace(element.Name.Namespace);

        return string.IsNullOrEmpty(prefix) ? element.Name.LocalName : prefix + ":" + element.Name.LocalName;
    }

    /// <inheritdoc cref="Attributes(string, string)"/>
    public static IReadOnlyList<SvgSourceAttribute> Attributes(SvgSourceDocument source, string addressKey)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (addressKey is null)
        {
            throw new ArgumentNullException(nameof(addressKey));
        }

        return Resolve(source.Document, addressKey) is { } element ? Attributes(element) : Array.Empty<SvgSourceAttribute>();
    }

    /// <summary>The attributes of <paramref name="element"/>, spelt as <see cref="Attributes(SvgSourceDocument, string)"/> spells them.</summary>
    /// <remarks>For a walk holding each element already, as <see cref="ElementName(XElement)"/> is.</remarks>
    public static IReadOnlyList<SvgSourceAttribute> Attributes(XElement element)
    {
        if (element is null)
        {
            throw new ArgumentNullException(nameof(element));
        }

        return element.Attributes()
            .Where(attribute => !attribute.IsNamespaceDeclaration)
            .Select(attribute => new SvgSourceAttribute(Spelling(attribute), attribute.Value))
            .ToList();
    }

    /// <summary>What one of an element's attributes says, as the file spells it.</summary>
    /// <remarks>
    /// The text and not a reading of it: an attribute an expression writes is wanted with the
    /// expression still in it, which is the whole reason for asking the source rather than the
    /// document built from it.
    /// </remarks>
    /// <returns>The attribute's value, or null where the element carries no such attribute.</returns>
    public static string? Attribute(SvgSourceDocument source, string addressKey, string name)
    {
        foreach (var attribute in Attributes(source, addressKey))
        {
            if (string.Equals(attribute.Name, name, StringComparison.Ordinal))
            {
                return attribute.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// What the element's <c>style</c> attribute sets <paramref name="name"/> to, or null where it
    /// does not.
    /// </summary>
    /// <remarks>
    /// Such a declaration wins over the attribute, which is why <see cref="SetAttribute(SvgSourceDocument, string, string, string?)"/>
    /// writes into it rather than under it.
    /// </remarks>
    public static string? Styled(SvgSourceDocument source, string addressKey, string name)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        return Resolve(source.Document, addressKey) is { } element ? Styled(element, name) : null;
    }

    /// <inheritdoc cref="SetAttribute(string, string, string, string?)"/>
    /// <returns>The sentence refusing the edit, or null where it was made.</returns>
    /// <remarks>
    /// Writing the tree rather than the text it was read from. What the file looks like afterwards
    /// is <see cref="SvgSourceDocument"/>'s to answer, and it writes the value over the old one
    /// rather than the tag over the tag, so the layout somebody gave the element survives.
    /// </remarks>
    public static string? SetAttribute(
        SvgSourceDocument source,
        string addressKey,
        string attributeName,
        string? value)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (addressKey is null)
        {
            throw new ArgumentNullException(nameof(addressKey));
        }

        if (attributeName is null)
        {
            throw new ArgumentNullException(nameof(attributeName));
        }

        return Resolve(source.Document, addressKey) is { } element
            ? SetAttribute(element, attributeName, value)
            : SvgElementEditor.Missing(addressKey);
    }

    /// <inheritdoc cref="SetAttribute(SvgSourceDocument, string, string, string?)"/>
    /// <remarks>
    /// A property a declaration in the element's style sets is written there, since the declaration
    /// is what paints; the rest of the style is left as it was written.
    /// </remarks>
    internal static string? SetAttribute(XElement element, string attributeName, string? value)
    {
        var name = attributeName.Trim();

        if (name.Length == 0)
        {
            return "An edit has to name an attribute.";
        }

        // Not an attribute of the element in the sense anybody edits, and taking one away would
        // leave every name under it standing for something else -- a root written back without its
        // xmlns reads as a different document, and nothing about it would look wrong.
        if (name == "xmlns" || name.StartsWith("xmlns:", StringComparison.Ordinal))
        {
            return "A namespace declaration is not something to edit here.";
        }

        if (name.IndexOf(':') < 0 && (string?)element.Attribute("style") is { } style && Winner(style, name) is { } declaration)
        {
            return s_geometry.Contains(name) ? Overridden(element, name) : Restyle(element, style, declaration, name, value);
        }

        // A prefix stands for a namespace, and an attribute named for one is that attribute rather
        // than a second one beside it: writing href on a <use> that has xlink:href would otherwise
        // add a second href and leave the drawing pointing two ways at once.
        var colon = name.IndexOf(':');

        if (colon > 0 && element.GetNamespaceOfPrefix(name.Substring(0, colon)) is null)
        {
            // Except the extension's own prefix, which is declared the way a first parameter declares
            // it: marking a box in a drawing that has no expressions is the same kind of first use.
            var prefix = name.Substring(0, colon);

            if (value is null || element.Document?.Root is not { } root ||
                SvgExpressionDeclarations.NamespacePrefixFor(root, out var declared) != prefix || declared)
            {
                return $"This drawing does not say what '{prefix}' stands for, so '{name}' cannot be written.";
            }

            root.SetAttributeValue(XNamespace.Xmlns + prefix, SvgExpressionDeclarations.Namespace);
        }

        try
        {
            // Inside, because naming the attribute is itself what rejects a name XML will not take.
            var written = colon > 0
                ? element.GetNamespaceOfPrefix(name.Substring(0, colon))! + name.Substring(colon + 1)
                : XName.Get(name);

            element.SetAttributeValue(written, value);
        }
        catch (XmlException)
        {
            // Splicing text would have written this and left a document that no longer parses; the
            // tree says no first, which is the one place this is stricter than the span it replaces.
            return $"'{name}' is not a name an attribute can have.";
        }
        catch (ArgumentException)
        {
            return $"'{name}' is not a name an attribute can have.";
        }

        return null;
    }

    /// <summary>
    /// The text written between an element's tags, or null where there is none to edit.
    /// </summary>
    /// <remarks>
    /// The other value an element carries. A <c>&lt;text&gt;</c>'s words are a child node rather than an
    /// attribute, so nothing above could reach them — and the language drives them, since content
    /// wholly written as <c>{{ … }}</c> is lifted and evaluated like any lifted attribute.
    ///
    /// Only the three elements whose text is their own. A <c>&lt;tref&gt;</c> takes its from whatever its
    /// href names, and everything else that holds content — <c>&lt;style&gt;</c>, <c>&lt;title&gt;</c>,
    /// <c>&lt;script&gt;</c> — holds something that is not a string an expression could produce.
    /// </remarks>
    /// <param name="refusal">
    /// Why there is text here that still cannot be edited, or null. Null with a null return means
    /// there is simply nothing to show, which is not a refusal and is not worth a sentence.
    /// </param>
    /// <returns>The text as written, empty where the element holds none, or null.</returns>
    public static string? Content(SvgSourceDocument source, string addressKey, out string? refusal)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (addressKey is null)
        {
            throw new ArgumentNullException(nameof(addressKey));
        }

        refusal = null;

        if (Resolve(source.Document, addressKey) is not { } element)
        {
            return null;
        }

        if (Carries(element) is { } why)
        {
            // A <tref> is told why; a <rect> is not. Saying "a rectangle has no text" under every
            // shape in a drawing is noise about something nobody expected to be there.
            refusal = element.Name.LocalName == "tref" ? why : null;

            return null;
        }

        if (element.Elements().FirstOrDefault() is { } child)
        {
            refusal = $"This element's text is written in its <{child.Name.LocalName}> children, "
                      + "so it cannot be edited as one value. Pick a child row instead.";

            return null;
        }

        return element.Nodes().OfType<XText>().FirstOrDefault()?.Value ?? string.Empty;
    }

    /// <summary>Writes the text between an element's tags, or takes it away.</summary>
    /// <remarks>
    /// The run is rewritten in place rather than through <see cref="XElement.Value"/>, which removes
    /// every child node — comments included — on its way to installing one of its own.
    ///
    /// Nothing is trimmed. Under <c>xml:space="preserve"</c> a leading space is a value, and an
    /// editor that trimmed would rewrite a file for somebody who only opened a box and left it.
    /// </remarks>
    /// <param name="value">The text to write, or null to leave the element with none.</param>
    /// <returns>The sentence refusing the edit, or null where it was made.</returns>
    public static string? SetContent(SvgSourceDocument source, string addressKey, string? value)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (addressKey is null)
        {
            throw new ArgumentNullException(nameof(addressKey));
        }

        if (Resolve(source.Document, addressKey) is not { } element)
        {
            return SvgElementEditor.Missing(addressKey);
        }

        if (Carries(element) is { } why)
        {
            return why;
        }

        if (element.Elements().FirstOrDefault() is { } child)
        {
            return $"This element's text is written in its <{child.Name.LocalName}> children, "
                   + "so it cannot be edited as one value. Pick a child row instead.";
        }

        var run = element.Nodes().OfType<XText>().FirstOrDefault();

        if (value is not { Length: > 0 })
        {
            // Removed rather than emptied, so an element that was written <text/> is written that
            // way again instead of coming back as a pair of tags nobody typed.
            run?.Remove();

            return null;
        }

        if (run is { })
        {
            run.Value = value;
        }
        else
        {
            element.Add(new XText(value));
        }

        return null;
    }

    /// <summary>Why this element has no text of its own, or null where it has.</summary>
    private static string? Carries(XElement element)
    {
        // A let's body is its expression, which is text of its own as much as a <text>'s words are.
        if (element.Name.Namespace == ExpressionNamespace)
        {
            return element.Name.LocalName == "let" ? null : Only;
        }

        if (element.Name.Namespace != SvgNamespace && element.Name.Namespace != XNamespace.None)
        {
            return Only;
        }

        return element.Name.LocalName switch
        {
            "text" or "tspan" or "textPath" => null,
            "tref" => "A <tref> takes its text from what its href names, so it has none of its own to edit.",
            _ => Only
        };
    }

    private const string Only = "Only a <text>, a <tspan>, a <textPath> or an <e:let> has text of its own to edit.";

    internal static readonly XNamespace SvgNamespace = "http://www.w3.org/2000/svg";

    private static readonly XNamespace ExpressionNamespace = "https://svg.skia/expr/1.0";

    /// <summary>
    /// The element <paramref name="addressKey"/> names, or null where the document has no such path.
    /// </summary>
    /// <remarks>
    /// The inverse of the walk in <c>SvgSourceElements</c>, and it has to count exactly what that
    /// counts or the two spell different paths: the root is the empty key, children are
    /// <see cref="XContainer.Elements()"/> so a comment or a run of text takes no index, and the
    /// indexes are invariant.
    /// </remarks>
    /// <summary>
    /// Whether the element <paramref name="addressKey"/> names is a part a clip draws: a child of a
    /// <c>&lt;clipPath&gt;</c>, or what a <c>&lt;use&gt;</c> there points at. Only there does a
    /// shape's geometry take an expression.
    /// </summary>
    public static bool IsClipped(SvgSourceDocument source, string addressKey)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (addressKey is null)
        {
            throw new ArgumentNullException(nameof(addressKey));
        }

        return Resolve(source.Document, addressKey) is { } element && SvgExpressionDeclarations.ClipParts(source.Document).Contains(element);
    }

    /// <summary>The element an address key names, or null where it names nothing.</summary>
    /// <remarks>Internal because the key convention should have one reader, not one per editor.</remarks>
    internal static XElement? Resolve(XDocument document, string addressKey)
    {
        if (document.Root is not { } root)
        {
            return null;
        }

        if (addressKey.Length == 0)
        {
            return root;
        }

        var at = root;

        foreach (var segment in addressKey.Split('/'))
        {
            if (!int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index < 0)
            {
                return null;
            }

            if (at.Elements().ElementAtOrDefault(index) is not { } child)
            {
                return null;
            }

            at = child;
        }

        return at;
    }

    /// <summary>The address key that names <paramref name="element"/>: what <see cref="Resolve"/> reads.</summary>
    internal static string Key(XElement element)
    {
        var indexes = new List<string>();

        for (var at = element; at.Parent is { } parent; at = parent)
        {
            indexes.Add(at.ElementsBeforeSelf().Count().ToString(CultureInfo.InvariantCulture));
        }

        indexes.Reverse();

        return string.Join("/", indexes);
    }

    /// <summary>An attribute's name as the file spells it, prefix and all.</summary>
    /// <remarks>
    /// The prefix is part of the name and not decoration: <c>href</c> and <c>xlink:href</c> are two
    /// attributes, and reporting the second as the first is how an editor comes to write both.
    /// </remarks>
    private static string Spelling(XAttribute attribute)
    {
        if (attribute.Name.Namespace == XNamespace.None)
        {
            return attribute.Name.LocalName;
        }

        var prefix = attribute.Parent?.GetPrefixOfNamespace(attribute.Name.Namespace);

        return string.IsNullOrEmpty(prefix) ? attribute.Name.LocalName : prefix + ":" + attribute.Name.LocalName;
    }

    /// <summary>
    /// The sentence refusing to write <paramref name="name"/> where the element's style sets it, or null.
    /// </summary>
    /// <remarks>
    /// A style declaration beats the presentation attribute under it, so writing the attribute
    /// would leave a document where the change paints nothing. Asked where the declaration cannot
    /// be written either: geometry, and where a clip takes a shape's paint.
    /// </remarks>
    internal static string? Overridden(XElement element, string name)
        => Styled(element, name) is { }
            ? $"'{name}' is set in this element's style attribute, which wins over the attribute. Change it there instead."
            : null;

    /// <summary>What a <c>style</c> declaration on the element sets the attribute to, overriding it, or null.</summary>
    /// <remarks>As written, its <c>!important</c> included.</remarks>
    internal static string? Styled(XElement element, string attributeName)
        => (string?)element.Attribute("style") is { } style && Winner(style, attributeName) is { } declaration
            ? style.Substring(declaration.ValueStart, declaration.WrittenEnd - declaration.ValueStart)
            : null;

    /// <summary>
    /// The expression the file writes <paramref name="name"/> with for the element, as written and
    /// without <c>!important</c>, or null where a literal or nothing does.
    /// </summary>
    /// <remarks>
    /// Its style before its attribute, as the cascade paints them, and for a property that inherits
    /// the nearest element around it that sets it. Not a <c>&lt;style&gt;</c> sheet, what a
    /// <c>&lt;use&gt;</c> passes on, or a <c>var()</c>.
    /// </remarks>
    public static string? Driven(SvgSourceDocument source, string addressKey, string name)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        for (var at = Resolve(source.Document, addressKey); at is { }; at = at.Parent)
        {
            var value = (string?)at.Attribute("style") is { } style && Winner(style, name) is { } declaration
                ? style.Substring(declaration.ValueStart, declaration.ValueEnd - declaration.ValueStart)
                : (string?)at.Attribute(name);

            if (value is null ? SvgElementEditor.Inherits(name) : value.Trim() == "inherit")
            {
                continue;
            }

            return value?.IndexOf("{{", StringComparison.Ordinal) >= 0 ? value : null;
        }

        return null;
    }

    // SVG 2 makes some of these CSS properties, but what is written for them is attribute grammar
    // (unitless lengths, rotate(a cx cy), bare path data), which in style="" no browser reads.
    private static readonly HashSet<string> s_geometry = new(StringComparer.OrdinalIgnoreCase)
    {
        "transform", "d", "points", "x", "y", "width", "height", "cx", "cy", "r", "rx", "ry", "x1", "y1", "x2", "y2"
    };

    /// <summary>One declaration of a style attribute, as offsets into it.</summary>
    /// <param name="Start">Just after the separator before it.</param>
    /// <param name="End">At the separator after it, or the end.</param>
    /// <param name="ValueStart">Its value, trimmed, up to <paramref name="ValueEnd"/> without <c>!important</c> and to <paramref name="WrittenEnd"/> with it.</param>
    private readonly record struct Declaration(string Name, int Start, int End, int ValueStart, int ValueEnd, int WrittenEnd)
    {
        public bool Important => WrittenEnd != ValueEnd;
    }

    /// <summary>The style's declarations, split where CSS splits them.</summary>
    /// <remarks>
    /// Upstream's <c>SvgInlineStyleService</c> scanner, kept as offsets so one value can be replaced
    /// and nothing else moves: not inside quotes, comments or brackets, which keeps a data: URL and
    /// an expression's braces whole.
    /// </remarks>
    private static List<Declaration> Declarations(string style) => Declarations(style, out _);

    /// <param name="closed">Whether the style ends outside every quote, comment and bracket it opens.</param>
    private static List<Declaration> Declarations(string style, out bool closed)
    {
        var found = new List<Declaration>();
        var (start, depth, quote, comment, colon) = (0, 0, '\0', false, -1);

        for (var i = 0; i < style.Length; i++)
        {
            var c = style[i];

            if (comment)
            {
                if (c == '*' && i + 1 < style.Length && style[i + 1] == '/')
                {
                    comment = false;
                    i++;
                }

                continue;
            }

            if (c == '\\')
            {
                i++;

                continue;
            }

            if (quote != '\0')
            {
                quote = c == quote ? '\0' : quote;

                continue;
            }

            if (c == '/' && i + 1 < style.Length && style[i + 1] == '*')
            {
                comment = true;
                i++;
            }
            else if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (c == ':' && depth == 0 && colon < 0)
            {
                colon = i;
            }
            else if (c == ';' && depth == 0)
            {
                Add(start, i, colon);
                (start, colon) = (i + 1, -1);
            }
        }

        Add(start, style.Length, colon);

        closed = quote == '\0' && !comment && depth == 0;

        return found;

        void Add(int from, int to, int at)
        {
            if (at < 0)
            {
                return;
            }

            var name = Regex.Replace(style.Substring(from, at - from), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline).Trim();
            var (valueStart, writtenEnd) = (at + 1, to);

            while (valueStart < writtenEnd && char.IsWhiteSpace(style[valueStart]))
            {
                valueStart++;
            }

            while (writtenEnd > valueStart && char.IsWhiteSpace(style[writtenEnd - 1]))
            {
                writtenEnd--;
            }

            var important = Regex.Match(style.Substring(valueStart, writtenEnd - valueStart), @"\s*!\s*important$", RegexOptions.IgnoreCase);

            if (name.Length > 0)
            {
                found.Add(new Declaration(name, from, to, valueStart, important.Success ? valueStart + important.Index : writtenEnd, writtenEnd));
            }
        }
    }

    /// <summary>The declaration of <paramref name="name"/> that paints: the last, unless an earlier one is <c>!important</c>; an empty one sets nothing.</summary>
    private static Declaration? Winner(string style, string name)
    {
        Declaration? winner = null;

        foreach (var declaration in Declarations(style))
        {
            if (string.Equals(declaration.Name, name, StringComparison.OrdinalIgnoreCase)
                && declaration.ValueEnd > declaration.ValueStart
                && (declaration.Important || winner is not { Important: true }))
            {
                winner = declaration;
            }
        }

        return winner;
    }

    /// <summary>Writes <paramref name="value"/> in place of the value of the declaration that paints, or takes <paramref name="name"/> away.</summary>
    private static string? Restyle(XElement element, string style, Declaration winner, string name, string? value)
    {
        if (value is null)
        {
            // Every declaration of it, and the attribute: a losing one, or the attribute under it,
            // would take over, and taking it away would read as a new value.
            foreach (var declaration in Declarations(style).Where(each => string.Equals(each.Name, name, StringComparison.OrdinalIgnoreCase)).Reverse())
            {
                style = declaration.End < style.Length && style[declaration.End] == ';'
                    ? style.Remove(declaration.Start, declaration.End + 1 - declaration.Start)
                    : declaration.Start > 0 && style[declaration.Start - 1] == ';'
                        ? style.Remove(declaration.Start - 1, declaration.End - declaration.Start + 1)
                        : style.Remove(declaration.Start, declaration.End - declaration.Start);
            }

            element.SetAttributeValue("style", style.Trim(' ', ';', '\t', '\r', '\n').Length == 0 ? null : style.TrimStart());

            // A style can name what no attribute can, such as -inkscape-font-specification or a --custom property.
            if (XmlConvert.IsStartNCNameChar(name[0]) && name.All(XmlConvert.IsNCNameChar))
            {
                element.SetAttributeValue(name, null);
            }

            return null;
        }

        var written = value.Trim();
        var spliced = style.Substring(0, winner.ValueStart) + written + style.Substring(winner.ValueEnd);

        // Read back: a value that ends the declaration early, opens a quote or a comment, or carries
        // its own !important would leave the style saying something other than what was asked.
        return written.Length > 0
               && !Regex.IsMatch(written, @"!\s*important$", RegexOptions.IgnoreCase)
               && Declarations(spliced, out var closed).Count == Declarations(style).Count
               && closed
               && Winner(spliced, name) is { } again
               && again.Start == winner.Start
               && spliced.Substring(again.ValueStart, again.ValueEnd - again.ValueStart) == written
            ? Set(element, spliced)
            : $"'{value}' cannot be written inside the style declaration that sets '{name}'.";

        static string? Set(XElement element, string spliced)
        {
            element.SetAttributeValue("style", spliced);

            return null;
        }
    }
}
