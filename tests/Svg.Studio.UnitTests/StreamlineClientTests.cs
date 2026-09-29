using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// What <see cref="StreamlineClient"/> asks for and what it makes of the answer, against answers
/// shaped like the examples in Streamline's API reference rather than the network.
/// </summary>
public class StreamlineClientTests
{
    private const string Key = "sk_test_123";

    private sealed class Stub(HttpStatusCode status, string body, string type = "application/json") : HttpMessageHandler
    {
        public List<HttpRequestMessage> Asked { get; } = new();

        public Action<HttpResponseMessage>? Headers { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request);

            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, type) };

            Headers?.Invoke(response);

            return Task.FromResult(response);
        }
    }

    private static (StreamlineClient Client, Stub Stub) Answering(string body, HttpStatusCode status = HttpStatusCode.OK, string type = "application/json")
    {
        var stub = new Stub(status, body, type);

        return (new StreamlineClient(Key, new HttpClient(stub)), stub);
    }

    private static void AskedFor(Stub stub, string url)
    {
        var request = Assert.Single(stub.Asked);

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(url, request.RequestUri!.AbsoluteUri);
        Assert.Equal(Key, Assert.Single(request.Headers.GetValues("x-api-key")));
    }

    [Fact]
    public async Task A_Search_Asks_With_Its_Filters_And_Reads_Results_And_Paging()
    {
        var (client, stub) = Answering("""
            {
              "query": "home",
              "results": [
                {
                  "hash": "ico_EKcwaIzWkazzlH",
                  "name": "home",
                  "imagePreviewUrl": "https://assets.streamlinehq.com/image/private/w_128,h_128,ar_1/f_auto/home.png?_a=DATAiZiuZAA0",
                  "webUrl": "https://www.streamlinehq.com/icons/download/home--22247",
                  "isFree": false,
                  "hasPremiumAccess": true,
                  "familySlug": "ultimate-regular",
                  "familyName": "Ultimate Regular",
                  "categorySlug": "interface-essential",
                  "categoryName": "Interface Essential",
                  "subcategorySlug": "home",
                  "subcategoryName": "Home"
                },
                {
                  "hash": "ico_YqRSKqWzYmBcUW",
                  "name": "home 2",
                  "imagePreviewUrl": "https://assets.streamlinehq.com/image/private/w_128,h_128,ar_1/f_auto/home-2.png",
                  "webUrl": "https://www.streamlinehq.com/icons/download/home-2--25374",
                  "isFree": true,
                  "hasPremiumAccess": false,
                  "familySlug": "core-line-free",
                  "familyName": "Core Line - Free",
                  "categorySlug": "interface-essential",
                  "categoryName": "Interface Essential",
                  "subcategorySlug": "home",
                  "subcategoryName": "Home"
                }
              ],
              "pagination": { "total": 10, "hasMore": true, "offset": 0, "nextOffset": 2 }
            }
            """);

        var page = await client.Search("home sweet", offset: 20, limit: 2, style: "line", productTier: "premium", cancellation: TestContext.Current.CancellationToken);

        AskedFor(stub, "https://public-api.streamlinehq.com/v1/search/global?productType=icons&query=home%20sweet&offset=20&limit=2&style=line&productTier=premium");

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(10, page.Total);
        Assert.True(page.HasMore);
        Assert.Equal(2, page.NextOffset);

        var first = page.Items[0];

        Assert.Equal("ico_EKcwaIzWkazzlH", first.Hash);
        Assert.Equal("home", first.Name);
        Assert.Equal("ultimate-regular", first.FamilySlug);
        Assert.Equal("Ultimate Regular", first.FamilyName);
        Assert.StartsWith("https://assets.streamlinehq.com/", first.ImagePreviewUrl);
        Assert.False(first.IsFree);
        Assert.True(first.HasPremiumAccess);
        Assert.True(page.Items[1].IsFree);
    }

    [Fact]
    public async Task A_Search_Leaves_Out_The_Filters_It_Was_Not_Given()
    {
        var (client, stub) = Answering("""{ "query": "", "results": [], "pagination": { "total": 0, "hasMore": false, "offset": 0, "nextOffset": 0 } }""");

        var page = await client.Search("", cancellation: TestContext.Current.CancellationToken);

        AskedFor(stub, "https://public-api.streamlinehq.com/v1/search/global?productType=icons&query=&offset=0&limit=50");
        Assert.Empty(page.Items);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task An_Icons_Details_Carry_Its_Colours_And_Its_Svg_Where_Given()
    {
        var (client, stub) = Answering("""
            {
              "hash": "ico_exooyaBtHapnFR",
              "name": "logout",
              "imagePreviewUrl": "https://assets.streamlinehq.com/image/private/w_128,h_128,ar_1/f_auto/logout.png",
              "webUrl": "https://www.streamlinehq.com/icons/download/logout--46103",
              "colors": ["#000000", "#ffffff"],
              "isFree": false,
              "familySlug": "core-line",
              "familyName": "Core Line",
              "familyProductType": "icons",
              "categorySlug": "interface-essential",
              "categoryName": "Interface Essential",
              "subcategorySlug": "login-logout",
              "subcategoryName": "Login/Logout",
              "svg": "<svg xmlns=\"http://www.w3.org/2000/svg\" fill=\"none\" viewBox=\"-1.5 -1.5 48 48\" height=\"48\" width=\"48\"><path stroke=\"#000\" d=\"M43.07 43.125H31.465\" stroke-width=\"3\"/></svg>"
            }
            """);

        var icon = await client.Icon("ico_exooyaBtHapnFR", cancellation: TestContext.Current.CancellationToken);

        AskedFor(stub, "https://public-api.streamlinehq.com/v1/icons/ico_exooyaBtHapnFR");

        Assert.Equal(new[] { "#000000", "#ffffff" }, icon.Colors);
        Assert.StartsWith("<svg", icon.Svg);
        Assert.Equal("core-line", icon.FamilySlug);
    }

    [Fact]
    public async Task An_Icons_Details_Without_Pro_Have_No_Svg()
    {
        var (client, _) = Answering("""
            { "hash": "ico_a", "name": "a", "imagePreviewUrl": "https://x/a.png", "webUrl": "https://y", "colors": [],
              "isFree": true, "familySlug": "f", "familyName": "F", "familyProductType": "icons",
              "categorySlug": "c", "categoryName": "C", "subcategorySlug": "s", "subcategoryName": "S" }
            """);

        var icon = await client.Icon("ico_a", cancellation: TestContext.Current.CancellationToken);

        Assert.Null(icon.Svg);
        Assert.Empty(icon.Colors!);
    }

    [Fact]
    public async Task A_Download_Sends_The_Required_Switches_And_Returns_The_Svg()
    {
        const string Svg = """
            <svg xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24" stroke="#000000" aria-hidden="true" id="Home--Streamline-Heroicons-Outline">
                    <path stroke-linecap="round" stroke-linejoin="round" d="m2.25 12 8.954 -8.955" stroke-width="1"></path>
                  </svg>
            """;
        var (client, stub) = Answering(Svg, type: "image/svg+xml");

        var text = await client.DownloadSvg("ico_XjMYRy9v4t96OdUn", 48, 1.5, new[] { "#000000", "#CDFF71" }, cancellation: TestContext.Current.CancellationToken);

        AskedFor(stub, "https://public-api.streamlinehq.com/v1/icons/ico_XjMYRy9v4t96OdUn/download/svg?size=48&responsive=true&strokeToFill=false&strokeWidth=1.5&colors=%23000000%2C%23CDFF71");
        Assert.Equal(Svg, text);
    }

    [Fact]
    public async Task A_Download_Without_Colours_Or_Weight_Leaves_Them_Out()
    {
        var (client, stub) = Answering("<svg/>", type: "image/svg+xml");

        await client.DownloadSvg("ico_a", 24, cancellation: TestContext.Current.CancellationToken);

        AskedFor(stub, "https://public-api.streamlinehq.com/v1/icons/ico_a/download/svg?size=24&responsive=true&strokeToFill=false");
    }

    [Fact]
    public async Task Family_Groups_Are_Read_From_A_List()
    {
        var (client, stub) = Answering("""
            [
              { "hash": "ico_ohCHrylKHTpkDe", "slug": "streamline", "name": "Streamline", "description": "The core sets.", "productType": "icons" },
              { "hash": "ico_uVYkWzmpfQbZcX", "slug": "illustrations", "name": "Illustrations", "description": "Scenes.", "productType": "illustrations" }
            ]
            """);

        var groups = await client.FamilyGroups(cancellation: TestContext.Current.CancellationToken);

        AskedFor(stub, "https://public-api.streamlinehq.com/v1/family-groups");
        Assert.Equal(new[] { "streamline", "illustrations" }, groups.Select(group => group.Slug));
        Assert.Equal("icons", groups[0].ProductType);
    }

    /// <summary>The documented example is one object, so a wrapped list is read as well as a bare one.</summary>
    [Fact]
    public async Task Families_Are_Read_From_A_Wrapped_List_With_Paging()
    {
        var (client, stub) = Answering("""
            {
              "families": [
                { "hash": "ico_CCoOzuoMrlylcO", "slug": "core-line", "productType": "icons", "name": "Core Line",
                  "isFree": false, "cover": "https://cdn/cover.png", "iconCount": 8426, "description": null }
              ],
              "pagination": { "total": 3, "hasMore": true, "offset": 100, "nextOffset": 101 }
            }
            """);

        var page = await client.Families("ico_ohCHrylKHTpkDe", offset: 100, limit: 1, cancellation: TestContext.Current.CancellationToken);

        AskedFor(stub, "https://public-api.streamlinehq.com/v1/family-groups/ico_ohCHrylKHTpkDe/families?offset=100&limit=1");

        var family = Assert.Single(page.Items);

        Assert.Equal("core-line", family.Slug);
        Assert.Equal(8426, family.IconCount);
        Assert.Equal(101, page.NextOffset);
        Assert.True(page.HasMore);
    }

    /// <summary>The documented examples for both family endpoints, verbatim: one object, read as one item.</summary>
    [Fact]
    public async Task A_Lone_Family_Group_Or_Family_Is_Read_As_One()
    {
        var (groups, _) = Answering("""
            {
              "hash": "ico_ohCHrylKHTpkDe",
              "slug": "valetudo",
              "name": "jet",
              "description": "Tergum charisma amoveo. Sono universe cursim delego color.",
              "productType": "icons"
            }
            """);

        Assert.Equal("valetudo", Assert.Single(await groups.FamilyGroups(cancellation: TestContext.Current.CancellationToken)).Slug);

        var (families, _) = Answering("""
            {
              "hash": "ico_CCoOzuoMrlylcO",
              "slug": "caelestis",
              "productType": "icons",
              "name": "editor",
              "isFree": true,
              "cover": "https://loremflickr.com/2851/3254?lock=6415425687682387",
              "iconCount": 8426,
              "description": "Tergiversatio pauci crudelis sustineo aestas culpa."
            }
            """);

        var page = await families.Families("g", cancellation: TestContext.Current.CancellationToken);

        Assert.Equal(8426, Assert.Single(page.Items).IconCount);
        Assert.Equal(1, page.Total);
        Assert.False(page.HasMore);
    }

    /// <summary>An answer with no list is a wrong guess about its shape, not an empty catalogue.</summary>
    [Fact]
    public async Task An_Answer_Holding_No_List_Is_Thrown_Rather_Than_Read_As_Empty()
    {
        var token = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() =>
            Answering("""{ "message": "ok" }""").Client.FamilyGroups(cancellation: token));
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() =>
            Answering("""{ "a": [], "b": [], "pagination": { "total": 0, "hasMore": false, "offset": 0 } }""").Client.Families("g", cancellation: token));
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() =>
            Answering("""{ "query": "home", "pagination": { "total": 0, "hasMore": false, "offset": 0 } }""").Client.Search("home", cancellation: token));
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() =>
            Answering("""{ "results": [], "pagination": { "total": 0, "hasMore": false, "offset": 0 } }""").Client.FamilyIcons("f", cancellation: token));
    }

    /// <summary>Where the reference names the list, no other array in the answer is taken for it.</summary>
    [Fact]
    public async Task A_Search_Reads_Its_Results_Past_Another_Array()
    {
        var (client, _) = Answering("""
            {
              "suggestions": [{ "hash": "not_an_icon", "name": "home office" }],
              "results": [{ "hash": "ico_EKcwaIzWkazzlH", "name": "home", "isFree": true }],
              "pagination": { "total": 1, "hasMore": false, "offset": 0, "nextOffset": 1 }
            }
            """);

        var page = await client.Search("home", cancellation: TestContext.Current.CancellationToken);

        Assert.Equal("ico_EKcwaIzWkazzlH", Assert.Single(page.Items).Hash);
    }

    [Fact]
    public async Task A_Familys_Icons_Are_Read_With_Their_Colours_And_Paging()
    {
        var (client, stub) = Answering("""
            {
              "icons": [
                { "hash": "ico_XEzqalzdaePEMv", "name": "bell", "imagePreviewUrl": "https://assets.streamlinehq.com/bell.png",
                  "webUrl": "https://www.streamlinehq.com/icons/download/bell--75895", "colors": ["#2859c5", "#8fbffa"],
                  "isFree": false, "familySlug": "core-duo", "familyName": "Core Duo", "familyProductType": "icons",
                  "categorySlug": "c", "categoryName": "C", "subcategorySlug": "s", "subcategoryName": "S" },
                { "hash": "ico_TdKUVpWfYBzKnG", "name": "bin", "imagePreviewUrl": "https://assets.streamlinehq.com/bin.png",
                  "webUrl": "https://www.streamlinehq.com/icons/download/bin--90672", "colors": ["#2859c5"],
                  "isFree": false, "familySlug": "core-duo", "familyName": "Core Duo", "familyProductType": "icons",
                  "categorySlug": "c", "categoryName": "C", "subcategorySlug": "s", "subcategoryName": "S" }
              ],
              "pagination": { "hasMore": false, "total": 2, "offset": 0, "nextOffset": 2 }
            }
            """);

        var page = await client.FamilyIcons("ico_family", limit: 100, cancellation: TestContext.Current.CancellationToken);

        AskedFor(stub, "https://public-api.streamlinehq.com/v1/families/ico_family/icons?offset=0&limit=100");
        Assert.Equal(new[] { "bell", "bin" }, page.Items.Select(icon => icon.Name));
        Assert.Equal(new[] { "#2859c5", "#8fbffa" }, page.Items[0].Colors);
        Assert.False(page.HasMore);
        Assert.Equal(2, page.Total);
    }

    /// <summary>The preview is on the CDN, which has no business seeing the key.</summary>
    [Fact]
    public async Task A_Thumbnail_Is_Fetched_Without_The_Key()
    {
        var (client, stub) = Answering("PNG", type: "image/png");

        var bytes = await client.Thumbnail("https://assets.streamlinehq.com/image/private/w_128,h_128,ar_1/f_auto/home.png", cancellation: TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Asked);

        Assert.Equal("https://assets.streamlinehq.com/image/private/w_128,h_128,ar_1/f_auto/home.png", request.RequestUri!.AbsoluteUri);
        Assert.False(request.Headers.Contains("x-api-key"));
        Assert.Equal("PNG"u8.ToArray(), bytes);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{ "message": "Api key is required", "error": "Unauthorized", "statusCode": 401 }""", "Api key is required")]
    [InlineData(HttpStatusCode.Forbidden, """{ "message": "'You don't have access to this resource.'", "error": "Forbidden Request", "statusCode": 403 }""", "'You don't have access to this resource.'")]
    [InlineData(HttpStatusCode.NotFound, """{ "message": "Resource not found", "error": "Not Found", "statusCode": 404 }""", "Resource not found")]
    [InlineData(HttpStatusCode.BadGateway, "<html>Bad gateway</html>", "Bad Gateway")]
    public async Task A_Failure_Carries_Its_Status_And_What_The_Server_Said(HttpStatusCode status, string body, string said)
    {
        var (client, _) = Answering(body, status);

        var failure = await Assert.ThrowsAsync<StreamlineException>(() => client.Icon("ico_a", cancellation: TestContext.Current.CancellationToken));

        Assert.Equal(status, failure.Status);
        Assert.Equal(said, failure.Message);
        Assert.Null(failure.ResetsAt);
    }

    [Fact]
    public async Task A_Spent_Limit_Says_When_It_Lifts_From_Retry_After()
    {
        var lifts = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var stub = new Stub((HttpStatusCode)429, """{ "message": "Weekly download limit reached", "statusCode": 429 }""")
        {
            Headers = response => response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(lifts)
        };
        var client = new StreamlineClient(Key, new HttpClient(stub));

        var failure = await Assert.ThrowsAsync<StreamlineException>(() => client.DownloadSvg("ico_a", 48, cancellation: TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.TooManyRequests, failure.Status);
        Assert.Equal("Weekly download limit reached", failure.Message);
        Assert.Equal(lifts, failure.ResetsAt);
    }

    [Fact]
    public async Task A_Spent_Limit_Says_When_It_Lifts_From_A_Rate_Limit_Reset()
    {
        var stub = new Stub((HttpStatusCode)429, """{ "message": "Too Many Requests", "statusCode": 429 }""")
        {
            Headers = response => response.Headers.Add("X-RateLimit-Reset", "1790000000")
        };
        var client = new StreamlineClient(Key, new HttpClient(stub));

        var failure = await Assert.ThrowsAsync<StreamlineException>(() => client.Search("home", cancellation: TestContext.Current.CancellationToken));

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), failure.ResetsAt);
    }

    [Fact]
    public async Task A_Spent_Limit_Says_When_It_Lifts_From_A_Retry_After_In_Seconds()
    {
        var stub = new Stub((HttpStatusCode)429, """{ "message": "Too Many Requests", "statusCode": 429 }""")
        {
            Headers = response => response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120))
        };
        var client = new StreamlineClient(Key, new HttpClient(stub));
        var before = DateTimeOffset.UtcNow;

        var failure = await Assert.ThrowsAsync<StreamlineException>(() => client.Search("home", cancellation: TestContext.Current.CancellationToken));

        Assert.InRange(failure.ResetsAt!.Value, before.AddSeconds(120), DateTimeOffset.UtcNow.AddSeconds(120));
    }

    [Fact]
    public async Task A_Spent_Limit_Says_When_It_Lifts_From_A_Rate_Limit_Reset_Date()
    {
        var stub = new Stub((HttpStatusCode)429, """{ "message": "Too Many Requests", "statusCode": 429 }""")
        {
            Headers = response => response.Headers.Add("X-RateLimit-Reset", "Mon, 05 Oct 2026 00:00:00 GMT")
        };
        var client = new StreamlineClient(Key, new HttpClient(stub));

        var failure = await Assert.ThrowsAsync<StreamlineException>(() => client.Search("home", cancellation: TestContext.Current.CancellationToken));

        Assert.Equal(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero), failure.ResetsAt);
    }

    [Fact]
    public async Task A_Spent_Limit_With_Nothing_Said_About_When_Has_No_Date()
    {
        var (client, _) = Answering("""{ "message": "Too Many Requests", "statusCode": 429 }""", (HttpStatusCode)429);

        var failure = await Assert.ThrowsAsync<StreamlineException>(() => client.Search("home", cancellation: TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.TooManyRequests, failure.Status);
        Assert.Null(failure.ResetsAt);
    }
}
