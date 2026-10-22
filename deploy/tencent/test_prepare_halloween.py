import copy
import json
import subprocess
import sys
import tempfile
import unittest
from datetime import UTC, datetime
from pathlib import Path

import check_config
import prepare_halloween


class PrepareHalloweenTest(unittest.TestCase):
    def setUp(self):
        self.base = {"EventOptions": {"EventList": [{"Id": 20401, "Start": "2026-10-18T06:00:00Z", "End": "2029-07-14T05:59:59Z"}]}, "Other": {"Keep": True}}
        self.banner_base = {"SummonBannerOptions": {"FilterPoolByReleaseDate": True, "Banners": [{"Id": 1020002, "Start": "2026-10-18T06:00:00Z", "End": "2026-11-01T05:59:59Z", "PickupCharas": ["Lily"], "PickupDragons": ["Leviathan"]}]}}
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.base_path = Path(directory.name) / "base-events.json"
        self.banner_path = Path(directory.name) / "base-banners.json"
        self.base_path.write_text(json.dumps(self.base))
        self.banner_path.write_text(json.dumps(self.banner_base))
        self.start = prepare_halloween.timestamp("2031-10-17T14:00:00+08:00")

    def test_preserves_first_event_and_input_and_uses_original_duration(self):
        before = copy.deepcopy(self.base)
        result = prepare_halloween.prepare(self.base, self.start)
        self.assertEqual(self.base, before)
        self.assertEqual(result["EventOptions"]["EventList"][0], before["EventOptions"]["EventList"][0])
        self.assertEqual(result["Other"], before["Other"])
        self.assertEqual(result["EventOptions"]["EventList"][1], {"Id": 20801, "Start": "2031-10-17T06:00:00Z", "End": "2031-10-26T05:59:59Z"})

    def test_custom_duration(self):
        result = prepare_halloween.prepare(self.base, self.start, datetime(2032, 1, 1, tzinfo=UTC))
        self.assertEqual(result["EventOptions"]["EventList"][-1]["End"], "2032-01-01T00:00:00Z")

    def test_rejects_naive_time(self):
        with self.assertRaises(ValueError):
            prepare_halloween.timestamp("2031-10-17T14:00:00")
        with self.assertRaises(ValueError):
            prepare_halloween.prepare(self.base, datetime(2031, 10, 17))

    def test_rejects_duplicate_event(self):
        result = prepare_halloween.prepare(self.base, self.start)
        with self.assertRaisesRegex(ValueError, "重复"):
            prepare_halloween.prepare(result, self.start)

    def test_rejects_empty_or_reversed_period(self):
        with self.assertRaises(ValueError):
            prepare_halloween.prepare(self.base, self.start, self.start)

    def test_banner_starts_with_event_and_preserves_existing_pool(self):
        base = self.banner_base
        before = copy.deepcopy(base)
        event = prepare_halloween.prepare(self.base, self.start)["EventOptions"]["EventList"][-1]
        result = prepare_halloween.prepare_banners(base, event)
        self.assertEqual(base, before)
        self.assertEqual(result["SummonBannerOptions"]["Banners"][:-1], before["SummonBannerOptions"]["Banners"])
        banner = result["SummonBannerOptions"]["Banners"][-1]
        self.assertEqual(banner["Id"], 1020003)
        self.assertEqual(banner["Start"], event["Start"])
        self.assertEqual(banner["End"], "2031-10-31T05:59:59Z")
        self.assertTrue(result["SummonBannerOptions"]["FilterPoolByReleaseDate"])

    def test_custom_event_and_banner_ends(self):
        event = prepare_halloween.prepare(self.base, self.start, datetime(2032, 1, 1, tzinfo=UTC))["EventOptions"]["EventList"][-1]
        result = prepare_halloween.prepare_banners({}, event)
        self.assertEqual(result["SummonBannerOptions"]["Banners"][-1]["End"], "2032-01-06T00:00:00Z")
        result = prepare_halloween.prepare_banners({}, event, datetime(2032, 2, 1, tzinfo=UTC))
        self.assertEqual(result["SummonBannerOptions"]["Banners"][-1]["End"], "2032-02-01T00:00:00Z")

    def test_rejects_duplicate_banner_and_invalid_end(self):
        event = prepare_halloween.prepare(self.base, self.start)["EventOptions"]["EventList"][-1]
        result = prepare_halloween.prepare_banners({}, event)
        with self.assertRaisesRegex(ValueError, "重复"):
            prepare_halloween.prepare_banners(result, event)
        for end in (datetime(2031, 10, 17), self.start):
            with self.subTest(end=end), self.assertRaises(ValueError):
                prepare_halloween.prepare_banners({}, event, end)

    def run_cli(self, event_path, banner_path):
        return subprocess.run([
            sys.executable, str(prepare_halloween.HERE / "prepare_halloween.py"),
            "--base", str(self.base_path), "--banner-base", str(self.banner_path),
            "--start", "2031-10-17T14:00:00+08:00", "--out", str(event_path), "--banner-out", str(banner_path),
        ], capture_output=True, text=True)

    def test_cli_generates_two_valid_configs_without_changing_production(self):
        original = {path: path.read_bytes() for path in (prepare_halloween.PRODUCTION, prepare_halloween.BANNERS)}
        with tempfile.TemporaryDirectory() as directory:
            events, banners = Path(directory) / "events.json", Path(directory) / "banners.json"
            result = self.run_cli(events, banners)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(check_config.check(events, banners), [])
            event = json.loads(events.read_text())["EventOptions"]["EventList"][-1]
            banner = json.loads(banners.read_text())["SummonBannerOptions"]["Banners"][-1]
            self.assertEqual(event["Start"], banner["Start"])
            self.assertEqual(banner["PickupCharas"], ["HalloweenElisanne", "HalloweenAlthemia", "HalloweenEdward"])
            self.assertEqual(banner["PickupDragons"], ["HalloweenSilke"])
        self.assertTrue(all(path.read_bytes() == data for path, data in original.items()))

    def test_cli_protects_inputs_and_rejects_same_output(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "same.json"
            for event_path, banner_path in ((path, path), (prepare_halloween.PRODUCTION, path), (path, prepare_halloween.BANNERS)):
                result = self.run_cli(event_path, banner_path)
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(path.exists())

    def test_existing_banner_output_prevents_partial_event_output(self):
        with tempfile.TemporaryDirectory() as directory:
            events, banners = Path(directory) / "events.json", Path(directory) / "banners.json"
            banners.write_text("keep")
            result = self.run_cli(events, banners)
            self.assertNotEqual(result.returncode, 0)
            self.assertFalse(events.exists())
            self.assertEqual(banners.read_text(), "keep")


if __name__ == "__main__":
    unittest.main()
