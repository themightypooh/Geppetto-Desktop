import sys, collections
import numpy as np
def read_obj(p):
    verts=[]; faces=[]
    with open(p, errors='ignore') as fh:
        for line in fh:
            if line.startswith('v '):
                verts.append(tuple(float(x) for x in line.split()[1:4]))
            elif line.startswith('f '):
                idx=[int(x.split('/')[0])-1 for x in line.split()[1:]]
                faces.append(tuple(idx))
    return np.array(verts, dtype=np.float64), faces
def analyze(p):
    V, F = read_obj(p)
    edges=collections.Counter(); val=collections.Counter()
    nquad=ntri=0
    for f in F:
        n=len(f)
        if n==4: nquad+=1
        elif n==3: ntri+=1
        for a,b in zip(f, f[1:]+f[:1]):
            edges[(min(a,b),max(a,b))]+=1
        for a in f: val[a]+=1
    nonman=sum(1 for c in edges.values() if c>2)
    bound=sum(1 for c in edges.values() if c==1)
    vhist=collections.Counter(val.values())
    poles=sum(c for k,c in vhist.items() if k!=4)
    nfaces=len(F)
    print(f"{p}")
    print(f"  verts={len(V)} faces={nfaces} quads={nquad} ({100.0*nquad/max(nfaces,1):.1f}%) tris={ntri} other={nfaces-nquad-ntri}")
    print(f"  edges={len(edges)} nonmanifold={nonman} boundary={bound}")
    print(f"  valence histogram={dict(sorted(vhist.items()))} poles(valence!=4)={poles} ({100.0*poles/max(len(V),1):.1f}% of verts)")
    return dict(verts=len(V), faces=nfaces, quads=nquad, tris=ntri, nonman=nonman, bound=bound, poles=poles)
if __name__=='__main__':
    for p in sys.argv[1:]: analyze(p)
