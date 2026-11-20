"""Rebuild the Windows icon from the same polygons used in App.xaml."""
from pathlib import Path
from PIL import Image, ImageDraw

SIZE = 1024
image = Image.new("RGBA", (SIZE, SIZE))
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((38, 38, SIZE - 38, SIZE - 38), radius=216, fill="#0d1924", outline="#314c57", width=25)
ring = [(16, 0), (28, 0), (34, 6), (20, 20), (14, 20), (26, 8), (24, 6), (17, 6),
        (6, 17), (6, 24), (8, 26), (15, 26), (20, 21), (27, 21), (16, 32), (5, 32), (0, 27), (0, 15)]
cut = [(30, 10), (34, 6), (34, 17), (29, 22), (22, 22), (28, 16), (28, 12)]
for contour, color in ((ring, "#70f5d9"), (cut, "#21a496")):
    draw.polygon([(239 + x * 16, 256 + y * 16) for x, y in contour], fill=color)
target = Path(__file__).resolve().parents[1] / "apps" / "desktop" / "ovrix.ico"
image.save(target, sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
packaging = target.parents[2] / "packaging"
for name, size in (("wizard-header.bmp", (58, 58)), ("wizard-side.bmp", (164, 314))):
    panel = Image.new("RGB", size, "#0d1924")
    panel_draw = ImageDraw.Draw(panel)
    for x in range(0, size[0], 18):
        panel_draw.line((x, 0, x, size[1]), fill="#162a37")
    for y in range(0, size[1], 18):
        panel_draw.line((0, y, size[0], y), fill="#162a37")
    icon = image.resize((46, 46) if size[0] < 60 else (94, 94), Image.Resampling.LANCZOS)
    panel.paste(icon, ((size[0] - icon.width) // 2, (size[1] - icon.height) // 2), icon)
    panel.save(packaging / name)
