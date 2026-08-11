#!/usr/bin/env python3
"""
Generates a synthetic, regulation-shaped PDF for testing the ingest pipeline.

THE CONTENT IS INVENTED AND IS NOT LAW. Every page says so. It exists only so
the parser, the page-anchoring and the refusal path can be tested without the
real documents present. It is written to tools/fixtures/ and is never served
by the app as regulation.

Pure stdlib -- writes raw PDF syntax, no reportlab needed.
"""

import os

WIDTH, HEIGHT = 595, 842  # A4 points


def esc(s):
    return s.replace("\\", r"\\").replace("(", r"\(").replace(")", r"\)")


def content_stream(lines, size=11, leading=15, x=56, y=790):
    out = ["BT", f"/F1 {size} Tf", f"1 0 0 1 {x} {y} Tm", f"{leading} TL"]
    for ln in lines:
        out.append(f"({esc(ln)}) Tj")
        out.append("T*")
    out.append("ET")
    return "\n".join(out)


def build_pdf(pages, path):
    """pages: list[list[str]] -- one list of text lines per page."""
    objects = {}          # number -> bytes body
    font_obj = 3 + 2 * len(pages)

    kids = []
    for i, lines in enumerate(pages):
        page_obj = 3 + 2 * i
        content_obj = page_obj + 1
        kids.append(f"{page_obj} 0 R")
        objects[page_obj] = (
            f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {WIDTH} {HEIGHT}] "
            f"/Resources << /Font << /F1 {font_obj} 0 R >> >> "
            f"/Contents {content_obj} 0 R >>"
        ).encode()
        stream = content_stream(lines).encode()
        objects[content_obj] = (
            f"<< /Length {len(stream)} >>\nstream\n".encode() + stream + b"\nendstream"
        )

    objects[1] = b"<< /Type /Catalog /Pages 2 0 R >>"
    objects[2] = (
        f"<< /Type /Pages /Kids [{' '.join(kids)}] /Count {len(pages)} >>"
    ).encode()
    objects[font_obj] = (
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"
    )

    buf = bytearray(b"%PDF-1.4\n")
    offsets = {}
    for num in sorted(objects):
        offsets[num] = len(buf)
        buf += f"{num} 0 obj\n".encode() + objects[num] + b"\nendobj\n"

    xref_at = len(buf)
    top = max(objects) + 1
    buf += f"xref\n0 {top}\n".encode()
    buf += b"0000000000 65535 f \n"
    for num in range(1, top):
        if num in offsets:
            buf += f"{offsets[num]:010d} 00000 n \n".encode()
        else:
            buf += b"0000000000 65535 f \n"
    buf += (
        f"trailer\n<< /Size {top} /Root 1 0 R >>\nstartxref\n{xref_at}\n%%EOF\n"
    ).encode()

    with open(path, "wb") as fh:
        fh.write(buf)
    return path


# --- Synthetic document -----------------------------------------------------
# Shaped like a real GDCR (dotted clause numbers, tables, a contents page,
# printed page numbers offset from PDF pages by 2) but entirely fictional.

BANNER = "FIXTURE DOCUMENT - INVENTED CONTENT - NOT LAW - FOR PIPELINE TESTING ONLY"

PAGES = [
    # PDF page 1 -- cover, no printed number
    [BANNER, "", "", "SAMPLE DEVELOPMENT CONTROL REGULATIONS", "TEST EDITION", "",
     "Issued for software testing purposes only."],
    # PDF page 2 -- contents (must NOT become sections)
    [BANNER, "", "CONTENTS", "",
     "1. Preliminary .......................................... 1",
     "2. Floor Space Index ................................... 2",
     "3. Margins ............................................. 3",
     "4. Parking ............................................. 4"],
    # PDF page 3 == printed 1
    [BANNER, "", "1. PRELIMINARY", "",
     "1.1 Short Title",
     "These fictional regulations may be called the Sample Development",
     "Control Regulations, Test Edition.",
     "",
     "1.2 Application",
     "These regulations apply only to the imaginary area described in",
     "Schedule A and have no legal effect anywhere.",
     "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "1"],
    # PDF page 4 == printed 2
    [BANNER, "", "2. FLOOR SPACE INDEX", "",
     "2.1 Base Floor Space Index",
     "The base FSI for a plot in Zone T1 shall be 1.8 where the abutting",
     "road width is not less than 9.0 m.",
     "",
     "2.2 Chargeable Floor Space Index",
     "Chargeable FSI over and above the base FSI may be permitted up to",
     "a maximum of 0.9 on payment of the prescribed charge.",
     "", "", "", "", "", "", "", "", "", "", "", "", "", "2"],
    # PDF page 5 == printed 3
    [BANNER, "", "3. MARGINS", "",
     "3.1 Front Margin",
     "The minimum front margin for a plot up to 500 sq m shall be 3.0 m.",
     "",
     "3.2 Side and Rear Margins",
     "The minimum side margin shall be 1.5 m for buildings not exceeding",
     "10.0 m in height.",
     "", "", "", "", "", "", "", "", "", "", "", "", "", "", "3"],
    # PDF page 6 == printed 4
    [BANNER, "", "4. PARKING", "",
     "4.1 Parking Requirement",
     "Parking shall be provided at the rate of one equivalent car space",
     "for every 80 sq m of built up area for residential use.",
     "",
     "TABLE 4.1 PARKING STANDARDS",
     "Use                     ECS per 100 sq m",
     "Residential             1.25",
     "Commercial              2.00",
     "", "", "", "", "", "", "", "", "", "", "", "", "4"],
]


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    out = os.path.join(here, "sample-regulations-FIXTURE.pdf")
    build_pdf(PAGES, out)
    print(f"wrote {out} ({len(PAGES)} pages)")
    print("CONTENT IS INVENTED - NOT LAW")


if __name__ == "__main__":
    main()
