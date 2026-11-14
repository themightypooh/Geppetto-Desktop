"""Crease following: what fraction of output edges lie on input sharp edges?
Input sharp edges = M2 edges with dihedral angle > 30deg. Metric: for each output
edge midpoint, distance to nearest input sharp segment; report % within eps."""
import sys, collections
import numpy as np

def read_obj(p):
    verts=[]; faces=[]
    with open(p, errors='ignore') as fh:
        for line in fh:
            if line.startswith('v '): verts.append(tuple(float(x) for x in line.split()[1:4]))
            elif line.startswith('f '):
                idx=[int(x.split('/')[0])-1 for x in line.split()[1:]]
                for i in range(1, len(idx)-1): faces.append((idx[0], idx[i], idx[i+1]))
    return np.array(verts, dtype=np.float64), np.array(faces, dtype=np.int64)

def sharp_segments(V, F, thresh_deg=30.0):
    # face normals
    tris = V[F]
    n = np.cross(tris[:,1]-tris[:,0], tris[:,2]-tris[:,0])
    ln = np.linalg.norm(n, axis=1, keepdims=True); ln[ln==0]=1
    n = n/ln
    edge_faces = collections.defaultdict(list)
    for fi,(a,b,c) in enumerate(F):
        for e in ((a,b),(b,c),(c,a)):
            edge_faces[(min(e),max(e))].append(fi)
    cos_t = np.cos(np.radians(thresh_deg))
    segs=[]
    for (a,b), fl in edge_faces.items():
        if len(fl)!=2: continue
        if abs(float(n[fl[0]] @ n[fl[1]])) < cos_t:
            segs.append((V[a], V[b]))
    return segs

def edges_of(V, F):
    es=set()
    for f in F:
        for a,b in zip(f, list(f[1:])+[f[0]]):
            es.add((min(a,b),max(a,b)))
    return es

def point_seg_dist(P, A, B):
    # P: (M,3), A/B: (N,3) -> (M,N) min distance; chunked over M
    out=np.empty((len(P),), dtype=np.float64)
    AB=B-A; denom=(AB*AB).sum(axis=1); denom[denom==0]=1
    for i in range(0, len(P), 512):
        Pc=P[i:i+512]
        AP=Pc[:,None,:]-A[None,:,:]
        t=np.einsum('mni,ni->mn', AP, AB)/denom[None,:]
        t=np.clip(t,0,1)
        Q=A[None,:,:]+t[:,:,None]*AB[None,:,:]
        d=np.linalg.norm(Pc[:,None,:]-Q, axis=2)
        out[i:i+512]=d.min(axis=1)
    return out

V, F = read_obj(sys.argv[1])
segs = sharp_segments(V, F)
print(f"input {sys.argv[1]}: {len(F)} faces, sharp edges (>30deg): {len(segs)}")
A=np.array([s[0] for s in segs]); B=np.array([s[1] for s in segs])
diag=float(np.linalg.norm(V.max(axis=0)-V.min(axis=0)))
for p in sys.argv[2:]:
    Vo, Fo = read_obj(p)
    es=list(edges_of(Vo, Fo))
    mid=np.array([(Vo[a]+Vo[b])/2 for a,b in es])
    d=point_seg_dist(mid, A, B)
    for pct in (0.5, 1.0):
        eps=diag*pct/100.0
        print(f"  {p}: edges={len(es)} within {pct}% diag of a sharp edge: {100.0*(d<eps).mean():.1f}%")
