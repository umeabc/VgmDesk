"""Generate synthetic audio, no external assets."""
import io
import math
import struct as s
import sys
import wave
from pathlib import Path

root = Path(sys.argv[1]); root.mkdir(parents=True, exist_ok=True)

def tone(n, freq):
    return s.pack('<' + 'h'*n, *(int(8000*math.sin(2*math.pi*i*freq/16000)) for i in range(n)))

buf = io.BytesIO()
with wave.open(buf,'wb') as w:
    w.setparams((1,2,16000,0,'NONE','not compressed'))
    w.writeframes(tone(64000,440))
raw = bytearray(buf.getvalue())
smpl = s.pack('<15I', 0,0,62500,60,0,0,0,1,0, 0,0,8000,23999,0,0)
raw[36:36] = b'smpl' + s.pack('<I',len(smpl)) + smpl
s.pack_into('<I', raw, 4, len(raw)-8)
(root/'循环 音频.lwav').write_bytes(raw)

n1, n2 = 16000, 32000
names = s.pack('<2I',8,14) + b'first\0second\0'
fsb = s.pack('<4s6I32x',b'FSB5',1,2,16,len(names),(n1+n2)*2,2)
fsb += s.pack('<2Q', (n1<<34) | (4<<1), (n2<<34) | ((n1*2//32)<<7) | (4<<1))
(root/'双音轨.fsb').write_bytes(fsb+names+tone(n1,220)+tone(n2,660))
(root/'broken.fsb').write_bytes(b'not audio')
