from PIL import Image, ImageDraw, ImageFilter
import math
N=1024
def grad(size,c1,c2):
    img=Image.new("RGBA",(size,size))
    px=img.load()
    for y in range(size):
        for x in range(size):
            t=(x+y)/(2*(size-1))
            px[x,y]=tuple(int(c1[i]+(c2[i]-c1[i])*t) for i in range(3))+(255,)
    return img
def icon(N=1024):
    g=grad(256,(0x4F,0x8C,0xFF),(0x8C,0x5C,0xF6)).resize((N,N),Image.BICUBIC)
    mask=Image.new("L",(N,N),0); d=ImageDraw.Draw(mask)
    m=int(N*0.04); d.rounded_rectangle([m,m,N-m,N-m],radius=int(N*0.22),fill=255)
    base=Image.new("RGBA",(N,N),(0,0,0,0)); base.paste(g,(0,0),mask)
    # top gloss
    gl=Image.new("RGBA",(N,N),(0,0,0,0)); gd=ImageDraw.Draw(gl)
    gd.ellipse([-N*0.3,-N*0.75,N*1.3,N*0.42],fill=(255,255,255,38))
    gm=Image.new("L",(N,N),0); ImageChops=None
    gl2=Image.new("RGBA",(N,N),(0,0,0,0)); gl2.paste(gl,(0,0),mask)
    base=Image.alpha_composite(base,gl2)
    P=lambda pts:[(x*N,y*N) for x,y in pts]
    cursor=P([(0.28,0.20),(0.28,0.74),(0.405,0.625),(0.505,0.835),(0.595,0.79),(0.495,0.585),(0.665,0.585)])
    # shadow
    sh=Image.new("RGBA",(N,N),(0,0,0,0)); sd=ImageDraw.Draw(sh)
    sd.polygon([(x+N*0.018,y+N*0.028) for x,y in cursor],fill=(20,10,60,110))
    sh=sh.filter(ImageFilter.GaussianBlur(N*0.018))
    base=Image.alpha_composite(base,sh)
    d=ImageDraw.Draw(base)
    d.polygon(cursor,fill=(255,255,255,255))
    # sparkles (AI)
    def star(cx,cy,r,a=255):
        k=0.28
        pts=[]
        for i in range(8):
            ang=math.pi/2*i/2-math.pi/2
            rr=r if i%2==0 else r*k
            pts.append((cx*N+math.cos(ang)*rr*N, cy*N+math.sin(ang)*rr*N))
        d.polygon(pts,fill=(255,255,255,a))
    star(0.70,0.30,0.135)
    star(0.81,0.50,0.065,235)
    return base
big=icon(1024)
big.save("icon_1024.png")
sizes=[16,20,24,32,40,48,64,128,256]
imgs=[big.resize((s,s),Image.LANCZOS) for s in sizes]
imgs[-1].save("MultiTool.ico",sizes=[(s,s) for s in sizes],append_images=imgs[:-1])
# preview sheet on dark and light bg
sheet=Image.new("RGBA",(900,320),(14,16,20,255))
x=20
for s in [256,128,64,48,32,16]:
    im=big.resize((s,s),Image.LANCZOS); sheet.alpha_composite(im,(x,(320-s)//2)); x+=s+24
sheet.save("preview.png")
print("ok")
