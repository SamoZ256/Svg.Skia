// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Collections.Generic;

namespace ShimSkiaSharp.Editing;

/// <summary>
/// Which of a picture's saves opened a layer, as it replays: what tells a back end whether a path
/// it draws has to stay a path.
/// </summary>
public sealed class SaveLayerTracker
{
    private int _saveDepth;
    private int _saveLayerDepth;
    private int _singleLayerSaveDepth;
    private List<int>? _nestedLayerSaveDepths;

    public int SaveLayerDepth => _saveLayerDepth;

    public void Save(bool isLayer)
    {
        _saveDepth++;
        if (isLayer)
        {
            _saveLayerDepth++;
            if (_saveLayerDepth == 1)
            {
                _singleLayerSaveDepth = _saveDepth;
            }
            else
            {
                (_nestedLayerSaveDepths ??= new List<int>(2)).Add(_saveDepth);
            }
        }
    }

    public void Restore()
    {
        if (_saveDepth == 0)
        {
            return;
        }

        if (_saveLayerDepth > 0 && CurrentLayerSaveDepth == _saveDepth)
        {
            if (_saveLayerDepth == 1)
            {
                _singleLayerSaveDepth = 0;
            }
            else
            {
                _nestedLayerSaveDepths!.RemoveAt(_saveLayerDepth - 2);
            }

            _saveLayerDepth--;
        }

        _saveDepth--;
    }

    /// <summary>
    /// The rect, round rect, oval or circle <paramref name="command"/> is drawn as instead of a path:
    /// a fill of that one command with no layer open, or null when it stays a path.
    /// </summary>
    /// <remarks>
    /// A back end that draws it as a path parts from one that does not: raster Skia supersamples a
    /// path's horizontal edges but covers a rect's exactly (symbol-widget-small, RMS 0.0165).
    /// </remarks>
    public PathCommand? Primitive(DrawPathCanvasCommand command)
    {
        if (_saveLayerDepth > 0 ||
            command.Paint?.Style != SKPaintStyle.Fill ||
            command.Path?.Commands is not { Count: 1 } commands)
        {
            return null;
        }

        return commands[0] is AddRectPathCommand or AddRoundRectPathCommand or AddOvalPathCommand or AddCirclePathCommand
            ? commands[0]
            : null;
    }

    private int CurrentLayerSaveDepth
        => _saveLayerDepth == 1 ? _singleLayerSaveDepth : _nestedLayerSaveDepths![_saveLayerDepth - 2];
}
