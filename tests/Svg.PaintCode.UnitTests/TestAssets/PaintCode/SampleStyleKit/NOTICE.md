# SampleStyleKit.pcvd

From https://github.com/BareFeetWare/BFWDrawView at 49f7c6c9e53068ab06cc853fc10b2f272e93db3a (`SampleStyleKit.pcvd`), unmodified.

```
Copyright (c) 2015 BareFeetWare

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

```

## The PaintCode export

`PaintCode/SampleStyleKit.cs` is PaintCode 3.6.2's Android export of this document, converted by
[PaintCode2Skia](https://github.com/michaldobrodenka/PaintCode2Skia) at ee8bf98, with
`PaintCode/PaintCodeClasses.cs` from the same commit. The tool's output didn't compile, and it was
patched by hand only where it fails, each edit following the Java line for line:

- The `// Colors` block the tool drops (`translucentBlack`, `baseColor`, `buttonHighlightedColor`,
  `baseLightColor`, `buttonColor`), copied from the Java.
- `CacheForBathroom`/`CacheForFrontDoor`'s `BottomShadow` and `BottomThinShadow` restored to lower case.
  The tool's `.bottom` → `.Bottom` rewrite caught them.
- The five paths drawn under an outer shadow (bath rim, towel rail, door top and bottom panel, door knob)
  built before their shadow layer, where the Java builds them. The tool built them inside it, out of
  reach of the fill that follows.
