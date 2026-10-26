"""验证活动菜单的单人入口和原版大逃杀的关闭。"""

import copy
import json
import tempfile
import unittest
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


class CompendiumTrialTest(unittest.TestCase):
    def setUp(self):
        SoloRaidMenuTest.setUp(self)
        self.shared = Path(__file__).resolve().parents[3] / "DragaliaAPI/DragaliaAPI.Shared/Resources"
        events = json.loads((self.shared / "Event/EventData.json").read_text())
        trial = copy.deepcopy(next(row for row in events if row["_Id"] == 20458))
        native = copy.deepcopy(next(row for row in events if row["_Id"] == 20427))
        self.tables["EventData"] = table(list(client_master.rows(self.tables["EventData"])) + [trial, native])
        self.tables["QuestEventGroup"] = table([
            *client_master.rows(self.tables["QuestEventGroup"]),
            {"_Id": 20458, "_BaseQuestGroupId": 20458, "_ViewStartDate": "", "_ViewEndDate": ""},
        ])
        period = json.loads((self.shared / "Missions/MissionPeriodData.json").read_text())
        self.tables["MissionPeriodData"] = table([row for row in period if row["_QuestGroupId"] == 20458])
        self.run = client_master.EventRun(20458, datetime(2026, 10, 25, 8), datetime(2099, 1, 1, 6))

    def test_only_trial_enters_compendium_and_reward_references_are_preserved(self):
        original = copy.deepcopy(self.tables)
        changes = client_master.plan_changes(self.tables, [self.run], {20458})
        for name, edit in client_master.make_edits(changes).items():
            edit(self.tables[name])
        by_id = {row["_Id"]: row for row in client_master.rows(self.tables["EventData"])}
        old = {row["_Id"]: row for row in client_master.rows(original["EventData"])}
        self.assertEqual(by_id[20427], old[20427])
        self.assertEqual(by_id[20458]["_IsMemoryEvent"], 1)
        self.assertEqual(by_id[20458]["_MemoryEventViewType"], 201)
        for key in ("_EndDate", "_ViewCloseDate"):
            self.assertEqual(by_id[20458][key], "2099/01/01 06:00:00")
        for key in ("_RewardMasterName", "_EventRewardText", "_ViewEntityType4", "_ViewEntityId4", "_IsUseEventPassive"):
            self.assertEqual(by_id[20458][key], old[20458][key])
        group = next(row for row in client_master.rows(self.tables["QuestEventGroup"]) if row["_Id"] == 20458)
        self.assertEqual(group["_BaseQuestGroupId"], 22100)
        self.assertEqual(client_master.plan_changes(self.tables, [self.run], {20458}), {})

    def test_memory_tasks_match_server_and_remain_retrievable_through_hash_buckets(self):
        additions = client_master.compendium_missions(self.tables, [self.run])
        expected = json.loads((self.shared / "Missions/MissionMemoryEventData.fixes.json").read_text())
        self.assertEqual(additions, expected)
        self.assertEqual(len(additions), 25)
        capacity = 41
        tree = {"dict": {"count": 0, "freeCount": 0, "freeList": -1,
            "buckets": [-1] * capacity, "entriesHashCode": [-1] * capacity,
            "entriesNext": [-1] * capacity, "entriesKey": [0] * capacity,
            "entriesValue": [{} for _ in range(capacity)]}}
        self.assertEqual(client_master.add_compendium_missions(tree, additions), 25)
        self.assertEqual(client_master.add_compendium_missions(tree, additions), 0)
        data = tree["dict"]
        for row in additions:
            index = data["buckets"][row["_Id"] % capacity]
            visited = set()
            while index >= 0 and data["entriesKey"][index] != row["_Id"]:
                self.assertNotIn(index, visited)
                visited.add(index)
                index = data["entriesNext"][index]
            self.assertGreaterEqual(index, 0)
            self.assertEqual(data["entriesValue"][index], row)

    def test_cleanup_keeps_generated_banner_in_addition_to_masters(self):
        with tempfile.TemporaryDirectory() as directory:
            out = Path(directory)
            version = out / "dl/manifests/Android/TRIAL"
            version.mkdir(parents=True)
            (version / ".masters.json").write_text('["MASTER"]')
            (version / ".assets.json").write_text('["MASTER", "BANNER"]')
            for name in ("MASTER", "BANNER", "UNUSED"):
                client_master.write_file(out / "dl/assetbundles/Android" / name[:2] / name, b"data")
            client_master.clean_up(out, "TRIAL")
            self.assertTrue((out / "dl/assetbundles/Android/MA/MASTER").exists())
            self.assertTrue((out / "dl/assetbundles/Android/BA/BANNER").exists())
            self.assertFalse((out / "dl/assetbundles/Android/UN/UNUSED").exists())


if __name__ == "__main__":
    unittest.main()
