"""Compose the shipped PNG themes from the Ren'Py tutorial's hand-drawn sprites.

Requires Pillow. Sources, credits and MIT license: assets/ATTRIBUTION.md.
Only scaling, cropping and ordinary compositing; no generative processing.
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageOps

assets = Path(__file__).resolve().parent.parent / 'assets'
eileen = Image.open(assets / 'eileen-original.png').convert('RGBA')
lucy = Image.open(assets / 'lucy-original.png').convert('RGBA')

def background(size):
    image = Image.new('RGBA', size)
    draw = ImageDraw.Draw(image)
    for y in range(size[1]):
        t = y / max(1, size[1] - 1)
        draw.line((0, y, size[0], y), fill=tuple(round(a + (b - a) * t) for a, b in zip((253, 220, 237), (210, 159, 200))) + (255,))
    return image

home = background((1920, 1080))
figure = eileen.resize((int(eileen.width * 1050 / eileen.height), 1050), Image.Resampling.LANCZOS)
home.alpha_composite(figure, (1390, 30))
home.save(assets / 'sakura-home.png', optimize=True)

menu = background((720, 1200))
figure = lucy.resize((int(lucy.width * 1150 / lucy.height), 1150), Image.Resampling.LANCZOS)
menu.alpha_composite(figure, (150, 40))
menu.save(assets / 'sakura-menu.png', optimize=True)

# One continuous PNG for tabs, navigation, bookmarks and caption buttons.
# A single portrait appears on the right; no repeated faces or per-row crops.
header = background((2400, 286))
portrait = eileen.crop((0, 0, 320, 310)).resize((290, 281), Image.Resampling.LANCZOS)
header.alpha_composite(portrait, (2080, 5))
header.save(assets / 'sakura-header.png', optimize=True)

icon = background((256, 256))
icon.alpha_composite(ImageOps.fit(eileen.crop((0, 0, 320, 310)), (256, 256), method=Image.Resampling.LANCZOS))
mask = Image.new('L', (256, 256)); ImageDraw.Draw(mask).rounded_rectangle((0, 0, 255, 255), radius=52, fill=255)
icon.putalpha(mask)
icon.save(assets / 'mbrowser.ico', sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
