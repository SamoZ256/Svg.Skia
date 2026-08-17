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
    public ExprException(string message, int position, string? expressionText = null)
        : base(message)
    {
        Position = position;
        ExpressionText = expressionText;
    }

    // Zero based offset into the expression text, so callers can point at the offending token.
    public int Position { get; }

    /// <summary>
    /// The expression this was raised from, attached once it is known. Deliberately not named
    /// Source, which would shadow <see cref="Exception.Source"/>.
    /// </summary>
    public string? ExpressionText { get; }

    public ExprException WithExpressionText(string text)
        => ExpressionText is null ? new ExprException(Message, Position, text) : this;

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
