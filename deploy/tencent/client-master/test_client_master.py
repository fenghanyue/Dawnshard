"""验证讨伐战改成单人时，活动菜单也改用单人入口。"""

import copy
import unittest
from datetime import datetime

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
        }])

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


if __name__ == "__main__":
    unittest.main()
