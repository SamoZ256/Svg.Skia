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
/// What the declaration block itself gets wrong — a name that is not a name, a range on a colour, a
/// min above its max — is reported here too, at the attribute it is about. The rules stay in
/// <see cref="SvgExpressionDeclarations.Builder"/> where both readers of a block reach them; each
/// says which <see cref="SvgDeclarationPart"/> it means, and turning that into a place in the
/// document is the reader's half. Nothing here restates a rule or reads a message to decide what it
/// was about.
/// </para>
/// <para>
/// A document whose declarations are wrong reports those and nothing else. The symbol table is
/// missing whatever the bad declaration would have put in it, so every use of that name would look
/// undeclared — a hundred of those bury the few that are real.
/// </para>
/// </remarks>
public static class SvgSourceDiagnostics
{
    /// <summary>Reports what is wrong with the declarations and expressions in <paramref name="source"/>.</summary>
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

        // Every declaration is read rather than only the first, so a block with three mistakes in it
        // shows three. A document that declares nothing costs one search of the text for the
        // extension's namespace.
        var declarations = SvgExpressionDeclarations.Parse(source, out var declared);

        if (declared.Count > 0)
        {
            foreach (var diagnostic in declared)
            {
                found.Add(Mark(diagnostic.Position, source!.Length, diagnostic.Message, tokens, source));
            }

            return found;
        }

        if (sites.Count == 0)
        {
            return found;
        }

        // Held by reference on purpose: each let is added as it checks, which is what puts the ones
        // declared earlier in scope for the ones after them and leaves a let out of its own scope.
        var symbols = declarations.CreateSymbolTable();
        var checker = new ExprChecker(symbols);

        // A default may use literals, constants and functions but not what the document declares.
        var isolated = new ExprChecker(new Dictionary<string, ExprType>(StringComparer.Ordinal));

        // Which parameters have already been reported on, so that a min the checker rejected is not
        // also evaluated below and rejected a second time for the same reason.
        var reported = new HashSet<string>(StringComparer.Ordinal);

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

                if (site.Owner is { } owner)
                {
                    reported.Add(owner);
                }
            }
            finally
            {
                if (site.Kind == SvgSourceSiteKind.Let)
                {
                    lets++;
                }
            }
        }

        Resolve(found, declarations, reported, sites, tokens, source!);

        // Document order, because two passes produced these and neither knows about the other: what
        // only numbers can settle is found after every expression has been checked, which would
        // otherwise report a bound in the declarations below a mistake in the drawing.
        found.Sort(static (left, right) => left.Start.CompareTo(right.Start));

        return found;
    }

    /// <summary>
    /// Reports what only running a declaration's own expressions can find.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A min above its max, a step that is not positive, a default that type-checks and still will
    /// not produce a value: these need numbers rather than types, so they cannot be settled while a
    /// document is read — <c>SKSvg.Load</c> is documented never to evaluate. Analysing is not
    /// reading, so they are settled here.
    /// </para>
    /// <para>
    /// The language reports which parameter and which part of it; where that was written is the
    /// splitter's answer, since it recorded the attribute each piece of declared code came from.
    /// </para>
    /// </remarks>
    private static void Resolve(
        List<SvgSourceDiagnostic> found,
        SvgExpressionDeclarations declarations,
        HashSet<string> reported,
        IReadOnlyList<SvgSourceSite> sites,
        IReadOnlyList<SvgSourceToken> tokens,
        string source)
    {
        foreach (var parameter in declarations.Parameters)
        {
            if (reported.Contains(parameter.Name))
            {
                continue;
            }

            try
            {
                parameter.ResolveRange();
            }
            catch (Exception failure) when (failure is ExprException or ArgumentException)
            {
                Place(found, failure, parameter.Name, SvgDeclarationPart.Min, sites, tokens, source);
            }

            if (parameter.DefaultExpression is not { } fallback)
            {
                continue;
            }

            try
            {
                ExprEvaluator.Isolated.EvaluateTo(fallback, parameter.Type, $"The default for '{parameter.Name}'");
            }
            catch (Exception failure) when (failure is ExprException or ArgumentException)
            {
                Place(found, failure, parameter.Name, SvgDeclarationPart.Default, sites, tokens, source);
            }
        }
    }

    /// <summary>Places a rule about a named parameter at the attribute of it the rule is about.</summary>
    /// <remarks>
    /// <c>ArgumentException</c> as well as the language's own: <c>clamp</c> with a reversed range
    /// throws one, and a pass whose job is to say what is wrong with a file must not be the thing
    /// that takes the file off the screen. Such a refusal names no part, so the caller says which
    /// expression it was running.
    /// </remarks>
    private static void Place(
        List<SvgSourceDiagnostic> found,
        Exception failure,
        string owner,
        SvgDeclarationPart fallback,
        IReadOnlyList<SvgSourceSite> sites,
        IReadOnlyList<SvgSourceToken> tokens,
        string source)
    {
        var part = failure is ExprException { Part: { } named } ? named : fallback;

        if (Attribute(part) is not { } attribute)
        {
            // A part that is not written in an attribute of its own, which nothing running a
            // declaration's expressions can produce — every one of those is a bound or a default.
            return;
        }

        var within = failure is ExprException expression ? expression.Position : 0;

        foreach (var site in sites)
        {
            if (site.Kind != SvgSourceSiteKind.Declaration
                || !string.Equals(site.Owner, owner, StringComparison.Ordinal)
                || !string.Equals(site.Attribute, attribute, StringComparison.Ordinal))
            {
                continue;
            }

            var at = site.Start + Math.Max(0, Math.Min(within, site.Length));

            found.Add(Mark(at, site.Start + site.Length, failure.Message, tokens, source));
            return;
        }

        // Nothing to point at, which a document the splitter and the XML reader disagree about could
        // produce. Marking the wrong place says something false; a host still has the message.
    }

    /// <summary>The attribute a part is written in, for the parts that are one.</summary>
    private static string? Attribute(SvgDeclarationPart part) => part switch
    {
        SvgDeclarationPart.Default => "default",
        SvgDeclarationPart.Min => "min",
        SvgDeclarationPart.Max => "max",
        SvgDeclarationPart.Step => "step",
        _ => null,
    };

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
        => Mark(
            site.Start + Math.Max(0, Math.Min(failure.Position, site.Length)),
            site.Start + site.Length,
            failure.Message,
            tokens,
            source);

    /// <summary>Marks the piece of the document at <paramref name="at"/>.</summary>
    private static SvgSourceDiagnostic Mark(
        int at,
        int stop,
        string message,
        IReadOnlyList<SvgSourceToken> tokens,
        string source)
    {
        // A mark never begins on a space. A rule about an expression as a whole reports position
        // zero, which in `default=" 1 "` is the gap before the value, and a one-space underline is
        // a mark a reader cannot see. The first thing actually written is what it is about.
        while (at < stop && at < source.Length && char.IsWhiteSpace(source[at]))
        {
            at++;
        }

        foreach (var token in tokens)
        {
            if (token.Kind is SvgSourceTokenKind.Expression or SvgSourceTokenKind.Text)
            {
                continue;
            }

            if (token.Start <= at && at < token.Start + token.Length && token.Length > 0)
            {
                return new SvgSourceDiagnostic(token.Start, token.Length, SvgSourceSeverity.Error, message);
            }
        }

        var start = Math.Max(0, Math.Min(at, Math.Max(0, source.Length - 1)));
        var end = start;

        while (end < stop && end < source.Length && !char.IsWhiteSpace(source[end]))
        {
            end++;
        }

        return new SvgSourceDiagnostic(start, Math.Max(1, end - start), SvgSourceSeverity.Error, message);
    }
}
