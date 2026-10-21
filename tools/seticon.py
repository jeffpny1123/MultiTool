"""把 .ico 加入 PE（apphost）嘅資源：python seticon.py in.exe icon.ico out.exe"""
import sys, struct, lief
src, ico, out = sys.argv[1:4]
data = open(ico, 'rb').read()
_, typ, n = struct.unpack_from('<HHH', data, 0)
entries = []
for i in range(n):
    w, h, cc, rsv, planes, bpp, size, off = struct.unpack_from('<BBBBHHII', data, 6 + i * 16)
    entries.append((w, h, cc, rsv, planes, bpp, size, data[off:off + size]))
b = lief.PE.parse(src)
root = b.resources
# 拎走舊嘅 icon / group icon
for t in list(root.childs):
    if t.id in (3, 14): root.delete_child(t)
icon_dir = lief.PE.ResourceDirectory(3)
grp = struct.pack('<HHH', 0, 1, n)
for i, (w, h, cc, rsv, planes, bpp, size, img) in enumerate(entries):
    rid = i + 1
    d = lief.PE.ResourceDirectory(rid)
    lang = lief.PE.ResourceData(list(img), 0)
    lang.id = 0
    d.add_child(lang)
    icon_dir.add_child(d)
    grp += struct.pack('<BBBBHHIH', w, h, cc, rsv, planes, bpp, size, rid)
grp_dir = lief.PE.ResourceDirectory(14)
g1 = lief.PE.ResourceDirectory(32512)  # IDI_APPLICATION 慣用 id
gd = lief.PE.ResourceData(list(grp), 0)
gd.id = 0
g1.add_child(gd)
grp_dir.add_child(g1)
root.add_child(icon_dir)
root.add_child(grp_dir)
cfg = lief.PE.Builder.config_t()
cfg.resources = True
b.write(out, cfg)
print('ok', out)
