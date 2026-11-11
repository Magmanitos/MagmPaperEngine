"""Generate the application's icon using Pillow (not needed to build the app)."""
from pathlib import Path
from math import hypot
from PIL import Image, ImageDraw
root = Path(__file__).resolve().parents[1]
im = Image.new('RGBA', (1024, 1024))
d = ImageDraw.Draw(im)
d.rounded_rectangle((30, 30, 994, 994), radius=220, fill='#111525')
d.rounded_rectangle((50, 50, 974, 974), radius=200, outline='#333250', width=12)
# A bright M over a sweeping wallpaper wave remains readable at shortcut sizes.
upper, lower = [], []
for i in range(501):
 t=i/500
 x=(1-t)**3*120+3*(1-t)**2*t*350+3*(1-t)*t*t*650+t**3*904
 y=(1-t)**3*760+3*(1-t)**2*t*1000+3*(1-t)*t*t*480+t**3*720
 dx=3*(1-t)**2*(350-120)+6*(1-t)*t*(650-350)+3*t*t*(904-650)
 dy=3*(1-t)**2*(1000-760)+6*(1-t)*t*(480-1000)+3*t*t*(720-480)
 nx,ny=-dy/hypot(dx,dy)*26,dx/hypot(dx,dy)*26
 upper.append((x+nx,y+ny)); lower.append((x-nx,y-ny))
d.polygon(upper+lower[::-1], fill='#8d6bff')
d.line([(255, 675),(255, 300),(512, 550),(769, 300),(769, 675)], fill='#eae9ff', width=78, joint='curve')
for x,y in [(255,675),(255,300),(512,550),(769,300),(769,675)]: d.ellipse((x-39,y-39,x+39,y+39),fill='#eae9ff')
im.resize((256,256),Image.Resampling.LANCZOS).save(root/'build/icon.png')
im.save(root/'build/icon.ico', sizes=[(n,n) for n in [16,24,32,48,64,128,256]])
