// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using Svg.Expressions;

namespace Svg.Highlighting;

/// <summary>How much a diagnostic matters.</summary>
public enum SvgSourceSeverity
{
    /// <summary>The document will not compile or render as written.</summary>
    Error,

    /// <summary>Worth knowing, but the document still works.</summary>
    Warning,
}

/// <summary>Something wrong with a document, and where.</summary>
/// <remarks>
/// A range into the same text a <see cref="SvgSourceToken"/> points at, so a view that already has
/// the tokens can mark the offending one without being told anything else.
/// </remarks>
public readonly record struct SvgSourceDiagnostic(
    int Start,
    int Length,
    SvgSourceSeverity Severity,
    string Message);

/// <summary>
/// Finds what is wrong with the expressions in a document.
/// </summary>
/// <remarks>
/// <para>
/// Through the language's own checker, so the wording and the rules are the compiler's rather than
/// an imitation of them: an unknown name, a function called with the wrong number of arguments, a
/// number where a colour belongs. Nothing here decides what is an error.
/// </para>
/// <para>
/// Splitting a document is context-free — a <c>{{ … }}</c> colours the same wherever it is — but
/// checking one is not: what a name may refer to depends on every declaration in the file, and on
/// where the expression is written. So this is a document-level pass, separate from
/// <see cref="SvgSourceHighlighter"/>, and it starts by reading the <c>&lt;e:code&gt;</c> block.
/// </para>
/// <para>
/// What it does not know is what an attribute expects. <c>opacity="{{ tint }}"</c> is a well-formed
/// colour expression written where a number belongs, and saying so needs the table of which SVG
/// attribute takes which type — which lives in the scene compiler, a dependency this library has no
/// other reason to carry. So an expression is checked on its own terms and not against its use.
/// </para>
/// <para>
/// What the declaration block itself gets wrong — a range on a colour, a default in the wrong order
/// — is reported by <see cref="SvgExpressionDeclarations"/> when it is read, and is not repeated
/// here. Those want to be placed against the declaration they came from rather than against an
/// expression, which is a separate piece of work.
/// </para>
/// </remarks>
public static class SvgSourceDiagnostics
{
    /// <summary>Reports what is wrong with the expressions in <paramref name="source"/>.</summary>
    /// <remarks>
    /// Empty rather than throwing for a document that cannot be read at all: a source view exists to
    /// show a file, and one that vanished because its own error reporting failed would be absurd.
    /// </remarks>
    public static IReadOnlyList<SvgSourceDiagnostic> Analyse(string? source)
    {
        var found = new List<SvgSourceDiagnostic>();

        if (string.IsNullOrEmpty(source))
        {
            return found;
        }

        var sites = new List<SvgSourceSite>();
        var tokens = SvgSourceHighlighter.Tokenize(source, sites);

        if (sites.Count == 0)
        {
            return found;
        }

        SvgExpressionDeclarations declarations;

        try
        {
            declarations = SvgExpressionDeclarations.Parse(source);
        }
        catch (ExprException)
        {
            // The block that says what is declared is itself unreadable, so every name in the
            // document would look undeclared. Saying so a hundred times would bury the one error
            // that matters, which the declaration reader has already reported.
            return found;
        }

        // Held by reference on purpose: each let is added as it checks, which is what puts the ones
        // declared earlier in scope for the ones after them and leaves a let out of its own scope.
        var symbols = declarations.CreateSymbolTable();
        var checker = new ExprChecker(symbols);

        // A default may use literals, constants and functions but not what the document declares.
        var isolated = new ExprChecker(new Dictionary<string, ExprType>(StringComparer.Ordinal));

        var lets = 0;

        foreach (var site in sites)
        {
            var text = source!.Substring(site.Start, site.Length);
            var scope = site.Kind == SvgSourceSiteKind.Declaration ? isolated : checker;

            try
            {
                var typed = scope.Check(text);

                if (site.Kind == SvgSourceSiteKind.Let && lets < declarations.Lets.Count)
                {
                    symbols[declarations.Lets[lets].Name] = typed.Type;
                }
            }
            catch (ExprException failure)
            {
                found.Add(Describe(failure, site, tokens, source!));
            }
            finally
            {
                if (site.Kind == SvgSourceSiteKind.Let)
                {
                    lets++;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Turns a refusal into something a view can underline.
    /// </summary>
    /// <remarks>
    /// The language reports a position but no extent, so the span comes from the token that position
    /// falls in: underlining a name is legible where a caret under one character is not. Where the
    /// position lands in the uncoloured remainder — everything past a point the lexer could not read
    /// — there is no piece to mark, so the run of non-space characters starting there is used
    /// instead. Marking the whole remainder would underline the rest of the line to say one symbol
    /// is wrong.
    /// </remarks>
    private static SvgSourceDiagnostic Describe(
        ExprException failure,
        SvgSourceSite site,
        IReadOnlyList<SvgSourceToken> tokens,
        string source)
    {
        var at = site.Start + Math.Max(0, Math.Min(failure.Position, site.Length));

        foreach (var token in tokens)
        {
            if (token.Kind is SvgSourceTokenKind.Expression or SvgSourceTokenKind.Text)
            {
                continue;
            }

            if (token.Start <= at && at < token.Start + token.Length && token.Length > 0)
            {
                return new SvgSourceDiagnostic(token.Start, token.Length, SvgSourceSeverity.Error, failure.Message);
            }
        }

        var start = Math.Max(site.Start, Math.Min(at, Math.Max(0, source.Length - 1)));
        var end = start;
        var stop = site.Start + site.Length;

        while (end < stop && !char.IsWhiteSpace(source[end]))
        {
            end++;
        }

        return new SvgSourceDiagnostic(
            start,
            Math.Max(1, end - start),
            SvgSourceSeverity.Error,
            failure.Message);
    }
}
