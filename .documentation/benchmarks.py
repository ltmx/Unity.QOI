# Generates the README benchmark tables as SVG (GitHub strips colors from Markdown tables).
# Rows where the GPU is faster than the CPU get a green background, the others a red one.
# Edit the numbers below and run: python .documentation/benchmarks.py
import os
from html import escape

HERE = os.path.dirname(os.path.abspath(__file__))

ENCODE = {
    'columns': ['Image', 'Resolution', 'QOI size', 'CPU', 'GPU'],
    'rows': [
        # image, resolution, size, cpu ms (EncodeToQOI, plain file), gpu ms (QoiGpu.Encode, incl. readback)
        ('Albedo', '512²', '1.0 MB', 1.5, 26.8),
        ('Albedo', '1024²', '3.7 MB', 6.0, 4.0),
        ('Albedo', '2048²', '14.1 MB', 19.4, 8.1),
        ('Albedo', '4096²', '57.4 MB', 84.7, 25.6),
        ('Normal map', '2048²', '3.1 MB', 14.3, 3.5),
        ('Mask', '2048²', '12.0 MB', 12.4, 6.8),
        ('Smooth gradient', '4096²', '2.9 MB', 48.7, 2.8),
        ('HDR albedo (half)', '2048²', '17.5 MB', 41.3, 9.6),
    ],
}

DECODE = {
    'columns': ['Image', 'Resolution', 'CPU 1 thread', 'CPU parallel', 'CPU parallel, plain file', 'GPU', 'GPU, reused RT'],
    'rows': [
        # image, resolution, cpu ms, cpu parallel ms (seekable), cpu parallel ms (plain), gpu ms, gpu reused ms
        ('Albedo', '512²', 3.6, 1.0, 2.5, 11.2, 9.6),
        ('Albedo', '1024²', 14.9, 3.3, 9.0, 10.7, 9.1),
        ('Albedo', '2048²', 62.9, 9.5, 34.6, 18.7, 16.7),
        ('Albedo', '4096²', 260.9, 49.9, 128.5, 42.5, 17.9),
        ('Normal map', '2048²', 29.5, 4.8, 29.7, 4.7, 4.0),
        ('Mask', '2048²', 40.8, 6.8, 22.2, 15.5, 14.2),
        ('Smooth gradient', '4096²', 65.7, 23.0, 35.0, 25.1, 11.0),
        ('HDR albedo (half)', '2048²', 145.1, 25.2, 155.7, 13.1, 10.6),
    ],
}

FONT = "-apple-system, BlinkMacSystemFont, 'Segoe UI', 'Noto Sans', Helvetica, Arial, sans-serif"
CHAR_WIDTH = 7.6  # average width of a 14px sans-serif character
ROW_HEIGHT = 34
PADDING = 14

# GitHub's own colors: diff backgrounds and text, light then dark theme
STYLE = """
  text { font: 14px %s; fill: #1f2328; }
  .head { font-weight: 600; }
  .head-row { fill: #f6f8fa; }
  .faster { fill: #dafbe1; }
  .slower { fill: #ffebe9; }
  .line { stroke: #d1d9e0; stroke-width: 1; }
  .frame { fill: none; stroke: #d1d9e0; stroke-width: 1; }
  @media (prefers-color-scheme: dark) {
    text { fill: #f0f6fc; }
    .head-row { fill: #151b23; }
    .faster { fill: #12261e; }
    .slower { fill: #25171c; }
    .line, .frame { stroke: #3d444d; }
  }
""" % FONT


def ms(v):
    return '–' if v is None else f'{v:.1f} ms'


def table(columns, rows, faster, path):
    cells = [columns] + rows
    widths = [max(len(str(r[i])) for r in cells) * CHAR_WIDTH + 2 * PADDING for i in range(len(columns))]
    width = sum(widths)
    height = ROW_HEIGHT * len(cells)

    out = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{width:.0f}" height="{height}" viewBox="0 0 {width:.0f} {height}">',
           f'<style>{STYLE}</style>',
           f'<clipPath id="round"><rect width="{width:.0f}" height="{height}" rx="6"/></clipPath>',
           '<g clip-path="url(#round)">']
    for r, row in enumerate(cells):
        y = r * ROW_HEIGHT
        cls = 'head-row' if r == 0 else 'faster' if faster[r - 1] else 'slower'
        out.append(f'<rect class="{cls}" x="0" y="{y}" width="{width:.0f}" height="{ROW_HEIGHT}"/>')
        if r > 0:
            out.append(f'<line class="line" x1="0" y1="{y}.5" x2="{width:.0f}" y2="{y}.5"/>')
        x = 0
        for i, value in enumerate(row):
            # first two columns are labels (left aligned), the rest numbers (right aligned)
            left = i < 2
            tx = x + PADDING if left else x + widths[i] - PADDING
            anchor = 'start' if left else 'end'
            cls_attr = ' class="head"' if r == 0 else ''
            out.append(f'<text{cls_attr} x="{tx:.0f}" y="{y + ROW_HEIGHT / 2 + 5:.0f}" text-anchor="{anchor}">{escape(str(value))}</text>')
            x += widths[i]
    out.append('</g>')
    out.append(f'<rect class="frame" x="0.5" y="0.5" width="{width - 1:.0f}" height="{height - 1}" rx="6"/>')
    out.append('</svg>')
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(out) + '\n')


enc_rows = [(img, res, size, ms(cpu), ms(gpu)) for img, res, size, cpu, gpu in ENCODE['rows']]
enc_faster = [gpu < cpu for *_, cpu, gpu in ENCODE['rows']]
table(ENCODE['columns'], enc_rows, enc_faster, os.path.join(HERE, 'benchmark-encode.svg'))

# Decode compares the GPU against the parallel CPU decode of the same seekable file
dec_rows = [(img, res, ms(c1), ms(cp), ms(pp), ms(g), ms(gr)) for img, res, c1, cp, pp, g, gr in DECODE['rows']]
dec_faster = [g < cp for _, _, _, cp, _, g, _ in DECODE['rows']]
table(DECODE['columns'], dec_rows, dec_faster, os.path.join(HERE, 'benchmark-decode.svg'))
