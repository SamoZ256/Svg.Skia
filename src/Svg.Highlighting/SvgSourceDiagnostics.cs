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

/// <summary>Finds what is wrong with a document.</summary>
/// <remarks>
/// Nothing here decides what is an error: every rule and every sentence comes from the language's
/// own checker, or from the converter an attribute actually uses. Order matters twice over. A
/// document that is not well-formed reports that alone, because the missing <c>xmlns:e</c> that
/// makes <c>&lt;e:code&gt;</c> unbound would otherwise surface as every expression naming something
/// undeclared. And a document whose declarations are wrong reports those and no expressions, since
/// the symbol table is missing whatever the bad declaration would have put in it.
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

        // This pass has to answer it, not the declarations reader: that one gives up before parsing
        // when the namespace string is absent, which is the very mistake being reported.
        if (SvgSourceAttributes.Analyse(source!, tokens, found) is { } malformed)
        {
            found.Add(Mark(
                new SvgExpressionDeclarations.Positions(source!).At(malformed.LineNumber, malformed.LinePosition),
                source!.Length,
                malformed.Message,
                tokens,
                source!));

            return found;
        }

        // Every declaration is read rather than only the first, so a block with three mistakes
        // shows three.
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

        // Document order: three passes produced these and none knows about the others.
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
            // What the language reads, not what the file holds: `a &lt; b` is one comparison and
            // not a broken `&&`.
            var text = site.Text ?? source.Substring(site.Start, site.Length);
            var scope = site.Kind == SvgSourceSiteKind.Declaration ? isolated : checker;

            try
            {
                // Checked against what the attribute will do with the answer, so an expression that
                // is well-formed and still wrong for its use is refused here rather than at render.
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

    /// <summary>Reports what only running a declaration's own expressions can find.</summary>
    /// <remarks>
    /// A min above its max, or a step that is not positive, needs numbers rather than types, and
    /// reading a document never evaluates — <c>SKSvg.Load</c> is documented not to. Analysing is not
    /// reading, so they are settled here.
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
    /// throws one, and it names no part, so the caller says which expression it was running.
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

            found.Add(Mark(At(site, within), site.Start + site.Length, failure.Message, tokens, source));
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

    /// <summary>Turns a refusal into something a view can underline.</summary>
    /// <remarks>
    /// The language reports a position but no extent, so the span is the token it falls in. Past the
    /// point the lexer stopped there is no token, and the run of non-space characters is used rather
    /// than the whole remainder, which would underline the rest of the line.
    /// </remarks>
    private static SvgSourceDiagnostic Describe(
        ExprException failure,
        SvgSourceSite site,
        IReadOnlyList<SvgSourceToken> tokens,
        string source)
        => Mark(
            At(site, failure.Position),
            site.Start + site.Length,
            failure.Message,
            tokens,
            source);

    /// <summary>Where in the document a position the language reported was written.</summary>
    private static int At(SvgSourceSite site, int within)
        => ExprText.Written(site.Offsets, within, site.Start + Math.Max(0, Math.Min(within, site.Length)));

    /// <summary>Marks the piece of the document at <paramref name="at"/>.</summary>
    /// <remarks>
    /// Internal because the attribute pass wants the same two behaviours: a mark that never begins on
    /// a space, and one that covers a whole piece rather than one character.
    /// </remarks>
    internal static SvgSourceDiagnostic Mark(
        int at,
        int stop,
        string message,
        IReadOnlyList<SvgSourceToken> tokens,
        string source,
        SvgSourceSeverity severity = SvgSourceSeverity.Error)
    {
        // A rule about a whole expression reports position zero, which in `default=" 1 "` is the
        // gap before the value — an underline a reader cannot see.
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
