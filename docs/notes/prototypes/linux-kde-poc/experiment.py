"""Drive only private XTest input; sample raw frames before compression to preserve early placement evidence."""
import json, pathlib, sys, time
from PIL import Image, ImageChops
from Xlib import display, X
from Xlib.ext import xtest
root_path=pathlib.Path('/work')
name,x,y=sys.argv[1],int(sys.argv[2]),int(sys.argv[3])
out=root_path/'frames'/name
out.mkdir(parents=True,exist_ok=True)
d=display.Display(':77')
root=d.screen().root
geometry=root.get_geometry()
xtest.fake_input(d,X.MotionNotify,x=x,y=y)
d.sync()
start=time.monotonic()
xtest.fake_input(d,X.ButtonPress,detail=1)
xtest.fake_input(d,X.ButtonRelease,detail=1)
d.sync()
frames=[]
for i in range(60):
    raw=root.get_image(0,0,geometry.width,geometry.height,X.ZPixmap,0xffffffff)
    frames.append((time.monotonic()-start,raw.data))
    time.sleep(0.012)
rows=[]
for i,(elapsed,data) in enumerate(frames):
    im=Image.frombytes('RGB',(geometry.width,geometry.height),data,'raw','BGRX')
    bounds={}
    for key,color in [('shortcuts',(11,208,232)),('web',(255,173,50))]:
        masks=[channel.point(lambda value,target=target:255 if value==target else 0) for channel,target in zip(im.split(),color)]
        mask=ImageChops.multiply(ImageChops.multiply(masks[0],masks[1]),masks[2])
        bounds[key]=mask.getbbox()
    rows.append({'frame':i,'elapsedSeconds':round(elapsed,4),'bounds':bounds})
    im.save(out/f'{i:02d}.png')
(out/'observations.json').write_text(json.dumps(rows,indent=2))
visible=[r for r in rows if any(r['bounds'].values())]
print(json.dumps({'experiment':name,'pointer':[x,y],'sampleCount':len(rows),'firstVisible':visible[0] if visible else None,'uniqueVisibleBounds':{key:list({tuple(r['bounds'][key]) for r in rows if r['bounds'][key]}) for key in ['shortcuts','web']}},indent=2))
