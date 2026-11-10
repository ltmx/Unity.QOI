# Generates the README benchmark tables as SVG (GitHub strips colors from Markdown tables).
# Rows where the GPU is faster than the CPU get a green background, the others a red one.
# Edit the numbers below and run: python .documentation/benchmarks.py
import os
from html import escape

HERE = os.path.dirname(os.path.abspath(__file__))

ENCODE = {
    'columns': ['Image', 'Resolution', 'QOI size', 'CPU', 'GPU'],
    'rows': [
        # image, resolution, size, cpu ms, gpu ms
        ('Albedo', '512²', '1.2 MB', 3.8, 23.4),
        ('Albedo', '1024²', '4.8 MB', 14.1, 6.3),
        ('Albedo', '2048²', '19.5 MB', 50.7, 11.4),
        ('Albedo', '4096²', '72.9 MB', 208.1, 34.9),
        ('Normal map', '2048²', '15.7 MB', 80.1, 28.9),
        ('Mask', '2048²', '2.8 MB', 48.3, 9.4),
        ('Smooth gradient', '4096²', '29.7 MB', 238.7, 17.4),
        ('HDR albedo (half)', '2048²', '19.2 MB', 213.6, 13.6),
    ],
}

DECODE = {
    'columns': ['Image', 'Resolution', 'CPU 1 thread', 'CPU parallel', 'GPU', 'GPU, reused RT'],
    'rows': [
        # image, resolution, cpu ms, cpu parallel ms, gpu ms, gpu reused ms (None: not measured)
        ('Albedo', '512²', 5.4, 3.2, 5.4, 4.4),
        ('Albedo', '1024²', 15.4, 7.8, 5.1, 4.9),
        ('Albedo', '2048²', 48.8, 23.8, 8.8, 8.4),
        ('Albedo', '4096²', 206.7, 82.1, 20.8, 19.4),
        ('Normal map', '2048²', 56.2, 28.7, 13.7, 7.3),
        ('Mask', '2048²', 50.0, 29.0, 7.3, 5.5),
        ('Smooth gradient', '4096²', 214.5, 89.6, 14.4, 12.4),
        ('HDR albedo (half)', '2048²', 109.5, 55.3, 20.2, None),
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

# Decode compares the GPU against the parallel CPU decode
dec_rows = [(img, res, ms(c1), ms(cp), ms(g), ms(gr)) for img, res, c1, cp, g, gr in DECODE['rows']]
dec_faster = [g < cp for _, _, _, cp, g, _ in DECODE['rows']]
table(DECODE['columns'], dec_rows, dec_faster, os.path.join(HERE, 'benchmark-decode.svg'))
