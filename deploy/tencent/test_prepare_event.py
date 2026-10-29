import copy
import json
import unittest
from datetime import UTC, datetime, timedelta

import prepare_event


class PrepareEventTest(unittest.TestCase):
    def setUp(self):
        self.start = datetime(2026, 10, 29, 2, 30, tzinfo=UTC)
        self.events = {"EventOptions": {"EventList": [
            {"Id": 20401, "Start": "2026-10-18T06:00:00Z", "End": "2026-10-21T15:59:59Z"},
            {"Id": 20801, "Start": "2026-10-21T16:00:00Z", "End": "2026-10-30T15:59:59Z"},
        ]}}
        self.banners = {"SummonBannerOptions": {"Banners": [
            {"Id": 1020003, "Start": "2026-10-21T16:00:00Z", "End": "2026-11-04T15:59:59Z"},
        ]}}
        self.preset = json.loads((prepare_event.PRODUCTION.parent / "kindnessBanner.json").read_text())

    def test_999_days_and_only_explicitly_replaced_entries_close(self):
        before = copy.deepcopy((self.events, self.banners, self.preset))
        events, banners = prepare_event.prepare(
            self.events, self.banners, 20459, self.preset, self.start,
            replace_event=20801, replace_banner=1020003,
        )
        self.assertEqual((self.events, self.banners, self.preset), before)
        self.assertEqual(events["EventOptions"]["EventList"][0], self.events["EventOptions"]["EventList"][0])
        event = events["EventOptions"]["EventList"][-1]
        banner = banners["SummonBannerOptions"]["Banners"][-1]
        self.assertEqual(event["Start"], banner["Start"])
        self.assertEqual(event["End"], banner["End"])
        self.assertEqual(prepare_event.timestamp(event["End"]) - self.start + timedelta(seconds=1), timedelta(days=999))
        self.assertEqual(events["EventOptions"]["EventList"][1]["End"], event["Start"])
        self.assertEqual(banners["SummonBannerOptions"]["Banners"][0]["End"], event["Start"])
        self.assertEqual(banner["PickupCharas"], ["Naveed"])
        self.assertEqual(banner["PickupDragons"], ["Cerberus"])

    def test_existing_schedules_are_preserved_without_explicit_replacement(self):
        events, banners = prepare_event.prepare(self.events, self.banners, 20459, self.preset, self.start)
        self.assertEqual(events["EventOptions"]["EventList"][:-1], self.events["EventOptions"]["EventList"])
        self.assertEqual(banners["SummonBannerOptions"]["Banners"][:-1], self.banners["SummonBannerOptions"]["Banners"])

    def test_explicit_end_overrides_default(self):
        end = self.start + timedelta(days=3)
        events, banners = prepare_event.prepare(self.events, self.banners, 20459, self.preset, self.start, end=end)
        self.assertEqual(prepare_event.timestamp(events["EventOptions"]["EventList"][-1]["End"]), end)
        self.assertEqual(prepare_event.timestamp(banners["SummonBannerOptions"]["Banners"][-1]["End"]), end)

    def test_duplicate_or_invalid_replacement_is_rejected(self):
        events, banners = prepare_event.prepare(self.events, self.banners, 20459, self.preset, self.start)
        with self.assertRaises(ValueError):
            prepare_event.prepare(events, banners, 20459, self.preset, self.start)
        with self.assertRaises(ValueError):
            prepare_event.prepare(self.events, self.banners, 20459, self.preset, self.start, replace_event=20458)


if __name__ == "__main__":
    unittest.main()
