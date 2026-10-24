"""Rebuild the shipped theme from Akane-K's CC0 drawing; requires Pillow.

Source and license: assets/ATTRIBUTION.md. No generative processing is used.
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageOps

assets = Path(__file__).resolve().parent.parent / 'assets'
character = Image.open(assets / 'kawaii-original.png').convert('RGBA')

def background(size):
    image = Image.new('RGB', size)
    draw = ImageDraw.Draw(image)
    for y in range(size[1]):
        t = y / max(1, size[1] - 1)
        draw.line((0, y, size[0], y), fill=tuple(round(a + (b - a) * t) for a, b in zip((255, 221, 239), (207, 153, 196))))
    return image.convert('RGBA')

home = background((1920, 1080))
figure = character.copy(); figure.thumbnail((1050, 1200), Image.Resampling.LANCZOS)
home.alpha_composite(figure, (1100, 50))
home.convert('RGB').save(assets / 'kawaii-home.jpg', quality=92, optimize=True)

menu = background((800, 1400))
figure = character.copy(); figure.thumbnail((830, 1360), Image.Resampling.LANCZOS)
menu.alpha_composite(figure, (90, 80))
menu.convert('RGB').save(assets / 'kawaii-menu.jpg', quality=92, optimize=True)

face = character.crop((500, 20, 1620, 1150))
chrome = background((2880, 240))
portrait = ImageOps.fit(face, (300, 290), method=Image.Resampling.LANCZOS)
for x in (150, 1250, 2450):
    chrome.alpha_composite(portrait, (x, 0))
chrome.convert('RGB').save(assets / 'kawaii-chrome.jpg', quality=92, optimize=True)

icon = background((256, 256))
portrait = ImageOps.fit(face, (256, 256), method=Image.Resampling.LANCZOS)
icon.alpha_composite(portrait)
mask = Image.new('L', (256, 256)); ImageDraw.Draw(mask).rounded_rectangle((0, 0, 255, 255), radius=52, fill=255)
icon.putalpha(mask)
icon.save(assets / 'mbrowser.ico', sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
