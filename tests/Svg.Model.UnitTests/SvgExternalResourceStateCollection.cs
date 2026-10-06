using Xunit;

namespace Svg.Model.UnitTests;

/// <remarks>
/// Alone, not merely in sequence: <see cref="SvgDocument.ResolveExternalImages"/> is one static for
/// every document in the process, and any class loading an <c>image</c> in parallel reads it.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SvgExternalResourceStateCollection
{
    public const string Name = "Svg external resource state";
}
