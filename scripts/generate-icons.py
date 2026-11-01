#!/usr/bin/env python3
"""Render the shared brand with Python's standard library; no runtime dependency."""
import argparse
import struct
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / 'src' / 'windows' / 'assets'
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
SVG = '''<svg xmlns="http://www.w3.org/2000/svg" width="512" height="512" viewBox="0 0 512 512">
  <title>OptiScaler Backup — layered backups with a checkmark</title>
  <defs><linearGradient id="tile" x1="0" y1="0" x2="0" y2="1"><stop stop-color="#CBECA0"/><stop offset="1" stop-color="#98C35F"/></linearGradient></defs>
  <rect x="24" y="24" width="464" height="464" rx="104" fill="url(#tile)"/>
  <rect x="184" y="112" width="208" height="208" rx="36" fill="#65864A"/>
  <rect x="112" y="176" width="232" height="232" rx="38" fill="#192330"/>
  <path d="M164 286 L210 332 L292 240" fill="none" stroke="#C5E992" stroke-width="30" stroke-linecap="round" stroke-linejoin="round"/>
</svg>
'''


def inside(x, y, left, top, right, bottom, radius):
    dx = max(left + radius - x, 0, x - (right - radius))
    dy = max(top + radius - y, 0, y - (bottom - radius))
    return left <= x <= right and top <= y <= bottom and dx * dx + dy * dy <= radius * radius


def segment_distance_squared(x, y, ax, ay, bx, by):
    dx, dy = bx - ax, by - ay
    fraction = max(0, min(1, ((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy)))
    return (x - ax - fraction * dx) ** 2 + (y - ay - fraction * dy) ** 2


def color_at(x, y):
    if not inside(x, y, 24, 24, 488, 488, 104):
        return None
    fraction = (y - 24) / 464
    color = tuple(round(a + (b - a) * fraction) for a, b in zip((203, 236, 160), (152, 195, 95)))
    if inside(x, y, 184, 112, 392, 320, 36):
        color = (101, 134, 74)
    if inside(x, y, 112, 176, 344, 408, 38):
        color = (25, 35, 48)
    if min(segment_distance_squared(x, y, 164, 286, 210, 332), segment_distance_squared(x, y, 210, 332, 292, 240)) <= 225:
        color = (197, 233, 146)
    return color


def chunk(kind, data):
    return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data))


def png(size):
    samples = 4
    step = 512 / (size * samples)
    rows = bytearray()
    for y in range(size):
        rows.append(0)
        for x in range(size):
            colors = [color_at((x * samples + sx + .5) * step, (y * samples + sy + .5) * step)
                      for sy in range(samples) for sx in range(samples)]
            opaque = [color for color in colors if color is not None]
            pixel = tuple(round(sum(color[channel] for color in opaque) / len(opaque)) for channel in range(3)) if opaque else (0, 0, 0)
            rows.extend((*pixel, round(255 * len(opaque) / (samples * samples))))
    header = struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', header) + chunk(b'IDAT', zlib.compress(rows, 9)) + chunk(b'IEND', b'')


def outputs():
    frames = [(size, png(size)) for size in SIZES]
    offset = 6 + 16 * len(frames)
    directory = bytearray(struct.pack('<HHH', 0, 1, len(frames)))
    for size, data in frames:
        directory.extend(struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(data), offset))
        offset += len(data)
    return {'optibackup.svg': SVG.encode(), 'optibackup.png': frames[-1][1],
            'optibackup.ico': bytes(directory) + b''.join(data for _, data in frames)}


def pixels(data):
    if data[:8] != b'\x89PNG\r\n\x1a\n':
        raise ValueError('Invalid PNG signature')
    position = 8
    compressed = bytearray()
    width = height = 0
    while position < len(data):
        length = struct.unpack_from('>I', data, position)[0]
        kind = data[position + 4:position + 8]
        payload = data[position + 8:position + 8 + length]
        if len(payload) != length or struct.unpack_from('>I', data, position + 8 + length)[0] != zlib.crc32(kind + payload):
            raise ValueError('Invalid PNG chunk')
        if kind == b'IHDR':
            width, height, depth, color, compression, filtering, interlace = struct.unpack('>IIBBBBB', payload)
            if width not in SIZES or width != height or (depth, color, compression, filtering, interlace) != (8, 6, 0, 0, 0):
                raise ValueError('Unexpected icon PNG format')
        elif kind == b'IDAT':
            compressed.extend(payload)
        position += 12 + length
    maximum = (width * 4 + 1) * height
    decoder = zlib.decompressobj()
    raw = decoder.decompress(compressed, maximum + 1)
    if len(raw) != maximum or not decoder.eof or decoder.unused_data or not width:
        raise ValueError('Unexpected icon pixel data')
    return width, raw


def canonical(name, data):
    if name.endswith('.svg'):
        return data.replace(b'\r\n', b'\n')
    if name.endswith('.png'):
        return pixels(data)
    if struct.unpack_from('<HHH', data) != (0, 1, len(SIZES)):
        raise ValueError('Unexpected ICO directory')
    frames = []
    for index, expected in enumerate(SIZES):
        width, height, colors, reserved, planes, bits, length, offset = struct.unpack_from('<BBBBHHII', data, 6 + index * 16)
        if (width or 256, height or 256, colors, reserved, planes, bits) != (expected, expected, 0, 0, 1, 32):
            raise ValueError('Unexpected ICO frame')
        if offset < 6 + 16 * len(SIZES) or offset + length > len(data):
            raise ValueError('Invalid ICO offset')
        frame = pixels(data[offset:offset + length])
        if frame[0] != expected:
            raise ValueError('ICO frame dimensions differ')
        frames.append(frame)
    return frames


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true', help='Verify committed assets without writing them.')
    args = parser.parse_args()
    for name, data in outputs().items():
        path = ROOT / name
        if args.check:
            if not path.exists() or canonical(name, path.read_bytes()) != canonical(name, data):
                raise SystemExit(f'Icon asset differs from source: {path}')
        else:
            ROOT.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
    print('Verified' if args.check else 'Generated', 'SVG, transparent PNG and ICO sizes', ', '.join(map(str, SIZES)))


if __name__ == '__main__':
    main()
