"""
Builds every Nexus icon from the logo PNG (src/Nexus.Hub/Assets/Nexus-logo.png, a black "N" on transparent).

Outputs (src/Nexus.Hub/Assets):
  Nexus.ico          app / Start Menu / tray icon: the N on a light rounded tile, readable on light and dark taskbars
  Nexus-dark.png     the N in near-black, for light backgrounds (hub header in light mode)
  Nexus-light.png    the N in white, for dark backgrounds (hub header in dark mode)
  Nexus-tray-white.ico / Nexus-tray-black.ico   notification area icon for dark / light taskbars
and src/Common/Resources (linked into the Revit and AutoCAD add-ins):
  Nexus-N-black-16/32.png, Nexus-N-white-16/32.png   ribbon button images for light / dark ribbons

Usage:  python build/make-icons.py      (needs Pillow: pip install pillow)
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageOps

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src" / "Nexus.Hub" / "Assets"
LOGO = ASSETS / "Nexus-logo.png"
TILE = (255, 255, 255, 255)
INK = (20, 20, 20, 255)


def glyph(color, size, padding=0.0):
    """The logo recoloured to one colour, fitted in a square with optional padding."""
    src = Image.open(LOGO).convert("RGBA")
    alpha = src.getchannel("A")
    if alpha.getextrema() == (255, 255):
        # No transparency: treat dark pixels as the mark.
        alpha = ImageOps.invert(src.convert("L"))
    bbox = alpha.getbbox()
    alpha = alpha.crop(bbox)
    inner = round(size * (1 - 2 * padding))
    w, h = alpha.size
    scale = inner / max(w, h)
    alpha = alpha.resize((max(1, round(w * scale)), max(1, round(h * scale))), Image.LANCZOS)
    out = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    solid = Image.new("RGBA", alpha.size, color)
    out.paste(solid, ((size - alpha.width) // 2, (size - alpha.height) // 2), alpha)
    return out


def tiled(size):
    """The N on a white rounded square with a hairline border."""
    scale = 4
    big = size * scale
    tile = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(tile)
    r = round(big * 0.2)
    d.rounded_rectangle([0, 0, big - 1, big - 1], radius=r, fill=TILE, outline=(0, 0, 0, 60), width=max(1, scale))
    mark = glyph(INK, big, padding=0.12 if size <= 24 else 0.18)
    tile.alpha_composite(mark)
    return tile.resize((size, size), Image.LANCZOS)


def main():
    if not LOGO.exists():
        raise SystemExit(f"Put the logo at {LOGO} first.")
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    images = [tiled(s) for s in sizes]
    images[-1].save(ASSETS / "Nexus.ico", sizes=[(s, s) for s in sizes], append_images=images[:-1])
    glyph(INK, 256).save(ASSETS / "Nexus-dark.png")
    glyph((255, 255, 255, 255), 256).save(ASSETS / "Nexus-light.png")
    res = ROOT / "src" / "Common" / "Resources"
    res.mkdir(parents=True, exist_ok=True)
    for stale in ("Nexus-16.png", "Nexus-32.png"):
        (res / stale).unlink(missing_ok=True)
    # Plain N for ribbons: black on light ribbons, white on dark ribbons (picked at run time).
    for size in (16, 32):
        glyph(INK, size, padding=0.06).save(res / f"Nexus-N-black-{size}.png")
        glyph((255, 255, 255, 255), size, padding=0.06).save(res / f"Nexus-N-white-{size}.png")
    # Plain N for the notification area: white on a dark taskbar, black on a light one.
    tray = [16, 20, 24, 32, 40, 48]
    for name, color in (("Nexus-tray-white.ico", (255, 255, 255, 255)), ("Nexus-tray-black.ico", INK)):
        imgs = [glyph(color, s, padding=0.04) for s in tray]
        imgs[-1].save(ASSETS / name, sizes=[(s, s) for s in tray], append_images=imgs[:-1])
    print("Icons written to", ASSETS)


if __name__ == "__main__":
    main()
