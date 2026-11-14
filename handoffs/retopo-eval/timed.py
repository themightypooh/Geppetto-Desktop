import subprocess, sys, time, resource
t0=time.time()
r=subprocess.run(sys.argv[1:])
dt=time.time()-t0
rss=resource.getrusage(resource.RUSAGE_CHILDREN).ru_maxrss/1024.0
print(f"[timed] exit={r.returncode} wall={dt:.2f}s maxrss={rss:.0f}MB cmd={' '.join(sys.argv[1:])}", flush=True)
sys.exit(r.returncode)
