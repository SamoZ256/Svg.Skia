namespace Svg.Studio.UiTests;

internal static class Drawings
{
    /// <summary>One square that fills the canvas when fitted, so a click in the canvas's middle picks it.</summary>
    public const string Square = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <rect id="square" width="24" height="24" fill="#3b82f6" />
        </svg>
        """;
}
