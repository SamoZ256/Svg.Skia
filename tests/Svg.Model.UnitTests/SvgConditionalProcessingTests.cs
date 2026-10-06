using System.Globalization;
using Svg.Model.Services;
using Xunit;

namespace Svg.Model.UnitTests;

public class SvgConditionalProcessingTests
{
    [Fact]
    public void HasSystemLanguage_MatchesPrimaryLanguagePrefix()
    {
        using var _ = SvgService.PushSystemLanguage(CultureInfo.GetCultureInfo("en-US"));

        var group = LoadGroup("en");

        Assert.True(group.HasSystemLanguage());
    }

    [Fact]
    public void HasSystemLanguage_DoesNotMatchMoreSpecificRequestedLanguage()
    {
        using var _ = SvgService.PushSystemLanguage(CultureInfo.GetCultureInfo("en"));

        var group = LoadGroup("en-US");

        Assert.False(group.HasSystemLanguage());
    }

    [Fact]
    public void HasSystemLanguage_DoesNotTreatLegacyJwTagAsInvariantCulture()
    {
        using var _ = SvgService.PushSystemLanguage(CultureInfo.InvariantCulture);

        var group = LoadGroup("jw");

        Assert.False(group.HasSystemLanguage());
    }

    private static SvgGroup LoadGroup(string systemLanguage)
    {
        var document = SvgService.FromSvg($$"""
            <svg xmlns="http://www.w3.org/2000/svg">
              <g id="target" systemLanguage="{{systemLanguage}}" />
            </svg>
            """);

        Assert.NotNull(document);
        return Assert.IsType<SvgGroup>(document.GetElementById("target"));
    }
}
