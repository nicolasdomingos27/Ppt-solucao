# Gera os ícones do Background Presenter (opção B: passador + sinal).
# O fundo muda de cor conforme o estado mostrado na bandeja.
from PIL import Image, ImageDraw
import struct, io, sys

COLORS = {
    "app":    (255, 122, 46),   # laranja da marca
    "green":  (39, 174, 96),    # passador pronto + apresentação rodando
    "yellow": (242, 169, 0),    # passador pronto, sem apresentação
    "gray":   (127, 140, 141),  # pausado
    "red":    (214, 48, 49),    # passador não encontrado / não configurado
}

def draw(size, bg):
    SS = 8
    N = size * SS
    s = N / 256.0
    im = Image.new("RGBA", (N, N), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    small = size <= 24
    d.rounded_rectangle([8*s, 8*s, 248*s, 248*s], radius=56*s, fill=bg + (255,))
    white = (255, 255, 255, 255)
    if small:
        # versão simplificada: corpo mais largo, arcos mais grossos
        d.rounded_rectangle([84*s, 44*s, 172*s, 224*s], radius=44*s, fill=white)
        d.ellipse([106*s, 72*s, 150*s, 116*s], fill=bg + (255,))
        d.arc([(200-44)*s, (60-44)*s, (200+44)*s, (60+44)*s], 200, 250, fill=white, width=int(22*s))
    else:
        d.rounded_rectangle([92*s, 40*s, 164*s, 220*s], radius=36*s, fill=white)
        d.ellipse([112*s, 70*s, 144*s, 102*s], fill=bg + (255,))
        light = bg + (90,)
        d.rounded_rectangle([112*s, 120*s, 144*s, 140*s], radius=8*s, fill=light)
        d.rounded_rectangle([112*s, 152*s, 144*s, 172*s], radius=8*s, fill=light)
        for r in (30, 52):
            d.arc([(200-r)*s, (64-r)*s, (200+r)*s, (64+r)*s], 200, 250, fill=white, width=int(9*s))
    return im.resize((size, size), Image.LANCZOS)

def bmp_entry(img):
    """Entrada BMP (DIB 32 bits + máscara), o formato mais compatível para ícones pequenos."""
    w, h = img.size
    px = img.load()
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    rows = bytearray()
    for y in range(h - 1, -1, -1):
        for x in range(w):
            r, g, b, a = px[x, y]
            rows += bytes((b, g, r, a))
    mask_row = ((w + 31) // 32) * 4
    mask = bytes(mask_row * h)
    return header + bytes(rows) + mask

def png_entry(img):
    buf = io.BytesIO()
    img.save(buf, "PNG")
    return buf.getvalue()

def write_ico(path, bg, sizes):
    images = [draw(sz, bg) for sz in sizes]
    datas = [png_entry(im) if im.size[0] >= 256 else bmp_entry(im) for im in images]
    out = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    for im, data in zip(images, datas):
        w = im.size[0]
        out += struct.pack("<BBBBHHII", w % 256, w % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for data in datas:
        out += data
    open(path, "wb").write(out)

dest = sys.argv[1]
write_ico(f"{dest}/app.ico", COLORS["app"], [16, 20, 24, 32, 40, 48, 64, 128, 256])
for name in ("green", "yellow", "gray", "red"):
    write_ico(f"{dest}/tray-{name}.ico", COLORS[name], [16, 20, 24, 32, 40, 48, 64])
draw(256, COLORS["app"]).save(f"{dest}/app-256.png")
