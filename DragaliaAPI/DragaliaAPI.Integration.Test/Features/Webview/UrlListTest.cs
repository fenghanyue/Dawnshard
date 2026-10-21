namespace DragaliaAPI.Integration.Test.Features.Webview;

public class UrlListTest : TestFixture
{
    public UrlListTest(CustomWebApplicationFactory factory, ITestOutputHelper outputHelper)
        : base(factory, outputHelper) { }

    [Fact]
    public async Task UrlList_ReturnsList()
    {
        WebviewVersionUrlListResponse response = (
            await this.Client.PostMsgpack<WebviewVersionUrlListResponse>(
                "webview_version/url_list",
                new WebviewVersionUrlListRequest("region"),
                cancellationToken: TestContext.Current.CancellationToken
            )
        ).Data;

        response.WebviewUrlList.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("http://localhost:8080")]
    [InlineData("https://localhost:8443")]
    public async Task UrlList_BossGuidesPreserveOriginAndServeHtml(string origin)
    {
        this.Client.BaseAddress = new($"{origin}/2.19.0_20220714193707/");

        WebviewVersionUrlListResponse response = (
            await this.Client.PostMsgpack<WebviewVersionUrlListResponse>(
                "webview_version/url_list",
                new WebviewVersionUrlListRequest("CN"),
                cancellationToken: TestContext.Current.CancellationToken
            )
        ).Data;

        foreach (string function in new[] { "dragon_battle_info", "quest_info" })
        {
            string url = response.WebviewUrlList.Single(x => x.FunctionName == function).Url;
            url.Should().Be($"{origin}/webview/boss-guide/index.html");

            using HttpResponseMessage page = await this.Client.GetAsync(
                $"{url}?id=21001&lang=zh_cn",
                TestContext.Current.CancellationToken
            );

            page.EnsureSuccessStatusCode();
            page.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
            string html = await page.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken
            );
            html.Should().Contain("真耶梦加得").And.Contain("关键技能与应对");
        }
    }

    [Theory]
    [InlineData("guide.css", "text/css")]
    [InlineData("guide.js", "text/javascript")]
    public async Task BossGuide_StaticDependenciesAreReachable(string file, string contentType)
    {
        using HttpResponseMessage response = await this.Client.GetAsync(
            $"/webview/boss-guide/{file}",
            TestContext.Current.CancellationToken
        );

        response.EnsureSuccessStatusCode();
        response.Content.Headers.ContentType!.MediaType.Should().Be(contentType);
    }
}
