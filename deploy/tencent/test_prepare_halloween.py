import copy
import unittest
from datetime import UTC, datetime

import prepare_halloween


class PrepareHalloweenTest(unittest.TestCase):
    def setUp(self):
        self.base = {"EventOptions": {"EventList": [{"Id": 20401, "Start": "2026-10-18T06:00:00Z", "End": "2029-07-14T05:59:59Z"}]}, "Other": {"Keep": True}}
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


if __name__ == "__main__":
    unittest.main()
