using DragaliaAPI.Features.Summoning;
using DragaliaAPI.Shared.Definitions.Enums;

namespace DragaliaAPI.Test.Features.Summon;

public class SummonBannerOptionsTest
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReleaseHistory_HasOriginalDates()
    {
        ReleaseHistory
            .CharaReleaseDates[Charas.Lily]
            .Should()
            .Be(new DateTimeOffset(2018, 10, 4, 6, 0, 0, TimeSpan.Zero));
        ReleaseHistory
            .DragonReleaseDates[DragonId.Cerberus]
            .Should()
            .Be(new DateTimeOffset(2018, 10, 31, 6, 0, 0, TimeSpan.Zero));
        ReleaseHistory
            .BannerStartDates[1020002]
            .Should()
            .Be(new DateTimeOffset(2018, 10, 4, 6, 0, 0, TimeSpan.Zero));

        // Never on rate up
        ReleaseHistory.CharaReleaseDates.Should().NotContainKey(Charas.Mikoto);
    }

    [Fact]
    public void GetPoolReleaseCutoff_FilterOff_ReturnsNull()
    {
        SummonBannerOptions options = CreateOptions(
            filterPoolByReleaseDate: false,
            CreateBanner(1020002, started: true)
        );

        options.GetPoolReleaseCutoff(options.BannerDict[1020002], Now).Should().BeNull();
        options.GetPoolReleaseCutoff(SystemBanners.AdventurerSummonBanner, Now).Should().BeNull();
    }

    [Fact]
    public void GetPoolReleaseCutoff_ConfiguredBanner_ReturnsOriginalStart()
    {
        SummonBannerOptions options = CreateOptions(
            filterPoolByReleaseDate: true,
            CreateBanner(1020002, started: true),
            CreateBanner(1020004, started: false)
        );

        options
            .GetPoolReleaseCutoff(options.BannerDict[1020002], Now)
            .Should()
            .Be(new DateTimeOffset(2018, 10, 4, 6, 0, 0, TimeSpan.Zero));
        options
            .GetPoolReleaseCutoff(options.BannerDict[1020004], Now)
            .Should()
            .Be(new DateTimeOffset(2018, 10, 31, 6, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void GetPoolReleaseCutoff_SystemBanner_ReturnsLatestOriginalStartOfStartedBanners()
    {
        SummonBannerOptions options = CreateOptions(
            filterPoolByReleaseDate: true,
            CreateBanner(1020003, started: true),
            CreateBanner(1020002, started: true),
            CreateBanner(1020005, started: false)
        );

        DateTimeOffset expected = new(2018, 10, 17, 6, 0, 0, TimeSpan.Zero);

        options
            .GetPoolReleaseCutoff(SystemBanners.AdventurerSummonBanner, Now)
            .Should()
            .Be(expected);
        options
            .GetPoolReleaseCutoff(SystemBanners.DragonSummonPlusBanner, Now)
            .Should()
            .Be(expected);
        options.GetPoolReleaseCutoff(SystemBanners.RerollBanner, Now).Should().Be(expected);
    }

    [Fact]
    public void GetPoolReleaseCutoff_BannerNotInOriginalGame_ReturnsLatestOriginalStartOfStartedBanners()
    {
        SummonBannerOptions options = CreateOptions(
            filterPoolByReleaseDate: true,
            CreateBanner(1020002, started: true),
            CreateBanner(1, started: true)
        );

        options
            .GetPoolReleaseCutoff(options.BannerDict[1], Now)
            .Should()
            .Be(new DateTimeOffset(2018, 10, 4, 6, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void GetPoolReleaseCutoff_NoBannerStarted_ReturnsNull()
    {
        SummonBannerOptions options = CreateOptions(
            filterPoolByReleaseDate: true,
            CreateBanner(1020002, started: false)
        );

        options.GetPoolReleaseCutoff(SystemBanners.DragonSummonBanner, Now).Should().BeNull();
    }

    private static Banner CreateBanner(int id, bool started) =>
        new()
        {
            Id = id,
            Start = started ? Now.AddDays(-1) : Now.AddDays(1),
            End = Now.AddDays(14),
        };

    private static SummonBannerOptions CreateOptions(
        bool filterPoolByReleaseDate,
        params Banner[] banners
    )
    {
        SummonBannerOptions options = new()
        {
            Banners = banners,
            FilterPoolByReleaseDate = filterPoolByReleaseDate,
        };
        options.PostConfigure();

        return options;
    }
}
