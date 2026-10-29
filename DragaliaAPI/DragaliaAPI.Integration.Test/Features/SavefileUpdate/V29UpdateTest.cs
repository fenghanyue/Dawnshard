using DragaliaAPI.Database.Entities;
using DragaliaAPI.Database.Utils;
using DragaliaAPI.Shared.MasterAsset.Models.Missions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DragaliaAPI.Integration.Test.Features.SavefileUpdate;

public class V29UpdateTest : SavefileUpdateTestFixture
{
    protected override int StartingVersion => 28;

    public V29UpdateTest(CustomWebApplicationFactory factory, ITestOutputHelper outputHelper)
        : base(factory, outputHelper) { }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(20458, 0)]
    [InlineData(20845, 20845)]
    [InlineData(0, 0)]
    public async Task Load_RetiresTrialAndPreservesProgress(int selected, int expected)
    {
        DbPlayer otherPlayer = new() { AccountId = $"withdrawal-other-{Guid.NewGuid()}" };
        this.ApiContext.Players.Add(otherPlayer);
        await this.ApiContext.SaveChangesAsync(Token);
        await this.AddRangeToDatabase([
            new DbPlayerMission
            {
                ViewerId = otherPlayer.ViewerId,
                Id = 12080101,
                Type = MissionType.MemoryEvent,
                GroupId = 20458,
                State = MissionState.Completed,
                Progress = 1,
                End = DateTimeOffset.MaxValue,
            },
        ]);
        await this.ApiContext.PlayerUserData.ExecuteUpdateAsync(
            x => x.SetProperty(y => y.ActiveMemoryEventId, selected),
            Token
        );
        int crystal = await this
            .ApiContext.PlayerUserData.Select(x => x.Crystal)
            .SingleAsync(Token);
        await this.AddRangeToDatabase([
            new DbPlayerMission
            {
                Id = 12080101,
                Type = MissionType.MemoryEvent,
                GroupId = 20458,
                State = MissionState.Claimed,
                Progress = 1,
            },
            new DbPlayerMission
            {
                Id = 11590101,
                Type = MissionType.Daily,
                GroupId = 20458,
                State = MissionState.Completed,
                Progress = 3,
                End = DateTimeOffset.MaxValue,
            },
            new DbPlayerMission
            {
                Id = 12080101,
                Type = MissionType.Period,
                GroupId = 20458,
                State = MissionState.InProgress,
                Progress = 0,
                End = DateTimeOffset.MaxValue,
            },
            new DbPlayerMission
            {
                Id = 10220101,
                Type = MissionType.MemoryEvent,
                GroupId = 20845,
                State = MissionState.Completed,
                Progress = 1,
            },
            new DbQuest
            {
                QuestId = 204580101,
                State = 3,
                PlayCount = 7,
            },
        ]);

        await this.LoadIndex();
        await this.LoadIndex();

        DbPlayerUserData user = await this.ApiContext.PlayerUserData.SingleAsync(Token);
        user.ActiveMemoryEventId.Should().Be(expected);
        user.Crystal.Should().Be(crystal);
        this.GetSavefileVersion().Should().Be(29);
        List<DbPlayerMission> trial = await this
            .ApiContext.PlayerMissions.Where(x => x.ViewerId == this.ViewerId && x.GroupId == 20458)
            .OrderBy(x => x.Type)
            .ToListAsync(Token);
        trial.Should().HaveCount(3);
        trial.Select(x => x.Progress).Should().Equal(3, 0, 1);
        trial
            .Select(x => x.State)
            .Should()
            .Equal(MissionState.Completed, MissionState.InProgress, MissionState.Claimed);
        trial
            .Should()
            .OnlyContain(x =>
                x.End != DateTimeOffset.UnixEpoch
                && x.End <= this.Services.GetRequiredService<TimeProvider>().GetUtcNow()
            );
        DbQuest quest = await this.ApiContext.PlayerQuests.SingleAsync(
            x => x.QuestId == 204580101,
            Token
        );
        quest.State.Should().Be(3);
        quest.PlayCount.Should().Be(7);
        (
            await this.ApiContext.PlayerMissions.SingleAsync(
                x => x.ViewerId == this.ViewerId && x.GroupId == 20845,
                Token
            )
        )
            .End.Should()
            .Be(DateTimeOffset.UnixEpoch);
        (
            await this.ApiContext.PlayerMissions.SingleAsync(
                x => x.ViewerId == otherPlayer.ViewerId,
                Token
            )
        )
            .End.Should()
            .Be(DateTimeOffset.MaxValue);
    }
}
