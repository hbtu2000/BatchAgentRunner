import zlib, struct
p = r"E:/Src/CodeBak/WorkAgent/src/BatchAgentRunner.Desktop/Assets/app-icon-512.png"
data = open(p,'rb').read()
assert data[:8]==b'\x89PNG\r\n\x1a\n'
i=8; idat=b''
while i < len(data):
    ln = struct.unpack(">I", data[i:i+4])[0]
    typ = data[i+4:i+8]
    payload = data[i+8:i+8+ln]
    if typ==b'IHDR':
        w,h,bd,ct,cm,fm,il = struct.unpack(">IIBBBBB", payload)
        print("IHDR", w,h,bd,ct,cm,fm,il)
    elif typ==b'IDAT':
        idat += payload
    i += 12+ln
raw = zlib.decompress(idat)
def px(x,y):
    o = (y*(512*4+1))+1+x*4
    return tuple(raw[o:o+4])
for (x,y) in [(0,0),(256,256),(100,100),(256,80),(256,420),(80,256),(430,256),(512-1,512-1)]:
    print((x,y), px(x,y))
