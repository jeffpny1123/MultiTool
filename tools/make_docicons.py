# -*- coding: utf-8 -*-
"""畫文書工具嘅檔案圖示（文件 + 類型色帶 + 右上角 Rabi 圖示）→ assets/<類型>.ico。
用法：python tools/make_docicons.py（要 Pillow）。pdf.ico 係之前另外整嘅，呢度唔會重畫。"""
import os
from PIL import Image, ImageDraw, ImageFont, ImageFilter
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
KINDS = {"docx": ("DOC", (47, 111, 237)), "xlsx": ("XLS", (31, 157, 99)), "pptx": ("PPT", (239, 125, 26))}
S = 4   # 超取樣

def font(size):
    for f in ("C:/Windows/Fonts/segoeuib.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf"):
        if os.path.exists(f): return ImageFont.truetype(f, size)
    return ImageFont.load_default()

def draw(label, color):
    n = 256 * S
    im = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    # 紙（陰影 + 圓角）
    sh = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    ImageDraw.Draw(sh).rounded_rectangle((44 * S, 30 * S, 196 * S, 240 * S), 18 * S, fill=(0, 0, 0, 70))
    im.alpha_composite(sh.filter(ImageFilter.GaussianBlur(5 * S)))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle((42 * S, 24 * S, 194 * S, 234 * S), 18 * S, fill=(247, 248, 251, 255), outline=(214, 219, 229, 255), width=2 * S)
    for y, w in ((68, 76), (92, 96), (116, 58)):
        d.rounded_rectangle((64 * S, y * S, (64 + w) * S, (y + 9) * S), 5 * S, fill=(205, 211, 223, 255))
    # 類型色帶
    d.rounded_rectangle((24 * S, 142 * S, 188 * S, 200 * S), 12 * S, fill=color + (255,))
    f = font(44 * S)
    box = d.textbbox((0, 0), label, font=f)
    d.text(((24 + 188) * S / 2 - (box[0] + box[2]) / 2, (142 + 200) * S / 2 - (box[1] + box[3]) / 2), label, font=f, fill=(255, 255, 255, 255))
    # 右上角 Rabi 圖示（圓形）
    logo = Image.open(os.path.join(root, "assets", "icon.png")).convert("RGBA").resize((112 * S, 112 * S), Image.LANCZOS)
    mask = Image.new("L", logo.size, 0)
    ImageDraw.Draw(mask).ellipse((0, 0, logo.size[0] - 1, logo.size[1] - 1), fill=255)
    ring = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    ImageDraw.Draw(ring).ellipse((136 * S, 2 * S, 254 * S, 120 * S), fill=(255, 255, 255, 255))
    im.alpha_composite(ring)
    im.paste(logo, (139 * S, 5 * S), mask)
    return im.resize((256, 256), Image.LANCZOS)

for kind, (label, color) in KINDS.items():
    out = os.path.join(root, "assets", kind + ".ico")
    draw(label, color).save(out, sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    print(out)
