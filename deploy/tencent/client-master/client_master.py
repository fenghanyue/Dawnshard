"""按服务器的活动安排，改手机端游戏数据表（master）里的活动日期，生成新的资源清单和资源版本号。

游戏里显示哪些活动，看的是手机从资源站下载的数据表里写的日期（还是当年的日期）；服务器的
EventOptions 只管放不放行。所以要把数据表里的日期改成服务器排的时间，再让手机重新下载：

1. 下载原版的资源清单和 5 个语言包的数据表，按写死的校验值核对，存在 --cache-dir 里，下次不用再下；
2. 把 appsettings.Production.json 的 EventOptions.EventList 里排的活动，日期改成服务器的时间
   （规则见 EventSchedule）。只换成一样长的日期，数据表别的地方一个字节都不动；
3. 改过的数据表按内容重新取文件名，更新资源清单里这个文件的名字和大小，重新加密清单；
4. 写到 --out-dir：dl/manifests/Android/<新版本号>/ 下是清单，dl/assetbundles/Android/xx/ 下是数据表。
   资源缓存（../asset-cache/default.conf）先从这里找文件，找不到再去资源站。

新版本号由输入算出来：活动安排、活动召唤配置、这个脚本和依赖都没变，版本号就不变，生成过的直接跳过。
没排活动时直接用原版的版本号。标准输出只有最后一行的版本号，其他信息都在标准错误里。

用法（部署时 deploy.sh 会自动跑）：
    python3 client_master.py --events appsettings.Production.json --event-summons eventSummon.json \\
        --cache-dir /opt/dawnshard/client-master/cache --out-dir /opt/asset-cache/overrides
"""

import argparse
import base64
import hashlib
import http.client
import json
import os
import shutil
import sys
import time
import urllib.request
from collections.abc import Callable, Iterator
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path

import unity_bundle

HERE = Path(__file__).resolve().parent

# 原作者服务器默认用的资源版本，以及这个版本 5 个资源清单（加密后的文件）的 SHA-256
SOURCE_VERSION = "y2XM6giU6zz56wCm"
SOURCE_MANIFESTS = {
    "assetbundle.manifest": "ac954f60f329396f253024f5c3bb4a0d0c5bcaed2f1e3e24f283c057c127303a",
    "assetbundle.en_us.manifest": "3410da49b47255003bd84c69dc9a6fbba3f28979f525127eabe2aaaf79087a6b",
    "assetbundle.en_eu.manifest": "25ec7574ebb1b70f87dda4247b65b53707c0a288d598aa637b62f549a3b043f6",
    "assetbundle.zh_cn.manifest": "dc1c22a423a16dcd4c13a6266c1629c211fbd150320c741490c31e7b1dc7a6d2",
    "assetbundle.zh_tw.manifest": "03a3b86d68682ff2545578b80a42be7cb17e66202c9527b067b8f466a62ebb2e",
}
# 只有安卓手机能连私服，生成的文件放在安卓的路径下
PLATFORM = "Android"
KEEP_VERSIONS = 5
DATE_FORMAT = "%Y/%m/%d %H:%M:%S"
VERSION_FILES = ["client_master.py", "unity_bundle.py", "requirements.txt", "requirements-build.txt"]

# 除了活动表，这些表里属于某个活动的行，日期也跟着活动走：表名 → (哪个字段是活动编号, 日期字段)
FOLLOWING_TABLES = {
    "QuestData": ("_Gid", ("_ViewStartDate", "_ViewEndDate", "_ViewStartDate2", "_ViewEndDate2")),
    "QuestEventGroup": ("_Id", ("_ViewStartDate", "_ViewEndDate")),
    "EventStory": ("_EventId", ("_ViewStartDate", "_ViewEndDate")),
    "EventTradeGroup": ("_EventId", ("_CommenceDate", "_CompleteDate")),
    "MyPageBanner": ("_EventId", ("_ViewStartDate", "_ViewEndDate")),
    "MyPageCenterBanner": ("_EventId", ("_ViewStartDate", "_ViewEndDate")),
    "MyPageLimitedAreaGroup": ("_EventId", ("_ViewStartDate", "_ViewEndDate")),
}
# 活动召唤（宝箱抽奖）只打开服务器 eventSummon.json 里配了奖品的活动，没配的打开了一点就报错
BOX_SUMMON_TABLE = ("BoxSummonData", "_EventId", ("_CommenceDate", "_CompleteDate"))
TABLES = {"EventData", "CharaData", *FOLLOWING_TABLES, BOX_SUMMON_TABLE[0]}


def log(message: str) -> None:
    print(message, file=sys.stderr, flush=True)


def to_master_date(date: datetime) -> str:
    return date.strftime(DATE_FORMAT)


def from_master_date(text: str) -> datetime:
    # 数据表里的时间都是 UTC
    return datetime.strptime(text, DATE_FORMAT)


@dataclass(frozen=True)
class EventRun:
    id: int
    start: datetime  # UTC
    end: datetime


def load_runs(path: Path) -> list[EventRun]:
    def parse(text: str) -> datetime:
        return datetime.fromisoformat(text.replace("Z", "+00:00")).astimezone(UTC).replace(tzinfo=None)

    settings = json.loads(path.read_text(encoding="utf-8-sig"))
    runs = [
        EventRun(int(item["Id"]), parse(item["Start"]), parse(item["End"]))
        for item in settings.get("EventOptions", {}).get("EventList", [])
    ]
    ids = [run.id for run in runs]
    if duplicates := sorted({x for x in ids if ids.count(x) > 1}):
        raise SystemExit(f"活动 {duplicates} 排了不止一次：手机的数据表里一个活动只能写一个时间")
    return sorted(runs, key=lambda run: (run.start, run.id))


def load_box_summons(path: Path) -> set[int]:
    settings = json.loads(path.read_text(encoding="utf-8-sig"))
    return {int(item["EventId"]) for item in settings.get("EventSummons", [])}


def plan_version(runs: list[EventRun], box_summons: set[int]) -> str:
    code = hashlib.sha256()
    for name in VERSION_FILES:
        code.update((HERE / name).read_bytes())
    plan = {
        "source": SOURCE_VERSION,
        "events": [[run.id, to_master_date(run.start), to_master_date(run.end)] for run in runs],
        "box_summons": sorted(box_summons),
        "code": code.hexdigest(),
    }
    digest = hashlib.sha256(json.dumps(plan, sort_keys=True).encode()).digest()
    return base64.b32encode(digest).decode()[:16]


class EventSchedule:
    """一个活动当年的时间和这次的时间的对照。

    开始、结束用服务器排的时间。预告提前几天照当年。结束以后不再显示：当年结束后还能进去看一周，
    但服务器到点就不放行了，点进去会报错。其他日期（比如开始几天后才解锁的关卡）按开始时间的差值
    平移；当年等于活动结束、关闭显示时间的，换成这次的结束时间；平移后晚于结束的，截到结束。
    """

    def __init__(self, origin: dict, run: EventRun):
        try:
            self.origin_start = from_master_date(origin["_StartDate"])
            self.origin_ends = {from_master_date(origin["_EndDate"]), from_master_date(origin["_ViewCloseDate"])}
        except ValueError:
            raise SystemExit(f"活动 {run.id} 在数据表里没有完整的时间，不能排") from None
        self.run = run

    def move(self, text: str) -> str:
        if not text:
            return text
        date = from_master_date(text)
        if date in self.origin_ends:
            return to_master_date(self.run.end)
        return to_master_date(min(date + (self.run.start - self.origin_start), self.run.end))

    def event_dates(self, row: dict) -> dict[str, str]:
        dates = {
            "_StartDate": to_master_date(self.run.start),
            "_EndDate": to_master_date(self.run.end),
            "_ViewCloseDate": to_master_date(self.run.end),
        }
        if row["_ViewAnnounceDate"]:
            lead = self.origin_start - from_master_date(row["_ViewAnnounceDate"])
            dates["_ViewAnnounceDate"] = to_master_date(self.run.start - lead)
        if row["_ViewNewDate"]:
            dates["_ViewNewDate"] = self.move(row["_ViewNewDate"])
        return dates


def rows(tree: dict) -> Iterator[dict]:
    """数据表是序列化的 .NET 字典：前 count 个槽位是数据，后面是空位。"""
    table = tree["dict"]
    for index in range(table["count"]):
        if table["entriesHashCode"][index] >= 0:
            yield table["entriesValue"][index]


# 每张表要改的行：表名 → {行的 _Id → {字段: 新值}}
Changes = dict[str, dict[int, dict[str, str]]]


def plan_changes(tables: dict[str, dict], runs: list[EventRun], box_summons: set[int]) -> Changes:
    """按原版数据表算出每一行要改成什么，只留真的会变的字段。"""
    origins = {row["_Id"]: row for row in rows(tables["EventData"])}
    if missing := [run.id for run in runs if run.id not in origins]:
        raise SystemExit(f"数据表里没有这些活动：{missing}")
    schedules = {run.id: EventSchedule(origins[run.id], run) for run in runs}
    # 活动角色在活动期间只用专属材料养成（2020 年以后的部分活动），时间也要跟服务器一致
    grow_runs = {origins[run.id]["_EventCharaId"]: run for run in runs if origins[run.id]["_EventCharaId"]}
    changes: Changes = {}

    def add(table: str, row: dict, values: dict[str, str]) -> None:
        values = {key: value for key, value in values.items() if row[key] != value}
        for key, value in values.items():
            if len(row[key]) != len(value):
                raise SystemExit(f"{table} {row['_Id']} 的 {key} 新旧值长度不一样：{row[key]!r} → {value!r}")
        if values:
            changes.setdefault(table, {})[row["_Id"]] = values

    for row in rows(tables["EventData"]):
        if row["_Id"] in schedules:
            add("EventData", row, schedules[row["_Id"]].event_dates(row))
    for row in rows(tables["CharaData"]):
        if run := grow_runs.get(row["_Id"]):
            dates = {"_GrowMaterialOnlyStartDate": run.start, "_GrowMaterialOnlyEndDate": run.end}
            add("CharaData", row, {key: to_master_date(date) for key, date in dates.items() if row[key]})
    following = [(table, key, fields, set(schedules)) for table, (key, fields) in FOLLOWING_TABLES.items()]
    following.append((*BOX_SUMMON_TABLE, set(schedules) & box_summons))
    for table, key, fields, events in following:
        for row in rows(tables[table]):
            if row[key] in events:
                add(table, row, {field: schedules[row[key]].move(row[field]) for field in fields})
    return changes


def make_edits(changes: Changes) -> dict[str, Callable[[dict], int]]:
    """把算好的新值写进读出来的表，返回写了几处。已经是新值的不算，所以对改好的表再跑一遍应该是 0。"""

    def edit_for(targets: dict[int, dict[str, str]]) -> Callable[[dict], int]:
        def edit(tree: dict) -> int:
            changed = 0
            for row in rows(tree):
                for key, value in targets.get(row["_Id"], {}).items():
                    if row[key] != value:
                        row[key] = value
                        changed += 1
            return changed

        return edit

    return {table: edit_for(targets) for table, targets in changes.items()}


def fetch(url: str) -> bytes:
    # 资源站会拒绝 Python 默认的 User-Agent（Python-urllib/x.y）
    request = urllib.request.Request(url, headers={"User-Agent": "Dawnshard-private-server client_master.py"})
    error: Exception | None = None
    for attempt in range(3):
        try:
            with urllib.request.urlopen(request, timeout=120) as response:
                return response.read()
        except (OSError, http.client.HTTPException) as e:
            error = e
            time.sleep(5 * (attempt + 1))
    raise SystemExit(f"下载失败：{url}（{error}）")


def write_file(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(path.name + ".tmp")
    temp.write_bytes(data)
    # 资源缓存里的 nginx 不是 root，要能读到
    temp.chmod(0o644)
    os.replace(temp, path)


class Sources:
    """原版的资源清单和数据表。先看缓存目录里有没有，没有（或者坏了）再下载，都要核对校验值。"""

    def __init__(self, url: str, platform: str, cache_dir: Path):
        self.url = url.rstrip("/")
        self.platform = platform
        self.cache_dir = cache_dir

    def _get(self, path: Path, url: str, valid: Callable[[bytes], bool], what: str) -> bytes:
        if path.exists():
            data = path.read_bytes()
            if valid(data):
                return data
            path.unlink()
        data = fetch(url)
        if not valid(data):
            raise SystemExit(f"{what} 的大小或校验值不对，可能下载不完整或资源站换了文件")
        write_file(path, data)
        return data

    def manifest(self, name: str) -> bytes:
        return self._get(
            self.cache_dir / "manifests" / SOURCE_VERSION / name,
            f"{self.url}/manifests/{self.platform}/{SOURCE_VERSION}/{name}",
            lambda data: hashlib.sha256(data).hexdigest() == SOURCE_MANIFESTS[name],
            f"原版资源清单 {name}",
        )

    def asset(self, name: str, size: int) -> bytes:
        return self._get(
            self.cache_dir / "assetbundles" / name,
            f"{self.url}/assetbundles/{self.platform}/{name[:2]}/{name}",
            lambda data: len(data) == size and unity_bundle.asset_hash(data) == name,
            f"原版数据表 {name}",
        )


def build_locale(
    manifest_name: str, sources: Sources, runs: list[EventRun], box_summons: set[int], out_dir: Path
) -> tuple[bytes, str]:
    """改一个语言包的数据表，返回新的清单（加密后）和新数据表的文件名。"""
    plain = unity_bundle.decrypt_manifest(sources.manifest(manifest_name))
    master_name, master_size = unity_bundle.manifest_master(plain)
    master = sources.asset(master_name, master_size)

    changes = plan_changes(unity_bundle.read_assets(master, TABLES), runs, box_summons)
    edits = make_edits(changes)
    patched, counts = unity_bundle.patch_assets(master, edits)

    # 核对：写进去的处数和算好的一样，只有这些表变了，读回来再写一遍没有要改的
    planned = {table: sum(len(values) for values in targets.values()) for table, targets in changes.items()}
    if counts != planned:
        raise SystemExit(f"{manifest_name}：改了的处数和算好的不一样：{counts} / {planned}")
    if unity_bundle.changed_assets(master, patched) != set(changes):
        raise SystemExit(f"{manifest_name}：改完的数据表里，变了的表和预期不一样")
    _, again = unity_bundle.patch_assets(patched, edits)
    if any(again.values()):
        raise SystemExit(f"{manifest_name}：改完的数据表读回来，日期还不对：{again}")

    new_name = unity_bundle.asset_hash(patched)
    write_file(out_dir / "dl" / "assetbundles" / PLATFORM / new_name[:2] / new_name, patched)
    new_plain = unity_bundle.set_manifest_master(plain, new_name, len(patched))
    if unity_bundle.manifest_master(new_plain) != (new_name, len(patched)):
        raise SystemExit(f"{manifest_name}：新清单里的数据表对不上")
    summary = "，".join(f"{name} {count} 处" for name, count in sorted(counts.items()) if count)
    log(f"  {manifest_name}：{summary}")
    return unity_bundle.encrypt_manifest(new_plain), new_name


def clean_up(out_dir: Path, current: str) -> None:
    """只留最近生成的几个版本，没有版本再用的数据表一起删掉。"""
    versions_dir = out_dir / "dl" / "manifests" / PLATFORM
    # 以前中途失败留下的半成品
    for path in versions_dir.glob(".*.tmp"):
        shutil.rmtree(path)
    versions = sorted(
        (path for path in versions_dir.iterdir() if path.is_dir() and not path.name.startswith(".")),
        key=lambda path: path.stat().st_mtime,
        reverse=True,
    )
    others = [path for path in versions if path.name != current]
    kept = [path for path in versions if path.name == current] + others[: KEEP_VERSIONS - 1]
    for path in versions:
        if path not in kept:
            shutil.rmtree(path)
            log(f"删掉旧版本 {path.name}")
    used = set()
    for path in kept:
        if not (path / ".masters.json").exists():
            return
        used.update(json.loads((path / ".masters.json").read_text()))
    for path in (out_dir / "dl" / "assetbundles" / PLATFORM).glob("*/*"):
        if path.name not in used:
            path.unlink()


def generate(args: argparse.Namespace) -> str:
    runs = load_runs(args.events)
    if not runs:
        log("没有排活动，用原版的资源版本")
        return SOURCE_VERSION
    box_summons = load_box_summons(args.event_summons) & {run.id for run in runs}
    version = plan_version(runs, box_summons)
    for run in runs:
        summon = "，活动召唤打开" if run.id in box_summons else ""
        log(f"活动 {run.id}：{to_master_date(run.start)} 到 {to_master_date(run.end)}（UTC）{summon}")

    versions_dir = args.out_dir / "dl" / "manifests" / PLATFORM
    target = versions_dir / version
    if target.exists():
        log(f"资源版本 {version} 已经生成过，直接用")
        os.utime(target)
    else:
        log(f"生成资源版本 {version}")
        sources = Sources(args.source_url, args.source_platform, args.cache_dir)
        staging = versions_dir / f".{version}.tmp"
        shutil.rmtree(staging, ignore_errors=True)
        staging.mkdir(parents=True)
        masters = []
        for manifest_name in SOURCE_MANIFESTS:
            manifest, master_name = build_locale(manifest_name, sources, runs, box_summons, args.out_dir)
            write_file(staging / manifest_name, manifest)
            masters.append(master_name)
        write_file(staging / ".masters.json", json.dumps(masters).encode())
        staging.chmod(0o755)
        os.replace(staging, target)
    clean_up(args.out_dir, version)
    return version


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--events", type=Path, required=True, help="appsettings.Production.json")
    parser.add_argument("--event-summons", type=Path, required=True, help="服务器的 eventSummon.json")
    parser.add_argument("--source-url", default="http://127.0.0.1:8081/dl", help="下载原版文件的地址，默认经资源缓存")
    parser.add_argument("--source-platform", default=PLATFORM, help="原版文件路径里的平台名，直连资源站时是 universe")
    parser.add_argument("--cache-dir", type=Path, required=True, help="存原版文件的目录")
    parser.add_argument("--out-dir", type=Path, required=True, help="生成的文件放在这里，资源缓存从这里取")
    args = parser.parse_args()
    # 生成的文件要让资源缓存里的 nginx（不是 root）读到
    os.umask(0o022)
    print(generate(args))
    return 0


if __name__ == "__main__":
    sys.exit(main())
