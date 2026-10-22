"""验证活动菜单的单人入口和原版大逃杀的关闭。"""

import copy
import json
import unittest
import tempfile
from datetime import datetime
from pathlib import Path

import client_master


def table(values):
    return {"dict": {"count": len(values), "entriesHashCode": [0] * len(values), "entriesValue": values}}


class SoloRaidMenuTest(unittest.TestCase):
    def setUp(self):
        self.run = client_master.EventRun(20401, datetime(2026, 10, 18, 6), datetime(2026, 11, 1, 6))
        self.tables = {name: table([]) for name in client_master.TABLES}
        self.tables["EventData"] = table([{
            "_Id": 20401, "_EventCharaId": 0,
            "_StartDate": "2018/10/04 06:00:00", "_EndDate": "2018/10/15 06:00:00",
            "_ViewCloseDate": "2018/10/22 06:00:00", "_ViewAnnounceDate": "", "_ViewNewDate": "",
        }, {
            "_Id": 22301, "_EventCharaId": 0,
            "_StartDate": "2020/09/24 15:00:00", "_EndDate": "2030/12/31 05:59:59",
            "_ViewCloseDate": "2030/12/31 05:59:59", "_ViewAnnounceDate": "", "_ViewNewDate": "",
        }])
        self.tables["QuestEventGroup"] = table([
            {"_Id": 22301, "_IsHideBanner": 0, "_ViewStartDate": "", "_ViewEndDate": ""},
        ])

    def quest(self, quest_id, group_id=20401, dungeon_type=2):
        return {
            "_Id": quest_id, "_Gid": group_id, "_DungeonType": dungeon_type,
            "_PayStaminaSingle": 0 if quest_id == 204010401 else 3,
            "_CampaignStaminaSingle": 0 if quest_id == 204010401 else 1,
            "_PayStaminaMulti": 1, "_CampaignStaminaMulti": 1, "_ConstraintType": 0,
            "_ViewStartDate": "", "_ViewEndDate": "", "_ViewStartDate2": "", "_ViewEndDate2": "",
        }

    def menu(self, menu_id, quest_id, subtype=3, group_id=20401, quest_type=1):
        return {
            "_Id": menu_id, "_GroupId": group_id, "_EntryQuestType": quest_type,
            "_EntryQuestId": quest_id, "_QuestSubType": subtype, "_ReleaseQuestId1": 2040104,
        }

    def test_converted_raids_use_solo_menu_without_changing_quest_ids_or_unlocks(self):
        quest_ids = [204010301, 204010302, 204010303, 204010401]
        menus = [self.menu(2040109 + i, quest_id, 4 if i == 3 else 3) for i, quest_id in enumerate(quest_ids)]
        self.tables["QuestData"] = table([self.quest(quest_id) for quest_id in quest_ids])
        self.tables["QuestEventMenu"] = table(copy.deepcopy(menus))

        changes = client_master.plan_changes(self.tables, [self.run], set())
        self.assertEqual(changes["QuestEventMenu"], {row["_Id"]: {"_QuestSubType": 2} for row in menus})
        edits = client_master.make_edits(changes)
        self.assertEqual(edits["QuestEventMenu"](self.tables["QuestEventMenu"]), 4)
        self.assertEqual(list(client_master.rows(self.tables["QuestEventMenu"])), [row | {"_QuestSubType": 2} for row in menus])
        self.assertEqual(edits["QuestEventMenu"](self.tables["QuestEventMenu"]), 0)
        self.assertEqual(set(changes["QuestData"]), set(quest_ids))

    def test_only_dungeon_entries_for_converted_scheduled_raids_are_changed(self):
        menus = [
            self.menu(1, 204010301),
            self.menu(2, 204010201, subtype=2),
            self.menu(3, 204010301, quest_type=2),
            self.menu(4, 204020301, group_id=20402),
            self.menu(5, 204010301, group_id=20402),
            self.menu(6, 204010601, subtype=6),
        ]
        self.tables["QuestData"] = table([
            self.quest(204010301), self.quest(204010201, dungeon_type=1),
            self.quest(204020301, group_id=20402), self.quest(204010601, dungeon_type=10),
        ])
        self.tables["QuestEventMenu"] = table(copy.deepcopy(menus))

        changes = client_master.plan_changes(self.tables, [self.run], set())
        self.assertEqual(changes["QuestEventMenu"], {1: {"_QuestSubType": 2}})
        client_master.make_edits(changes)["QuestEventMenu"](self.tables["QuestEventMenu"])
        self.assertEqual(list(client_master.rows(self.tables["QuestEventMenu"]))[1:], menus[1:])

    def test_battle_royale_closes_even_without_scheduled_events(self):
        original = copy.deepcopy(self.tables)
        changes = client_master.plan_changes(self.tables, [], set())
        for name, edit in client_master.make_edits(changes).items():
            edit(self.tables[name])

        event = list(client_master.rows(self.tables["EventData"]))[1]
        self.assertEqual(event["_EndDate"], event["_StartDate"])
        self.assertEqual(event["_ViewCloseDate"], event["_StartDate"])
        self.assertEqual(list(client_master.rows(self.tables["QuestEventGroup"]))[0]["_IsHideBanner"], 1)
        self.assertEqual(list(client_master.rows(self.tables["EventData"]))[0],
                         list(client_master.rows(original["EventData"]))[0])
        self.assertEqual(client_master.plan_changes(self.tables, [], set()), {})

    def test_closed_battle_royale_cannot_be_reopened_by_scheduling(self):
        run = client_master.EventRun(22301, self.run.start, self.run.end)
        with self.assertRaisesRegex(SystemExit, "22301.*已停用"):
            client_master.plan_changes(self.tables, [run], set())


class HalloweenMapsTest(unittest.TestCase):
    def setUp(self):
        SoloRaidMenuTest.setUp(self)
        self.run = client_master.EventRun(20801, datetime(2031, 10, 17, 6), datetime(2031, 10, 26, 5, 59, 59))
        self.tables["EventData"]["dict"]["entriesValue"][0]["_Id"] = 20801
        shared = Path(__file__).resolve().parents[3] / "DragaliaAPI/DragaliaAPI.Shared/Resources/QuestData.json"
        self.server = {row["_Id"]: row for row in json.loads(shared.read_text(encoding="utf-8-sig")) if row["_Gid"] == 20801}
        quests = copy.deepcopy(list(self.server.values()))
        for row in quests:
            if row["_Id"] in client_master.HALLOWEEN_AREA_SOURCES:
                for area in range(1, 4):
                    for field in ("_Scene", "_AreaName"):
                        key = f"{field}{area:02}"
                        row[key] = row[key].replace("0102", "0199").replace("0104", "0199")
        self.tables["QuestData"] = table(quests)

    def test_halloween_maps_match_server_without_changing_rewards_or_unlocks(self):
        before = copy.deepcopy(self.tables)
        changes = client_master.plan_changes(self.tables, [self.run], set())
        for name, edit in client_master.make_edits(changes).items():
            edit(self.tables[name])
        for row in client_master.rows(self.tables["QuestData"]):
            self.assertEqual(row, self.server[row["_Id"]])
        for quest_id, fields in changes["QuestData"].items():
            self.assertIn(quest_id, client_master.HALLOWEEN_AREA_SOURCES)
            self.assertTrue(all(key.startswith(("_Scene", "_AreaName")) for key in fields))
            original = next(r for r in client_master.rows(before["QuestData"]) if r["_Id"] == quest_id)
            for key, value in fields.items():
                self.assertEqual(len(original[key]), len(value))

    def test_unscheduled_halloween_maps_are_untouched(self):
        changes = client_master.plan_changes(self.tables, [], set())
        self.assertNotIn("QuestData", changes)


class ArchivedEventTest(unittest.TestCase):
    def setUp(self):
        SoloRaidMenuTest.setUp(self)
        event = self.tables["EventData"]["dict"]["entriesValue"][0]
        event.update({"_IsMemoryEvent": 0, "_MemoryEventViewType": 0, "_IsPickupMemoryEvent": 0})
        self.tables["QuestEventGroup"] = table([
            {"_Id": 20401, "_BaseQuestGroupId": 20401, "_IsHideBanner": 0,
             "_ViewStartDate": "", "_ViewEndDate": ""},
            {"_Id": 22301, "_BaseQuestGroupId": 22301, "_IsHideBanner": 0,
             "_ViewStartDate": "", "_ViewEndDate": ""},
        ])
        self.tables["QuestData"] = table([SoloRaidMenuTest.quest(self, 204010301)])
        self.tables["QuestEventMenu"] = table([SoloRaidMenuTest.menu(self, 1, 204010301)])
        self.tables["BoxSummonData"] = table([
            {"_Id": 1, "_EventId": 20401, "_CommenceDate": "2018/10/04 06:00:00",
             "_CompleteDate": "2018/10/15 05:59:59", "_CostNum": 2},
        ])
        self.tables["MyPageBanner"] = table([
            {"_Id": 1, "_EventId": 20401, "_ViewStartDate": "2018/10/02 06:00:00",
             "_ViewEndDate": "2018/10/15 05:59:59"},
        ])

    def test_archive_uses_compendium_parent_and_original_quest_summon_and_unlocks(self):
        original_banner = copy.deepcopy(self.tables["MyPageBanner"])
        original_menu = copy.deepcopy(self.tables["QuestEventMenu"])
        changes = client_master.plan_changes(self.tables, [self.run], {20401}, {20401})
        for name, edit in client_master.make_edits(changes).items():
            edit(self.tables[name])
        event = next(client_master.rows(self.tables["EventData"]))
        self.assertEqual((event["_Id"], event["_IsMemoryEvent"], event["_MemoryEventViewType"]), (20401, 1, 201))
        self.assertEqual(event["_EndDate"], "2099/12/31 23:59:59")
        group = next(client_master.rows(self.tables["QuestEventGroup"]))
        self.assertEqual(group["_BaseQuestGroupId"], 22100)
        quest = next(client_master.rows(self.tables["QuestData"]))
        self.assertEqual((quest["_Id"], quest["_DungeonType"]), (204010301, 10))
        menu = next(client_master.rows(self.tables["QuestEventMenu"]))
        self.assertEqual(menu, next(client_master.rows(original_menu)) | {"_QuestSubType": 2})
        summon = next(client_master.rows(self.tables["BoxSummonData"]))
        self.assertEqual(summon["_CostNum"], 2)
        self.assertEqual(summon["_CompleteDate"], "2099/12/31 23:59:59")
        self.assertEqual(self.tables["MyPageBanner"], original_banner)
        self.assertEqual(client_master.plan_changes(self.tables, [self.run], {20401}, {20401}), {})

    def test_archive_stays_available_without_any_timed_schedule(self):
        changes = client_master.plan_changes(self.tables, [], {20401}, {20401})
        self.assertEqual(changes["QuestData"][204010301]["_DungeonType"], 10)
        self.assertEqual(changes["QuestEventMenu"][1]["_QuestSubType"], 2)
        self.assertEqual(changes["EventData"][20401]["_IsMemoryEvent"], 1)

    def test_archive_configuration_rejects_unsupported_or_duplicate_ids(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "events.json"
            for values in ([20801], [20401, 20401], "20401", [True]):
                path.write_text(json.dumps({"EventOptions": {"ArchivedEventIds": values}}))
                with self.subTest(values=values), self.assertRaises(SystemExit):
                    client_master.load_archives(path)
            path.write_text(json.dumps({"EventOptions": {"ArchivedEventIds": [20401]}}))
            self.assertEqual(client_master.load_archives(path), {20401})


if __name__ == "__main__":
    unittest.main()
