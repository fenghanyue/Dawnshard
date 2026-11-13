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
using Microsoft.Extensions.Options;

namespace DragaliaAPI.Integration.Test.Features.Summoning;

public sealed class WaterGalaBannerTest : TestFixture, IDisposable
{
    private const int BannerId = 1020203;
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

    public WaterGalaBannerTest(CustomWebApplicationFactory factory, ITestOutputHelper outputHelper)
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
    public async Task List_AddsWaterGalaFor999DaysAndKeepsSeimeiPool()
    {
        SummonGetSummonListResponse result = (
            await this.Client.PostMsgpack<SummonGetSummonListResponse>(
                "summon/get_summon_list",
                cancellationToken: Token
            )
        ).Data;
        result.SummonList.Should().ContainSingle(x => x.SummonId == 1020116);
        SummonList banner = result
            .SummonList.Should()
            .ContainSingle(x => x.SummonId == BannerId)
            .Which;
        Banner preset = this.production.Banners.Single(x => x.Id == BannerId);
        banner.CommenceDate.Should().Be(preset.Start);
        banner.CompleteDate.Should().Be(preset.End);
        banner.ExchangeSummonPoint.Should().Be(300);
        (preset.End - preset.Start).Should().Be(TimeSpan.FromDays(999) - TimeSpan.FromSeconds(1));
        result.SummonList.Should().NotContain(x => x.SummonId >= 1020002 && x.SummonId <= 1020005);
    }

    [Fact]
    public async Task Odds_HasWaterDragonPickupAndNonPickupBurnResistantWands()
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
        AtgenUnitList[] pickupCharas = odds
            .Unit.CharaOddsList.Where(x => x.Pickup)
            .SelectMany(x => x.UnitList)
            .ToArray();
        pickupCharas
            .Select(x => x.Id)
            .Should()
            .BeEquivalentTo([(int)Charas.GalaMascula, (int)Charas.GalaElisanne]);
        pickupCharas.Should().OnlyContain(x => ParseRate(x.Rate) == 0.5m);
        AtgenUnitList dragon = odds
            .Unit.DragonOddsList.Where(x => x.Pickup)
            .SelectMany(x => x.UnitList)
            .Should()
            .ContainSingle()
            .Which;
        dragon.Id.Should().Be((int)DragonId.GalaBeastCiella);
        ParseRate(dragon.Rate).Should().Be(0.8m);
        odds.RarityList.Should()
            .ContainEquivalentOf(new AtgenRarityList { Rarity = 5, TotalRate = "6.00%" });
        int[] charas = odds
            .Unit.CharaOddsList.Where(x => !x.Pickup)
            .SelectMany(x => x.UnitList)
            .Where(x => ParseRate(x.Rate) > 0)
            .Select(x => x.Id)
            .ToArray();
        charas
            .Should()
            .Contain([
                (int)Charas.GalaEmile,
                (int)Charas.Yurius,
                (int)Charas.Pipple,
                (int)Charas.Lily,
                (int)Charas.GalaNedrick,
            ]);
        charas
            .Should()
            .NotContain([
                (int)Charas.Seimei,
                (int)Charas.Joker,
                (int)Charas.Origa,
                (int)Charas.ForagerCleo,
            ]);
        odds.Unit.DragonOddsList.SelectMany(x => x.UnitList)
            .Select(x => x.Id)
            .Should()
            .NotContain([(int)DragonId.GozuTenno, (int)DragonId.Arsene]);
    }

    [Theory]
    [InlineData(EntityTypes.Chara, (int)Charas.GalaMascula)]
    [InlineData(EntityTypes.Chara, (int)Charas.GalaElisanne)]
    [InlineData(EntityTypes.Dragon, (int)DragonId.GalaBeastCiella)]
    public async Task Exchange_GrantsEachPickupFor300Points(EntityTypes kind, int unitId)
    {
        SummonGetSummonPointTradeResponse trades = (
            await this.Client.PostMsgpack<SummonGetSummonPointTradeResponse>(
                "summon/get_summon_point_trade",
                new SummonGetSummonPointTradeRequest(BannerId),
                cancellationToken: Token
            )
        ).Data;
        trades.SummonPointTradeList.Should().HaveCount(3);
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
    public async Task Tenfold_ConsumesWyrmiteAndPreservesSeimeiPoints()
    {
        await this.ApiContext.PlayerUserData.ExecuteUpdateAsync(
            x => x.SetProperty(y => y.Crystal, 2000),
            Token
        );
        await this.AddToDatabase(
            new DbPlayerBannerData { SummonBannerId = 1020116, SummonPoints = 20 }
        );
        SummonRequestResponse result = (
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
        result.ResultUnitList.Should().HaveCount(10);
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
                x => x.SummonBannerId == 1020116,
                Token
            )
        )
            .SummonPoints.Should()
            .Be(20);
    }

    [Fact]
    public void ReleaseCutoffs_AdvanceVouchersAndKeepEachPoolAtItsOriginalDate()
    {
        SummonBannerOptions options = this
            .application.Services.GetRequiredService<IOptionsMonitor<SummonBannerOptions>>()
            .CurrentValue;
        DateTimeOffset now = this.production.Banners.Single(x => x.Id == BannerId).Start.AddDays(1);
        DateTimeOffset waterDate = new(2022, 8, 29, 6, 0, 0, TimeSpan.Zero);
        options.GetPoolReleaseCutoff(options.BannerDict[BannerId], now).Should().Be(waterDate);
        options
            .GetPoolReleaseCutoff(options.BannerDict[1020116], now)
            .Should()
            .Be(new DateTimeOffset(2020, 12, 31, 6, 0, 0, TimeSpan.Zero));
        options
            .GetPoolReleaseCutoff(
                options.BannerDict[SummonConstants.AdventurerSummonPlusBannerId],
                now
            )
            .Should()
            .Be(waterDate);
    }

    private static decimal ParseRate(string rate) =>
        decimal.Parse(rate.TrimEnd('%'), CultureInfo.InvariantCulture);
}
