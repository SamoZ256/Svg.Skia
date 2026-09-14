// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
namespace Svg.PaintCode;

/// <summary>A <c>CF$UID</c>: an index into the keyed archive's object table.</summary>
/// <remarks>
/// Its own type rather than an int, because a plist holds both and only one of them is a reference.
/// Index 0 is the archive's <c>$null</c> and means the value was nil when it was written.
/// </remarks>
internal readonly struct PaintCodeUid
{
    internal PaintCodeUid(int index) => Index = index;

    internal int Index { get; }

    internal bool IsNull => Index == 0;

    public override string ToString() => $"CF$UID({Index})";
}
