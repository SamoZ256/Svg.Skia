using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Xml;

namespace Svg
{
    /// <summary>
    /// Parses and creates <see cref="SvgElement"/> instances from XML.
    ///
    /// Diverges from upstream in one place: a raw <c>marker="..."</c> presentation attribute is
    /// ignored, while stylesheet and inline-style <c>marker</c> still take the CSS path. The W3C
    /// marker tests expect that distinction; upstream treated both as CSS shorthand.
    /// </summary>
    [ElementFactory]
    internal partial class SvgElementFactory
    {
        private static readonly ConcurrentDictionary<Type, HashSet<string>> s_eventDescriptorAttributeNamesByType = new();

        private static readonly ConcurrentDictionary<Type, Dictionary<string, ISvgPropertyDescriptor>> s_propertiesByType = new();

        private readonly SvgInlineStyleAttributeParser inlineStyleAttributeParser = new();

        internal bool PreserveJavaScriptDomState { get; set; }

        internal bool PreserveCompatibilityPresentationAttributes { get; set; }

        /// <summary>
        /// Gets a list of available types that can be used when creating an <see cref="SvgElement"/>.
        /// </summary>
        public List<ElementInfo> AvailableElements => availableElements;

        /// <summary>
        /// Gets a list of available types that can be used when creating an <see cref="SvgElement"/>.
        /// </summary>
        internal Dictionary<string, List<Type>> AvailableElementsDictionary => availableElementsDictionary;

        /// <summary>
        /// Creates an <see cref="SvgDocument"/> from the current node in the specified <see cref="XmlReader"/>.
        /// </summary>
        /// <param name="reader">The <see cref="XmlReader"/> containing the node to parse into an <see cref="SvgDocument"/>.</param>
        /// <exception cref="ArgumentNullException">The <paramref name="reader"/> parameter cannot be <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">The CreateDocument method can only be used to parse root &lt;svg&gt; elements.</exception>
        public T CreateDocument<T>(XmlReader reader) where T : SvgDocument, new()
        {
            if (reader == null)
            {
                throw new ArgumentNullException("reader");
            }

            if (reader.LocalName != "svg")
            {
                throw new InvalidOperationException("The CreateDocument method can only be used to parse root <svg> elements.");
            }

            return (T)CreateElement<T>(reader, true, null);
        }

        /// <summary>
        /// Creates an <see cref="SvgElement"/> from the current node in the specified <see cref="XmlReader"/>.
        /// </summary>
        /// <param name="reader">The <see cref="XmlReader"/> containing the node to parse into a subclass of <see cref="SvgElement"/>.</param>
        /// <param name="document">The <see cref="SvgDocument"/> that the created element belongs to.</param>
        /// <exception cref="ArgumentNullException">The <paramref name="reader"/> and <paramref name="document"/> parameters cannot be <c>null</c>.</exception>
        public SvgElement CreateElement(XmlReader reader, SvgDocument document)
        {
            if (reader == null)
            {
                throw new ArgumentNullException("reader");
            }

            return CreateElement<SvgDocument>(reader, false, document);
        }

        private SvgElement CreateElement<T>(XmlReader reader, bool fragmentIsDocument, SvgDocument document) where T : SvgDocument, new()
        {
            SvgElement createdElement = null;
            string elementName = reader.LocalName;
            string elementNS = reader.NamespaceURI;

            //Trace.TraceInformation("Begin CreateElement: {0}", elementName);

            if (elementNS == SvgNamespaces.SvgNamespace || string.IsNullOrEmpty(elementNS))
            {
                if (elementName == "svg")
                {
                    createdElement = (fragmentIsDocument) ? new T() : new SvgFragment();
                }
                else
                {
                    if (availableElementsWithoutSvg.TryGetValue(elementName, out var validType))
                    {
                        createdElement = validType.CreateInstance();
                    }
                    else
                    {
                        createdElement = new SvgUnknownElement(elementName);
                    }
                }

                if (createdElement != null)
                {
                    SetAttributes(createdElement, reader, document);
                }
            }
            else
            {
                // All non svg element (html, ...)
                createdElement = new NonSvgElement(elementName, elementNS);
                SetAttributes(createdElement, reader, document);
            }

            //Trace.TraceInformation("End CreateElement");

            return createdElement;
        }

        private void SetAttributes(SvgElement element, XmlReader reader, SvgDocument document)
        {
            //Trace.TraceInformation("Begin SetAttributes");


            while (reader.MoveToNextAttribute())
            {
                var prefix = reader.Prefix;
                var localName = reader.LocalName;
                if (reader.ReadAttributeValue())
                {
                    if (prefix.Length == 0)
                    {
                        if (localName.Equals("xmlns"))
                        {
                            element.Namespaces[string.Empty] = reader.Value;
                            continue;
                        }
                        else if (localName.Equals("version"))
                            continue;
                    }
                    else if (prefix.Equals("xmlns"))
                    {
                        element.Namespaces[localName] = reader.Value;
                        continue;
                    }
                    if (localName.Equals("style") && !(element is NonSvgElement))
                    {
                        if (PreserveJavaScriptDomState)
                        {
                            element.SetJavaScriptDomAttributeValue(localName, reader.Value);
                            element.CustomAttributes["style"] = reader.Value;
                            TrackCompatibilityStyleStateCandidate(document, element);
                        }

                        inlineStyleAttributeParser.ApplyStyles(element, reader.Value);
                    }
                    else if (prefix.Length == 0 && localName.Equals("marker"))
                    {
                        // Upstream forwarded this through the CSS machinery, which populated
                        // SvgMarkerElement.Marker on groups and paths. Only the raw XML attribute
                        // is skipped.
                        continue;
                    }
                    else if (prefix.Length == 0 && IsStyleAttribute(localName))
                    {
                        if (PreserveJavaScriptDomState)
                        {
                            element.SetJavaScriptDomAttributeValue(localName, reader.Value);
                        }

                        // Lifted out before the style system rejects it as malformed; a
                        // placeholder keeps the element painting for the expression to attach to.
                        if (SvgExpressionAttributes.IsSupported(localName) &&
                            SvgExpressionAttributes.TryUnwrap(reader.Value, out var inlineExpression))
                        {
                            SvgExpressionAttributes.Lift(
                                element.CustomAttributes,
                                localName,
                                inlineExpression,
                                SvgElement.StyleSpecificity_PresAttribute);

                            // One resolved before the drawing is recorded is left absent instead:
                            // substitution writes the real value in before every compile, and a
                            // stand-in would only be seen where that fails -- where the element
                            // should look as though the attribute had not been written.
                            if (!SvgExpressionAttributes.IsResolvedBeforeRecording(localName))
                            {
                                element.AddStyle(
                                    localName,
                                    SvgExpressionAttributes.PlaceholderFor(localName),
                                    SvgElement.StyleSpecificity_PresAttribute);
                            }

                            continue;
                        }

                        if (ShouldIgnoreInvalidPresentationStyleAttribute(localName, reader.Value))
                        {
                            continue;
                        }

                        if (PreserveCompatibilityPresentationAttributes)
                        {
                            PreserveCompatibilityPresentationAttribute(document, element, localName, reader.Value);
                        }

                        element.AddStyle(localName, reader.Value, SvgElement.StyleSpecificity_PresAttribute);
                    }
                    else
                    {
                        var ns = prefix.Length == 0 ? string.Empty : reader.LookupNamespace(prefix);
                        if (localName.Equals("href", StringComparison.Ordinal) &&
                            (string.IsNullOrEmpty(ns) || string.Equals(ns, SvgNamespaces.XLinkNamespace, StringComparison.Ordinal)))
                        {
                            element.SetCompatibilityHrefAttributeValue(ns, reader.Value);
                        }

                        if (PreserveJavaScriptDomState)
                        {
                            element.SetJavaScriptDomAttributeValue(GetJavaScriptDomAttributeName(prefix, localName), reader.Value);
                        }

                        if (CanBindAttributeNamespace(ns))
                        {
                            // The same lift as the presentation branch above, for the attributes
                            // that are not presentation attributes and so never reach it.
                            if (ns.Length == 0 &&
                                SvgExpressionAttributes.IsSupported(localName) &&
                                SvgExpressionAttributes.TryUnwrap(reader.Value, out var liftedExpression))
                            {
                                SvgExpressionAttributes.Lift(
                                    element.CustomAttributes,
                                    localName,
                                    liftedExpression,
                                    SvgElement.StyleSpecificity_PresAttribute);

                                continue;
                            }

                            SetPropertyValue(element, ns, localName, reader.Value, document);
                        }
                        else
                        {
                            element.CustomAttributes[$"{ns}:{localName}"] = reader.Value;
                        }
                    }
                }
            }

            if (element.HasCompatibilityHrefAttributeValues() && element.Attributes.ContainsKey("href"))
            {
                element.SetCompatibilityHrefAttributeValueAfterParse(element.Attributes.GetAttribute<object>("href"));
            }

            //Trace.TraceInformation("End SetAttributes");
        }

        private static void TrackCompatibilityStyleStateCandidate(SvgDocument document, SvgElement element)
        {
            var ownerDocument = document ?? element as SvgDocument;
            ownerDocument?.TrackCompatibilityStyleStateCandidate(element);
        }

        private static void PreserveCompatibilityPresentationAttribute(SvgDocument document, SvgElement element, string name, string value)
        {
            var ownerDocument = document ?? element as SvgDocument;
            ownerDocument?.PreserveCompatibilityPresentationAttribute(element, name, value);
        }

        private static string GetJavaScriptDomAttributeName(string prefix, string localName)
        {
            if (prefix.Equals("xlink", StringComparison.OrdinalIgnoreCase) &&
                localName.Equals("href", StringComparison.OrdinalIgnoreCase))
            {
                return "href";
            }

            return prefix.Length == 0 ? localName : $"{prefix}:{localName}";
        }

        private static bool CanBindAttributeNamespace(string ns)
        {
            return string.IsNullOrEmpty(ns) ||
                   ns.Equals(SvgNamespaces.SvgNamespace, StringComparison.Ordinal) ||
                   ns.Equals(SvgNamespaces.XLinkNamespace, StringComparison.Ordinal) ||
                   ns.Equals(SvgNamespaces.XmlNamespace, StringComparison.Ordinal);
        }

        private static bool IsStyleAttribute(string name)
        {
            return SvgStyleAttributeNames.Contains(name) &&
                   !SvgStyleAttributeNames.IsCssOnlyProperty(name) &&
                   !IsSvg2GeometryAttribute(name);
        }

        private static bool IsSvg2GeometryAttribute(string name)
        {
            return name is
                "cx" or
                "cy" or
                "d" or
                "height" or
                "r" or
                "rx" or
                "ry" or
                "width" or
                "x" or
                "x1" or
                "x2" or
                "y" or
                "y1" or
                "y2";
        }

        private static bool IsOpacityAttribute(string name)
        {
            switch (name)
            {
                case "fill-opacity":
                case "flood-opacity":
                case "opacity":
                case "stop-opacity":
                case "stroke-opacity":
                    return true;
            }

            return false;
        }

        private static bool ShouldIgnoreInvalidPresentationStyleAttribute(string attributeName, string attributeValue)
        {
            return IsCaseSensitivePresentationLengthAttribute(attributeName) &&
                   HasUppercaseLengthUnitIdentifier(attributeValue.AsSpan());
        }

        private static bool IsCaseSensitivePresentationLengthAttribute(string attributeName)
        {
            return attributeName is
                "stroke-width" or
                "font-size" or
                "letter-spacing" or
                "word-spacing" or
                "baseline-shift" or
                "kerning" or
                "shape-padding" or
                "shape-margin" or
                "inline-size";
        }

        private static bool HasUppercaseLengthUnitIdentifier(ReadOnlySpan<char> value)
        {
            value = TrimWhitespace(value);
            if (value.Length == 0)
            {
                return false;
            }

            var index = 0;
            if (value[index] is '+' or '-')
            {
                index++;
            }

            var sawNumber = false;
            while (index < value.Length && char.IsDigit(value[index]))
            {
                sawNumber = true;
                index++;
            }

            if (index < value.Length && value[index] == '.')
            {
                index++;
                while (index < value.Length && char.IsDigit(value[index]))
                {
                    sawNumber = true;
                    index++;
                }
            }

            if (!sawNumber)
            {
                return false;
            }

            if (index < value.Length && (value[index] is 'e' or 'E'))
            {
                var exponentIndex = index + 1;
                if (exponentIndex < value.Length && value[exponentIndex] is '+' or '-')
                {
                    exponentIndex++;
                }

                var exponentDigitsStart = exponentIndex;
                while (exponentIndex < value.Length && char.IsDigit(value[exponentIndex]))
                {
                    exponentIndex++;
                }

                if (exponentIndex > exponentDigitsStart)
                {
                    index = exponentIndex;
                }
            }

            while (index < value.Length && char.IsWhiteSpace(value[index]))
            {
                index++;
            }

            if (index >= value.Length || value[index] == '%')
            {
                return false;
            }

            while (index < value.Length && char.IsLetter(value[index]))
            {
                if (value[index] is >= 'A' and <= 'Z')
                {
                    return true;
                }

                index++;
            }

            return false;
        }

        private static ReadOnlySpan<char> TrimWhitespace(ReadOnlySpan<char> value)
        {
#if NETSTANDARD20
            var start = 0;
            while (start < value.Length && char.IsWhiteSpace(value[start]))
            {
                start++;
            }

            var end = value.Length;
            while (end > start && char.IsWhiteSpace(value[end - 1]))
            {
                end--;
            }

            return value.Slice(start, end - start);
#else
            return value.Trim();
#endif
        }

        private static bool TryParseInvariantFloat(ReadOnlySpan<char> value, out float parsed)
        {
#if NETSTANDARD20
            return float.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
#else
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
#endif
        }

        private static bool TryHandlePercentageOpacityAttribute(string attributeName, string attributeValue, out string normalizedValue)
        {
            normalizedValue = attributeValue;

            if (!IsOpacityAttribute(attributeName) || string.IsNullOrWhiteSpace(attributeValue))
            {
                return false;
            }

            var trimmedValue = TrimWhitespace(attributeValue.AsSpan());
            if (trimmedValue.Length == 0 || trimmedValue[trimmedValue.Length - 1] != '%')
            {
                return false;
            }

            var percentageValue = TrimWhitespace(trimmedValue.Slice(0, trimmedValue.Length - 1));
            if (percentageValue.Length == 0 || !TryParseInvariantFloat(percentageValue, out var parsedPercentage))
            {
                return true;
            }

            normalizedValue = Clamp(parsedPercentage / 100f, 0f, 1f).ToString("0.########", CultureInfo.InvariantCulture);
            return false;
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }

        internal static bool SetPropertyValue(
            SvgElement element,
            string ns,
            string attributeName,
            string attributeValue,
            SvgDocument document,
            bool isStyle = false)
        {
            var isEventDescriptorAttribute = ns.Length == 0 &&
                                             attributeName.Length >= 4 &&
                                             (attributeName[0] == 'o' || attributeName[0] == 'O') &&
                                             (attributeName[1] == 'n' || attributeName[1] == 'N') &&
                                             IsEventDescriptorAttribute(element, attributeName);
            if (isEventDescriptorAttribute)
            {
                element.CustomAttributes[attributeName] = attributeValue;
            }

            if (SvgCssVariableResolver.IsCustomPropertyName(attributeName))
            {
                SvgCssVariableResolver.AddCustomProperty(
                    element,
                    attributeName,
                    attributeValue,
                    isStyle ? SvgElement.StyleSpecificity_InlineStyle : SvgElement.StyleSpecificity_PresAttribute);
                return true;
            }

            if (!isEventDescriptorAttribute &&
                !string.IsNullOrEmpty(attributeValue) &&
                SvgCssVariableResolver.TryResolveValue(element, attributeValue, out var resolvedAttributeValue))
            {
                attributeValue = resolvedAttributeValue;
            }

            if (attributeName == "mix-blend-mode" || attributeName == "isolation")
            {
                if (isStyle)
                {
                    element.CustomAttributes[ns.Length == 0 ? attributeName : $"{ns}:{attributeName}"] = attributeValue;
                }

                return true;
            }

            if (attributeName == "text-decoration" && !string.IsNullOrWhiteSpace(attributeValue))
            {
                element.CustomAttributes[SvgStyleAttributeNames.RawTextDecorationAttributeKey] = attributeValue;
            }

            if (attributeName == "stop-opacity" && string.Equals(attributeValue, "inherit", StringComparison.OrdinalIgnoreCase))
            {
                if (isStyle)
                {
                    // Keep style values staged exactly as authored so TryGetAttribute can still
                    // see the inherit keyword later.
                    return false;
                }

                // Upstream's float conversion loses the literal "inherit" before gradient
                // evaluation, so the raw attribute is kept for the inheritance chain.
                element.CustomAttributes[ns.Length == 0 ? attributeName : $"{ns}:{attributeName}"] = attributeValue;
                return true;
            }

            if (attributeName == "opacity" && attributeValue == "undefined")
            {
                attributeValue = "1";
            }

            if (TryHandlePercentageOpacityAttribute(attributeName, attributeValue, out var normalizedOpacityValue))
            {
                // Percentage opacity values are normalized before reaching the upstream float
                // converters. Malformed percentage tokens are ignored as invalid declarations.
                return true;
            }

            attributeValue = normalizedOpacityValue;
            if (isStyle && ShouldKeepComputedStyleDeclaration(attributeName, attributeValue))
            {
                return false;
            }

            var setValueResult = element.SetValue(attributeName, document, CultureInfo.InvariantCulture, attributeValue);
            if (setValueResult)
            {
                return true;
            }
            {
                if (isStyle)
                    // custom styles shall remain as style
                    return false;
                // attribute is not a svg attribute, store it in custom attributes
                element.CustomAttributes[ns.Length == 0 ? attributeName : $"{ns}:{attributeName}"] = attributeValue;
            }
            return true;
        }

        private static bool ShouldKeepComputedStyleDeclaration(string attributeName, string attributeValue)
        {
            return ShouldKeepFilterComputedStyleDeclaration(attributeName, attributeValue) ||
                   IsMultiKeywordWhiteSpaceDeclaration(attributeName, attributeValue) ||
                   (IsGeometryAttribute(attributeName) &&
                    (IsCssIdentifier(attributeValue, "auto") ||
                     IsCssIdentifier(attributeValue, "inherit") ||
                     IsCssIdentifier(attributeValue, "initial") ||
                     IsCssIdentifier(attributeValue, "unset")));
        }

        private static bool ShouldKeepFilterComputedStyleDeclaration(string attributeName, string attributeValue)
        {
            if (!attributeName.Equals("filter", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var trimmedValue = attributeValue.AsSpan().Trim();
            return !IsCssIdentifier(trimmedValue, "none") &&
                   !IsSingleCssUrlReference(trimmedValue);
        }

        private static bool IsSingleCssUrlReference(ReadOnlySpan<char> value)
        {
            if (value.Length < 6 ||
                !value.Slice(0, 4).Equals("url(".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                value[value.Length - 1] != ')')
            {
                return false;
            }

            var quote = '\0';
            for (var i = 4; i < value.Length - 1; i++)
            {
                var ch = value[i];
                if (quote != '\0')
                {
                    if (ch == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (ch == '"' || ch == '\'')
                {
                    quote = ch;
                    continue;
                }

                if (ch == ')')
                {
                    return false;
                }
            }

            if (quote != '\0')
            {
                return false;
            }

            return value.Slice(4, value.Length - 5).Trim().Length > 0;
        }

        private static bool IsMultiKeywordWhiteSpaceDeclaration(string attributeName, string attributeValue)
        {
            return attributeName.Equals("white-space", StringComparison.OrdinalIgnoreCase) &&
                   attributeValue.Split(new[] { ' ', '\t', '\r', '\n', '\f' }, StringSplitOptions.RemoveEmptyEntries).Length > 1 &&
                   SvgComputedStyleMetadata.TryParseWhiteSpaceShorthandLonghands(attributeValue, out _, out _, out _);
        }

        private static bool IsGeometryAttribute(string attributeName)
        {
            return attributeName.Equals("x", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("x1", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("y1", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("x2", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("y2", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("cx", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("cy", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("r", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("rx", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("ry", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("width", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("height", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// An element of <paramref name="elementName"/> to ask about attributes, or null for a name
        /// this factory does not know.
        /// </summary>
        /// <remarks>
        /// Which converter an attribute uses depends on the element carrying it, and descriptors are
        /// reached through an instance. Nothing is parented or kept, so one per name serves a whole
        /// document.
        /// </remarks>
        internal static SvgElement CreateProbe(string elementName)
        {
            if (elementName == "svg")
            {
                return new SvgFragment();
            }

            return availableElementsWithoutSvg.TryGetValue(elementName, out var known)
                ? known.CreateInstance()
                : null;
        }

        /// <summary>
        /// The id in this document that <paramref name="attributeValue"/> names, or null where it
        /// names none.
        /// </summary>
        /// <remarks>
        /// The value says whether it holds a reference, so no table of names is needed. A paint with
        /// a fallback — <c>fill="url(#a) none"</c> — names nothing here, since SVG uses the fallback
        /// when the reference does not resolve. The unwrapping mirrors
        /// <c>SvgElementIdManager.GetUrlString</c>, which is private to a file this repository does
        /// not own; if the two stop agreeing, a working reference is reported as missing.
        /// </remarks>
        internal static string FindReferencedId(string attributeName, string attributeValue)
        {
            if (string.IsNullOrEmpty(attributeValue))
            {
                return null;
            }

            var value = attributeValue.Trim();

            if (string.Equals(attributeName, "href", StringComparison.Ordinal))
            {
                return value.Length > 1 && value[0] == '#' ? value.Substring(1) : null;
            }

            if (!value.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var close = value.IndexOf(')', 4);

            if (close < 0)
            {
                // No closing parenthesis, so there is no id to be sure of.
                return null;
            }

            // By the closing parenthesis, not the end of the value: `url(#a) green icc-color(…)`
            // ends in one too, and reading to the last takes the whole remainder for an id.
            if (value.Substring(close + 1).Trim().Length > 0)
            {
                return null;
            }

            var inside = value.Substring(4, close - 4).Trim();

            if (inside.Length > 1
                && ((inside[0] == '"' && inside[inside.Length - 1] == '"')
                    || (inside[0] == '\'' && inside[inside.Length - 1] == '\'')))
            {
                inside = inside.Substring(1, inside.Length - 2).Trim();
            }

            // Only a same-document reference. Anything else is a file this pass cannot open.
            return inside.Length > 1 && inside[0] == '#' ? inside.Substring(1) : null;
        }

        /// <summary>
        /// Why an element of <paramref name="elementName"/> draws nothing, or null where this parser
        /// knows the name.
        /// </summary>
        /// <remarks>
        /// An unrecognised name becomes an <see cref="SvgUnknownElement"/>, drawn by nothing. A
        /// warning rather than a refusal, and worded <em>this renderer</em> rather than <em>SVG</em>,
        /// because the table cannot tell a typo from real SVG that is not implemented here.
        /// <c>&lt;style&gt;</c> is the one name that misses the table and is still used.
        /// </remarks>
        internal static string FindElementFault(string elementName)
        {
            if (string.IsNullOrEmpty(elementName)
                || elementName == "style"
                || elementName == "svg"
                || availableElementsWithoutSvg.ContainsKey(elementName))
            {
                return null;
            }

            return $"'{elementName}' is not an element this renderer knows, so it and everything inside it draw nothing.";
        }

        /// <summary>
        /// Why the converter for <paramref name="attributeName"/> refuses
        /// <paramref name="attributeValue"/>, or null when it does not refuse it.
        /// </summary>
        /// <remarks>
        /// The one question <see cref="SetPropertyValue"/> cannot be asked: <c>SvgElement.SetValue</c>
        /// catches whatever the converter threw, warns to <see cref="Trace"/> and returns
        /// <c>true</c>, so the property keeps its default and nothing above can tell. This asks the
        /// converter directly instead, and its refusal is the message. It cannot report a converter
        /// that refuses nothing — a path builder reads <c>d="QQQ"</c> as happily as a real path.
        /// </remarks>
        internal static string FindAttributeFault(
            SvgElement element,
            string ns,
            string attributeName,
            string attributeValue,
            SvgDocument document)
        {
            if (element is null || attributeName is null || attributeValue is null)
            {
                return null;
            }

            // Never bound at all, so never converted.
            if (!CanBindAttributeNamespace(ns) ||
                (ns.Length == 0 && (attributeName == "xmlns" || attributeName == "version" || attributeName == "marker")) ||
                attributeName.StartsWith("xmlns", StringComparison.Ordinal))
            {
                return null;
            }

            if (SvgExpressionAttributes.TryUnwrap(attributeValue, out _))
            {
                // Expression code, not a value: unlifted braces make every converter refuse, which
                // is true but reports a malformed number rather than an attribute taking no
                // expression.
                return SvgExpressionAttributes.WhyUnsupported(attributeName);
            }

            if (IsEventDescriptorAttribute(element, attributeName) ||
                SvgCssVariableResolver.IsCustomPropertyName(attributeName))
            {
                return null;
            }

            // A custom property is substituted before the converter sees anything, and what it holds
            // is a question about the cascade rather than about this attribute.
            if (attributeValue.IndexOf("var(", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return null;
            }

            // Kept as custom attributes, ahead of any converter.
            if (attributeName == "mix-blend-mode" || attributeName == "isolation" || attributeName == "text-decoration")
            {
                return null;
            }

            if (attributeName == "stop-opacity" && IsCssIdentifier(attributeValue, "inherit"))
            {
                return null;
            }

            if (attributeName == "opacity" && attributeValue == "undefined")
            {
                return null;
            }

            // Rewritten before conversion; a percentage this refuses is dropped rather than reported.
            if (TryHandlePercentageOpacityAttribute(attributeName, attributeValue, out var normalized))
            {
                return null;
            }

            attributeValue = normalized;

            if (ShouldKeepComputedStyleDeclaration(attributeName, attributeValue) ||
                ShouldIgnoreInvalidPresentationStyleAttribute(attributeName, attributeValue))
            {
                return null;
            }

            // Staged as authored wherever the cascade still has to see the word.
            if (IsCssIdentifier(attributeValue, "inherit") ||
                IsCssIdentifier(attributeValue, "initial") ||
                IsCssIdentifier(attributeValue, "unset"))
            {
                return null;
            }

            var descriptor = FindProperty(element, attributeName);

            if (descriptor is null || descriptor.Converter is null)
            {
                // Not an attribute this element converts. Whether that makes it a mistake is a
                // question about names, which this does not answer.
                return null;
            }

            try
            {
                descriptor.Converter.ConvertFrom(document, CultureInfo.InvariantCulture, attributeValue);
            }
            catch (Exception failure)
            {
                return Explain(attributeName, attributeValue, (failure.InnerException ?? failure).Message);
            }

            return null;
        }

        /// <summary>
        /// Why the converter for one inline-style declaration refuses its value, or null.
        /// </summary>
        /// <remarks>
        /// The same question as <see cref="FindAttributeFault"/>, where most drawings put their
        /// paint: a declaration reaches the same converter later, so <c>fill="#gggggg"</c> and
        /// <c>style="fill:#gggggg"</c> fail identically. <c>!important</c> belongs to CSS and comes
        /// off first, or <c>fill: red !important</c> would be refused.
        /// </remarks>
        internal static string FindStyleFault(
            SvgElement element,
            string propertyName,
            string propertyValue,
            SvgDocument document)
        {
            if (propertyValue is null)
            {
                return null;
            }

            var value = propertyValue;

            SvgCssDeclarationPriority.NormalizePriority(ref value, SvgElement.StyleSpecificity_InlineStyle);

            return FindAttributeFault(element, string.Empty, propertyName, value, document);
        }

        /// <summary>Says which attribute a converter's refusal was about.</summary>
        /// <remarks>
        /// The verdict is kept word for word, but the converter names neither the attribute nor the
        /// value: <c>The input string '' was not in a correct format</c> is all it says about
        /// <c>width="abc"</c>. Naming the two is the reader's half.
        /// </remarks>
        private static string Explain(string attributeName, string attributeValue, string message)
        {
            var detail = string.IsNullOrWhiteSpace(message) ? string.Empty : " " + message.Trim();

            return $"'{attributeName}' cannot be set from '{attributeValue}'.{detail}";
        }

        /// <summary>The descriptor <c>SvgElement.SetValue</c> would pick for an attribute.</summary>
        /// <remarks>
        /// <para>
        /// The generated <c>GetProperties</c> yields a type's own descriptors before its base's and
        /// drops any the derived type shadows, so the first match is the one that would be used.
        /// </para>
        /// <para>
        /// Built once per type because it is walked once per attribute, and it is a chain of
        /// iterators over every property an element inherits -- around a hundred of them on anything
        /// that can be painted. Searching it for each attribute in turn made checking a 57KB drawing
        /// cost 34ms; a dictionary makes the same drawing 3.4ms, which is what splitting it into
        /// coloured pieces costs.
        /// </para>
        /// </remarks>
        private static ISvgPropertyDescriptor FindProperty(SvgElement element, string attributeName)
        {
            var properties = s_propertiesByType.GetOrAdd(element.GetType(), _ => CreatePropertyMap(element));

            return properties.TryGetValue(attributeName, out var descriptor) ? descriptor : null;
        }

        private static Dictionary<string, ISvgPropertyDescriptor> CreatePropertyMap(SvgElement element)
        {
            var properties = new Dictionary<string, ISvgPropertyDescriptor>(StringComparer.Ordinal);

            foreach (var property in element.GetProperties())
            {
                if (property.DescriptorType == DescriptorType.Property &&
                    !properties.ContainsKey(property.AttributeName))
                {
                    properties[property.AttributeName] = property;
                }
            }

            return properties;
        }

        private static bool IsCssIdentifier(string value, string identifier)
        {
            return value.AsSpan().Trim().Equals(identifier.AsSpan(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCssIdentifier(ReadOnlySpan<char> value, string identifier)
        {
            return value.Trim().Equals(identifier.AsSpan(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsEventDescriptorAttribute(SvgElement element, string attributeName)
        {
            if (!IsKnownScriptAttributeName(attributeName))
            {
                return false;
            }

            var eventAttributeNames = s_eventDescriptorAttributeNamesByType.GetOrAdd(
                element.GetType(),
                _ => CreateEventDescriptorAttributeNameSet(element));

            return eventAttributeNames.Contains(attributeName);
        }

        private static bool IsKnownScriptAttributeName(string attributeName)
        {
            if (attributeName.Length < 4 ||
                (attributeName[0] != 'o' && attributeName[0] != 'O') ||
                (attributeName[1] != 'n' && attributeName[1] != 'N'))
            {
                return false;
            }

            switch (attributeName)
            {
                case "onabort":
                case "onactivate":
                case "onbegin":
                case "onchange":
                case "onclick":
                case "onend":
                case "onerror":
                case "onfocusin":
                case "onfocusout":
                case "onload":
                case "onmousedown":
                case "onmousemove":
                case "onmouseout":
                case "onmouseover":
                case "onmouseup":
                case "onmousescroll":
                case "onrepeat":
                case "onresize":
                case "onscroll":
                case "onunload":
                case "onzoom":
                    return true;
            }

            return attributeName.Equals("onabort", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("onactivate", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("onbegin", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("onchange", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("onclick", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("onend", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("onerror", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("onfocusin", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("onfocusout", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("onload", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("onmousedown", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("onmousemove", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("onmouseout", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("onmouseover", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("onmouseup", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("onmousescroll", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("onrepeat", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("onresize", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("onscroll", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("onunload", StringComparison.OrdinalIgnoreCase) ||
                   attributeName.Equals("onzoom", StringComparison.OrdinalIgnoreCase);
        }

        private static HashSet<string> CreateEventDescriptorAttributeNameSet(SvgElement element)
        {
            var eventAttributeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.GetProperties())
            {
                if (property.DescriptorType == DescriptorType.Event &&
                    !string.IsNullOrEmpty(property.AttributeName))
                {
                    eventAttributeNames.Add(property.AttributeName);
                }
            }

            return eventAttributeNames;
        }

        /// <summary>
        /// Contains information about a type inheriting from <see cref="SvgElement"/>.
        /// </summary>
        [DebuggerDisplay("{ElementName}, {ElementType}")]
        internal sealed class ElementInfo
        {
            /// <summary>
            /// Gets the SVG name of the <see cref="SvgElement"/>.
            /// </summary>
            public string ElementName { get; set; }
            /// <summary>
            /// Gets the <see cref="Type"/> of the <see cref="SvgElement"/> subclass.
            /// </summary>
            public Type ElementType { get; set; }
            /// <summary>
            /// Creates a new instance based on <see cref="ElementType"/> type.
            /// </summary>
            public Func<SvgElement> CreateInstance { get; set; }
            /// <summary>
            /// Initializes a new instance of the <see cref="ElementInfo"/> struct.
            /// </summary>
            /// <param name="elementName">Name of the element.</param>
            /// <param name="elementType">Type of the element.</param>
            public ElementInfo(string elementName, Type elementType)
            {
                this.ElementName = elementName;
                this.ElementType = elementType;
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="ElementInfo"/> class.
            /// </summary>
            public ElementInfo()
            {
            }
        }
    }
}
