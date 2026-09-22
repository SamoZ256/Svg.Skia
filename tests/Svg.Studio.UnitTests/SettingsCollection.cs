using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>The classes that read and write <see cref="StudioSettings"/>, run one at a time.</summary>
/// <remarks>
/// The store is one static path shared by the whole run, and two of these classes repoint it at a
/// file of their own and put it back afterwards. Run in parallel they write into each other's: a
/// class asserting what is in its settings file would find a setting another class had just written
/// there, which showed up as a theme or a caption size failing perhaps one run in ten and passing
/// on its own every time.
/// </remarks>
[CollectionDefinition("settings")]
public sealed class SettingsCollection
{
}
