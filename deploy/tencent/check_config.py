"""部署前检查这个目录里的配置：活动编号、卡池编号、角色和龙的名字、时间。

服务器读配置时，写错的角色名、龙名会被悄悄丢掉，不报错也不记日志，所以先在这里查一遍。
tat_run.py 部署前会自动跑；也可以单独跑：python3 deploy/tencent/check_config.py
"""

import argparse
import json
import re
import sys
from datetime import datetime
from pathlib import Path

HERE = Path(__file__).resolve().parent
SHARED = HERE.parent.parent / "DragaliaAPI" / "DragaliaAPI.Shared"


def read_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def enum_names(name: str) -> set[str]:
    text = (SHARED / "Definitions" / "Enums" / f"{name}.cs").read_text(encoding="utf-8-sig")
    body = text.split(f"public enum {name}", 1)[1].split("}", 1)[0]
    # 服务器认名字时不分大小写
    return {x.lower() for x in re.findall(r"^\s*(\w+)\s*=", body, re.MULTILINE)}


def master_ids(relative_path: str) -> set[int]:
    return {x["_Id"] for x in read_json(SHARED / "Resources" / relative_path)}


def check_period(item: dict, where: str, errors: list[str]) -> None:
    times = []
    for key in ("Start", "End"):
        try:
            times.append(datetime.fromisoformat(item[key].replace("Z", "+00:00")))
        except (KeyError, AttributeError, ValueError):
            errors.append(f"{where}：{key} 没写或看不懂，要写成 2026-10-18T06:00:00Z 这样")
    if len(times) == 2 and times[0] >= times[1]:
        errors.append(f"{where}：开始时间要早于结束时间")


def check(
    events_path: Path = HERE / "appsettings.Production.json",
    banners_path: Path = HERE / "bannerConfig.json",
) -> list[str]:
    errors: list[str] = []

    event_ids = master_ids("Event/EventData.json")
    event_options = read_json(events_path).get("EventOptions", {})
    events = event_options.get("EventList", [])
    for index, event in enumerate(events, 1):
        where = f"appsettings.Production.json 第 {index} 个活动（{event.get('Id')}）"
        if event.get("Id") not in event_ids:
            errors.append(f"{where}：游戏里没有这个活动编号")
        check_period(event, where, errors)

    archives = event_options.get("ArchivedEventIds", [])
    if not isinstance(archives, list) or any(type(value) is not int for value in archives):
        errors.append("ArchivedEventIds 必须是活动编号组成的数组")
    else:
        if len(archives) != len(set(archives)):
            errors.append("ArchivedEventIds 的活动编号不能重复")
        # Expand only after another original event has passed the compendium compatibility checks.
        for event_id in archives:
            if event_id not in event_ids or event_id != 20401:
                errors.append(f"活动 {event_id} 尚未适配回忆入口")

    summon_ids = master_ids("Summon/SummonData.json")
    names = {"Charas": enum_names("Charas"), "Dragons": enum_names("DragonId")}
    summon_options = read_json(banners_path).get("SummonBannerOptions", {})
    if not isinstance(summon_options.get("FilterPoolByReleaseDate", False), bool):
        errors.append("bannerConfig.json：FilterPoolByReleaseDate 只能写 true 或 false")
    banners = summon_options.get("Banners", [])
    banner_ids = [banner.get("Id") for banner in banners]
    for duplicate in sorted({x for x in banner_ids if banner_ids.count(x) > 1}):
        errors.append(f"bannerConfig.json：卡池编号 {duplicate} 写了不止一次")
    for index, banner in enumerate(banners, 1):
        where = f"bannerConfig.json 第 {index} 个卡池（{banner.get('Id')}）"
        if banner.get("Id") not in summon_ids:
            errors.append(f"{where}：游戏里没有这个卡池编号")
        check_period(banner, where, errors)
        for kind, label in (("Charas", "角色"), ("Dragons", "龙")):
            for key in (f"Pickup{kind}", f"Limited{kind}", f"Trade{kind}"):
                for name in banner.get(key, []):
                    if str(name).lower() not in names[kind]:
                        errors.append(f"{where} 的 {key}：没有叫 {name} 的{label}")

    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--events", type=Path, default=HERE / "appsettings.Production.json")
    parser.add_argument("--banners", type=Path, default=HERE / "bannerConfig.json")
    args = parser.parse_args()
    errors = check(args.events, args.banners)
    for error in errors:
        print(f"配置有误：{error}", file=sys.stderr)
    if not errors:
        print("配置检查通过")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
