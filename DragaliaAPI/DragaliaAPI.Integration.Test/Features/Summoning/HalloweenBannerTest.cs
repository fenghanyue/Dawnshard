using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DragaliaAPI.Features.Summoning;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DragaliaAPI.Integration.Test.Features.Summoning;

public class HalloweenBannerTest(
    CustomWebApplicationFactory factory,
    ITestOutputHelper outputHelper
) : TestFixture(factory, outputHelper)
{
    private const int BannerId = 1020003;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters =
        {
            new JsonStringEnumConverter<Charas>(),
            new JsonStringEnumConverter<DragonId>(),
        },
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private HttpClient CreateBannerClient(DateTimeOffset start, DateTimeOffset end)
    {
        JsonObject preset = JsonNode
            .Parse(
                File.ReadAllText(
                    Path.Combine(AppContext.BaseDirectory, "Data", "halloweenBanner.json")
                )
            )!
            .AsObject();
        preset["Start"] = start;
        preset["End"] = end;
        Banner banner = preset.Deserialize<Banner>(SerializerOptions)!;
        return this.CreateClient(builder =>
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
    }

    [Fact]
    public async Task Odds_UsesPreparedRosterAndExcludesLaterUnits()
    {
        using HttpClient client = this.CreateBannerClient(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(13)
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
        charas
            .Should()
            .Contain([
                (int)Charas.HalloweenElisanne,
                (int)Charas.HalloweenAlthemia,
                (int)Charas.HalloweenEdward,
            ]);
        dragons.Should().Contain((int)DragonId.HalloweenSilke);
        charas.Should().Contain((int)Charas.Lily).And.Contain((int)Charas.Mikoto);
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
            .HaveCount(3);
        odds.Unit.DragonOddsList.Where(x => x.Pickup)
            .SelectMany(x => x.UnitList)
            .Should()
            .ContainSingle();
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
