// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;

namespace Svg.SourceEditing;

/// <summary>What an edit would do to a document, or why it cannot be done.</summary>
/// <remarks>
/// <para>
/// A refusal rather than an exception, because every one of them is something a person can act on —
/// a name already taken, a range with one end, a document that is not well-formed yet — and none of
/// them is a fault in the caller. A host shows <see cref="Refusal"/> where it would have shown the
/// edit.
/// </para>
/// <para>
/// The wording is not this assembly's. A refusal about a declaration comes from the language's own
/// rules, so that what an editor says about a name and what the source pane says about the same name
/// cannot disagree.
/// </para>
/// </remarks>
public sealed class SvgSourceEditResult
{
    private static readonly SvgTextEdit[] s_none = Array.Empty<SvgTextEdit>();

    private SvgSourceEditResult(IReadOnlyList<SvgTextEdit> edits, string? refusal)
    {
        Edits = edits;
        Refusal = refusal;
    }

    /// <summary>Whether there is an edit to apply.</summary>
    /// <remarks>
    /// An edit that would change nothing succeeds with no spans: setting a default to what it
    /// already says is not a failure, and a host that applies <see cref="Edits"/> does nothing,
    /// which is the right amount of work and the right number of undo steps.
    /// </remarks>
    public bool Succeeded => Refusal is null;

    /// <summary>The spans to replace, ascending by position.</summary>
    /// <remarks>
    /// Ascending because that is the order a reader would find them in, and non-overlapping, so a
    /// host may apply them in either direction as long as it accounts for the shift. See
    /// <see cref="SvgTextEdit.ApplyAll"/>.
    /// </remarks>
    public IReadOnlyList<SvgTextEdit> Edits { get; }

    /// <summary>Why nothing can be done, or null.</summary>
    public string? Refusal { get; }

    public static SvgSourceEditResult Nothing { get; } = new(s_none, null);

    public static SvgSourceEditResult From(IReadOnlyList<SvgTextEdit> edits)
        => edits.Count == 0 ? Nothing : new SvgSourceEditResult(edits, null);

    public static SvgSourceEditResult Refuse(string refusal) => new(s_none, refusal);
}
