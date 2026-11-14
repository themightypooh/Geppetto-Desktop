import sys
def read_obj(p):
    verts=[]; faces=[]
    with open(p, errors='ignore') as fh:
        for line in fh:
            if line.startswith('v '):
                verts.append(tuple(float(x) for x in line.split()[1:4]))
            elif line.startswith('f '):
                idx=[int(x.split('/')[0])-1 for x in line.split()[1:]]
                for i in range(1, len(idx)-1):
                    faces.append((idx[0], idx[i], idx[i+1]))
    return verts, faces
def subdivide(verts, faces):
    mid={}; nv=list(verts)
    def midpoint(a,b):
        k=(min(a,b),max(a,b))
        if k not in mid:
            va,vb=verts[a],verts[b]
            mid[k]=len(nv); nv.append(tuple((va[i]+vb[i])/2 for i in range(3)))
        return mid[k]
    nf=[]
    for a,b,c in faces:
        ab=midpoint(a,b); bc=midpoint(b,c); ca=midpoint(c,a)
        nf += [(a,ab,ca),(b,bc,ab),(c,ca,bc),(ab,bc,ca)]
    return nv, nf
def write_obj(p, verts, faces):
    with open(p,'w') as fh:
        for v in verts: fh.write(f"v {v[0]:.6g} {v[1]:.6g} {v[2]:.6g}\n")
        for f in faces: fh.write(f"f {f[0]+1} {f[1]+1} {f[2]+1}\n")
if __name__=='__main__':
    src, dst, iters = sys.argv[1], sys.argv[2], int(sys.argv[3])
    v,f = read_obj(src)
    for _ in range(iters): v,f = subdivide(v,f)
    write_obj(dst, v, f)
    print(f"{src} -> {dst}: {len(v)}v {len(f)}f")
