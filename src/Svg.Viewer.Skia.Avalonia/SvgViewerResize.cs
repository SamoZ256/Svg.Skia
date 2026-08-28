// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using Svg.Skia;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// The three ways of saying one size — a width, a height and a scale — kept in step with each other.
/// </summary>
/// <remarks>
/// <para>
/// A plain class rather than part of the window, because this is the whole of what a resize dialog
/// does and none of it needs a screen to be true.
/// </para>
/// <para>
/// A scale belongs to the locked ratio alone, and that is the sizing model's rule rather than a
/// choice made here: <see cref="SvgSizeRequest"/> refuses a scale given with a width or a height,
/// since they are two ways of asking for the same thing, and it has one scale for both axes.
/// </para>
/// </remarks>
public sealed class SvgViewerResize
{
    private float _width;
    private float _height;
    private bool _locked = true;

    /// <param name="naturalWidth">The width the drawing has now, which every ratio is taken against.</param>
    /// <param name="naturalHeight">The height it has now.</param>
    public SvgViewerResize(float naturalWidth, float naturalHeight)
    {
        if (naturalWidth <= 0f || naturalHeight <= 0f)
        {
            throw new ArgumentException($"A drawing of {naturalWidth}x{naturalHeight} has no size to resize from.");
        }

        NaturalWidth = naturalWidth;
        NaturalHeight = naturalHeight;

        _width = naturalWidth;
        _height = naturalHeight;
    }

    public float NaturalWidth { get; }

    public float NaturalHeight { get; }

    public float Width => _width;

    public float Height => _height;

    /// <summary>What the width comes to as a factor of the size the drawing has now.</summary>
    /// <remarks>
    /// Derived rather than stored: with the ratio locked it is the width and the height both, and
    /// storing it as well would be a third value to keep in step with two that already agree.
    /// </remarks>
    public float Scale => _width / NaturalWidth;

    /// <summary>Whether the height follows the width, and a scale means anything at all.</summary>
    public bool IsAspectRatioLocked
    {
        get => _locked;
        set
        {
            _locked = value;

            if (value)
            {
                // Locking mid-edit takes the width as the answer and puts the height back on the
                // ratio, rather than keeping a shape the lock says cannot exist.
                _height = _width * NaturalHeight / NaturalWidth;
            }
        }
    }

    public void SetWidth(float width)
    {
        _width = Positive(width, nameof(width));

        if (_locked)
        {
            _height = _width * NaturalHeight / NaturalWidth;
        }
    }

    public void SetHeight(float height)
    {
        _height = Positive(height, nameof(height));

        if (_locked)
        {
            _width = _height * NaturalWidth / NaturalHeight;
        }
    }

    /// <summary>Sets both dimensions to a factor of the size the drawing has now.</summary>
    /// <exception cref="InvalidOperationException">The ratio is not locked, where a scale means nothing.</exception>
    public void SetScale(float scale)
    {
        if (!_locked)
        {
            throw new InvalidOperationException(
                "A scale is one factor for both axes, so it says nothing while the width and the height are free of each other.");
        }

        Positive(scale, nameof(scale));

        _width = NaturalWidth * scale;
        _height = NaturalHeight * scale;
    }

    /// <summary>What to ask the sizing model for, or nothing where this is the size already.</summary>
    /// <remarks>
    /// A locked resize is asked for as a width alone, and the model derives the height from the
    /// same ratio this does. Sending both would be a box to fit into — the same answer here, and a
    /// different one for a drawing whose own width and height disagree with its viewBox.
    /// </remarks>
    public SvgSizeRequest ToRequest()
    {
        if (_locked)
        {
            return _width == NaturalWidth
                ? SvgSizeRequest.None
                : new SvgSizeRequest(_width, null, null);
        }

        return _width == NaturalWidth && _height == NaturalHeight
            ? SvgSizeRequest.None
            : new SvgSizeRequest(_width, _height, null);
    }

    private static float Positive(float value, string name)
        => value > 0f && !float.IsNaN(value) && !float.IsInfinity(value)
            ? value
            : throw new ArgumentException($"A {name} has to be a positive number, but was {value}.", name);
}
