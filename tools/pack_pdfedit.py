# -*- coding: utf-8 -*-
"""將 lib/pdfedit、lib/office 壓做 lib/pdfedit.zip、lib/office.zip（編譯時嵌入 exe 做 resource）。內容冇變就唔重寫。"""
import io, os, zipfile
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
for name in ("pdfedit", "office"):
    src = os.path.join(root, "lib", name); out = os.path.join(root, "lib", name + ".zip")
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for dp, dn, fn in os.walk(src):
            dn.sort()
            for f in sorted(fn):
                p = os.path.join(dp, f)
                zi = zipfile.ZipInfo(os.path.relpath(p, src).replace(os.sep, "/"), (2026, 1, 1, 0, 0, 0))   # 固定時間：內容一樣 zip 就一樣
                zi.compress_type = zipfile.ZIP_DEFLATED
                z.writestr(zi, open(p, "rb").read())
    data = buf.getvalue()
    if not os.path.exists(out) or open(out, "rb").read() != data:
        open(out, "wb").write(data)
    print(name + ".zip", len(data))
