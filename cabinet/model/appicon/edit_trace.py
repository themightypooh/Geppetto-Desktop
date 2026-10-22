# Edit 1: pic through the model (no parallax), limbs joint-centre to joint-centre (no gaps), torso = chest + waist + pelvis.
import importlib.util,sys
spec=importlib.util.spec_from_file_location("t","trace.py")
src=open("trace.py").read().split("lines=[]")[0]   # just the P/r/v/cone/sph helpers
exec(src)
J=dict(shL=(592,318,24),elL=(432,290,20),wrL=(390,410,14),shR=(770,425,24),elR=(905,483,19),wrR=(925,610,14),
       hipL=(505,550,26),knL=(468,688,25),anL=(444,860,16),hipR=(610,580,26),knR=(668,720,25),anR=(697,885,16))
def seg(a,b,ra,rb): return cone(J[a][:2],ra,J[b][:2],rb)
out=[]
O=out.append
O("# edits the traced marionette: run with geppetto-live edit1.txt marionette.effigy .")
O("refimage front depth=0 opacity=0.55")
O("echo torso: chest, waist and pelvis stacked like the drawing")
O(f'set Torso shapes="ellipsoid {v(P(655,400))} 3.6,{r(88)},{r(92)} rot=31,0,0 mat=1; ellipsoid {v(P(598,488))} 3.0,{r(58)},{r(34)} rot=25,0,0 mat=1"')
O(f'set Pelvis shapes="ellipsoid {v(P(560,546))} 3.2,{r(72)},{r(44)} rot=20,0,0 mat=1"')
O(f'set Neck shapes="cylinder {v(P(690,340))} {v(P(738,380))} {r(24)} r=0.2 mat=1"')
O("echo ball joints on the traced joint centres")
O(f'set Joints shapes="{"; ".join(sph(J[k][:2],J[k][2]) for k in ["shL","shR","elL","elR","wrL","wrR","hipL","hipR","knL","knR"])}"')
O("echo limbs run joint to joint, no gaps")
O(f'set UpperArms shapes="{seg("shL","elL",27,21)}; {seg("shR","elR",27,20)}"')
O(f'set Forearms shapes="{seg("elL","wrL",23,17)}; {seg("elR","wrR",25,18)}"')
O(f'set Hands shapes="ellipsoid {v(P(392,462))} {r(28)},{r(28)},{r(44)} mat=1; ellipsoid {v(P(918,658))} {r(30)},{r(30)},{r(44)} mat=1"')
O(f'set Thighs shapes="{seg("hipL","knL",34,25)}; {seg("hipR","knR",34,25)}"')
O(f'set Shins shapes="{seg("knL","anL",27,18)}; {seg("knR","anR",27,18)}"')
fl=P(440,890); fr=P(700,915)
O(f'set Feet shapes="ellipsoid {fl[0]+1.2},{fl[1]},{fl[2]} 2.6,{r(24)},{r(22)} mat=1; ellipsoid {fr[0]+1.2},{fr[1]},{fr[2]} 2.6,{r(24)},{r(22)} mat=1; {sph((444,862),17)}; {sph((697,887),17)}"')
O("render out/front_edit1.png view=front size=900 wire=0 grid=0 labels=0")
O("save marionette.effigy")
open("edit1.txt","w").write("\n".join(out)+"\n")
