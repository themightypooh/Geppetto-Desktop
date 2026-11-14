// Minimal OBJ in/out harness for Mobile's shipped libgminstantmeshes.so.
// Same call Mobile's AutoRetopo.cs makes: gm_im_remesh(targetFaces, quads=1,
// smoothIterations=2, threads=4). Usage: im-run <in.obj> <out.obj> <targetFaces>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>
#include <chrono>
#include <dlfcn.h>

typedef void* (*RemeshFn)(const float*, int, const unsigned int*, int, int, int, int, int, volatile int*, volatile int*);
typedef void (*CountsFn)(void*, int*, int*, int*);
typedef int (*CopyFn)(void*, float*, int*);
typedef void (*FreeFn)(void*);
typedef const char* (*ErrorFn)();

static bool read_obj(const char* path, std::vector<float>& pos, std::vector<unsigned int>& tri) {
    FILE* f = fopen(path, "r");
    if (!f) { fprintf(stderr, "cannot open %s\n", path); return false; }
    char line[1024];
    while (fgets(line, sizeof(line), f)) {
        if (line[0] == 'v' && line[1] == ' ') {
            float x, y, z;
            if (sscanf(line + 2, "%f %f %f", &x, &y, &z) == 3) { pos.push_back(x); pos.push_back(y); pos.push_back(z); }
        } else if (line[0] == 'f' && line[1] == ' ') {
            std::vector<unsigned int> idx;
            char* p = line + 2;
            while (*p) {
                while (*p == ' ' || *p == '\t') p++;
                if (!*p || *p == '\n') break;
                unsigned int v = 0;
                if (sscanf(p, "%u", &v) == 1) idx.push_back(v - 1);
                while (*p && *p != ' ' && *p != '\t' && *p != '\n') p++;
            }
            if (idx.size() < 3) continue;
            for (size_t i = 1; i + 1 < idx.size(); i++) {
                tri.push_back(idx[0]); tri.push_back(idx[i]); tri.push_back(idx[i + 1]);
            }
        }
    }
    fclose(f);
    return true;
}

static bool write_obj(const char* path, const std::vector<float>& pos, const std::vector<int>& faces, int corners) {
    FILE* f = fopen(path, "w");
    if (!f) { fprintf(stderr, "cannot write %s\n", path); return false; }
    for (size_t i = 0; i + 2 < pos.size(); i += 3) fprintf(f, "v %.6g %.6g %.6g\n", pos[i], pos[i + 1], pos[i + 2]);
    int nf = (int)faces.size() / 4;
    for (int i = 0; i < nf; i++) {
        int a = faces[i * 4], b = faces[i * 4 + 1], c = faces[i * 4 + 2], d = faces[i * 4 + 3];
        if (corners == 4 && d >= 0) fprintf(f, "f %d %d %d %d\n", a + 1, b + 1, c + 1, d + 1);
        else fprintf(f, "f %d %d %d\n", a + 1, b + 1, c + 1);
    }
    fclose(f);
    return true;
}

int main(int argc, char** argv) {
    if (argc != 4) { fprintf(stderr, "usage: im-run <in.obj> <out.obj> <targetFaces>\n"); return 2; }
    const char* libpath = "/home/poo/geppetto-mobile/app/addons/instantmeshes/lib/linux/x86_64/libgminstantmeshes.so";
    void* lib = dlopen(libpath, RTLD_NOW | RTLD_LOCAL);
    if (!lib) { fprintf(stderr, "dlopen failed: %s\n", dlerror()); return 2; }
    RemeshFn remesh = (RemeshFn)dlsym(lib, "gm_im_remesh");
    CountsFn counts = (CountsFn)dlsym(lib, "gm_im_counts");
    CopyFn copy = (CopyFn)dlsym(lib, "gm_im_copy");
    FreeFn freefn = (FreeFn)dlsym(lib, "gm_im_free");
    ErrorFn errorfn = (ErrorFn)dlsym(lib, "gm_im_error");
    if (!remesh || !counts || !copy || !freefn || !errorfn) { fprintf(stderr, "missing export\n"); return 2; }

    std::vector<float> pos; std::vector<unsigned int> tri;
    if (!read_obj(argv[1], pos, tri)) return 2;
    int targetFaces = atoi(argv[3]);
    volatile int cancel = 0, progress = 0;
    auto t0 = std::chrono::steady_clock::now();
    void* h = remesh(pos.data(), (int)pos.size() / 3, tri.data(), (int)tri.size() / 3, targetFaces, 1, 2, 4, &cancel, &progress);
    auto t1 = std::chrono::steady_clock::now();
    double secs = std::chrono::duration<double>(t1 - t0).count();
    if (!h) { fprintf(stderr, "remesh failed: %s\n", errorfn()); return 1; }
    int nv = 0, nf = 0, corners = 0;
    counts(h, &nv, &nf, &corners);
    std::vector<float> opos((size_t)nv * 3);
    std::vector<int> ofaces((size_t)nf * 4);
    copy(h, opos.data(), ofaces.data());
    freefn(h);
    if (!write_obj(argv[2], opos, ofaces, corners)) return 2;
    printf("instant-meshes: in %d verts %d tris -> out %d verts %d faces (%d corners/face) in %.2fs\n",
           (int)pos.size() / 3, (int)tri.size() / 3, nv, nf, corners, secs);
    return 0;
}
