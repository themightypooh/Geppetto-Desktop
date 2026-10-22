# Converts pixel points traced off ref.png (1376x768) into model coordinates and writes build.txt.
# Picture plane: right = -Y, up = +Z, scale S in/px, head (800,300) at y=0, z=32.6.
import math
S=0.0677; W,H=1376,768
OX=-(800-W/2)*S; OY=32.6-(H/2-300)*S
def P(px,py,x=0.0): return (round(x,2), round(-(OX+(px-W/2)*S),2), round(OY+(H/2-py)*S,2))
def r(px): return round(px*S,2)
def v(p): return f"{p[0]},{p[1]},{p[2]}"
def cone(a,ra,b,rb,m=1): return f"cone {v(P(*a))} {r(ra)} {v(P(*b))} {r(rb)} mat={m}"
def sph(a,rr,m=1,x=0.0): return f"sphere {v(P(*a,x))} {r(rr)} mat={m}"
lines=[]
L=lines.append
L("# Geppetto's app icon: the marionette from po's pixel art, every part TRACED off ref.png (pixel points in trace.py).")
L("# Z up, front +X, left +Y. The X control bar's top ends (cut off in the pic) are mirrored from the bottom ends.")
L(f"refimage front ref.png height={H*S:.2f} offset={OX:.2f},{OY:.2f} depth=8 opacity=0.6")
# torso: centre (640,420), lean 31 deg top to screen-right
L("echo the torso: traced barrel chest, leaning like the pic")
tc=P(640,420)
L(f'add Blob name=Torso voxel=0.1 blend=0.4 shapes="ellipsoid {v(tc)} 3.6,{r(80)},{r(110)} rot=31,0,0 mat=1"')
L("material 1 color=#a8682f rough=0.55")
L("echo the pelvis")
L(f'add Blob name=Pelvis voxel=0.1 shapes="ellipsoid {v(P(565,540))} 3.2,{r(70)},{r(42)} rot=20,0,0 mat=1; cylinder {v(P(600,505))} {v(P(585,525))} {r(40)} mat=1"')
L("echo the neck peg")
L(f'add Blob name=Neck voxel=0.08 shapes="cylinder {v(P(665,350))} {v(P(725,385))} {r(22)} r=0.2 mat=1"')
L("echo ball joints: shoulders, elbows, wrists, hips, knees")
J=[sph((592,318),24),sph((770,425),24),sph((432,290),20),sph((905,483),19),sph((388,412),14),sph((925,612),14),sph((505,550),26),sph((610,580),26),sph((468,688),25),sph((668,720),25)]
L(f'add Blob name=Joints voxel=0.07 blend=0.05 shapes="{"; ".join(J)}"')
L("echo upper arms")
L(f'add Blob name=UpperArms voxel=0.07 blend=0.05 shapes="{cone((572,312),28,(452,286),21)}; {cone((792,438),27,(888,474),20)}"')
L("echo forearms")
L(f'add Blob name=Forearms voxel=0.07 blend=0.05 shapes="{cone((426,308),24,(397,398),17)}; {cone((910,502),26,(924,598),19)}"')
L("echo egg hands")
hl=P(392,465); hr=P(918,662)
L(f'add Blob name=Hands voxel=0.07 shapes="ellipsoid {v(hl)} {r(28)},{r(28)},{r(42)} mat=1; ellipsoid {v(hr)} {r(30)},{r(30)},{r(40)} mat=1"')
L("echo thighs")
L(f'add Blob name=Thighs voxel=0.07 blend=0.05 shapes="{cone((497,568),33,(471,668),25)}; {cone((617,600),33,(660,700),25)}"')
L("echo shins (below the frame: carried on down the same line)")
L(f'add Blob name=Shins voxel=0.07 blend=0.05 shapes="{cone((463,708),27,(445,850),19)}; {cone((673,742),27,(697,880),19)}"')
L("echo feet")
fl=P(442,885); fr=P(700,915)
L(f'add Blob name=Feet voxel=0.07 blend=0.3 shapes="ellipsoid {fl[0]+1.0},{fl[1]},{fl[2]} 2.6,{r(24)},{r(20)} mat=1; ellipsoid {fr[0]+1.0},{fr[1]},{fr[2]} 2.6,{r(24)},{r(20)} mat=1"')
# head: centre (805,305), tilt 40 deg top to screen-right
hc=P(805,305); R=r(95); T=40
up=(0,-math.sin(math.radians(T)),math.cos(math.radians(T)))
def surf(px,py,inset=0.0):
    p=P(px,py); dy=p[1]-hc[1]; dz=p[2]-hc[2]
    x=math.sqrt(max(R*R-dy*dy-dz*dz,0.3))-inset
    return (round(hc[0]+x,2),p[1],p[2])
def add(a,k,d=(0,0,0)): return f"{round(a[0]+up[0]*k+d[0],2)},{round(a[1]+up[1]*k+d[1],2)},{round(a[2]+up[2]*k+d[2],2)}"
head=[f"ellipsoid {v(hc)} {round(R-0.4,2)},{R},{r(102)} rot={T},0,0 mat=2", f"ellipsoid {v(surf(735,262,0.6))} 0.6,0.9,1.3 rot={T},0,0 mat=2"]
hair=[f"ellipsoid {add(hc,1.6,(-1.0,0,0))} {round(R-0.2,2)},{round(R+0.25,2)},{r(90)} rot={T},0,0 mat=3", f"paint ellipsoid {add(hc,4.2,(-1.6,0,0))} {R+1},{R+1},4.4 rot={T},0,0 mat=3", f"paint ellipsoid {add(hc,0,(-5.5,0,0))} 3,{R+1},{R+1} rot={T},0,0 mat=3"]
eyes=[f"paint sphere {v(surf(775,310))} {r(19)} mat=5", f"paint sphere {v(surf(818,357))} {r(19)} mat=5", f"paint sphere {v(surf(776,311))} {r(11)} mat=6", f"paint sphere {v(surf(819,358))} {r(11)} mat=6"]
face=[f"sphere {v(surf(786,350,0.5))} {r(16)} mat=4", f"paint sphere {v(surf(736,322))} {r(20)} mat=4", f"paint sphere {v(surf(806,388))} {r(18)} mat=4", f"-box {v(surf(752,378,0.2))} 2.4,{r(48)},0.3 rot={T},0,0", f"-box {v(surf(728,392,0.2))} 2.4,0.3,{r(30)} rot={T},0,0", f"-box {v(surf(778,404,0.2))} 2.4,0.3,{r(26)} rot={T},0,0"]
L("echo the head: big pale painted egg, tilted like the pic, with its ear")
L(f'add Blob name=Head voxel=0.07 blend=0.3 shapes="{"; ".join(head)}"')
L("material 2 color=#c4b6a2 rough=0.5")
L("echo carved brown hair cap")
L(f'set Head shapes="{"; ".join(head+hair)}"')
L("material 3 color=#6a3418 rough=0.6")
L("echo big round eyes")
L(f'set Head shapes="{"; ".join(head+hair+eyes)}"')
L("material 5 color=#e8e2d4 rough=0.4")
L("material 6 color=#14100c rough=0.3")
L("echo nose, rosy cheeks and the hinged jaw slot")
L(f'set Head shapes="{"; ".join(head+hair+eyes+face)}"')
L("material 4 color=#b84a3c rough=0.5")
# control X: centre (690,62); bottom ends toward viewer, top ends (cut off) mirrored and away
c=P(690,62,0); BL=P(520,100,5); BR=P(835,132,5); TR=P(860,24,-5); TL=P(545,-8,-5)
def bar(a,b,z=0.0):
    mid=[(a[i]+b[i])/2 for i in range(3)]; d=[b[i]-a[i] for i in range(3)]; L_=math.dist(a,b)
    return f"capsule {a[0]},{a[1]},{a[2]+z} {b[0]},{b[1]},{b[2]+z} {r(15)} mat=8"
L("echo the control: an X of two bars, the cut-off ends filled in")
L(f'add Blob name=Control voxel=0.07 blend=0.1 shapes="box {v(BL)} 0.1,0.1,0.1 mat=8"'.replace(f"box {v(BL)} 0.1,0.1,0.1 mat=8", f"{bar(BL,TR)}; {bar(BR,TL,0.9)}; cylinder {c[0]},{c[1]},{c[2]-0.6} {c[0]},{c[1]},{c[2]+1.6} {r(16)} mat=8"))
L("material 8 color=#8a5428 rough=0.5")
L("echo strings, each one as it runs in the pic")
def lerp(a,b,t): return tuple(round(a[i]+(b[i]-a[i])*t,2) for i in range(3))
S_=[("StringElbowL",lerp(TL,TR,0.06),P(440,270,0.8)),("StringKneeL",lerp(BL,TR,0.06),P(472,676,1.6)),("StringNeck",c,P(672,312,-1.5)),("StringHead",lerp(BR,TL,0.12),P(790,214,-0.5)),("StringElbowR",lerp(TR,BL,0.06),P(905,462,0.6)),("StringKneeR",lerp(BR,TL,0.06),P(670,700,1.6))]
first=True
for n,a,b in S_:
    L(f"add Spline name={n} points={v(a)};{v(b)} tube=1 radius=0.07 material=7")
    if first: L("material 7 color=#b8a07c rough=0.8"); first=False
L("describe")
L("turnaround out shade=material size=768 name=marionette views=front,quarter,side,back")
L("save marionette.effigy")
L("export out name=marionette")
open("/home/poo/Desktop/geppetto-desktop/cabinet/model/appicon/build.txt","w").write("\n".join(lines)+"\n")
