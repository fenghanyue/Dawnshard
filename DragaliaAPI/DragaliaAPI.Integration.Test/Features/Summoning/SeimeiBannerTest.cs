using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DragaliaAPI.Database.Entities;
using DragaliaAPI.Features.Summoning;
using DragaliaAPI.Shared.Definitions.Enums.Summon;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DragaliaAPI.Integration.Test.Features.Summoning;

public sealed class SeimeiBannerTest : TestFixture, IDisposable
{
    private const int BannerId = 1020116;
    private readonly WebApplicationFactory<Program> application;
    private readonly SummonBannerOptions production;
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters =
        {
            new JsonStringEnumConverter<Charas>(),
            new JsonStringEnumConverter<DragonId>(),
        },
    };

    public SeimeiBannerTest(CustomWebApplicationFactory factory, ITestOutputHelper outputHelper)
        : base(factory, outputHelper)
    {
        JsonObject preset = JsonNode
            .Parse(
                File.ReadAllText(
                    Path.Combine(AppContext.BaseDirectory, "Data", "tencentBanners.json")
                )
            )!
            .AsObject();
        this.production = preset["SummonBannerOptions"]!.Deserialize<SummonBannerOptions>(
            SerializerOptions
        )!;
        this.application = factory.WithWebHostBuilder(builder =>
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
                    banners.AddRange(this.production.Banners);
                })
            );
        });
        using HttpClient original = this.Client;
        this.Client = this.application.CreateClient(new() { BaseAddress = original.BaseAddress! });
        foreach ((string name, IEnumerable<string> values) in original.DefaultRequestHeaders)
        {
            this.Client.DefaultRequestHeaders.TryAddWithoutValidation(name, values);
        }
    }

    void IDisposable.Dispose()
    {
        this.application.Dispose();
        base.Dispose();
    }

    [Fact]
    public async Task List_ClosesOldPoolAndShowsNewPoolWith300PointExchange()
    {
        SummonGetSummonListResponse result = (
            await this.Client.PostMsgpack<SummonGetSummonListResponse>(
                "summon/get_summon_list",
                cancellationToken: Token
            )
        ).Data;
        result.SummonList.Should().NotContain(x => x.SummonId >= 1020002 && x.SummonId <= 1020005);
        SummonList banner = result
            .SummonList.Should()
            .ContainSingle(x => x.SummonId == BannerId)
            .Which;
        banner.ExchangeSummonPoint.Should().Be(300);
        Banner preset = this.production.Banners.Single(x => x.Id == BannerId);
        banner.CommenceDate.Should().Be(preset.Start);
        banner.CompleteDate.Should().Be(preset.End);
        (preset.End - preset.Start).Should().Be(TimeSpan.FromDays(999) - TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Odds_HasBothLimitedPickupsAndExcludesLaterAndGalaUnits()
    {
        OddsRate odds = (
            await this.Client.PostMsgpack<SummonGetOddsDataResponse>(
                "summon/get_odds_data",
                new SummonGetOddsDataRequest(BannerId),
                cancellationToken: Token
            )
        )
            .Data
            .OddsRateList
            .Normal;
        AtgenUnitList chara = odds
            .Unit.CharaOddsList.Where(x => x.Pickup)
            .SelectMany(x => x.UnitList)
            .Should()
            .ContainSingle()
            .Which;
        AtgenUnitList dragon = odds
            .Unit.DragonOddsList.Where(x => x.Pickup)
            .SelectMany(x => x.UnitList)
            .Should()
            .ContainSingle()
            .Which;
        chara.Id.Should().Be((int)Charas.Seimei);
        dragon.Id.Should().Be((int)DragonId.GozuTenno);
        decimal.Parse(chara.Rate.TrimEnd('%'), CultureInfo.InvariantCulture).Should().Be(0.5m);
        decimal.Parse(dragon.Rate.TrimEnd('%'), CultureInfo.InvariantCulture).Should().Be(0.8m);
        odds.RarityList.Should()
            .ContainEquivalentOf(new AtgenRarityList { Rarity = 5, TotalRate = "4.00%" });
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
            .Contain((int)Charas.Nadine)
            .And.NotContain([
                (int)Charas.KimonoElisanne,
                (int)Charas.KimonoLuca,
                (int)Charas.Joker,
                (int)Charas.GalaLeonidas,
                (int)Charas.Nobunaga,
            ]);
        dragons
            .Should()
            .Contain((int)DragonId.Kagutsuchi)
            .And.NotContain([(int)DragonId.GalaMars, (int)DragonId.GalaRebornAgni]);
    }

    [Theory]
    [InlineData(EntityTypes.Chara, (int)Charas.Seimei)]
    [InlineData(EntityTypes.Dragon, (int)DragonId.GozuTenno)]
    public async Task Exchange_GrantsSelectedUnitAndDeducts300Points(EntityTypes kind, int unitId)
    {
        SummonGetSummonPointTradeResponse trades = (
            await this.Client.PostMsgpack<SummonGetSummonPointTradeResponse>(
                "summon/get_summon_point_trade",
                new SummonGetSummonPointTradeRequest(BannerId),
                cancellationToken: Token
            )
        ).Data;
        trades.SummonPointTradeList.Should().HaveCount(2);
        int tradeId = trades
            .SummonPointTradeList.Single(x => x.EntityType == kind && x.EntityId == unitId)
            .TradeId;
        await this
            .ApiContext.PlayerBannerData.Where(x => x.SummonBannerId == BannerId)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.SummonPoints, 300), Token);
        SummonSummonPointTradeResponse exchange = (
            await this.Client.PostMsgpack<SummonSummonPointTradeResponse>(
                "summon/summon_point_trade",
                new SummonSummonPointTradeRequest(BannerId, tradeId),
                cancellationToken: Token
            )
        ).Data;
        exchange
            .ExchangeEntityList.Should()
            .Contain(x => x.EntityType == kind && x.EntityId == unitId && x.EntityQuantity == 1);
        (
            await this.ApiContext.PlayerBannerData.SingleAsync(
                x => x.SummonBannerId == BannerId,
                Token
            )
        )
            .SummonPoints.Should()
            .Be(0);
    }

    [Fact]
    public async Task Tenfold_ConsumesWyrmiteAndPreservesOldBannerPoints()
    {
        await this.ApiContext.PlayerUserData.ExecuteUpdateAsync(
            x => x.SetProperty(y => y.Crystal, 2000),
            Token
        );
        await this.AddToDatabase(
            new DbPlayerBannerData { SummonBannerId = 1020005, SummonPoints = 20 }
        );
        SummonRequestResponse response = (
            await this.Client.PostMsgpack<SummonRequestResponse>(
                "summon/request",
                new SummonRequestRequest(
                    BannerId,
                    SummonExecTypes.Tenfold,
                    0,
                    PaymentTypes.Wyrmite,
                    new(2000, 1200)
                ),
                cancellationToken: Token
            )
        ).Data;
        response.ResultUnitList.Should().HaveCount(10);
        (await this.ApiContext.PlayerUserData.Select(x => x.Crystal).SingleAsync(Token))
            .Should()
            .Be(800);
        (
            await this.ApiContext.PlayerBannerData.SingleAsync(
                x => x.SummonBannerId == BannerId,
                Token
            )
        )
            .SummonPoints.Should()
            .Be(10);
        (
            await this.ApiContext.PlayerBannerData.SingleAsync(
                x => x.SummonBannerId == 1020005,
                Token
            )
        )
            .SummonPoints.Should()
            .Be(20);
    }

    [Theory]
    [InlineData(-1, true, false)]
    [InlineData(0, false, false)]
    [InlineData(1, false, true)]
    public void Dates_SwitchBothPoolsTogether(int seconds, bool oldActive, bool newActive)
    {
        Banner current = this.production.Banners.Single(x => x.Id == BannerId);
        Banner previous = this.production.Banners.Single(x => x.Id == 1020005);
        previous.End.Should().Be(current.Start);
        FakeTimeProvider clock = new(current.Start.AddSeconds(seconds));
        previous.GetIsCurrentlyActive(clock).Should().Be(oldActive);
        current.GetIsCurrentlyActive(clock).Should().Be(newActive);
    }
}
