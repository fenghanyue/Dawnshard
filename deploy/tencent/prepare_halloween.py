"""生成第二期活动的待上线配置；不修改线上配置、不连接服务器、不部署。

使用时必须明确传入带时区的开始时间。默认保留原版 9 天时长；也可传 --end。
生成文件供 client_master.py 离线验证，决定上线后再审阅并替换正式配置。
"""

import argparse
import json
from datetime import UTC, datetime, timedelta
from pathlib import Path

HERE = Path(__file__).resolve().parent
PRODUCTION = HERE / "appsettings.Production.json"
EVENT_ID = 20801


def timestamp(value: str) -> datetime:
    date = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if date.tzinfo is None:
        raise ValueError("时间必须包含时区，例如 2031-10-17T14:00:00+08:00")
    return date.astimezone(UTC)


def prepare(config: dict, start: datetime, end: datetime | None = None) -> dict:
    if start.tzinfo is None or end is not None and end.tzinfo is None:
        raise ValueError("开始和结束时间必须包含时区")
    start = start.astimezone(UTC)
    end = end.astimezone(UTC) if end is not None else start + timedelta(days=9, seconds=-1)
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


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--base", type=Path, default=PRODUCTION)
    parser.add_argument("--start", type=timestamp, required=True)
    parser.add_argument("--end", type=timestamp)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    if args.out.resolve() in {PRODUCTION.resolve(), args.base.resolve()}:
        parser.error("请输出到独立的待上线文件，不能覆盖正式配置或输入文件")
    try:
        config = prepare(json.loads(args.base.read_text(encoding="utf-8-sig")), args.start, args.end)
        with args.out.open("x", encoding="utf-8") as output:
            json.dump(config, output, ensure_ascii=False, indent=2)
            output.write("\n")
    except (ValueError, FileExistsError) as error:
        parser.error(str(error))
    print(f"已生成待上线配置：{args.out}（未部署）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
