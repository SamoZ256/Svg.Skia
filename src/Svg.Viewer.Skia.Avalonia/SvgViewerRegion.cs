// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using Avalonia.Controls;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>One panel a <see cref="SvgViewerDock"/> arranges around the drawing.</summary>
/// <remarks>
/// The id is not the header. A layout is written down and read back, so what it names a panel by has
/// to survive the panel being renamed and has to be the same in every language — where the header is
/// what somebody reads off the strip. <see cref="SvgViewerPane"/> is the host's way of handing one
/// over and carries only a header, so a host's pane is given an id made from it.
/// </remarks>
public sealed class SvgViewerRegion
{
    public SvgViewerRegion(string id, string header, Control content)
    {
        Id = string.IsNullOrWhiteSpace(id) ? throw new ArgumentNullException(nameof(id)) : id;
        Header = header ?? throw new ArgumentNullException(nameof(header));
        Content = content ?? throw new ArgumentNullException(nameof(content));
    }

    /// <summary>What a written layout calls it.</summary>
    public string Id { get; }

    /// <summary>What is written on its header.</summary>
    public string Header { get; }

    public Control Content { get; }

    /// <summary>An id for a host's pane, which has a header and nothing else to be known by.</summary>
    /// <remarks>
    /// Lowered and stripped of everything a layout uses as punctuation, so "Project" is <c>project</c>
    /// and a pane called "Bits + Pieces" cannot write a separator into the middle of a line.
    /// </remarks>
    public static string IdFor(string header)
    {
        var id = new System.Text.StringBuilder(header.Length);

        foreach (var letter in header)
        {
            if (char.IsLetterOrDigit(letter))
            {
                id.Append(char.ToLowerInvariant(letter));
            }
        }

        return id.Length > 0 ? id.ToString() : "pane";
    }
}
