using DragaliaAPI.MissionDesigner.Models;
using DragaliaAPI.MissionDesigner.Models.Attributes;
using DragaliaAPI.MissionDesigner.Models.EventMission;
using DragaliaAPI.MissionDesigner.Models.RegularMission;

namespace DragaliaAPI.MissionDesigner.Missions.Period;

// Original 2018 event 20801. The compendium rerun 20826 has different missions.
[ContainsMissionList]
public static class TrickOrTreasureOriginal
{
    private const int EventId = 20801;
    private const int ExQuestId = 208010401;
    private const int ChallengeQuestId = 208010501;

    [MissionType(MissionType.Period)]
    [EventId(EventId)]
    public static List<Mission> PeriodMissions { get; } =
    [
        new EventParticipationMission() { MissionId = 10020001 },
        new FortPlantPlacedMission() { MissionId = 10020101, Plant = FortPlants.SweetRetreat },
        new FortPlantLevelUpMission() { MissionId = 10020201, Plant = FortPlants.SweetRetreat },
        new FortPlantLevelUpMission() { MissionId = 10020202, Plant = FortPlants.SweetRetreat },
        new FortPlantLevelUpMission() { MissionId = 10020203, Plant = FortPlants.SweetRetreat },
        new EventQuestClearWithCrestMission()
        {
            MissionId = 10020301,
            Crest = AbilityCrestId.PlunderPals,
        },
        new EventPointCollectionRecordMission() { MissionId = 10020401 },
        new EventPointCollectionRecordMission() { MissionId = 10020402 },
        new EventPointCollectionRecordMission() { MissionId = 10020403 },
        new EventPointCollectionRecordMission() { MissionId = 10020404 },
        new EventPointCollectionRecordMission() { MissionId = 10020405 },
        new EventRegularBattleClearMission() { MissionId = 10020501 },
        new EventRegularBattleClearMission() { MissionId = 10020502 },
        new EventRegularBattleClearMission() { MissionId = 10020503 },
        new EventRegularBattleClearMission() { MissionId = 10020504 },
        new ClearQuestMission() { MissionId = 10020601, QuestId = ExQuestId },
        new ClearQuestMission() { MissionId = 10020602, QuestId = ExQuestId },
        new ClearQuestMission() { MissionId = 10020603, QuestId = ExQuestId },
        new ClearQuestMission() { MissionId = 10020604, QuestId = ExQuestId },
        new EventChallengeWaveClearMission() { MissionId = 10020701, QuestId = ChallengeQuestId },
        new EventChallengeWaveClearMission() { MissionId = 10020702, QuestId = ChallengeQuestId },
        new EventChallengeWaveClearMission() { MissionId = 10020703, QuestId = ChallengeQuestId },
        new EventChallengeWaveClearMission() { MissionId = 10020704, QuestId = ChallengeQuestId },
        new EventChallengeWaveClearMission() { MissionId = 10020705, QuestId = ChallengeQuestId },
        new EventChallengeBattleClearMission()
        {
            MissionId = 10020801,
            QuestId = ChallengeQuestId,
            FullClear = true,
        },
        new EventChallengeBattleClearTimeMission()
        {
            MissionId = 10020901,
            QuestId = ChallengeQuestId,
            MaxSeconds = 120,
        },
    ];

    [MissionType(MissionType.Daily)]
    [EventId(EventId)]
    public static List<Mission> DailyMissions { get; } =
    [
        new EventPointCollectionMission() { MissionId = 10003001 },
        new EventItemCollectionMission() { MissionId = 10003101, ItemId = 2080102 },
        new EventRegularBattleClearMission() { MissionId = 10003201 },
    ];
}
