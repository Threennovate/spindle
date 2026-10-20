#!/usr/bin/env python3
"""Draws the Android app's logo, launcher icon and notification icon from the SVGs in branding/.

The SVGs are made of rounded rectangles and paths, which map onto vector drawables directly. Run it again after
branding/build.mjs changes the drawing.
"""
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
BRAND = ROOT / "branding"
RES = ROOT / "android/app/src/main/res"


def rect_path(x, y, w, h, rx):
    rx = min(rx, w / 2, h / 2)
    return (f"M{x + rx:.3f},{y:.3f}h{w - 2 * rx:.3f}a{rx:.3f},{rx:.3f} 0 0 1 {rx:.3f},{rx:.3f}v{h - 2 * rx:.3f}"
            f"a{rx:.3f},{rx:.3f} 0 0 1 {-rx:.3f},{rx:.3f}h{-(w - 2 * rx):.3f}a{rx:.3f},{rx:.3f} 0 0 1 {-rx:.3f},{-rx:.3f}"
            f"v{-(h - 2 * rx):.3f}a{rx:.3f},{rx:.3f} 0 0 1 {rx:.3f},{-rx:.3f}z")


def number(attrs, name):
    match = re.search(rf'\b{name}="([-\d.]+)"', attrs)
    return float(match.group(1)) if match else 0.0


def shapes(svg, fill=None):
    """The rects and paths of an SVG as vector drawable paths, inside the groups of its transforms."""
    out = []
    tokens = re.finditer(r"<(g|rect|path)\b([^>]*?)/?>|</g>", svg)
    for token in tokens:
        if token.group(0) == "</g>":
            out.append("</group>")
            continue
        tag, attrs = token.group(1), token.group(2)
        if tag == "g":
            t = re.search(r'translate\(([-\d.]+) ([-\d.]+)\)', attrs)
            s = re.search(r'scale\(([-\d.]+)\)', attrs)
            group = "<group"
            if t: group += f' android:translateX="{float(t.group(1)):.4f}" android:translateY="{float(t.group(2)):.4f}"'
            if s: group += f' android:scaleX="{float(s.group(1)):.5f}" android:scaleY="{float(s.group(1)):.5f}"'
            out.append(group + ">")
            continue
        color = fill or re.search(r'fill="([^"]+)"', attrs).group(1)
        if tag == "rect":
            if 'width="64" height="64"' in attrs and "rx" not in attrs:
                continue  # The tile behind the app icon's mark: the launcher draws its own.
            data = rect_path(number(attrs, "x"), number(attrs, "y"), number(attrs, "width"), number(attrs, "height"), number(attrs, "rx"))
        else:
            data = re.search(r'\bd="([^"]+)"', attrs).group(1)
        out.append(f'<path android:fillColor="{color}" android:pathData="{data}"/>')
    return "\n    ".join(out)


def vector(svg, width_dp, height_dp, fill=None, viewport=None, wrap=None):
    box = viewport or [float(v) for v in re.search(r'viewBox="([^"]+)"', svg).group(1).split()][2:]
    body = shapes(svg, fill)
    if wrap:
        body = f"{wrap}\n    {body}\n    </group>"
    return (f'<?xml version="1.0" encoding="utf-8"?>\n<!-- Drawn by android/scripts/generate-brand.py from branding/. -->\n'
            f'<vector xmlns:android="http://schemas.android.com/apk/res/android" android:width="{width_dp}dp" android:height="{height_dp}dp"'
            f' android:viewportWidth="{box[0]:g}" android:viewportHeight="{box[1]:g}">\n    {body}\n</vector>\n')


def write(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text)
    print("wrote", path.relative_to(ROOT))


light = (BRAND / "spindle-logo-light.svg").read_text()
dark = (BRAND / "spindle-logo-dark.svg").read_text()
write(RES / "drawable/spindle_logo.xml", vector(light, 169, 46))
write(RES / "drawable-night/spindle_logo.xml", vector(dark, 169, 46))

# The launcher icon: the mark on midnight, inside the adaptive icon's 66 of 108 units that every mask keeps.
mark_dark = (BRAND / "spindle-mark-dark.svg").read_text()
scale = 52 / 64
offset = (108 - 64 * scale) / 2
wrap = f'<group android:translateX="{offset:.3f}" android:translateY="{offset:.3f}" android:scaleX="{scale:.4f}" android:scaleY="{scale:.4f}">'
write(RES / "drawable/ic_launcher_foreground.xml", vector(mark_dark, 108, 108, viewport=[108, 108], wrap=wrap))
write(RES / "drawable/ic_launcher_monochrome.xml", vector(mark_dark, 108, 108, fill="#FFFFFFFF", viewport=[108, 108], wrap=wrap))
write(RES / "values/ic_launcher_background.xml", '<?xml version="1.0" encoding="utf-8"?>\n<resources>\n    <color name="ic_launcher_background">#0E1322</color>\n</resources>\n')
adaptive = ('<?xml version="1.0" encoding="utf-8"?>\n<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">\n'
            '    <background android:drawable="@color/ic_launcher_background"/>\n    <foreground android:drawable="@drawable/ic_launcher_foreground"/>\n'
            '    <monochrome android:drawable="@drawable/ic_launcher_monochrome"/>\n</adaptive-icon>\n')
write(RES / "mipmap-anydpi-v26/ic_launcher.xml", adaptive)
write(RES / "mipmap-anydpi-v26/ic_launcher_round.xml", adaptive)

# The notification icon: the compact mark in one colour, as the system tints it.
compact = (BRAND / "spindle-mark-compact.svg").read_text()
write(RES / "drawable/ic_notification.xml", vector(compact, 24, 24, fill="#FFFFFFFF"))
