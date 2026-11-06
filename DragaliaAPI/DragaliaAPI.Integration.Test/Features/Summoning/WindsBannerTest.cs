using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DragaliaAPI.Features.Summoning;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DragaliaAPI.Integration.Test.Features.Summoning;

public sealed class WindsBannerTest : TestFixture, IDisposable
{
    private readonly CustomWebApplicationFactory factory;

    public WindsBannerTest(CustomWebApplicationFactory factory, ITestOutputHelper outputHelper)
        : base(factory, outputHelper)
    {
        this.factory = factory;
    }

    private const int BannerId = 1020005;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters =
        {
            new JsonStringEnumConverter<Charas>(),
            new JsonStringEnumConverter<DragonId>(),
        },
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private WebApplicationFactory<Program>? application;

    void IDisposable.Dispose()
    {
        this.application?.Dispose();
        base.Dispose();
    }

    private HttpClient CreateBannerClient(DateTimeOffset start, DateTimeOffset end)
    {
        JsonObject preset = JsonNode
            .Parse(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "windsBanner.json"))
            )!
            .AsObject();
        preset["Start"] = start;
        preset["End"] = end;
        Banner banner = preset.Deserialize<Banner>(SerializerOptions)!;
        this.application = this.factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration(config =>
                config.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["SummonBannerOptions:FilterPoolByReleaseDate"] = "true",
                    }
                )
            );
            builder.ConfigureTestServices(services =>
                services.Configure<SummonBannerOptions>(options =>
                {
                    List<Banner> banners = (List<Banner>)options.Banners;
                    banners.Clear();
                    banners.Add(banner);
                })
            );
        });
        HttpClient client = this.application.CreateClient(
            new() { BaseAddress = this.Client.BaseAddress! }
        );
        foreach ((string name, IEnumerable<string> values) in this.Client.DefaultRequestHeaders)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(name, values);
        }

        return client;
    }

    [Fact]
    public async Task Odds_UsesPreparedRosterAndExcludesLaterUnits()
    {
        using HttpClient client = this.CreateBannerClient(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(998)
        );
        OddsRate odds = (
            await client.PostMsgpack<SummonGetOddsDataResponse>(
                "summon/get_odds_data",
                new SummonGetOddsDataRequest(BannerId),
                cancellationToken: Token
            )
        )
            .Data
            .OddsRateList
            .Normal;
        int[] charas = odds
            .Unit.CharaOddsList.SelectMany(x => x.UnitList)
            .Select(x => x.Id)
            .ToArray();
        int[] dragons = odds
            .Unit.DragonOddsList.SelectMany(x => x.UnitList)
            .Select(x => x.Id)
            .ToArray();
        charas.Should().Contain((int)Charas.Louise);
        dragons.Should().Contain((int)DragonId.Garuda);
        charas.Should().Contain((int)Charas.Naveed).And.Contain((int)Charas.Lily);
        dragons.Should().Contain((int)DragonId.Cerberus);
        charas
            .Should()
            .NotContain([(int)Charas.Lucretia, (int)Charas.HalloweenMym, (int)Charas.Joker]);
        dragons
            .Should()
            .NotContain([
                (int)DragonId.HalloweenMaritimus,
                (int)DragonId.Arsene,
                (int)DragonId.GalaThor,
            ]);
        odds.RarityList.Should()
            .ContainEquivalentOf(new AtgenRarityList() { Rarity = 5, TotalRate = "4.00%" });
        odds.Unit.CharaOddsList.Where(x => x.Pickup)
            .SelectMany(x => x.UnitList)
            .Should()
            .ContainSingle()
            .Which.Id.Should()
            .Be((int)Charas.Louise);
        odds.Unit.DragonOddsList.Where(x => x.Pickup)
            .SelectMany(x => x.UnitList)
            .Should()
            .ContainSingle()
            .Which.Id.Should()
            .Be((int)DragonId.Garuda);
    }

    [Theory]
    [InlineData(-1, 13, true)]
    [InlineData(1, 15, false)]
    [InlineData(-15, -1, false)]
    public async Task List_UsesPreparedDatesAndOnlyShowsActiveBanner(
        int startDays,
        int endDays,
        bool active
    )
    {
        DateTimeOffset start = DateTimeOffset.UtcNow.AddDays(startDays);
        DateTimeOffset end = DateTimeOffset.UtcNow.AddDays(endDays);
        using HttpClient client = this.CreateBannerClient(start, end);
        SummonGetSummonListResponse response = (
            await client.PostMsgpack<SummonGetSummonListResponse>(
                "summon/get_summon_list",
                cancellationToken: Token
            )
        ).Data;
        if (active)
        {
            SummonList banner = response
                .SummonList.Should()
                .ContainSingle(x => x.SummonId == BannerId)
                .Which;
            banner.CommenceDate.Should().BeCloseTo(start, TimeSpan.FromSeconds(1));
            banner.CompleteDate.Should().BeCloseTo(end, TimeSpan.FromSeconds(1));
        }
        else
        {
            response.SummonList.Should().NotContain(x => x.SummonId == BannerId);
        }
    }
}
