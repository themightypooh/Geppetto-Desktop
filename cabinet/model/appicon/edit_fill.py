# Edit 2: fill the drawn outlines (radii read off a 20 px grid over ref.png) and un-mirror the model.
# Effigy's front view puts +Y on screen right, but the front refimage plane draws with right = -Y, so the pic
# showed flipped in the chest. The model now maps picture-right to +Y (front render = the pic), and the chest
# shows ref_flip.png, which that plane flips back the right way round.
import math, re
src=open("trace.py").read()
src=src.replace("round(-(OX+","round((OX+").replace("rot={T},0,0","rot={-T},0,0").replace("-math.sin(","math.sin(")
src=src.replace('open("/home/poo/Desktop/geppetto-desktop/cabinet/model/appicon/build.txt","w").write("\\n".join(lines)+"\\n")',"")
exec(src)                       # helpers + the traced head/control/strings, mirrored
head=[l for l in lines if l.startswith("set Head")][-1]
ctrl=[l for l in lines if l.startswith("add Blob name=Control")][0].replace("add Blob name=Control voxel=0.07 blend=0.1","set Control")
strings=[re.sub(r"add Spline name=(\w+) (points=\S+).*",r"set \1 \2",l) for l in lines if l.startswith("add Spline")]
J=dict(shL=(592,320,25),elL=(437,288,21),wrL=(388,410,12),shR=(752,420,23),elR=(905,481,20),wrR=(925,612,13),
       hipL=(487,545,32),knL=(462,692,23),anL=(412,862,18),hipR=(608,588,32),knR=(667,721,23),anR=(690,882,18))
def seg(a,b,ra,rb): return cone(J[a][:2],ra,J[b][:2],rb)
def via(a,m,b,ra,rm,rb): return f"{cone(J[a][:2],ra,m,rm)}; {cone(m,rm,J[b][:2],rb)}"
out=[]; O=out.append
O("# edits the traced marionette: run with geppetto-live edit2.txt marionette.effigy .")
O(f"refimage front ref_flip.png offset={-OX:.2f},{OY:.2f} depth=0 opacity=0.55")
O("echo un-mirrored: head, control and strings now sit where the pic has them")
O(head); O(ctrl); [O(s) for s in strings]
O("echo torso fills the drawn chest, waist and pelvis")
O(f'set Torso shapes="ellipsoid {v(P(640,405))} 4.2,{r(82)},{r(108)} rot=-31,0,0 mat=1; ellipsoid {v(P(590,490))} 3.6,{r(62)},{r(40)} rot=-25,0,0 mat=1"')
O(f'set Pelvis shapes="ellipsoid {v(P(565,540))} 3.6,{r(74)},{r(52)} rot=-20,0,0 mat=1"')
O(f'set Neck shapes="cylinder {v(P(690,340))} {v(P(738,380))} {r(26)} r=0.2 mat=1"')
O("echo joints at their drawn size")
O(f'set Joints shapes="{"; ".join(sph(J[k][:2],J[k][2]) for k in ["shL","shR","elL","elR","wrL","wrR","hipL","hipR","knL","knR"])}"')
O("echo limbs as thick as drawn, forearms swelling toward the wrist like the pic")
O(f'set UpperArms shapes="{seg("shL","elL",27,23)}; {seg("shR","elR",25,22)}"')
O(f'set Forearms shapes="{via("elL",(408,350),"wrL",24,29,17)}; {via("elR",(915,550),"wrR",20,28,19)}"')
O(f'set Hands shapes="ellipsoid {v(P(392,462))} {r(28)},{r(28)},{r(44)} mat=1; ellipsoid {v(P(920,666))} {r(28)},{r(28)},{r(38)} mat=1"')
O(f'set Thighs shapes="{via("hipL",(476,615),"knL",35,35,31)}; {via("hipR",(628,650),"knR",40,38,32)}"')
O(f'set Shins shapes="{seg("knL","anL",26,19)}; {seg("knR","anR",26,19)}"')
fl=P(405,888); fr=P(692,908)
O(f'set Feet shapes="ellipsoid {fl[0]+1.2},{fl[1]},{fl[2]} 2.6,{r(26)},{r(22)} mat=1; ellipsoid {fr[0]+1.2},{fr[1]},{fr[2]} 2.6,{r(26)},{r(22)} mat=1; {sph(J["anL"][:2],19)}; {sph(J["anR"][:2],19)}"')
O("render out/front_edit2.png view=front size=900 wire=0 grid=0 labels=0")
O("save marionette.effigy")
open("edit2.txt","w").write("\n".join(out)+"\n")
