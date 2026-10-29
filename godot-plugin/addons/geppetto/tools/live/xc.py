import ctypes, sys, time
x=ctypes.CDLL("libX11.so.6"); t=ctypes.CDLL("libXtst.so.6")
x.XOpenDisplay.restype=ctypes.c_void_p
d=x.XOpenDisplay(None)
if not d: sys.exit("no display")
d=ctypes.c_void_p(d)
def mv(a,b): t.XTestFakeMotionEvent(d,-1,int(a),int(b),0); x.XFlush(d)
def btn(n,down): t.XTestFakeButtonEvent(d,n,down,0); x.XFlush(d)
MODS={"ctrl":"Control_L","shift":"Shift_L","alt":"Alt_L"}
def kc(name): return x.XKeysymToKeycode(d,x.XStringToKeysym(name.encode()))
def key(name):
    parts=name.split("+"); codes=[kc(MODS.get(q,q)) for q in parts]
    for c in codes: t.XTestFakeKeyEvent(d,c,1,0); x.XFlush(d); time.sleep(.03)
    time.sleep(.05)
    for c in reversed(codes): t.XTestFakeKeyEvent(d,c,0,0); x.XFlush(d); time.sleep(.03)
args=sys.argv[1:]
i=0
while i<len(args):
    c=args[i]
    if c=="m": mv(args[i+1],args[i+2]); i+=3
    elif c=="c": mv(args[i+1],args[i+2]); time.sleep(.1); btn(1,1); time.sleep(.08); btn(1,0); i+=3
    elif c=="r": mv(args[i+1],args[i+2]); time.sleep(.1); btn(3,1); time.sleep(.08); btn(3,0); i+=3
    elif c=="k": key(args[i+1]); i+=2
    elif c=="t":
        for ch in args[i+1]: key({".":"period","-":"minus"}.get(ch,ch))
        i+=2
    elif c=="w": time.sleep(float(args[i+1])); i+=2
    else: sys.exit("bad "+c)
    time.sleep(.15)
