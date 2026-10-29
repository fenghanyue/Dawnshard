"""读写游戏的资源包（Unity 的 UnityFS 格式）和加密的资源清单，给 client_master.py 用。

只做"原地替换"：改动前后每个对象的字节数不变，包里其余内容一个字节都不动，没改到的压缩块
照抄原文件。资源清单用 Rijndael（256 位分组）加密，密钥是原作者的改表工具
SapiensAnatis/DragaliaModTools 里公开的那一把。
"""

import base64
import hashlib
import struct
from collections.abc import Callable

import lz4.block
import UnityPy

MANIFEST_KEY = base64.b64decode("2JDKdLwjKMDLgxXGsI4AxBQ9t7d7of9Jp5gQkdBryoM=")
MANIFEST_IV = base64.b64decode("HzL3PqQVDY4H1QvMn5KghO+Is8NnJ+ydTYafQb+8HpI=")

# 压缩块的类型（块标志位的低 6 位）
STORED, LZ4HC = 0, 3
COMPRESSION_MASK = 0x3F
# 文件头里的标志位：块信息用 LZ4HC 压缩、紧跟在文件头后面。这个游戏的资源包都是这样
EXPECTED_FLAGS = 0x43


def asset_hash(data: bytes) -> str:
    """资源的文件名：内容的 SHA-256 转成 base32，取前 52 个字符（游戏就是这么截的）。"""
    return base64.b32encode(hashlib.sha256(data).digest()).decode()[:52]


# ---- Rijndael，分组和密钥都是 256 位。AES 是它 128 位分组的特例，常见的加密库都不支持 256 位分组 ----

NB = 8  # 每个分组 8 列，共 32 字节
ROUNDS = 14
ROW_SHIFTS = (0, 1, 3, 4)


def _xtime(a: int) -> int:
    a <<= 1
    return a ^ 0x11B if a & 0x100 else a


def _gf_mul(a: int, b: int) -> int:
    result = 0
    while b:
        if b & 1:
            result ^= a
        a = _xtime(a)
        b >>= 1
    return result


def _make_sbox() -> list[int]:
    sbox = []
    for x in range(256):
        inverse = 0
        if x:
            inverse = 1
            for _ in range(254):  # x 的 254 次方就是 x 在 GF(2^8) 里的逆
                inverse = _gf_mul(inverse, x)
        s = inverse
        for shift in range(1, 5):
            s ^= ((inverse << shift) | (inverse >> (8 - shift))) & 0xFF
        sbox.append(s ^ 0x63)
    return sbox


SBOX = _make_sbox()
INV_SBOX = [0] * 256
for _index, _value in enumerate(SBOX):
    INV_SBOX[_value] = _index
MUL = {k: [_gf_mul(x, k) for x in range(256)] for k in (2, 3, 9, 11, 13, 14)}
# 分组里第 r 行第 c 列的字节在下标 r + 4c
SHIFT_ROWS = [r + 4 * ((c + ROW_SHIFTS[r]) % NB) for c in range(NB) for r in range(4)]
INV_SHIFT_ROWS = [r + 4 * ((c - ROW_SHIFTS[r]) % NB) for c in range(NB) for r in range(4)]


def _round_keys(key: bytes) -> list[list[int]]:
    nk = len(key) // 4
    words = [list(key[4 * i : 4 * i + 4]) for i in range(nk)]
    rcon = 1
    for i in range(nk, NB * (ROUNDS + 1)):
        temp = list(words[i - 1])
        if i % nk == 0:
            temp = [SBOX[b] for b in temp[1:] + temp[:1]]
            temp[0] ^= rcon
            rcon = _xtime(rcon)
        elif nk > 6 and i % nk == 4:
            temp = [SBOX[b] for b in temp]
        words.append([a ^ b for a, b in zip(words[i - nk], temp)])
    return [sum(words[r * NB : (r + 1) * NB], []) for r in range(ROUNDS + 1)]


def _encrypt_block(state: list[int], keys: list[list[int]]) -> list[int]:
    m2, m3 = MUL[2], MUL[3]
    s = [b ^ k for b, k in zip(state, keys[0])]
    for rnd in range(1, ROUNDS + 1):
        s = [SBOX[s[i]] for i in SHIFT_ROWS]
        if rnd < ROUNDS:
            mixed = []
            for c in range(0, 32, 4):
                a0, a1, a2, a3 = s[c : c + 4]
                mixed += (
                    m2[a0] ^ m3[a1] ^ a2 ^ a3,
                    a0 ^ m2[a1] ^ m3[a2] ^ a3,
                    a0 ^ a1 ^ m2[a2] ^ m3[a3],
                    m3[a0] ^ a1 ^ a2 ^ m2[a3],
                )
            s = mixed
        s = [b ^ k for b, k in zip(s, keys[rnd])]
    return s


def _decrypt_block(state: list[int], keys: list[list[int]]) -> list[int]:
    m9, m11, m13, m14 = MUL[9], MUL[11], MUL[13], MUL[14]
    s = [b ^ k for b, k in zip(state, keys[ROUNDS])]
    for rnd in range(ROUNDS - 1, -1, -1):
        s = [INV_SBOX[s[i]] for i in INV_SHIFT_ROWS]
        s = [b ^ k for b, k in zip(s, keys[rnd])]
        if rnd:
            mixed = []
            for c in range(0, 32, 4):
                a0, a1, a2, a3 = s[c : c + 4]
                mixed += (
                    m14[a0] ^ m11[a1] ^ m13[a2] ^ m9[a3],
                    m9[a0] ^ m14[a1] ^ m11[a2] ^ m13[a3],
                    m13[a0] ^ m9[a1] ^ m14[a2] ^ m11[a3],
                    m11[a0] ^ m13[a1] ^ m9[a2] ^ m14[a3],
                )
            s = mixed
    return s


def encrypt_manifest(plain: bytes) -> bytes:
    """CBC + PKCS7 加密，末尾接上密文的 SHA-256，和原版清单的格式一样。"""
    keys = _round_keys(MANIFEST_KEY)
    pad = 32 - len(plain) % 32
    data = plain + bytes([pad]) * pad
    previous = list(MANIFEST_IV)
    out = bytearray()
    for offset in range(0, len(data), 32):
        block = [a ^ b for a, b in zip(data[offset : offset + 32], previous)]
        previous = _encrypt_block(block, keys)
        out += bytes(previous)
    return bytes(out) + hashlib.sha256(out).digest()


def decrypt_manifest(data: bytes) -> bytes:
    body, digest = data[:-32], data[-32:]
    if len(body) % 32 or hashlib.sha256(body).digest() != digest:
        raise ValueError("资源清单末尾的 SHA-256 对不上，文件不完整或不是资源清单")
    keys = _round_keys(MANIFEST_KEY)
    previous = list(MANIFEST_IV)
    out = bytearray()
    for offset in range(0, len(body), 32):
        block = list(body[offset : offset + 32])
        out += bytes(a ^ b for a, b in zip(_decrypt_block(block, keys), previous))
        previous = block
    pad = out[-1]
    if not 1 <= pad <= 32 or out[-pad:] != bytes([pad]) * pad:
        raise ValueError("资源清单解密后的填充不对")
    return bytes(out[:-pad])


# ---- UnityFS 资源包 ----


class UnityFS:
    """UnityFS 资源包：文件头、压缩过的块信息，后面是一串压缩块。块信息里记着每块的大小和压缩方式，
    还有包里的文件列表；原地替换不改大小，文件列表原样保留。"""

    def __init__(self, data: bytes):
        pos = 0

        def read_cstr() -> bytes:
            nonlocal pos
            end = data.index(b"\0", pos)
            text = data[pos:end]
            pos = end + 1
            return text

        self.signature = read_cstr()
        (self.version,) = struct.unpack_from(">I", data, pos)
        pos += 4
        self.player_version = read_cstr()
        self.engine_version = read_cstr()
        _size, info_size, info_raw_size, self.flags = struct.unpack_from(">qIII", data, pos)
        pos += 20
        if self.signature != b"UnityFS" or self.version != 7 or self.flags != EXPECTED_FLAGS:
            raise ValueError(f"不支持的资源包格式：{self.signature!r} 版本 {self.version} 标志 {self.flags:#x}")
        pos += -pos % 16
        info = lz4.block.decompress(data[pos : pos + info_size], uncompressed_size=info_raw_size)
        pos += info_size

        self.data_hash = info[:16]
        (count,) = struct.unpack_from(">i", info, 16)
        self.blocks: list[tuple[int, int, bytes]] = []  # (解压后大小, 标志, 压缩后的内容)
        for i in range(count):
            raw_size, size, flags = struct.unpack_from(">IIH", info, 20 + 10 * i)
            self.blocks.append((raw_size, flags, data[pos : pos + size]))
            pos += size
        if pos != len(data):
            raise ValueError("资源包最后一块后面还有多余的数据")
        self.directory = info[20 + 10 * count :]

    def contents(self) -> bytes:
        out = bytearray()
        for raw_size, flags, packed in self.blocks:
            match flags & COMPRESSION_MASK:
                case 0:
                    out += packed
                case 2 | 3:
                    out += lz4.block.decompress(packed, uncompressed_size=raw_size)
                case kind:
                    raise ValueError(f"不支持的压缩方式 {kind}")
        return bytes(out)

    def rebuild(self, contents: bytes) -> bytes:
        """换上新的内容（大小必须和原来一样）重新打包。内容没变的块照抄原文件。"""
        original = self.contents()
        if len(contents) != len(original):
            raise ValueError("新内容和原来的大小不一样")
        blocks = []
        pos = 0
        for raw_size, flags, packed in self.blocks:
            chunk = contents[pos : pos + raw_size]
            if chunk != original[pos : pos + raw_size]:
                packed = lz4.block.compress(chunk, mode="high_compression", compression=12, store_size=False)
                kind = LZ4HC
                if len(packed) >= raw_size:  # 压不小就原样存，和 Unity 一样
                    packed, kind = chunk, STORED
                flags = (flags & ~COMPRESSION_MASK) | kind
            blocks.append((raw_size, flags, packed))
            pos += raw_size

        info = bytearray(self.data_hash)
        info += struct.pack(">i", len(blocks))
        for raw_size, flags, packed in blocks:
            info += struct.pack(">IIH", raw_size, len(packed), flags)
        info += self.directory
        packed_info = lz4.block.compress(bytes(info), mode="high_compression", compression=12, store_size=False)

        head = bytearray(self.signature + b"\0")
        head += struct.pack(">I", self.version)
        head += self.player_version + b"\0" + self.engine_version + b"\0"
        size_pos = len(head)
        head += struct.pack(">qIII", 0, len(packed_info), len(info), self.flags)
        head += bytes(-len(head) % 16)
        body = packed_info + b"".join(packed for _, _, packed in blocks)
        struct.pack_into(">q", head, size_pos, len(head) + len(body))
        return bytes(head) + body


def _monobehaviours(bundle: bytes):
    for obj in UnityPy.load(bundle).objects:
        if obj.type.name == "MonoBehaviour":
            yield obj.peek_name(), obj


def read_assets(bundle: bytes, names: set[str]) -> dict[str, dict]:
    """把包里这些名字的 MonoBehaviour 读成字典。"""
    found = {name: obj.read_typetree() for name, obj in _monobehaviours(bundle) if name in names}
    if missing := names - found.keys():
        raise ValueError(f"资源包里没有这些表：{sorted(missing)}")
    return found


def patch_assets(bundle: bytes, edits: dict[str, Callable[[dict], int]]) -> tuple[bytes, dict[str, int]]:
    """按名字找到包里的 MonoBehaviour，读成字典交给 edits 里对应的函数去改（函数返回改了几处），
    再写回原来的位置。写回的字节数必须和原来一样，所以只能换成一样长的值。"""
    contents = bytearray(UnityFS(bundle).contents())
    counts = {}
    for name, obj in _monobehaviours(bundle):
        if name not in edits:
            continue
        original = obj.get_raw_data()
        if contents[obj.byte_start : obj.byte_start + obj.byte_size] != original:
            raise ValueError(f"{name} 在资源包里的位置不对")
        tree = obj.read_typetree()
        if obj.save_typetree(tree) != original:
            raise ValueError(f"{name} 读出来再写回去和原来不一样，不能原地改")
        counts[name] = edits[name](tree)
        if counts[name]:
            patched = obj.save_typetree(tree)
            if len(patched) != len(original):
                raise ValueError(f"{name} 改完以后大小变了")
            contents[obj.byte_start : obj.byte_start + obj.byte_size] = patched
    if missing := edits.keys() - counts.keys():
        raise ValueError(f"资源包里没有这些表：{sorted(missing)}")
    if not any(counts.values()):
        return bundle, counts
    return UnityFS(bundle).rebuild(bytes(contents)), counts


def changed_assets(old: bytes, new: bytes) -> set[str]:
    """两个资源包里内容不一样的 MonoBehaviour 的名字。"""
    before = {name: obj.get_raw_data() for name, obj in _monobehaviours(old)}
    after = {name: obj.get_raw_data() for name, obj in _monobehaviours(new)}
    if before.keys() != after.keys():
        raise ValueError("两个资源包里的表不一样")
    return {name for name in before if before[name] != after[name]}


def _master_entry(tree: dict) -> dict:
    entry = tree["categories"][0]["assets"][0]
    if entry["name"] != "master":
        raise ValueError(f"资源清单的第一项不是数据表，而是 {entry['name']}")
    return entry


def manifest_master(plain: bytes) -> tuple[str, int]:
    """资源清单（解密后）里数据表的文件名和大小。"""
    entry = _master_entry(read_assets(plain, {"manifest"})["manifest"])
    return entry["hash"], entry["size"]


def set_manifest_master(plain: bytes, name: str, size: int) -> bytes:
    """把资源清单（解密后）里数据表的文件名和大小换掉，别的不动。"""

    def edit(tree: dict) -> int:
        entry = _master_entry(tree)
        if len(entry["hash"]) != len(name):
            raise ValueError("新文件名和原来的长度不一样")
        entry["hash"], entry["size"] = name, size
        return 1

    patched, _ = patch_assets(plain, {"manifest": edit})
    return patched
