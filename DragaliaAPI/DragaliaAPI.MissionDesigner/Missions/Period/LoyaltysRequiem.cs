using DragaliaAPI.MissionDesigner.Models;
using DragaliaAPI.MissionDesigner.Models.Attributes;
using DragaliaAPI.MissionDesigner.Models.EventMission;
using DragaliaAPI.MissionDesigner.Models.RegularMission;

namespace DragaliaAPI.MissionDesigner.Missions.Period;

// Original 20458 task descriptions and rewards are retained, including the daily wrap-up goals.
[ContainsMissionList]
public static class LoyaltysRequiem
{
    private const int EventId = 20458;
    private const int DailyGoalsGroup = 11590501;
    private const int CompletedDaysGroup = 12080401;

    [MissionType(MissionType.Period)]
    [EventId(EventId)]
    public static List<Mission> PeriodMissions { get; } = CreateEventMissions();

    [MissionType(MissionType.MemoryEvent)]
    [EventId(EventId)]
    public static List<Mission> MemoryMissions { get; } = CreateEventMissions();

    private static List<Mission> CreateEventMissions() =>
        [
            new EventParticipationMission() { MissionId = 12080101 },
            new EventParticipationMission() { MissionId = 12080201 },
            new DragonEarnedMission() { MissionId = 12080301, Dragon = DragonId.Pele },
            new ClearProgressionGroupMission()
            {
                MissionId = 12080401,
                ProgressionGroupToClear = CompletedDaysGroup,
            },
            new ClearProgressionGroupMission()
            {
                MissionId = 12080402,
                ProgressionGroupToClear = CompletedDaysGroup,
            },
            new ClearProgressionGroupMission()
            {
                MissionId = 12080403,
                ProgressionGroupToClear = CompletedDaysGroup,
            },
            new EventRegularBattleClearMission() { MissionId = 12080501 },
            new EventRegularBattleClearMission() { MissionId = 12080502 },
            new EventRegularBattleClearMission() { MissionId = 12080503 },
            new EventRegularBattleClearMission() { MissionId = 12080504 },
            new EventRegularBattleClearMission() { MissionId = 12080505 },
            new ClearRaidMission() { MissionId = 12080601 },
            new ClearRaidMission() { MissionId = 12080602 },
            new ClearRaidMission() { MissionId = 12080603 },
            new ClearRaidMission() { MissionId = 12080604 },
            new ClearRaidMission() { MissionId = 12080605 },
            new ClearRaidMission() { MissionId = 12080606 },
            new ClearRaidMission() { MissionId = 12080607 },
            new ClearRaidMission() { MissionId = 12080608 },
            new ClearRaidMission() { MissionId = 12080609 },
            new ClearRaidMission() { MissionId = 12080610 },
            new ClearRaidMission() { MissionId = 12080611 },
            new ClearQuestMission() { MissionId = 12080701, QuestId = 204580501 },
            new ClearQuestMission() { MissionId = 12080801, QuestId = 204580602 },
            new ClearQuestMission() { MissionId = 12080901, QuestId = 204580603 },
        ];

    [MissionType(MissionType.Daily)]
    [EventId(EventId)]
    public static List<Mission> DailyMissions { get; } =
    [
        new ClearQuestMission()
        {
            MissionId = 11590101,
            QuestGroupId = EventId,
            ProgressionGroupId = DailyGoalsGroup,
        },
        new ClearRaidMission() { MissionId = 11590201, ProgressionGroupId = DailyGoalsGroup },
        new EventRegularBattleClearMission()
        {
            MissionId = 11590301,
            ProgressionGroupId = DailyGoalsGroup,
        },
        new EventItemCollectionMission() { MissionId = 11590401, ItemId = 2045801 },
        new EventItemCollectionMission() { MissionId = 11590402, ItemId = 2045801 },
        new EventItemCollectionMission() { MissionId = 11590403, ItemId = 2045801 },
        new EventItemCollectionMission() { MissionId = 11590404, ItemId = 2045801 },
        new EventItemCollectionMission()
        {
            MissionId = 11590405,
            ItemId = 2045801,
            ProgressionGroupId = DailyGoalsGroup,
        },
        new ClearProgressionGroupMission()
        {
            MissionId = 11590501,
            ProgressionGroupToClear = DailyGoalsGroup,
            ProgressionGroupId = CompletedDaysGroup,
        },
    ];
}
