"""用一個現成嘅 .NET 9 WinForms single-file exe 做底（攞佢個 apphost + runtime），
砌一個乾淨嘅新 bundle：唔包舊 app 嘅 dll，換成新嘅 MultiTool.dll。
底本可以係舊 StickyPad.exe，亦可以直接用上一版 MultiTool.exe。
用法: python make_exe.py <base.exe> <MultiTool.dll> <out.exe> [apphost.exe]"""
import sys, os, struct, json, hashlib, base64
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bundle import parse

OLD, NEW = 'StickyPad', 'MultiTool'   # 同長度，直接喺 apphost 度換名
base, appdll, out = sys.argv[1:4]
d = open(base, 'rb').read()
m = parse(d)

# apphost 結尾 = PE 最後一個 section 完結位置
pe_off = struct.unpack_from('<I', d, 0x3C)[0]
nsec = struct.unpack_from('<H', d, pe_off + 6)[0]
opt = struct.unpack_from('<H', d, pe_off + 20)[0]
sec = pe_off + 24 + opt
host_end = max(struct.unpack_from('<II', d, sec + i * 40 + 16)[1] + struct.unpack_from('<II', d, sec + i * 40 + 16)[0]
               for i in range(nsec))
assert host_end <= min(e['off'] for e in m['ents'])
host = bytearray(d[:host_end])
# 可選：用另一個 apphost（例如加咗程式 icon 嘅版本）：python make_exe.py base.exe app.dll out.exe host.exe
if len(sys.argv) > 4:
    host = bytearray(open(sys.argv[4], 'rb').read())

def swap(buf, a, b):
    assert len(a) == len(b)
    n = buf.count(a); buf[:] = buf.replace(a, b); return n
print('rename ascii', swap(host, OLD.encode(), NEW.encode()),
      'utf16', swap(host, OLD.encode('utf-16-le'), NEW.encode('utf-16-le')))

def entdata(e):
    import zlib
    raw = d[e['off']:e['off'] + (e['csize'] or e['size'])]
    return zlib.decompress(raw) if e['csize'] else raw

files = []   # (path, type, bytes)
for e in m['ents']:
    p = e['path']
    if p.startswith(OLD + '.') or p.startswith(NEW + '.'):
        continue
    files.append((p, e['type'], entdata(e)))
deps_e = next(e for e in m['ents'] if e['type'] == 3)
rc_e = next(e for e in m['ents'] if e['type'] == 4)
deps = json.loads(entdata(deps_e))
tgt = deps['targets'][[k for k in deps['targets'] if '/' in k][0]]
old_key = next(k for k in tgt if k.startswith(OLD + '/') or k.startswith(NEW + '/'))
val = tgt.pop(old_key); val['runtime'] = {NEW + '.dll': {}}
tgt[NEW + '/1.0.0'] = val
lib = deps['libraries']; lv = lib.pop(old_key); lib[NEW + '/1.0.0'] = lv
files.insert(0, (NEW + '.dll', 1, open(appdll, 'rb').read()))
files.append((NEW + '.deps.json', 3, json.dumps(deps, indent=2).encode()))
files.append((NEW + '.runtimeconfig.json', 4, entdata(rc_e)))

blob = bytearray(host)
ents = []
for p, t, b in files:
    align = 4096 if t == 1 else 16
    blob += b'\0' * ((-len(blob)) % align)
    ents.append((p, t, len(blob), len(b)))
    blob += b

def wstr(s):
    b = s.encode(); l = len(b); o = bytearray()
    while True:
        x = l & 0x7f; l >>= 7; o.append(x | (0x80 if l else 0))
        if not l: break
    return bytes(o) + b

bid = base64.b64encode(hashlib.sha256(bytes(blob)).digest()).decode()[:len(m['bid'])]
dep = next(x for x in ents if x[1] == 3); rc = next(x for x in ents if x[1] == 4)
h = bytearray(struct.pack('<IIi', m['major'], m['minor'], len(ents))) + wstr(bid)
h += struct.pack('<qqqqQ', dep[2], dep[3], rc[2], rc[3], m['flags'])
for p, t, off, size in ents:
    h += struct.pack('<qqq', off, size, 0) + bytes([t]) + wstr(p)
hoff = len(blob); blob += h
from bundle import SIG
sigpos = bytes(host).find(SIG)
struct.pack_into('<q', blob, sigpos - 8, hoff)
open(out, 'wb').write(blob)
m2 = parse(bytes(blob))
assert len(m2['ents']) == len(ents)
print('ok', out, len(blob), 'entries', len(ents), 'bid', bid)
