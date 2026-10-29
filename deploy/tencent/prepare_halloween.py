"""生成第二期活动和同期卡池的待上线配置；不修改线上配置、不连接服务器、不部署。

使用时必须明确传入带时区的开始时间。默认活动及卡池均 999 天，同日起止。
--end 同时改变默认卡池结束时间；可用 --banner-end 单独指定卡池结束时间。
生成两份独立文件，决定上线后再审阅并替换正式配置。
"""

import argparse
import json
from datetime import UTC, datetime
from pathlib import Path

from event_schedule import default_end

HERE = Path(__file__).resolve().parent
PRODUCTION = HERE / "appsettings.Production.json"
BANNERS = HERE / "bannerConfig.json"
BANNER_PRESET = HERE / "halloweenBanner.json"
EVENT_ID = 20801
BANNER_ID = 1020003


def timestamp(value: str) -> datetime:
    date = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if date.tzinfo is None:
        raise ValueError("时间必须包含时区，例如 2031-10-17T14:00:00+08:00")
    return date.astimezone(UTC)


def prepare(config: dict, start: datetime, end: datetime | None = None) -> dict:
    if start.tzinfo is None or end is not None and end.tzinfo is None:
        raise ValueError("开始和结束时间必须包含时区")
    start = start.astimezone(UTC)
    end = end.astimezone(UTC) if end is not None else default_end(start)
    if end <= start:
        raise ValueError("结束时间必须晚于开始时间")
    result = json.loads(json.dumps(config))
    events = result.setdefault("EventOptions", {}).setdefault("EventList", [])
    if any(event["Id"] == EVENT_ID for event in events):
        raise ValueError("20801 已在配置中，不能重复排期；请先审阅已有排期")
    events.append({
        "Id": EVENT_ID,
        "Start": start.isoformat(timespec="seconds").replace("+00:00", "Z"),
        "End": end.isoformat(timespec="seconds").replace("+00:00", "Z"),
    })
    return result


def prepare_banners(config: dict, event: dict, end: datetime | None = None) -> dict:
    start = timestamp(event["Start"])
    end = end if end is not None else timestamp(event["End"])
    if end.tzinfo is None or end <= start:
        raise ValueError("卡池结束时间必须包含时区且晚于开始时间")
    result = json.loads(json.dumps(config))
    options = result.setdefault("SummonBannerOptions", {})
    banners = options.setdefault("Banners", [])
    if any(banner["Id"] == BANNER_ID for banner in banners):
        raise ValueError("1020003 已在配置中，不能重复排期；请先审阅已有卡池")
    banner = json.loads(BANNER_PRESET.read_text(encoding="utf-8"))
    if banner["Id"] != BANNER_ID:
        raise ValueError("万圣节卡池预设编号不正确")
    banner.update({
        "Start": start.isoformat(timespec="seconds").replace("+00:00", "Z"),
        "End": end.astimezone(UTC).isoformat(timespec="seconds").replace("+00:00", "Z"),
    })
    banners.append(banner)
    options["FilterPoolByReleaseDate"] = True
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--base", type=Path, default=PRODUCTION)
    parser.add_argument("--banner-base", type=Path, default=BANNERS)
    parser.add_argument("--start", type=timestamp, required=True)
    parser.add_argument("--end", type=timestamp)
    parser.add_argument("--banner-end", type=timestamp)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--banner-out", type=Path, required=True)
    args = parser.parse_args()
    outputs = [args.out.resolve(), args.banner_out.resolve()]
    protected = {
        path.resolve()
        for path in (PRODUCTION, BANNERS, BANNER_PRESET, args.base, args.banner_base)
    }
    if len(set(outputs)) != 2 or any(path in protected for path in outputs):
        parser.error("请输出到两个独立的待上线文件，不能覆盖正式配置、预设或输入文件")
    try:
        config = prepare(json.loads(args.base.read_text(encoding="utf-8-sig")), args.start, args.end)
        banner_base = json.loads(args.banner_base.read_text(encoding="utf-8-sig"))
        event = config["EventOptions"]["EventList"][-1]
        banners = prepare_banners(banner_base, event, args.banner_end)
        if any(path.exists() for path in outputs):
            raise FileExistsError("输出文件已存在，请另选文件名，避免覆盖已有待上线配置")
        for path, data in zip(outputs, (config, banners)):
            with path.open("x", encoding="utf-8") as output:
                json.dump(data, output, ensure_ascii=False, indent=2)
                output.write("\n")
    except (ValueError, FileExistsError) as error:
        parser.error(str(error))
    print(f"已生成待上线活动配置：{args.out}")
    print(f"已生成待上线卡池配置：{args.banner_out}（未部署）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
