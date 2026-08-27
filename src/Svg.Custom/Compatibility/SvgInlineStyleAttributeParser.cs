using System;
using System.Collections.Generic;
using System.Text;
using ExCSS;

namespace Svg;

/// <summary>
/// Parses and applies inline <c>style="..."</c> attribute declarations independently from
/// element creation so the XML factory remains focused on attribute dispatch.
/// </summary>
internal sealed class SvgInlineStyleAttributeParser
{
    private readonly StylesheetParser stylesheetParser = new(true, true, tolerateInvalidValues: true);

    public void ApplyStyles(SvgElement element, string styleText)
    {
        if (TryApplyInlineDeclarations(element, styleText))
        {
            return;
        }

        var inlineSheet = stylesheetParser.Parse("#a{" + styleText + "}");
        foreach (var rule in inlineSheet.StyleRules)
        {
            foreach (var declaration in rule.Style)
            {
                if (string.IsNullOrWhiteSpace(declaration.Original) &&
                    !SvgCssVariableResolver.IsCustomPropertyName(declaration.Name))
                {
                    continue;
                }

                ApplyDeclaration(element, declaration.Name, declaration.Original, SvgElement.StyleSpecificity_InlineStyle, declaration.IsImportant);
            }
        }
    }

    /// <summary>
    /// Every declaration in <paramref name="styleText"/>, each with the span of its value.
    /// </summary>
    /// <remarks>
    /// The same scanner <see cref="TryApplyInlineDeclarations"/> walks, because a declaration
    /// boundary is not a semicolon-split — one inside quotes, <c>url(…)</c> or a comment is not a
    /// separator, and a second answer would mark the wrong piece of somebody's file. Empty wherever
    /// that scanner gives up, since the CSS parser its caller falls back to offers no positions.
    /// </remarks>
    /// <remarks>
    /// A tuple rather than a type of its own, because this assembly still targets netstandard2.0,
    /// where a positional record's init accessors have no <c>IsExternalInit</c> to compile against.
    /// </remarks>
    internal static List<(string Name, string Value, int ValueStart, int ValueLength)> Split(string styleText)
    {
        var found = new List<(string Name, string Value, int ValueStart, int ValueLength)>();

        if (string.IsNullOrWhiteSpace(styleText))
        {
            return found;
        }

        var index = 0;

        while (true)
        {
            if (!SkipIgnorableStyleContent(styleText, ref index))
            {
                found.Clear();
                return found;
            }

            if (index >= styleText.Length)
            {
                return found;
            }

            var declarationStart = index;

            if (!TryFindInlineDeclarationEnd(styleText, ref index, out var declarationEnd) ||
                !TryFindInlineDeclarationSeparator(styleText, declarationStart, declarationEnd, out var separatorIndex))
            {
                found.Clear();
                return found;
            }

            var name = NormalizeInlineDeclarationName(styleText, declarationStart, separatorIndex - declarationStart);

            if (string.IsNullOrEmpty(name))
            {
                found.Clear();
                return found;
            }

            var value = NormalizeInlineDeclarationSegment(styleText, separatorIndex + 1, declarationEnd - separatorIndex - 1);

            // The span is trimmed to what was written, so a mark lands on the value rather than on
            // the gap after the colon.
            var valueStart = separatorIndex + 1;

            while (valueStart < declarationEnd && char.IsWhiteSpace(styleText[valueStart]))
            {
                valueStart++;
            }

            var valueEnd = declarationEnd;

            while (valueEnd > valueStart && char.IsWhiteSpace(styleText[valueEnd - 1]))
            {
                valueEnd--;
            }

            found.Add((name, value, valueStart, valueEnd - valueStart));
        }
    }

    private static bool TryApplyInlineDeclarations(SvgElement element, string styleText)
    {
        if (string.IsNullOrWhiteSpace(styleText))
        {
            return true;
        }

        var index = 0;
        while (true)
        {
            if (!SkipIgnorableStyleContent(styleText, ref index))
            {
                return false;
            }

            if (index >= styleText.Length)
            {
                return true;
            }

            if (!TryReadInlineDeclaration(styleText, ref index, out var name, out var value))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(value) &&
                !SvgCssVariableResolver.IsCustomPropertyName(name))
            {
                continue;
            }

            ApplyDeclaration(element, name, value, SvgElement.StyleSpecificity_InlineStyle);
        }
    }

    private static void ApplyDeclaration(SvgElement element, string name, string value, int specificity, bool important = false)
    {
        if (SvgCssVariableResolver.IsCustomPropertyName(name))
        {
            SvgCssVariableResolver.AddCustomProperty(element, name, value, specificity);
            return;
        }

        var effectiveSpecificity = SvgCssDeclarationPriority.NormalizePriority(ref value, specificity, important);

        if (SvgCssPaintDeclarationValidator.ShouldIgnoreInvalidPaintDeclaration(element, name, value))
        {
            return;
        }

        if (SvgComputedStyleMetadata.ShouldIgnoreInvalidDeclaration(name, value))
        {
            return;
        }

        element.AddCompatibilityStyle(name, value, effectiveSpecificity);
    }

    private static bool TryReadInlineDeclaration(string styleText, ref int index, out string name, out string value)
    {
        name = string.Empty;
        value = string.Empty;
        var declarationStart = index;
        if (!TryFindInlineDeclarationEnd(styleText, ref index, out var declarationEnd))
        {
            return false;
        }

        if (!TryFindInlineDeclarationSeparator(styleText, declarationStart, declarationEnd, out var separatorIndex))
        {
            return false;
        }

        name = NormalizeInlineDeclarationName(styleText, declarationStart, separatorIndex - declarationStart);
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        value = NormalizeInlineDeclarationSegment(styleText, separatorIndex + 1, declarationEnd - separatorIndex - 1);
        return true;
    }

    private static bool TryFindInlineDeclarationEnd(string styleText, ref int index, out int declarationEnd)
    {
        var quote = '\0';
        var escape = false;
        var parentheses = 0;
        var current = index;

        while (current < styleText.Length)
        {
            var character = styleText[current];
            if (quote != '\0')
            {
                if (escape)
                {
                    escape = false;
                }
                else if (character == '\\')
                {
                    escape = true;
                }
                else if (character == quote)
                {
                    quote = '\0';
                }

                current++;
                continue;
            }

            if (character == '/' && current + 1 < styleText.Length && styleText[current + 1] == '*')
            {
                current += 2;
                if (!TrySkipStyleComment(styleText, ref current))
                {
                    declarationEnd = 0;
                    return false;
                }

                continue;
            }

            switch (character)
            {
                case '\'':
                case '"':
                    quote = character;
                    break;
                case '(':
                    parentheses++;
                    break;
                case ')':
                    if (parentheses > 0)
                    {
                        parentheses--;
                    }
                    break;
                case ';' when parentheses == 0:
                    declarationEnd = current;
                    current++;
                    index = current;
                    return true;
            }

            current++;
        }

        declarationEnd = current;
        index = current;
        return quote == '\0' && parentheses == 0;
    }

    private static bool TryFindInlineDeclarationSeparator(string styleText, int startIndex, int endIndex, out int separatorIndex)
    {
        var quote = '\0';
        var escape = false;
        var parentheses = 0;

        for (var i = startIndex; i < endIndex; i++)
        {
            var character = styleText[i];
            if (quote != '\0')
            {
                if (escape)
                {
                    escape = false;
                }
                else if (character == '\\')
                {
                    escape = true;
                }
                else if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character == '/' && i + 1 < endIndex && styleText[i + 1] == '*')
            {
                i += 2;
                if (!TrySkipStyleComment(styleText, ref i, endIndex))
                {
                    separatorIndex = -1;
                    return false;
                }

                i--;
                continue;
            }

            switch (character)
            {
                case '\'':
                case '"':
                    quote = character;
                    break;
                case '(':
                    parentheses++;
                    break;
                case ')':
                    if (parentheses > 0)
                    {
                        parentheses--;
                    }
                    break;
                case ':' when parentheses == 0:
                    separatorIndex = i;
                    return true;
            }
        }

        separatorIndex = -1;
        return false;
    }

    private static bool SkipIgnorableStyleContent(string styleText, ref int index)
    {
        while (index < styleText.Length)
        {
            var character = styleText[index];
            if (char.IsWhiteSpace(character) || character == ';')
            {
                index++;
                continue;
            }

            if (character == '/' && index + 1 < styleText.Length && styleText[index + 1] == '*')
            {
                index += 2;
                if (!TrySkipStyleComment(styleText, ref index))
                {
                    return false;
                }

                continue;
            }

            break;
        }

        return true;
    }

    private static bool TrySkipStyleComment(string styleText, ref int index)
    {
        return TrySkipStyleComment(styleText, ref index, styleText.Length);
    }

    private static bool TrySkipStyleComment(string styleText, ref int index, int endIndex)
    {
        while (index + 1 < endIndex)
        {
            if (styleText[index] == '*' && styleText[index + 1] == '/')
            {
                index += 2;
                return true;
            }

            index++;
        }

        return false;
    }

    private static string NormalizeInlineDeclarationSegment(string styleText, int startIndex, int length)
    {
        if (!TryTrimSegment(styleText, startIndex, length, out var trimmedStart, out var trimmedLength))
        {
            return string.Empty;
        }

        StringBuilder builder = null;
        var quote = '\0';
        var escape = false;
        var segmentEnd = trimmedStart + trimmedLength;

        for (var i = trimmedStart; i < segmentEnd; i++)
        {
            var character = styleText[i];
            if (quote != '\0')
            {
                builder?.Append(character);
                if (escape)
                {
                    escape = false;
                }
                else if (character == '\\')
                {
                    escape = true;
                }
                else if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character == '/' && i + 1 < segmentEnd && styleText[i + 1] == '*')
            {
                builder ??= new StringBuilder(trimmedLength);
                if (builder.Length == 0 && i > trimmedStart)
                {
                    builder.Append(styleText, trimmedStart, i - trimmedStart);
                }

                i += 2;
                if (!TrySkipStyleComment(styleText, ref i, segmentEnd))
                {
                    return styleText.Substring(trimmedStart, trimmedLength);
                }

                i--;
                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }

            builder?.Append(character);
        }

        return builder is null
            ? styleText.Substring(trimmedStart, trimmedLength)
            : builder.ToString().Trim();
    }

    private static string NormalizeInlineDeclarationName(string styleText, int startIndex, int length)
    {
        var normalized = NormalizeInlineDeclarationSegment(styleText, startIndex, length);
        if (string.IsNullOrEmpty(normalized))
        {
            return normalized;
        }

        if (SvgCssVariableResolver.IsCustomPropertyName(normalized))
        {
            return normalized;
        }

        StringBuilder builder = null;
        for (var i = 0; i < normalized.Length; i++)
        {
            var character = normalized[i];
            if (character is < 'A' or > 'Z')
            {
                if (builder is not null)
                {
                    builder.Append(character);
                }

                continue;
            }

            builder ??= new StringBuilder(normalized.Length).Append(normalized, 0, i);
            builder.Append((char)(character + ('a' - 'A')));
        }

        return builder?.ToString() ?? normalized;
    }

    private static bool TryTrimSegment(string styleText, int startIndex, int length, out int trimmedStart, out int trimmedLength)
    {
        if (length <= 0)
        {
            trimmedStart = 0;
            trimmedLength = 0;
            return false;
        }

        var endIndex = startIndex + length - 1;
        while (startIndex <= endIndex && char.IsWhiteSpace(styleText[startIndex]))
        {
            startIndex++;
        }

        while (endIndex >= startIndex && char.IsWhiteSpace(styleText[endIndex]))
        {
            endIndex--;
        }

        if (startIndex > endIndex)
        {
            trimmedStart = 0;
            trimmedLength = 0;
            return false;
        }

        trimmedStart = startIndex;
        trimmedLength = endIndex - startIndex + 1;
        return true;
    }
}
