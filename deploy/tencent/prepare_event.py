"""Prepare an event and concurrent banner (999 days by default), without deploying."""

import argparse
import copy
import json
from datetime import UTC
from pathlib import Path

from event_schedule import default_end
from prepare_halloween import BANNERS, PRODUCTION, timestamp


def prepare(events, banners, event_id, preset, start, end=None, banner_end=None,
            replace_event=None, replace_banner=None):
    end = end or default_end(start)
    banner_end = banner_end or end
    if start.tzinfo is None or any(value.tzinfo is None or value <= start for value in (end, banner_end)):
        raise ValueError("开始和结束时间必须包含时区，结束时间必须晚于开始时间")
    events, banners = copy.deepcopy(events), copy.deepcopy(banners)
    event_list = events.setdefault("EventOptions", {}).setdefault("EventList", [])
    options = banners.setdefault("SummonBannerOptions", {})
    banner_list = options.setdefault("Banners", [])
    if any(x["Id"] == event_id for x in event_list) or any(x["Id"] == preset["Id"] for x in banner_list):
        raise ValueError("活动或卡池已经排期，不能重复添加")

    def text(value):
        return value.astimezone(UTC).isoformat(timespec="seconds").replace("+00:00", "Z")

    for values, target in ((event_list, replace_event), (banner_list, replace_banner)):
        if target is None:
            continue
        old = next((x for x in values if x["Id"] == target), None)
        if old is None or timestamp(old["Start"]) >= start:
            raise ValueError("要替换的活动或卡池不存在，或开始时间不早于切换时间")
        old["End"] = text(min(timestamp(old["End"]), start))
    event_list.append({"Id": event_id, "Start": text(start), "End": text(end)})
    banner = copy.deepcopy(preset)
    banner.update(Start=text(start), End=text(banner_end))
    banner_list.append(banner)
    options["FilterPoolByReleaseDate"] = True
    return events, banners


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", type=Path, default=PRODUCTION)
    parser.add_argument("--banner-base", type=Path, default=BANNERS)
    parser.add_argument("--event-id", type=int, required=True)
    parser.add_argument("--banner-preset", type=Path, required=True)
    parser.add_argument("--start", type=timestamp, required=True)
    parser.add_argument("--end", type=timestamp)
    parser.add_argument("--banner-end", type=timestamp)
    parser.add_argument("--replace-event", type=int)
    parser.add_argument("--replace-banner", type=int)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--banner-out", type=Path, required=True)
    args = parser.parse_args()
    outputs = [args.out.resolve(), args.banner_out.resolve()]
    protected = {x.resolve() for x in (PRODUCTION, BANNERS, args.base, args.banner_base, args.banner_preset)}
    if len(set(outputs)) != 2 or any(x in protected or x.exists() for x in outputs):
        parser.error("请输出到两个不存在的独立文件，不能覆盖输入或正式配置")
    try:
        configs = prepare(
            json.loads(args.base.read_text(encoding="utf-8-sig")),
            json.loads(args.banner_base.read_text(encoding="utf-8-sig")), args.event_id,
            json.loads(args.banner_preset.read_text(encoding="utf-8-sig")), args.start,
            args.end, args.banner_end, args.replace_event, args.replace_banner,
        )
        for path, config in zip(outputs, configs):
            with path.open("x", encoding="utf-8") as output:
                json.dump(config, output, ensure_ascii=False, indent=4)
                output.write("\n")
    except ValueError as error:
        parser.error(str(error))
    print("已生成活动与卡池待上线配置；尚未部署")


if __name__ == "__main__":
    main()
