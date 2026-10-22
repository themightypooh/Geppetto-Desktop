# High-density ASCII of the Geppetto marionette render: every cell is a glyph picked by brightness and
# tinted with the render's colour, on a dark rounded tile. Writes icon_ascii.{txt,png} at 1024.
import numpy as np
from PIL import Image, ImageDraw, ImageFont, ImageFilter
src=Image.open("out/iconkey_front.png").convert("RGB"); a=np.asarray(src).astype(int)
key=(a[...,0]-a[...,1]>90)&(a[...,2]-a[...,1]>90)
ys,xs=np.nonzero(~key); x0,x1,y0,y1=xs.min(),xs.max(),ys.min(),ys.max()
side=int(max(x1-x0,y1-y0)*1.08); cx,cy=(x0+x1)//2,(y0+y1)//2
box=(cx-side//2,cy-side//2,cx+side//2,cy+side//2)
img=src.crop(box); m=Image.fromarray((~key*255).astype(np.uint8)).crop(box)
N=1024; CW=4; CH=7                         # cell size in px: ~256 x 146 glyphs
cols,rows=N//CW,N//CH
mf=np.asarray(m).astype(float)[...,None]/255   # colour from model pixels only, so no magenta bleeds in
small=np.asarray(Image.fromarray((np.asarray(img)*mf).astype(np.uint8)).resize((cols,rows),Image.BOX)).astype(float)
small=small/np.maximum(np.asarray(m.resize((cols,rows),Image.BOX)).astype(float)[...,None]/255,1e-3)
cov=np.asarray(m.resize((cols,rows),Image.BOX)).astype(float)/255
ramp=" .'`^\",:;Il!i><~+_-?][}{1)(|/tfjrxnuvczXYUJCLQ0OZmwqpdbkhao*#MW&8%B@$"
font=ImageFont.truetype("/usr/share/fonts/TTF/DejaVuSansMono-Bold.ttf",7)
out=Image.new("RGBA",(N,N),(0,0,0,0)); tile=Image.new("RGBA",(N,N),(0,0,0,0))
ImageDraw.Draw(tile).rounded_rectangle([8,8,N-9,N-9],radius=180,fill=(18,14,12,255),outline=(120,78,40,255),width=10)
out.alpha_composite(tile); d=ImageDraw.Draw(out); txt=[]
for j in range(rows):
    line=""
    for i in range(cols):
        if cov[j,i]<0.2: line+=" "; continue
        c=small[j,i]; lum=(0.3*c[0]+0.59*c[1]+0.11*c[2])/255
        ch=ramp[min(len(ramp)-1,int(0.25*len(ramp)+lum*0.75*(len(ramp)-1)))]
        line+=ch
        col=tuple(int(min(255,v*1.12)) for v in c)
        d.text((i*CW+ (N-cols*CW)//2, j*CH+(N-rows*CH)//2-1),ch,font=font,fill=col+(255,))
    txt.append(line.rstrip())
# keep glyphs inside the rounded tile
mask=Image.new("L",(N,N),0); ImageDraw.Draw(mask).rounded_rectangle([18,18,N-19,N-19],radius=170,fill=255)
final=tile.copy(); glyphs=out.copy(); glyphs.putalpha(Image.composite(out.getchannel("A"),Image.new("L",(N,N),0),mask))
final.alpha_composite(glyphs)
final.save("icon_ascii.png"); open("icon_ascii.txt","w").write("\n".join(txt)+"\n")
