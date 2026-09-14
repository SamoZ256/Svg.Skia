// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
namespace Svg.PaintCode;

public enum PaintCodeImportSeverity
{
    /// <summary>Something was converted, but not as the PaintCode document said it.</summary>
    Approximated,

    /// <summary>Something was left out, and the drawing is short of what PaintCode draws.</summary>
    Dropped
}

/// <summary>One thing an import could not carry across, named so it can be looked at.</summary>
/// <remarks>
/// The converter never emits a binding or a feature it cannot express: it writes the value the
/// PaintCode document would have drawn with and records it here. Nothing renders wrong, and what was
/// lost is visible rather than silent.
/// </remarks>
public sealed class PaintCodeImportNote
{
    internal PaintCodeImportNote(PaintCodeImportSeverity severity, string canvas, string element, string property, string message)
    {
        Severity = severity;
        Canvas = canvas;
        Element = element;
        Property = property;
        Message = message;
    }

    public PaintCodeImportSeverity Severity { get; }

    public string Canvas { get; }

    public string Element { get; }

    public string Property { get; }

    public string Message { get; }

    public override string ToString() => $"{Canvas}/{Element}: {Property} — {Message}";
}
