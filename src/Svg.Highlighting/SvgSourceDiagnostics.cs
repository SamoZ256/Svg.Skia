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
/// Finds what is wrong with a document.
/// </summary>
/// <remarks>
/// <para>
/// Through the language's own checker, so the wording and the rules are the compiler's rather than
/// an imitation of them: an unknown name, a function called with the wrong number of arguments, a
/// number where a colour belongs. Nothing here decides what is an error.
/// </para>
/// <para>
/// The SVG around the expressions is checked the same way and on the same terms, by
/// <see cref="SvgSourceAttributes"/> — through the converter each attribute actually uses, so a
/// value that will not convert is the parser's verdict rather than this library's.
/// </para>
/// <para>
/// Splitting a document is context-free — a <c>{{ … }}</c> colours the same wherever it is — but
/// checking one is not: what a name may refer to depends on every declaration in the file, and on
/// where the expression is written. So this is a document-level pass, separate from
/// <see cref="SvgSourceHighlighter"/>, and it starts by reading the <c>&lt;e:code&gt;</c> block.
/// </para>
/// <para>
/// An expression is checked against its use as well as on its own terms:
/// <c>opacity="{{ tint }}"</c> is well-formed and still wrong, because that attribute scales an
/// alpha and a colour is not a number. What each of the five attributes wants is
/// <see cref="SvgExpressionAttributes.TypeFor"/>, and the refusal is
/// <see cref="ExprChecker.CheckAs"/>'s — the same one the emitter and the renderer already raise
/// while reading the document, so all three say the same sentence and this only says it sooner.
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
/// A document that is not well-formed XML reports that alone. Every other rule here reads a parsed
/// document, so with nothing to parse they would be answering a question about text that does not
/// yet mean anything -- and the missing <c>xmlns:e</c> that makes <c>&lt;e:code&gt;</c> an unbound
/// prefix would otherwise surface as every expression in the file using an undeclared name.
/// </para>
/// <para>
/// A document whose declarations are wrong reports those and no <em>expressions</em>. The symbol
/// table is missing whatever the bad declaration would have put in it, so every use of that name
/// would look undeclared — a hundred of those bury the few that are real. Attribute values are
/// reported regardless, since no converter consults the symbol table.
/// </para>
/// </remarks>
public static class SvgSourceDiagnostics
{
    /// <summary>Reports what is wrong with <paramref name="source"/>: its declarations, its expressions and its attribute values.</summary>
    /// <remarks>
    /// A document that cannot be read at all reports why, and nothing else. Never throws: a source
    /// view exists to show a file, and one that vanished because its own error reporting failed
    /// would be absurd.
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

        // Asked first, and of the pass that always parses. A document that is not well-formed has
        // one thing wrong with it, and everything else here would be a guess at what the text was
        // going to say -- so this reports the reader's refusal and stops.
        //
        // It has to be this pass that answers. The declarations reader gives up on a document with
        // no expression namespace anywhere in its text before it parses anything, and a document
        // that uses `<e:code>` without declaring the prefix is exactly that document: the string it
        // searches for is the one the author forgot. Left to it, the one mistake that breaks the
        // file is the same mistake that hides the error, and what surfaces instead is every name in
        // every expression reported as undeclared -- consequences, in place of the cause.
        if (SvgSourceAttributes.Analyse(source!, tokens, found) is { } malformed)
        {
            // The reader's own sentence, position and all. It names the line a second time over the
            // mark that already points there, which is a small redundancy against trimming a
            // localised resource string by guessing at its shape.
            found.Add(Mark(
                new SvgExpressionDeclarations.Positions(source!).At(malformed.LineNumber, malformed.LinePosition),
                source!.Length,
                malformed.Message,
                tokens,
                source!));

            return found;
        }

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
        }
        else if (sites.Count > 0)
        {
            Check(found, declarations, sites, tokens, source!);
        }

        // Document order, because three passes produced these and none knows about the others: what
        // only numbers can settle is found after every expression has been checked, and what a
        // converter refuses is found before any of it, so a file read top to bottom reads in order.
        found.Sort(static (left, right) => left.Start.CompareTo(right.Start));

        return found;
    }

    /// <summary>Checks every expression in the document, in the scope the language gives it.</summary>
    private static void Check(
        List<SvgSourceDiagnostic> found,
        SvgExpressionDeclarations declarations,
        IReadOnlyList<SvgSourceSite> sites,
        IReadOnlyList<SvgSourceToken> tokens,
        string source)
    {
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
            var text = source.Substring(site.Start, site.Length);
            var scope = site.Kind == SvgSourceSiteKind.Declaration ? isolated : checker;

            try
            {
                // A placeholder is checked against what the attribute holding it will do with the
                // answer, where that is known. Both back ends already refuse a paint expression
                // that is not a colour; asking the same question here only moves the same refusal
                // from generating or rendering the drawing to reading it, and the label comes from
                // the language so all three say it identically.
                var typed = site.Kind == SvgSourceSiteKind.Placeholder
                            && site.Attribute is { } attribute
                            && SvgExpressionAttributes.TypeFor(attribute) is { } expected
                    ? scope.CheckAs(text, expected, ExprFunctions.DescribeUse(expected))
                    : scope.Check(text);

                if (site.Kind == SvgSourceSiteKind.Let && lets < declarations.Lets.Count)
                {
                    symbols[declarations.Lets[lets].Name] = typed.Type;
                }
            }
            catch (ExprException failure)
            {
                found.Add(Describe(failure, site, tokens, source));

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

        Resolve(found, declarations, reported, sites, tokens, source);
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
    /// <remarks>
    /// Internal rather than private because the attribute pass wants the same two behaviours: a mark
    /// that never begins on a space, and one that covers the piece the pane already draws rather than
    /// a caret under one character.
    /// </remarks>
    internal static SvgSourceDiagnostic Mark(
        int at,
        int stop,
        string message,
        IReadOnlyList<SvgSourceToken> tokens,
        string source,
        SvgSourceSeverity severity = SvgSourceSeverity.Error)
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
                return new SvgSourceDiagnostic(token.Start, token.Length, severity, message);
            }
        }

        var start = Math.Max(0, Math.Min(at, Math.Max(0, source.Length - 1)));
        var end = start;

        while (end < stop && end < source.Length && !char.IsWhiteSpace(source[end]))
        {
            end++;
        }

        return new SvgSourceDiagnostic(start, Math.Max(1, end - start), severity, message);
    }
}
