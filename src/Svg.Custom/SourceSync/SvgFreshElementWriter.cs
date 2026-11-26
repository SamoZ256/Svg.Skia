// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace Svg;

/// <summary>
/// An element that is not in the file — a copy a command made, a path it computed — written as the
/// markup that reads back as it.
/// </summary>
/// <remarks>
/// Written in no namespace, as the draw tools write one: <c>SvgElementEditor.Insert</c> moves it into
/// its parent's and indents it where it lands. An expression is written back as the braces it was
/// lifted from, where it was written; nothing the parser keeps for itself is written at all.
/// </remarks>
public static class SvgFreshElementWriter
{
    private static readonly string s_expression = SvgExpressionAttributes.Namespace + ":";

    /// <summary><paramref name="element"/> and what it holds, as markup at depth zero.</summary>
    /// <param name="indentUnit">One level of the drawing's indentation, which the children are written at.</param>
    /// <param name="refusal">Why it cannot be written, naming what could not.</param>
    public static XElement? Write(SvgElement element, string indentUnit, out string? refusal)
    {
        if (element is null)
        {
            throw new ArgumentNullException(nameof(element));
        }

        if (element is SvgDocument)
        {
            refusal = "The drawing itself is not written as a new element.";

            return null;
        }

        // Under a sheet a presentation attribute loses to any rule that matches it, where a style declaration does not.
        var sheet = element.OwnerDocument?.Descendants().Any(each => SvgElementNames.NameOf(each) == "style") == true;

        return Written(element, indentUnit ?? string.Empty, 0, sheet, out refusal);
    }

    private static XElement? Written(SvgElement element, string indentUnit, int depth, bool sheet, out string? refusal)
    {
        refusal = null;

        var tag = SvgElementNames.NameOf(element);

        // The declarations are the Parameters panel's, and anything else foreign is not the drawing's to make.
        if (element is NonSvgElement)
        {
            refusal = $"A <{tag}> is not something a command writes into a drawing.";

            return null;
        }

        var written = new XElement(tag);
        var custom = element.CustomAttributes;
        var done = new HashSet<string>(StringComparer.Ordinal) { "style" };
        var style = new List<string>();
        string? why = null;

        if (element.ID is { Length: > 0 } id)
        {
            written.SetAttributeValue("id", id);
        }

        done.Add("id");

        if (custom.TryGetValue("class", out var classes))
        {
            written.SetAttributeValue("class", classes);
            done.Add("class");
        }

        element.Attributes.TryGetValue("marker", out var marker);

        // The SVG 2 href wins where both are written, and what the element holds is the last one parsed.
        if (element.TryGetCompatibilityHrefAttributeValue(string.Empty, out var plain)
            && element.TryGetCompatibilityHrefAttributeValue(SvgNamespaces.XLinkNamespace, out var linked)
            && !string.Equals(plain, linked, StringComparison.Ordinal))
        {
            refusal = $"<{tag}> is written with an href and an xlink:href that say different things, and a copy could say only one.";

            return null;
        }

        // The raw text is what the text compiler reads, and it draws nothing for a malformed one; one the
        // parser could not read at all is held only as this.
        custom.TryGetValue(SvgStyleAttributeNames.RawTextDecorationAttributeKey, out var decoration);

        // The shorthand before its longhands, which it would otherwise overwrite as it is read.
        foreach (var property in element.GetProperties().OrderBy(property => property.AttributeName == "font" ? 0 : 1))
        {
            var name = property.AttributeName;

            if (property.DescriptorType != DescriptorType.Property || done.Contains(name))
            {
                continue;
            }

            if (SvgExpressionAttributes.Lifted(custom, name) is { } expression)
            {
                // The value under the key is the placeholder, never what the drawing says.
                Place(name, property.AttributeNamespace, Braced(name, expression));
            }
            else if (element.Attributes.TryGetValue(name, out var value) && value is { } && !ReferenceEquals(value, SvgPaintServer.NotSet))
            {
                // What marker sets, each of the three it fills in is too.
                if (marker is { } && name is "marker-start" or "marker-mid" or "marker-end" && SvgValueSpelling.Precise(marker, value))
                {
                    done.Add(name);

                    continue;
                }

                if (SvgValueSpelling.Spell(element, name, value, name == "text-decoration" ? decoration : null, out why) is not { } text)
                {
                    refusal = why;

                    return null;
                }

                Place(name, property.AttributeNamespace, text);
            }
            else if (name == "text-decoration" && decoration is { })
            {
                Place(name, property.AttributeNamespace, decoration);
            }
        }

        if (element.Attributes.FirstOrDefault(own => own.Value is { } && !ReferenceEquals(own.Value, SvgPaintServer.NotSet) && !done.Contains(own.Key)) is { Key: { } unread })
        {
            refusal = $"'{unread}' on <{tag}> is held by no attribute the element is read with.";

            return null;
        }

        foreach (var pair in custom)
        {
            var key = pair.Key;
            var colon = key.LastIndexOf(':');

            if (done.Contains(key)
                || (key == "tagName" && element is SvgUnknownElement)
                || (colon > 0 && SvgBookkeepingFilterWriter.IsBookkeeping(key.Substring(0, colon))))
            {
                continue;
            }

            if (key.StartsWith(s_expression, StringComparison.Ordinal))
            {
                var local = key.Substring(s_expression.Length);

                if (local == SvgExpressionAttributes.ContentName)
                {
                    continue;
                }

                if (SvgExpressionAttributes.IsSupported(local))
                {
                    // Lifted on a name this element has no attribute for, which the descriptors did not reach.
                    if (!done.Contains(local))
                    {
                        Place(local, null, Braced(local, pair.Value));
                    }

                    continue;
                }

                if (local is SvgExpressionAttributes.Bounds or SvgExpressionAttributes.Draws || local.StartsWith(SvgExpressionAttributes.Argument, StringComparison.Ordinal))
                {
                    written.SetAttributeValue(XNamespace.Get(SvgExpressionAttributes.Namespace) + local, pair.Value);

                    continue;
                }
            }
            else if (SvgStyleAttributeNames.IsStyleOnly(key))
            {
                Place(key, null, pair.Value);

                continue;
            }
            else if (IsName(key))
            {
                written.SetAttributeValue(key, pair.Value);

                continue;
            }
            else if (colon > 0 && IsName(key.Substring(colon + 1)) && Uri.IsWellFormedUriString(key.Substring(0, colon), UriKind.Absolute))
            {
                written.SetAttributeValue(XNamespace.Get(key.Substring(0, colon)) + key.Substring(colon + 1), pair.Value);

                continue;
            }

            refusal = $"<{tag}> holds '{key}', which is no attribute a file can be written with.";

            return null;
        }

        foreach (var staged in element.StagedStyles())
        {
            if (staged.Value.Count == 0 || !done.Add(staged.Key))
            {
                continue;
            }

            // Only the rule that wins. One from a sheet is the sheet's to say again, and is not copied inline.
            var (strength, value) = (staged.Value[staged.Value.Count - 1].Key, staged.Value[staged.Value.Count - 1].Value);
            var important = strength >= SvgCssDeclarationPriority.ImportantSpecificityOffset;
            var band = important ? strength - SvgCssDeclarationPriority.ImportantSpecificityOffset : strength;

            if (band == SvgElement.StyleSpecificity_PresAttribute && IsName(staged.Key))
            {
                written.SetAttributeValue(staged.Key, value);
            }
            else if (band >= SvgElement.StyleSpecificity_InlineStyle)
            {
                style.Add(staged.Key + ": " + value + (important ? " !important" : string.Empty));
            }
        }

        // What the element says in its own style; what a sheet gives it, the sheet gives a copy too.
        if (SvgCssVariableResolver.CreateSnapshot(element) is { } variables)
        {
            foreach (var variable in variables.Rules)
            {
                if (variable.Value.Where(rule => rule.Specificity >= SvgElement.StyleSpecificity_InlineStyle).OrderBy(rule => rule.SourceOrder).LastOrDefault() is { Value: { } } inline)
                {
                    style.Add(variable.Key + ": " + inline.Value + (inline.Important ? " !important" : string.Empty));
                }
            }
        }

        if (style.Count > 0)
        {
            // The shorthand before the markers it would otherwise override.
            written.SetAttributeValue("style", string.Join("; ", style.OrderBy(declaration => declaration.StartsWith("marker:", StringComparison.Ordinal) ? 0 : 1)));
        }

        var lifted = SvgExpressionAttributes.Lifted(custom, SvgExpressionAttributes.ContentName);

        // A command that adds a child edits Children and not Nodes, and the two would write different things.
        if (lifted is null && element.Nodes.OfType<SvgContentNode>().Any() && !element.Nodes.OfType<SvgElement>().SequenceEqual(element.Children))
        {
            refusal = $"The text of <{tag}> and the elements in it disagree about what it holds.";

            return null;
        }

        if (lifted is { } said)
        {
            // The one empty node the loader leaves in the text's place is not written; what is beside it, an
            // animation say, still is.
            written.Add(new XText("{{ " + said + " }}"));

            foreach (var child in element.Children)
            {
                if (Written(child, indentUnit, depth + 1, sheet, out refusal) is not { } inner)
                {
                    return null;
                }

                written.Add(inner);
            }
        }
        else if (element.Nodes.OfType<SvgContentNode>().Any() || InText(element) || Preserved(element))
        {
            // Text and elements as they run, with nothing between them: whitespace in text is text, and
            // under xml:space="preserve" in anything else too.
            foreach (var node in element.Nodes.Count > 0 ? element.Nodes : element.Children.Cast<ISvgNode>())
            {
                if (node is SvgContentNode content)
                {
                    written.Add(new XText(content.Content));
                }
                else if (node is SvgElement child)
                {
                    if (Written(child, indentUnit, depth + 1, sheet, out refusal) is not { } inner)
                    {
                        return null;
                    }

                    written.Add(inner);
                }
            }

            if (element.Nodes.Count == 0 && element.Children.Count == 0 && !string.IsNullOrEmpty(element.Content))
            {
                written.Add(new XText(element.Content));
            }
        }
        else if (element.Children.Count > 0)
        {
            foreach (var child in element.Children)
            {
                if (Written(child, indentUnit, depth + 1, sheet, out refusal) is not { } inner)
                {
                    return null;
                }

                written.Add(new XText("\n" + Repeated(indentUnit, depth + 1)), inner);
            }

            written.Add(new XText("\n" + Repeated(indentUnit, depth)));
        }
        else if (!string.IsNullOrEmpty(element.Content))
        {
            // Text a command set as Content, with no node to hold it: what the element's own writer falls back to.
            written.Add(new XText(element.Content));
        }

        return written;

        void Place(string name, string? ns, string text)
        {
            done.Add(name);

            var strength = custom.TryGetValue(SvgExpressionAttributes.SourceKeyFor(name), out var from)
                           && int.TryParse(from, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : SvgElement.StyleSpecificity_PresAttribute;

            // A marker beside the shorthand goes in the style with it, where an attribute would lose to it.
            if (SvgStyleAttributeNames.IsStyleOnly(name) || strength >= SvgElement.StyleSpecificity_InlineStyle
                || (marker is { } && name.StartsWith("marker-", StringComparison.Ordinal))
                || (sheet && s_presentation.Contains(name) && element is not (SvgAnimationElement or SvgFontFace)))
            {
                style.Add(name + ": " + text + (strength >= SvgCssDeclarationPriority.ImportantSpecificityOffset ? " !important" : string.Empty));

                return;
            }

            written.SetAttributeValue(Named(element, name, ns), text);
        }
    }

    private static readonly HashSet<string> s_presentation = new(
        SvgStyleAttributeNames.All.Where(name => !SvgElementFactory.IsSvg2GeometryAttribute(name)),
        StringComparer.Ordinal);

    /// <summary>Whether <paramref name="element"/> is text, or inside some, where whitespace between its children would be text.</summary>
    private static bool InText(SvgElement element)
    {
        for (SvgElement? at = element; at is { }; at = at.Parent)
        {
            if (at is SvgTextBase)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Preserved(SvgElement element)
    {
        for (SvgElement? at = element; at is { }; at = at.Parent)
        {
            if (at.Attributes.TryGetValue("space", out var space) && space is XmlSpaceHandling handling and not XmlSpaceHandling.Inherit)
            {
                return handling == XmlSpaceHandling.Preserve;
            }
        }

        return false;
    }

    private static string Braced(string name, string expression)
        => SvgExpressionAttributes.IsInArguments(name) ? expression : "{{ " + expression + " }}";

    /// <remarks>href as the element was written with it, and plain where it was never written.</remarks>
    private static XName Named(SvgElement element, string name, string? ns)
    {
        if (string.Equals(ns, SvgNamespaces.XLinkNamespace, StringComparison.Ordinal))
        {
            return name != "href" || element.TryGetCompatibilityHrefAttributeValue(SvgNamespaces.XLinkNamespace, out _)
                ? XNamespace.Get(SvgNamespaces.XLinkNamespace) + name
                : name;
        }

        return string.Equals(ns, SvgNamespaces.XmlNamespace, StringComparison.Ordinal) ? XNamespace.Xml + name : name;
    }

    private static bool IsName(string name) => name.Length > 0 && XmlConvert.IsStartNCNameChar(name[0]) && name.All(XmlConvert.IsNCNameChar);

    private static string Repeated(string unit, int times) => string.Concat(Enumerable.Repeat(unit, times));
}
