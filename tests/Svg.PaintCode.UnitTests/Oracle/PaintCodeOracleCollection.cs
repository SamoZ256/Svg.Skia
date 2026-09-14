#if PAINTCODE_ORACLE
using Xunit;

namespace Svg.PaintCode.UnitTests.Oracle;

/// <summary>Holds every class that draws through the oracle to one thread.</summary>
/// <remarks>
/// PaintCode's generated code keeps its geometry in 1014 <c>CacheFor*</c> classes of lazily created,
/// process-wide <c>SKPaint</c> and <c>SKPath</c>, with no guard on the initialisation and no lock on
/// the reuse; <c>Helpers.RGBColorCache</c> is a bare <c>Dictionary</c> mutated as it draws. Drawing
/// two canvases at once corrupts both.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PaintCodeOracleCollection
{
    internal const string Name = "PaintCode oracle";
}
#endif
