#!/usr/bin/env python3
"""AssemblyStore (XABA v1) 读写工具 —— 纯 Python，无第三方依赖。

用途：把重新编译/注入过的程序集**原位替换**回 `assemblies.blob`，保持
条目数量与 hash 映射表不变（因此 libxamarin-app.so 里的 assembly_image_cache 依旧有效）。

格式（实测，见 docs/audit/runtime-feasibility.md §4.1）：
    offset 0   : 'XABA' magic (0x41424158)
    offset 4   : u32 version = 1
    offset 8   : u32 entry_count
    offset 12  : u32 total_count（所有 store 的条目总和）
    offset 16  : u32 unknown（实测 0；原样保留）
    offset 20  : entry[entry_count]，每项 24 字节
                 { data_offset, data_size, debug_off, debug_size, cfg_off, cfg_size }
    gap        : 条目表之后到第一个负载之间的一段（实测 total_count × 40 字节的
                 hash→(store,index) 映射表；原样保留）
    payload    : 'XALZ' + u32 descriptor_index + u32 uncompressed_length + LZ4 block

命令：
    list    <blob>                      列出条目（偏移/大小/名称）
    extract <blob> <outdir>              解出全部程序集
    replace <blob> <name> <newdll> <out> 原位替换一个条目并写出新 blob
    verify  <blob> [--expect <name>=<sha256> ...]
                                         结构校验 + 可选的内容哈希校验
    selftest <blob>                      LZ4 编解码往返自测（58 条全过）
"""

import hashlib
import os
import struct
import sys

MAGIC = 0x41424158
MANIFEST = "assemblies.manifest"


# --------------------------------------------------------------------------- LZ4

def lz4_decompress(src: bytes, out_len: int) -> bytes:
    dst = bytearray()
    i, n = 0, len(src)
    while i < n:
        token = src[i]
        i += 1
        lit = token >> 4
        if lit == 15:
            while True:
                b = src[i]
                i += 1
                lit += b
                if b != 255:
                    break
        dst += src[i:i + lit]
        i += lit
        if i >= n:
            break
        offset = src[i] | (src[i + 1] << 8)
        i += 2
        mlen = token & 0x0F
        if mlen == 15:
            while True:
                b = src[i]
                i += 1
                mlen += b
                if b != 255:
                    break
        mlen += 4
        start = len(dst) - offset
        if start < 0:
            raise ValueError("invalid match offset")
        for k in range(mlen):
            dst.append(dst[start + k])
    if len(dst) != out_len:
        raise ValueError("decompressed %d != expected %d" % (len(dst), out_len))
    return bytes(dst)


def lz4_compress(src: bytes) -> bytes:
    """贪心 LZ4 block 编码器（hash 表 1<<16，匹配距离 <= 65535）。"""
    n = len(src)
    out = bytearray()
    if n == 0:
        return bytes(out)

    table = {}
    anchor = 0
    i = 0
    limit = n - 12          # 最后 12 字节只做字面量，避免越界匹配
    while i < limit:
        key = src[i:i + 4]
        cand = table.get(key, -1)
        table[key] = i
        if cand >= 0 and i - cand <= 65535 and src[cand:cand + 4] == key:
            # 扩展匹配
            mlen = 4
            while i + mlen < n - 5 and src[cand + mlen] == src[i + mlen]:
                mlen += 1
            lit = i - anchor
            token = (min(lit, 15) << 4) | min(mlen - 4, 15)
            out.append(token)
            if lit >= 15:
                rem = lit - 15
                while rem >= 255:
                    out.append(255)
                    rem -= 255
                out.append(rem)
            out += src[anchor:i]
            out.append((i - cand) & 0xFF)
            out.append(((i - cand) >> 8) & 0xFF)
            if mlen - 4 >= 15:
                rem = mlen - 4 - 15
                while rem >= 255:
                    out.append(255)
                    rem -= 255
                out.append(rem)
            # 把匹配区间内的位置也登记进 hash 表
            for p in range(i + 1, min(i + mlen, limit)):
                table[src[p:p + 4]] = p
            i += mlen
            anchor = i
        else:
            i += 1

    # 收尾：剩余全部作为字面量
    lit = n - anchor
    token = (min(lit, 15) << 4)
    out.append(token)
    if lit >= 15:
        rem = lit - 15
        while rem >= 255:
            out.append(255)
            rem -= 255
        out.append(rem)
    out += src[anchor:]
    return bytes(out)


# --------------------------------------------------------------------------- store

class Store:
    def __init__(self, path, manifest=None):
        self.path = path
        self.manifest = manifest
        self.data = open(path, "rb").read()
        d = self.data
        magic, self.version, self.entry_count, self.total_count = struct.unpack_from("<IIII", d, 0)
        if magic != MAGIC:
            raise ValueError("bad magic %#x" % magic)
        self.reserved = struct.unpack_from("<I", d, 16)[0]
        self.entries = []
        for i in range(self.entry_count):
            off, size, doff, dsize, coff, csize = struct.unpack_from("<IIIIII", d, 20 + i * 24)
            # XALZ 头的第二个 u32 不是"调试索引"，而是 libxamarin-app.so 里
            # 16 字节记录尺寸表的条目下标（运行时会用它取 expected size 做校验）。
            # 重建时必须原样保留，否则会触发
            # "Compressed assembly ... is larger than when the application was built"。
            desc = None
            if off + 12 <= len(d) and d[off:off + 4] == b"XALZ":
                desc = struct.unpack_from("<I", d, off + 4)[0]
            self.entries.append(dict(index=i, offset=off, size=size,
                                     debug_off=doff, debug_size=dsize,
                                     cfg_off=coff, cfg_size=csize, desc=desc))
        table_end = 20 + self.entry_count * 24
        first_payload = min(e["offset"] for e in self.entries)
        self.gap = d[table_end:first_payload]
        self.table_end = table_end
        self.first_payload = first_payload

    def payload(self, e):
        return self.data[e["offset"]:e["offset"] + e["size"]]

    def decode(self, e):
        p = self.payload(e)
        if p[:4] == b"XALZ":
            idx, ulen = struct.unpack_from("<II", p, 4)
            return lz4_decompress(p[12:], ulen), idx
        return p, None

    def names(self):
        mpath = self.manifest or os.path.join(os.path.dirname(os.path.abspath(self.path)), MANIFEST)
        rows = []
        if os.path.exists(mpath):
            for line in open(mpath, "r", encoding="utf-8"):
                parts = line.split()
                if len(parts) != 5 or parts[0] == "Hash":
                    continue
                rows.append((parts[2], int(parts[3]), parts[4]))
        rows.sort(key=lambda r: (r[0], r[1]))
        return [r[2] for r in rows if r[0] == "000"]

    def rebuild(self, replacements):
        """replacements: {entry_index: raw_bytes}；返回新的 blob 字节。"""
        for e in self.entries:
            if e["debug_off"] or e["debug_size"] or e["cfg_off"] or e["cfg_size"]:
                raise NotImplementedError("entry %d has debug/config payloads" % e["index"])

        chunks = []
        for e in self.entries:
            raw = replacements.get(e["index"])
            desc = e.get("desc")
            if desc is None:
                desc = 0
            if raw is None:
                chunks.append(self.payload(e))
                continue
            comp = lz4_compress(raw)
            chunks.append(b"XALZ" + struct.pack("<II", desc, len(raw)) + comp)

        header = struct.pack("<IIIII", MAGIC, self.version, self.entry_count,
                             self.total_count, self.reserved)
        table = bytearray()
        cursor = len(header) + self.entry_count * 24 + len(self.gap)
        body = bytearray()
        for e, chunk in zip(self.entries, chunks):
            table += struct.pack("<IIIIII", cursor, len(chunk), 0, 0, 0, 0)
            body += chunk
            cursor += len(chunk)
        return bytes(header + table + self.gap + body)


# --------------------------------------------------------------------------- cli

def cmd_list(store):
    names = store.names()
    print("%-4s %-10s %-10s %-46s %s" % ("idx", "offset", "size", "name", "raw"))
    for e in store.entries:
        raw, _ = store.decode(e)
        nm = names[e["index"]] if e["index"] < len(names) else "?"
        print("%-4d %-10d %-10d %-46s %d" % (e["index"], e["offset"], e["size"], nm, len(raw)))
    print("\nentry_count=%d total_count=%d gap=%d bytes  file=%d" % (
        store.entry_count, store.total_count, len(store.gap), len(store.data)))


def cmd_extract(store, outdir):
    os.makedirs(outdir, exist_ok=True)
    names = store.names()
    for e in store.entries:
        raw, _ = store.decode(e)
        nm = names[e["index"]] if e["index"] < len(names) else "entry%02d" % e["index"]
        path = os.path.join(outdir, nm + ".dll")
        open(path, "wb").write(raw)
        print("%-46s %9d bytes sha256=%s" % (nm, len(raw), hashlib.sha256(raw).hexdigest()[:16]))


def cmd_replace(store, name, newdll, out):
    names = store.names()
    if name not in names:
        raise SystemExit("name not found in manifest: " + name)
    idx = names.index(name)
    raw = open(newdll, "rb").read()
    if raw[:2] != b"MZ":
        raise SystemExit("replacement is not a PE: " + newdll)
    old, _ = store.decode(store.entries[idx])
    blob = store.rebuild({idx: raw})
    open(out, "wb").write(blob)
    print("replaced entry %d (%s)" % (idx, name))
    print("  old: %d raw / %d stored" % (len(old), store.entries[idx]["size"]))
    print("  new: %d raw / %d stored" % (len(raw), len(lz4_compress(raw)) + 12))
    print("  blob: %d -> %d bytes" % (len(store.data), len(blob)))
    print("  wrote " + out)


def cmd_verify(store, expects):
    ok = True
    names = store.names()
    seen = set()
    for e in store.entries:
        raw, desc = store.decode(e)
        nm = names[e["index"]] if e["index"] < len(names) else "?"
        seen.add(nm)
        if raw[:2] != b"MZ":
            print("FAIL %-46s not a PE" % nm)
            ok = False
        if nm in expects:
            got = hashlib.sha256(raw).hexdigest()
            if got != expects[nm]:
                print("FAIL %-46s sha256 %s != expected %s" % (nm, got[:16], expects[nm][:16]))
                ok = False
            else:
                print("ok   %-46s sha256 match" % nm)
    for nm in expects:
        if nm not in seen:
            print("FAIL expected assembly missing: " + nm)
            ok = False
    print("entries=%d gap=%d bytes  RESULT: %s" % (store.entry_count, len(store.gap), "OK" if ok else "FAILED"))
    return 0 if ok else 1


def cmd_selftest(store):
    names = store.names()
    bad = 0
    total_raw = total_comp = 0
    for e in store.entries:
        raw, _ = store.decode(e)
        comp = lz4_compress(raw)
        back = lz4_decompress(comp, len(raw))
        total_raw += len(raw)
        total_comp += len(comp)
        same = back == raw
        if not same:
            bad += 1
        nm = names[e["index"]] if e["index"] < len(names) else "?"
        print("%-46s %9d -> %8d (%5.2f%%) %s" % (
            nm, len(raw), len(comp), 100.0 * len(comp) / max(1, len(raw)), "ok" if same else "MISMATCH"))
    print("\nroundtrip: %d/%d ok; total %d -> %d (%.2f%%)" % (
        store.entry_count - bad, store.entry_count, total_raw, total_comp,
        100.0 * total_comp / max(1, total_raw)))
    return 0 if bad == 0 else 1


def main(argv):
    argv = list(argv)
    manifest = None
    if "--manifest" in argv:
        i = argv.index("--manifest")
        manifest = argv[i + 1]
        del argv[i:i + 2]

    if len(argv) < 3:
        print(__doc__)
        return 2
    cmd, blob = argv[1], argv[2]
    store = Store(blob, manifest)
    if cmd == "list":
        cmd_list(store)
        return 0
    if cmd == "extract":
        cmd_extract(store, argv[3])
        return 0
    if cmd == "replace":
        cmd_replace(store, argv[3], argv[4], argv[5])
        return 0
    if cmd == "verify":
        expects = {}
        for a in argv[3:]:
            if "=" in a:
                k, v = a.split("=", 1)
                expects[k] = v
        return cmd_verify(store, expects)
    if cmd == "selftest":
        return cmd_selftest(store)
    print("unknown command: " + cmd)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
