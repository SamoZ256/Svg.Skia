// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;

namespace Svg.Expressions;

public enum ExprType
{
    Number,
    Color,
    Boolean
}

public sealed class ExprException : Exception
{
    public ExprException(
        string message,
        int position,
        string? expressionText = null,
        SvgDeclarationPart? part = null)
        : base(message)
    {
        Position = position;
        ExpressionText = expressionText;
        Part = part;
    }

    // Zero based offset into the expression text, so callers can point at the offending token.
    public int Position { get; }

    /// <summary>
    /// The expression this was raised from, attached once it is known. Deliberately not named
    /// Source, which would shadow <see cref="Exception.Source"/>.
    /// </summary>
    public string? ExpressionText { get; }

    /// <summary>
    /// Which part of a declaration this is about, when it is about one.
    /// </summary>
    /// <remarks>
    /// A rule knows what it is complaining about; only a reader knows where that was written. So the
    /// rules say which part and the reader turns that into a position, which is the only arrangement
    /// where both readers keep reporting identically — the one that walks a parsed tree has no
    /// positions to give, and says so by ignoring this.
    /// </remarks>
    public SvgDeclarationPart? Part { get; }

    public ExprException WithExpressionText(string text)
        => ExpressionText is null ? new ExprException(Message, Position, text, Part) : this;

    /// <summary>Renders the message with the expression and a caret under the offending token.</summary>
    public string ToDiagnostic()
    {
        if (ExpressionText is null)
        {
            return Message;
        }

        var caret = new string(' ', Math.Max(0, Math.Min(Position, ExpressionText.Length))) + "^";

        // "\n" rather than Environment.NewLine: analyzers may not touch Environment, and a
        // diagnostic should not vary with the machine that produced it.
        return $"{Message}\n    {ExpressionText}\n    {caret}";
    }
}
