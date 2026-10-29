using DragaliaAPI.MissionDesigner.Models;
using DragaliaAPI.MissionDesigner.Models.Attributes;
using DragaliaAPI.MissionDesigner.Models.EventMission;
using DragaliaAPI.MissionDesigner.Models.RegularMission;

namespace DragaliaAPI.MissionDesigner.Missions.Period;

[ContainsMissionList]
public static class KindnessAndCaptivity
{
    private const int EventId = 20459;
    private const int DailyGoalsGroup = 11600501;
    private const int CompletedDaysGroup = 12090401;

    [MissionType(MissionType.Period)]
    [EventId(EventId)]
    public static List<Mission> PeriodMissions { get; } =
    [
        // These are separate original rewards (wyrmite and Better Together), not two event parts.
        new EventParticipationMission() { MissionId = 12090101 },
        new EventParticipationMission() { MissionId = 12090102 },
        new DragonEarnedMission() { MissionId = 12090201, Dragon = DragonId.Sylvia },
        new ClearProgressionGroupMission()
        {
            MissionId = 12090401,
            ProgressionGroupToClear = CompletedDaysGroup,
        },
        new ClearProgressionGroupMission()
        {
            MissionId = 12090402,
            ProgressionGroupToClear = CompletedDaysGroup,
        },
        new ClearProgressionGroupMission()
        {
            MissionId = 12090403,
            ProgressionGroupToClear = CompletedDaysGroup,
        },
        new EventRegularBattleClearMission() { MissionId = 12090501 },
        new EventRegularBattleClearMission() { MissionId = 12090502 },
        new EventRegularBattleClearMission() { MissionId = 12090503 },
        new EventRegularBattleClearMission() { MissionId = 12090504 },
        new EventRegularBattleClearMission() { MissionId = 12090505 },
        new ClearRaidMission() { MissionId = 12090601 },
        new ClearRaidMission() { MissionId = 12090602 },
        new ClearRaidMission() { MissionId = 12090603 },
        new ClearRaidMission() { MissionId = 12090604 },
        new ClearRaidMission() { MissionId = 12090605 },
        new ClearRaidMission() { MissionId = 12090606 },
        new ClearRaidMission() { MissionId = 12090607 },
        new ClearRaidMission() { MissionId = 12090608 },
        new ClearRaidMission() { MissionId = 12090609 },
        new ClearRaidMission() { MissionId = 12090610 },
        new ClearRaidMission() { MissionId = 12090611 },
        new ClearQuestMission() { MissionId = 12090701, QuestId = 204590501 },
        new ClearQuestMission() { MissionId = 12090901, QuestId = 204590602 },
    ];

    [MissionType(MissionType.Daily)]
    [EventId(EventId)]
    public static List<Mission> DailyMissions { get; } =
    [
        new ClearQuestMission()
        {
            MissionId = 11600101,
            QuestGroupId = EventId,
            ProgressionGroupId = DailyGoalsGroup,
        },
        new ClearRaidMission() { MissionId = 11600201, ProgressionGroupId = DailyGoalsGroup },
        new EventRegularBattleClearMission()
        {
            MissionId = 11600301,
            ProgressionGroupId = DailyGoalsGroup,
        },
        new EventItemCollectionMission() { MissionId = 11600401, ItemId = 2045901 },
        new EventItemCollectionMission() { MissionId = 11600402, ItemId = 2045901 },
        new EventItemCollectionMission() { MissionId = 11600403, ItemId = 2045901 },
        new EventItemCollectionMission() { MissionId = 11600404, ItemId = 2045901 },
        new EventItemCollectionMission()
        {
            MissionId = 11600405,
            ItemId = 2045901,
            ProgressionGroupId = DailyGoalsGroup,
        },
        new ClearProgressionGroupMission()
        {
            MissionId = 11600501,
            ProgressionGroupToClear = DailyGoalsGroup,
            ProgressionGroupId = CompletedDaysGroup,
        },
    ];
}
