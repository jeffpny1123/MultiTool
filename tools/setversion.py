"""改 apphost 嘅版本資訊（工作管理員 / 內容 / 「開啟檔案」顯示嘅名）：
python tools/setversion.py in.exe out.exe "Rabi Toolkit" RabiToolkit.exe 0.2.7 "白兔糖v3v"
（要 pip install lief）"""
import sys, struct, lief

src, out, name, filename, ver, company = sys.argv[1:7]

def pad(b):
    return b + b'\0' * ((4 - len(b) % 4) % 4)

def node(key, value=b'', vlen=0, typ=0, children=b''):
    k = (key + '\0').encode('utf-16-le')
    head = struct.pack('<HHH', 0, vlen, typ) + k
    body = pad(head) + (pad(value) if value else b'') + children
    return struct.pack('<H', len(body)) + body[2:]

def sval(key, text):
    v = (text + '\0').encode('utf-16-le')
    return pad(node(key, v, len(v) // 2, 1))

nums = [int(x) for x in (ver.split('.') + ['0'] * 4)[:4]]
ms, ls = (nums[0] << 16) | nums[1], (nums[2] << 16) | nums[3]
fixed = struct.pack('<13I', 0xFEEF04BD, 0x00010000, ms, ls, ms, ls, 0x3F, 0, 0x40004, 1, 0, 0, 0)
strings = b''.join(sval(k, v) for k, v in [
    ('CompanyName', company), ('FileDescription', name), ('FileVersion', ver), ('InternalName', filename.rsplit('.', 1)[0]),
    ('LegalCopyright', '© ' + company), ('OriginalFilename', filename), ('ProductName', name), ('ProductVersion', ver)])
table = pad(node('040904b0', children=strings, typ=1))
sfi = pad(node('StringFileInfo', children=table, typ=1))
var = pad(node('Translation', struct.pack('<HH', 0x0409, 0x04B0), 4, 0))
vfi = pad(node('VarFileInfo', children=var, typ=1))
info = node('VS_VERSION_INFO', fixed, len(fixed), 0, sfi + vfi)

b = lief.PE.parse(src)
done = False
for t in b.resources.childs:
    if t.id == 16:
        for d in t.childs:
            for leaf in d.childs:
                leaf.content = info
                done = True
if not done:
    sys.exit('冇版本資源')
b.write(out)
print('ok', out)
