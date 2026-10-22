using DragaliaAPI.Database.Entities;
using DragaliaAPI.Features.Shared.Options;
using DragaliaAPI.Infrastructure.Results;
using DragaliaAPI.Shared.MasterAsset.Models.Missions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DragaliaAPI.Integration.Test.Features.Event;

public class EventScheduleTest : TestFixture
{
    private readonly FakeTimeProvider timeProvider = new(
        new DateTimeOffset(2026, 10, 18, 6, 0, 0, TimeSpan.Zero)
    );

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public EventScheduleTest(CustomWebApplicationFactory factory, ITestOutputHelper outputHelper)
        : base(factory, outputHelper)
    {
        this.Client.Dispose();
        this.Client = this.CreateClient(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(this.timeProvider);
                services.Configure<EventOptions>(options =>
                {
                    options.EventList.Clear();
                    options.EventList.AddRange([
                        new()
                        {
                            Id = 20401,
                            Start = new(2026, 10, 18, 6, 0, 0, TimeSpan.Zero),
                            End = new(2026, 10, 21, 15, 59, 59, TimeSpan.Zero),
                        },
                        new()
                        {
                            Id = 20801,
                            Start = new(2026, 10, 21, 16, 0, 0, TimeSpan.Zero),
                            End = new(2026, 10, 30, 15, 59, 59, TimeSpan.Zero),
                        },
                    ]);
                });
            })
        );
    }

    [Theory]
    [InlineData("2026-10-21T15:59:59Z", false)]
    [InlineData("2026-10-21T16:00:00Z", true)]
    [InlineData("2026-10-22T00:55:00Z", true)]
    [InlineData("2026-10-30T15:59:58Z", true)]
    [InlineData("2026-10-30T15:59:59Z", false)]
    [InlineData("2026-10-30T16:00:00Z", false)]
    public async Task BuildEvent_UsesCurrentTimeForConfiguredPeriod(string now, bool active)
    {
        this.timeProvider.SetUtcNow(DateTimeOffset.Parse(now));
        DragaliaResponse<ResultCodeResponse> response =
            await this.Client.PostMsgpack<ResultCodeResponse>(
                "build_event/get_event_data",
                new BuildEventGetEventDataRequest(20801),
                ensureSuccessHeader: false,
                cancellationToken: Token
            );
        response
            .DataHeaders.ResultCode.Should()
            .Be(active ? ResultCode.Success : ResultCode.EventOutThePeriod);
    }

    [Fact]
    public async Task BuildEvent_EntryWorksBeforeNextDailyReset()
    {
        this.timeProvider.SetUtcNow(new(2026, 10, 22, 0, 55, 0, TimeSpan.Zero));
        BuildEventEntryResponse response = (
            await this.Client.PostMsgpack<BuildEventEntryResponse>(
                "build_event/entry",
                new BuildEventEntryRequest(20801),
                cancellationToken: Token
            )
        ).Data;
        response.BuildEventUserData.Should().NotBeNull();
        response.BuildEventUserData.UserBuildEventItemList.Should().HaveCount(3);
    }

    [Fact]
    public async Task OldRaidEvent_ClosesBeforeNextDailyReset()
    {
        this.timeProvider.SetUtcNow(new(2026, 10, 22, 0, 55, 0, TimeSpan.Zero));
        DragaliaResponse<ResultCodeResponse> response =
            await this.Client.PostMsgpack<ResultCodeResponse>(
                "raid_event/get_event_data",
                new RaidEventGetEventDataRequest(20401),
                ensureSuccessHeader: false,
                cancellationToken: Token
            );
        response.DataHeaders.ResultCode.Should().Be(ResultCode.EventOutThePeriod);
    }

    [Fact]
    public async Task Login_ResetsDailyMissionsOnlyForCurrentlyActiveEvents()
    {
        this.timeProvider.SetUtcNow(new(2026, 10, 22, 0, 55, 0, TimeSpan.Zero));
        await this.AddToDatabase(
            new DbPlayerEventData() { EventId = 20401 },
            new DbPlayerEventData() { EventId = 20801 }
        );

        await this.Client.PostMsgpack<LoginIndexResponse>(
            "login/index",
            new LoginIndexRequest(),
            cancellationToken: Token
        );

        List<DbPlayerMission> eventDailies = await this
            .ApiContext.PlayerMissions.Where(x =>
                x.ViewerId == this.ViewerId
                && x.Type == MissionType.Daily
                && (x.GroupId == 20401 || x.GroupId == 20801)
            )
            .ToListAsync(Token);
        eventDailies.Should().HaveCount(3).And.OnlyContain(x => x.GroupId == 20801);
        eventDailies
            .Should()
            .OnlyContain(x =>
                x.Start == new DateTimeOffset(2026, 10, 21, 6, 0, 0, TimeSpan.Zero)
                && x.End == new DateTimeOffset(2026, 10, 22, 6, 0, 0, TimeSpan.Zero)
            );
    }
}
