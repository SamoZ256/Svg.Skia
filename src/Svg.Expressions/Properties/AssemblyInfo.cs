using System.Runtime.CompilerServices;

// ExprMathFallback only runs on netstandard2.0, where MathF does not exist, so nothing that this
// repository tests would ever execute it. Granting the test project access lets a net10.0 host
// compare it against MathF directly and pin how far apart the two are.
[assembly: InternalsVisibleTo("Svg.Skia.UnitTests,PublicKey=" +
"00240000048000009400000006020000002400005253413100040000010001000958ee05055101" +
"5f5db159c2fcc56a83ca8a54083e1ac6cac40312e0b0dcb26ce9e1cba2358c8644ffd7b21efbbc" +
"1304b44f6d6487c23218986ab356ce0461e2e8886d8269a47e534b4a48310151719fdfdde82aad" +
"3667eb87baad62c7bb7cf826a4095229fbed8904f90cf9dc553c9ad5d6a3e543058847431fdda7" +
"58211bd3")]

// The highlighter colours the expression language by running this lexer over a {{ … }} span or an
// <e:let> body. Anything else would be a second description of the language, and would drift from
// it: that a percent sign is a suffix on a number literal rather than an operator, and that
// and/or/not/lt/le/gt/ge/eq/ne are word forms of the symbolic operators, is knowable only here.
[assembly: InternalsVisibleTo("Svg.Highlighting,PublicKey=" +
"00240000048000009400000006020000002400005253413100040000010001000958ee05055101" +
"5f5db159c2fcc56a83ca8a54083e1ac6cac40312e0b0dcb26ce9e1cba2358c8644ffd7b21efbbc" +
"1304b44f6d6487c23218986ab356ce0461e2e8886d8269a47e534b4a48310151719fdfdde82aad" +
"3667eb87baad62c7bb7cf826a4095229fbed8904f90cf9dc553c9ad5d6a3e543058847431fdda7" +
"58211bd3")]
