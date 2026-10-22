# Measures every part off the reference silhouette: cross-section width + centre at stations along each traced line,
# and the biggest circle that fits for round parts. Output: measured.json (pixels).
import json, sys, numpy as np
from PIL import Image, ImageFilter, ImageDraw
mask=np.load(sys.argv[1]); H,W=mask.shape
mk=Image.fromarray((mask*255).astype(np.uint8)).filter(ImageFilter.MaxFilter(5)).filter(ImageFilter.MinFilter(5))
mask=np.asarray(mk)>0
edge=mask & ~(np.asarray(mk.filter(ImageFilter.MinFilter(3)))>0)
ey,ex=np.nonzero(edge); E=np.stack([ex,ey],1).astype(float)
def inscribed(p): return float(np.sqrt(((E-p)**2).sum(1)).min())
def best_circle(c,win=25):
    best=(0,c)
    for dy in range(-win,win+1,2):
        for dx in range(-win,win+1,2):
            p=np.array([c[0]+dx,c[1]+dy],float)
            if 0<=p[0]<W and 0<=p[1]<H and mask[int(p[1]),int(p[0])]:
                r=inscribed(p); best=max(best,(r,tuple(p)),key=lambda b:b[0])
    return best[1],best[0]
def section(p,d):
    n=np.array([-d[1],d[0]]); out=[]
    for s in (1,-1):
        k=0
        while True:
            q=p+n*s*(k+1)
            if not(0<=q[0]<W and 0<=q[1]<H) or not mask[int(q[1]),int(q[0])]: break
            k+=1
            if k>120: break
        out.append(k)
    a,b=out; mid=p+n*(a-b)/2
    return mid,(a+b)/2
def limb(A,B,n=5):
    A,B=np.array(A,float),np.array(B,float); d=(B-A)/np.linalg.norm(B-A); st=[]
    for t in np.linspace(0.12,0.88,n):
        p=A+(B-A)*t
        if not(0<=p[1]<H-2): continue
        mid,r=section(p,d); st.append([round(mid[0],1),round(mid[1],1),round(r,1)])
    med=np.median([s[2] for s in st])
    for s in st: s[2]=min(s[2],med*1.35)
    return st
J=dict(shL=(592,318),elL=(432,290),wrL=(390,410),shR=(770,425),elR=(905,483),wrR=(925,610),
       hipL=(505,550),knL=(468,688),anL=(444,860),hipR=(610,580),knR=(668,720),anR=(697,885))
M={}
M['upperL']=limb(J['shL'],J['elL']); M['foreL']=limb(J['elL'],J['wrL'])
M['upperR']=limb(J['shR'],J['elR']); M['foreR']=limb(J['elR'],J['wrR'])
M['thighL']=limb(J['hipL'],J['knL']); M['shinL']=limb(J['knL'],J['anL'])
M['thighR']=limb(J['hipR'],J['knR']); M['shinR']=limb(J['knR'],J['anR'])
M['torso']=limb((700,330),(560,540),7)
for k,c in dict(head=(805,305),handL=(392,462),handR=(918,658),
                shL=J['shL'],elL=J['elL'],wrL=J['wrL'],shR=J['shR'],elR=J['elR'],wrR=J['wrR'],
                knL=J['knL'],knR=J['knR'],hipL=J['hipL'],hipR=J['hipR']).items():
    w=40 if k=='head' else 12
    p,r=best_circle(c,w); M[k]=[round(p[0],1),round(p[1],1),round(r,1)]
json.dump(M,open('measured.json','w'),indent=1)
ov=Image.open('ref.png').convert('RGB'); dr=ImageDraw.Draw(ov)
for k,v in M.items():
    for s in (v if isinstance(v[0],list) else [v]):
        x,y,r=s; dr.ellipse([x-r,y-r,x+r,y+r],outline=(255,0,255),width=2)
ov.save(sys.argv[2])
for k,v in M.items(): print(k,v)
