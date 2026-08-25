// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;

namespace ShimSkiaSharp;

/// <summary>
/// Every command a picture can hold, for anything that walks one.
/// </summary>
/// <remarks>
/// No default implementations, because this multi-targets netstandard2.0 and net461 where the
/// language has none. So adding a <c>CanvasCommand</c> is a breaking change for every implementor
/// outside this repository, and needs an arm in the <c>DeepClone</c> switch in <c>SKCanvas.cs</c>
/// as well — the compiler will not ask for that one.
/// </remarks>
public interface ICanvasCommandVisitor
{
    void Visit(ClipPathCanvasCommand cmd);
    void Visit(ClipRectCanvasCommand cmd);
    void Visit(DrawImageCanvasCommand cmd);
    void Visit(DrawPictureCanvasCommand cmd);
    void Visit(DrawPathCanvasCommand cmd);
    void Visit(DrawPositionedTextRunCanvasCommand cmd);
    void Visit(DrawTextBlobCanvasCommand cmd);
    void Visit(DrawTextCanvasCommand cmd);
    void Visit(DrawTextOnPathCanvasCommand cmd);
    void Visit(RestoreCanvasCommand cmd);
    void Visit(SaveCanvasCommand cmd);
    void Visit(SaveLayerCanvasCommand cmd);
    void Visit(SetMatrixCanvasCommand cmd);
    void Visit(BeginConditionalCanvasCommand cmd);
    void Visit(EndConditionalCanvasCommand cmd);
}
