// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
namespace Svg.Studio;

/// <summary>Where a row dropped on another one goes.</summary>
public enum ProjectDrop
{
    /// <summary>Above it, as its sibling.</summary>
    Before,

    /// <summary>Below it, as its sibling.</summary>
    After,

    /// <summary>Into it, at the end. Groups only.</summary>
    Inside
}
