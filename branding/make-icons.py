import math, sys
TOP, BOT = "#4F46E5", "#1E3A8A"   # indigo → navy
W = "#FFFFFF"
DY = -2  # optical centre: lift the glyph 2 px

def arc(cx, cy, r, a0, a1):
    p = lambda a: (cx + r*math.cos(math.radians(a)), cy + r*math.sin(math.radians(a)))
    (x0, y0), (x1, y1) = p(a0), p(a1)
    return f"M{x0:.2f} {y0:.2f}A{r} {r} 0 0 1 {x1:.2f} {y1:.2f}"

def glyph(small=False, color=W):
    """Monitor with a hub node broadcasting to both sides: a computer you reach remotely."""
    sw = 18 if small else 13            # outline weight
    cy = 114 + DY
    g = []
    g.append(("rect", dict(x=46, y=58+DY, w=164, h=112, rx=22, sw=sw)))
    g.append(("path", dict(d=f"M100 {202+DY}h56M128 {172+DY}v{28 if not small else 26}", sw=sw)))
    g.append(("circle", dict(cx=128, cy=cy, r=15 if small else 12)))
    radii = (34,) if small else (26, 44)
    for r in radii:
        for a in (-35, 145):
            g.append(("path", dict(d=arc(128, cy, r, a, a+70), sw=13 if small else 9)))
    return g

def svg_glyph(g, color=W):
    out = []
    for kind, a in g:
        if kind == "rect":
            out.append(f'<rect x="{a["x"]}" y="{a["y"]}" width="{a["w"]}" height="{a["h"]}" rx="{a["rx"]}" fill="none" stroke="{color}" stroke-width="{a["sw"]}"/>')
        elif kind == "path":
            out.append(f'<path d="{a["d"]}" fill="none" stroke="{color}" stroke-width="{a["sw"]}" stroke-linecap="round"/>')
        else:
            out.append(f'<circle cx="{a["cx"]}" cy="{a["cy"]}" r="{a["r"]}" fill="{color}"/>')
    return "".join(out)

def svg_icon(small=False):
    return ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">'
            f'<defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{TOP}"/><stop offset="1" stop-color="{BOT}"/></linearGradient></defs>'
            '<rect width="256" height="256" rx="58" fill="url(#g)"/>' + svg_glyph(glyph(small)) + '</svg>')

def rect_path(x, y, w, h, r):
    return (f"M{x+r} {y}H{x+w-r}A{r} {r} 0 0 1 {x+w} {y+r}V{y+h-r}A{r} {r} 0 0 1 {x+w-r} {y+h}"
            f"H{x+r}A{r} {r} 0 0 1 {x} {y+h-r}V{y+r}A{r} {r} 0 0 1 {x+r} {y}Z")

def android_vector(comment, color="#FFFFFFFF"):
    # 108dp canvas; the glyph (≈177×157 of 256) scaled to fit the 66dp safe circle.
    s = 0.26; tx = 54 - 128*s; ty = 54 - (128+DY)*s
    lines = []
    for kind, a in glyph():
        if kind == "rect": d = rect_path(a["x"], a["y"], a["w"], a["h"], a["rx"]); lines.append((d, a["sw"], False, False))
        elif kind == "path": lines.append((a["d"], a["sw"], False, True))
        else:
            cx, cy, r = a["cx"], a["cy"], a["r"]
            lines.append((f"M{cx-r} {cy}A{r} {r} 0 1 1 {cx+r} {cy}A{r} {r} 0 1 1 {cx-r} {cy}Z", 0, True, False))
    body = []
    for d, sw, fill, cap in lines:
        attrs = [f'android:pathData="{d}"']
        if fill: attrs.append(f'android:fillColor="{color}"')
        else:
            attrs += [f'android:strokeColor="{color}"', f'android:strokeWidth="{sw}"']
            if cap: attrs.append('android:strokeLineCap="round"')
        body.append("        <path\n            " + "\n            ".join(attrs) + " />")
    return (f'<?xml version="1.0" encoding="utf-8"?>\n<!-- {comment} -->\n'
            '<vector xmlns:android="http://schemas.android.com/apk/res/android"\n'
            '    android:width="108dp"\n    android:height="108dp"\n    android:viewportWidth="108"\n    android:viewportHeight="108">\n'
            f'    <group android:translateX="{tx:.3f}" android:translateY="{ty:.3f}" android:scaleX="{s}" android:scaleY="{s}">\n'
            + "\n".join(body) + "\n    </group>\n</vector>\n")

if __name__ == "__main__":
    out = sys.argv[1]
    open(f"{out}/showcontrol.svg", "w").write(svg_icon())
    open(f"{out}/showcontrol-small.svg", "w").write(svg_icon(True))
    open(f"{out}/fg.xml", "w").write(android_vector("Generated from branding/showcontrol.svg: a monitor with a hub node broadcasting both ways, fitted inside the 66dp safe circle."))
    open(f"{out}/mono.xml", "w").write(android_vector("Themed (monochrome) launcher icon, Android 13+. Same glyph as the foreground."))
    # tiny white mark for the agent dashboard's brand tile (no background)
    open(f"{out}/mark-white.svg", "w").write('<svg xmlns="http://www.w3.org/2000/svg" viewBox="40 48 176 168">' + svg_glyph(glyph(True)) + '</svg>')
