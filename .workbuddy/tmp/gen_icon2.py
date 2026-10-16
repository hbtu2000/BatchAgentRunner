#!/usr/bin/env python3
"""Pure-stdlib renderer: BatchAgentRunner icon -> PNG set + multi-size ICO.
No Pillow / no network needed. Supersampling gives anti-aliased edges.
"""
import math, zlib, struct, os

OUT = r"E:/Src/CodeBak/WorkAgent/src/BatchAgentRunner.Desktop/Assets"
SS = 4  # supersample factor

# 1024-space geometry
CX = CY = 512.0
TILE = (32.0, 32.0, 992.0, 992.0, 200.0)   # x0,y0,x1,y1,corner_r
RING_R, RING_HW = 318.0, 22.0
GAP_LO, GAP_HI = -116.0, -64.0             # gap centered at top (-90)
BARS = [
    (322.0, 394.0, 380.0, 56.0, 28.0, "w"),
    (322.0, 484.0, 380.0, 56.0, 28.0, "c"),   # active
    (322.0, 574.0, 380.0, 56.0, 28.0, "w"),
]
SPARK = [(512,166),(527.6,210.4),(572,226),(527.6,241.6),(512,286),
         (496.4,241.6),(452,226),(496.4,210.4)]
C0, C1, C2 = (139,92,246),(99,102,241),(79,70,229)
CYAN = (34,211,238)

def lerp(a,b,t): return tuple(int(a[i]+(b[i]-a[i])*t) for i in range(3))
def grad(t):
    return lerp(C0,C1,t/0.55) if t<0.55 else lerp(C1,C2,(t-0.55)/0.45)

def inside_rounded(nx,ny, x0,y0,x1,y1,r):
    if nx<x0 or nx>x1 or ny<y0 or ny>y1: return False
    left,right,top,bottom = nx-x0, x1-nx, ny-y0, y1-ny
    if left<r and top<r and (r-left)**2+(r-top)**2 > r*r: return False
    if left<r and bottom<r and (r-left)**2+(r-bottom)**2 > r*r: return False
    if right<r and top<r and (r-right)**2+(r-top)**2 > r*r: return False
    if right<r and bottom<r and (r-right)**2+(r-bottom)**2 > r*r: return False
    return True

def inside_tile(nx,ny):
    x0,y0,x1,y1,r = TILE
    if nx<x0 or nx>x1 or ny<y0 or ny>y1: return False
    left,right,top,bottom = nx-x0, x1-nx, ny-y0, y1-ny
    if left<r and top<r and (r-left)**2+(r-top)**2 > r*r: return False
    if left<r and bottom<r and (r-left)**2+(r-bottom)**2 > r*r: return False
    if right<r and top<r and (r-right)**2+(r-top)**2 > r*r: return False
    if right<r and bottom<r and (r-right)**2+(r-bottom)**2 > r*r: return False
    return True

def inside_poly(nx,ny,poly):
    inside=False; n=len(poly)
    for i in range(n):
        x1,y1=poly[i]; x2,y2=poly[(i+1)%n]
        if ((y1>ny)!=(y2>ny)) and (nx < (x2-x1)*(ny-y1)/(y2-y1)+x1):
            inside=not inside
    return inside

def pixel(nx,ny):
    if not inside_tile(nx,ny):
        return (0,0,0,0)
    t=(nx+ny)/(2*1024.0)
    r,g,b = grad(t)
    ga = int(56*max(0.0,1.0-ny/560.0))
    if ga>0:
        a=ga/255.0; r=int(r*(1-a)+255*a); g=int(g*(1-a)+255*a); b=int(b*(1-a)+255*a)
    dx,dy = nx-CX, ny-CY
    dist=math.hypot(dx,dy); ang=math.degrees(math.atan2(dy,dx))
    if abs(dist-RING_R)<=RING_HW and not (GAP_LO<=ang<=GAP_HI):
        return (255,255,255,255)
    for (bx,by,bw,bh,br,kind) in BARS:
        if inside_rounded(nx,ny, bx,by,bx+bw,by+bh,br):
            return (CYAN+(255,)) if kind=="c" else (255,255,255,255)
    if inside_poly(nx,ny,SPARK):
        return (CYAN+(255,))
    return (r,g,b,255)

def render(size):
    HR = size*SS
    acc = [0.0]*(HR*HR*4)
    for Y in range(HR):
        ny = (Y+0.5)/HR*1024.0
        row = Y*HR
        for X in range(HR):
            nx = (X+0.5)/HR*1024.0
            r,g,b,a = pixel(nx,ny)
            a/=255.0
            idx=(row+X)*4
            acc[idx]+=r*a; acc[idx+1]+=g*a; acc[idx+2]+=b*a; acc[idx+3]+=a
    out=bytearray(size*size*4)
    for oy in range(size):
        for ox in range(size):
            sR=sG=sB=sA=0.0
            base=((oy*SS)*HR+(ox*SS))
            for yy in range(SS):
                for xx in range(SS):
                    p=(base+yy*HR+xx)*4
                    sR+=acc[p]; sG+=acc[p+1]; sB+=acc[p+2]; sA+=acc[p+3]
            n=SS*SS
            avgA=sA/n
            o=(oy*size+ox)*4
            if avgA>1e-6:
                out[o]  = max(0,min(255,int(sR/sA+0.5)))
                out[o+1]= max(0,min(255,int(sG/sA+0.5)))
                out[o+2]= max(0,min(255,int(sB/sA+0.5)))
                out[o+3]= max(0,min(255,int(avgA*255+0.5)))
            else:
                out[o]=out[o+1]=out[o+2]=out[o+3]=0
    return bytes(out)

def chunk(typ,data):
    return struct.pack(">I",len(data))+typ+data+struct.pack(">I", zlib.crc32(typ+data)&0xffffffff)

def png_bytes(size,rgba):
    raw=bytearray()
    for y in range(size):
        raw.append(0)
        raw+=rgba[y*size*4:(y+1)*size*4]
    comp=zlib.compress(bytes(raw),9)
    p=b'\x89PNG\r\n\x1a\n'
    p+=chunk(b'IHDR',struct.pack(">IIBBBBB",size,size,8,6,0,0,0))
    p+=chunk(b'IDAT',comp)
    p+=chunk(b'IEND',b'')
    return p

def write_ico(path,entries):
    cnt=len(entries)
    head=struct.pack("<HHH",0,1,cnt)
    offset=6+cnt*16
    dirb=b''
    for (sz,png) in entries:
        w = sz if sz<256 else 0
        h = sz if sz<256 else 0
        dirb+=struct.pack("<BBBBHHII",w,h,0,0,1,32,len(png),offset)
        offset+=len(png)
    with open(path,'wb') as f:
        f.write(head+dirb)
        for (_,png) in entries:
            f.write(png)

def main():
    sizes=[16,24,32,48,64,128,256,512]
    entries=[]
    for s in sizes:
        rgba=render(s)
        png=png_bytes(s,rgba)
        with open(os.path.join(OUT,f"app-icon-{s}.png"),'wb') as f: f.write(png)
        entries.append((s,png))
        print("rendered",s,flush=True)
    write_ico(os.path.join(OUT,"app-icon.ico"),entries)
    with open(os.path.join(OUT,"app-icon-512.png"),'wb') as f:
        f.write(png_bytes(512,render(512)))
    print("done -> app-icon.ico + png sizes",flush=True)

if __name__=="__main__":
    main()
