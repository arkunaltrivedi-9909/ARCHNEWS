#!/usr/bin/env python3
"""
CODEX — regulation ingestion pipeline.

Converts an official regulation PDF into the corpus format the app serves:

    corpus/docs/<doc_id>/pages.json     verbatim text, one record per PDF page
    corpus/docs/<doc_id>/sections.json  clause tree, every node anchored to a page
    corpus/docs/<doc_id>/manifest.json  provenance: sha256, page count, OCR gaps

Design rule that everything else depends on:
    NOTHING in the output is authored here. Every character of regulation text
    is lifted verbatim from the PDF, and every section carries the physical PDF
    page it was found on. If a page yields no text (scanned image), it is
    recorded as an OCR gap rather than silently dropped -- a missing page must
    never look like an absent rule.

Usage:
    python3 tools/ingest.py --doc cgdcr-2017-part2 --pdf /path/to/file.pdf
    python3 tools/ingest.py --all            # ingest everything in corpus/inbox/
"""

import argparse
import hashlib
import json
import os
import re
import sys
from datetime import datetime, timezone

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CORPUS = os.path.join(ROOT, "corpus")
DOCS = os.path.join(CORPUS, "docs")

# ----------------------------------------------------------------------------
# Page-number offset
#
# PDF page 1 is rarely printed page 1 (covers, notification letters, blank
# leaves). A citation is only useful if it names the number printed on the
# page the user is looking at, so we track both and always report both.
# ----------------------------------------------------------------------------


def sha256_of(path):
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def extract_pages(pdf_path):
    """Return [{'pdf_page': int, 'text': str}], text verbatim, never edited."""
    try:
        from pypdf import PdfReader
    except ImportError:
        sys.exit("pypdf is required:  pip install pypdf")

    reader = PdfReader(pdf_path)
    pages = []
    for i, page in enumerate(reader.pages, start=1):
        try:
            text = page.extract_text() or ""
        except Exception as exc:  # a damaged page must not abort the document
            text = ""
            print(f"  ! page {i}: extraction failed ({exc})", file=sys.stderr)
        pages.append({"pdf_page": i, "text": text})
    return pages


def detect_printed_offset(pages, probe=40):
    """
    Infer printed_number - pdf_page by looking for a stable integer in the
    header/footer band of each page. Returns (offset, confidence 0..1).
    Offset 0 with low confidence means 'could not tell' -- the UI then shows
    PDF page only rather than inventing a printed number.
    """
    votes = {}
    checked = 0
    for pg in pages[:probe]:
        lines = [ln.strip() for ln in pg["text"].splitlines() if ln.strip()]
        if not lines:
            continue
        checked += 1
        for cand in (lines[:2] + lines[-2:]):
            m = re.fullmatch(r"[^\d]{0,12}?(\d{1,4})[^\d]{0,12}?", cand)
            if not m:
                continue
            printed = int(m.group(1))
            if 0 < printed < 5000:
                votes[printed - pg["pdf_page"]] = votes.get(printed - pg["pdf_page"], 0) + 1
    if not votes or not checked:
        return 0, 0.0
    offset, count = max(votes.items(), key=lambda kv: kv[1])
    return offset, round(count / checked, 3)


# ----------------------------------------------------------------------------
# Clause detection
# ----------------------------------------------------------------------------

# "12.4.2  Parking Requirements"  /  "5. GENERAL"
NUMBERED = re.compile(r"^\s{0,8}(\d{1,3}(?:\.\d{1,3}){0,4})\.?\s+(\S.{0,150})$")
# "CHAPTER 5 - ..." / "PART II ..." / "TABLE 4.1 ..." / "ANNEXURE A"
STRUCTURAL = re.compile(
    r"^\s{0,8}((?:CHAPTER|PART|SCHEDULE|ANNEXURE|APPENDIX|TABLE)\s+"
    r"[IVXLC0-9A-Z]{1,6}(?:\.\d{1,3}){0,3})\b\.?[\s\-:]*(.{0,150})$",
    re.IGNORECASE,
)
# Table-of-contents lines: "12.4 Parking .......... 87" or trailing bare page no.
TOC_LINE = re.compile(r"(\.\s*){4,}\s*\d{1,4}\s*$|\s{4,}\d{1,4}\s*$")


def looks_like_heading(line):
    """Return (number, title) if the line opens a clause, else None."""
    raw = line.rstrip()
    if not raw.strip() or len(raw) > 200:
        return None
    if TOC_LINE.search(raw):  # contents page, not the clause itself
        return None

    m = STRUCTURAL.match(raw)
    if m:
        keyword = raw.strip().split()[0]
        rest = m.group(2).strip()
        # "…as described in Schedule A and shall not apply." is prose, not a
        # heading. Real headings shout (uppercase) or stay short and label-like.
        is_heading = keyword.isupper() or (len(raw.strip()) <= 80 and not rest.endswith("."))
        if is_heading:
            return m.group(1).upper().strip(), rest

    m = NUMBERED.match(raw)
    if m:
        num, title = m.group(1), m.group(2).strip()
        # A clause heading is a label, not prose. Reject sentence-shaped tails.
        if title.endswith((".", ";", ",")) and len(title.split()) > 12:
            return None
        # "1.5 m" / "2.5 metres" are dimensions inside prose, not headings.
        if re.match(r"^(m|mm|cm|metre|meter|sq|m2|%|to|and|or|of)\b", title, re.I):
            return None
        return num, title
    return None


def build_sections(pages, doc_id, offset):
    """
    Walk pages in order, cut at headings, attach verbatim body text.
    Every section records the page it starts on -- that is the citation.
    """
    sections = []
    current = None

    def close(sec, end_pdf_page):
        if sec is None:
            return
        sec["text"] = sec["text"].strip("\n")
        sec["pdf_page_end"] = end_pdf_page
        sections.append(sec)

    for pg in pages:
        pdf_page = pg["pdf_page"]
        for line in pg["text"].splitlines():
            head = looks_like_heading(line)
            if head:
                close(current, pdf_page)
                num, title = head
                current = {
                    "id": f"{doc_id}::{num}",
                    "number": num,
                    "title": title,
                    "pdf_page": pdf_page,
                    "printed_page": pdf_page + offset if offset else None,
                    "depth": num.count(".") if num[0].isdigit() else 0,
                    "text": "",
                }
            elif current is not None:
                current["text"] += line + "\n"
            # Text before the first heading (cover, gazette notification) is
            # deliberately dropped from the tree; it is still in pages.json.
    close(current, pages[-1]["pdf_page"] if pages else 0)

    # Parent linkage by dotted prefix, so the UI can render a real tree.
    by_number = {}
    for s in sections:
        by_number.setdefault(s["number"], s)
    for s in sections:
        num = s["number"]
        parent = None
        if "." in num:
            head = num.rsplit(".", 1)[0]
            if head in by_number:
                parent = by_number[head]["id"]
        s["parent"] = parent

    # Duplicate clause numbers (reprinted headers, amendment reprints) get
    # suffixed so permalinks stay unique and resolvable.
    seen = {}
    for s in sections:
        base = s["id"]
        if base in seen:
            seen[base] += 1
            s["id"] = f"{base}#{seen[base]}"
            s["duplicate_of"] = base
        else:
            seen[base] = 0
    return sections


def ingest(doc_id, pdf_path, title=None, authority=None, edition=None):
    if not os.path.isfile(pdf_path):
        sys.exit(f"not found: {pdf_path}")

    print(f"→ {doc_id}\n  source: {pdf_path}")
    pages = extract_pages(pdf_path)
    if not pages:
        sys.exit("  ! no pages extracted")

    empty = [p["pdf_page"] for p in pages if len(p["text"].strip()) < 20]
    offset, confidence = detect_printed_offset(pages)
    sections = build_sections(pages, doc_id, offset if confidence >= 0.5 else 0)

    out_dir = os.path.join(DOCS, doc_id)
    os.makedirs(out_dir, exist_ok=True)

    manifest = {
        "doc_id": doc_id,
        "title": title or doc_id,
        "authority": authority,
        "edition": edition,
        "source_file": os.path.basename(pdf_path),
        "sha256": sha256_of(pdf_path),
        "pdf_pages": len(pages),
        "sections": len(sections),
        "printed_page_offset": offset if confidence >= 0.5 else None,
        "printed_page_offset_confidence": confidence,
        "ocr_gap_pages": empty,
        "ocr_gap_count": len(empty),
        "coverage": round(1 - len(empty) / len(pages), 4),
        "ingested_at": datetime.now(timezone.utc).isoformat(),
        "pipeline": "tools/ingest.py",
        # Fixture documents are invented test data. This flag follows the document
        # everywhere: the browser paints it red, and the Q&A layer refuses to
        # treat it as authority. Never remove it from a synthetic document.
        "fixture": doc_id.startswith("fixture-"),
    }

    for name, payload in (
        ("pages.json", {"doc_id": doc_id, "pages": pages}),
        ("sections.json", {"doc_id": doc_id, "sections": sections}),
        ("manifest.json", manifest),
    ):
        with open(os.path.join(out_dir, name), "w", encoding="utf-8") as fh:
            json.dump(payload, fh, ensure_ascii=False, separators=(",", ":"))

    print(f"  pages     {len(pages)}")
    print(f"  sections  {len(sections)}")
    if offset and confidence >= 0.5:
        print(f"  printed page = pdf page {offset:+d}  (confidence {confidence})")
    else:
        print("  printed page offset: undetermined — citations will show PDF page only")
    if empty:
        pct = 100 * len(empty) / len(pages)
        print(f"  ! {len(empty)} page(s) have no extractable text ({pct:.1f}%) — scanned?")
        print(f"    {empty[:20]}{' …' if len(empty) > 20 else ''}")
        print("    Run OCR before trusting this document:")
        print(f"    ocrmypdf --force-ocr '{pdf_path}' ocr.pdf && python3 tools/ingest.py "
              f"--doc {doc_id} --pdf ocr.pdf")
    rebuild_index()
    return manifest


def rebuild_index():
    """Regenerate corpus/index.json — the list of loaded docs the app reads."""
    docs = []
    if os.path.isdir(DOCS):
        for doc_id in sorted(os.listdir(DOCS)):
            mpath = os.path.join(DOCS, doc_id, "manifest.json")
            if os.path.isfile(mpath):
                with open(mpath, encoding="utf-8") as fh:
                    man = json.load(fh)
                # The app fetches corpus/docs/<doc_id>/…, so a manifest whose
                # doc_id does not match its directory produces an index entry
                # that 404s. Report it rather than shipping a broken link.
                if man.get("doc_id") != doc_id:
                    print(f"  ! {doc_id}/manifest.json declares doc_id "
                          f"'{man.get('doc_id')}' — directory and id must match; skipping")
                    continue
                docs.append(man)
    with open(os.path.join(CORPUS, "index.json"), "w", encoding="utf-8") as fh:
        json.dump({
            "generated_at": datetime.now(timezone.utc).isoformat(),
            "documents": docs,
        }, fh, ensure_ascii=False, indent=2)
    print(f"  index: {len(docs)} document(s) loaded")


def main():
    ap = argparse.ArgumentParser(description="Ingest a regulation PDF into the corpus.")
    ap.add_argument("--doc", help="document id, e.g. cgdcr-2017-part2")
    ap.add_argument("--pdf", help="path to the source PDF")
    ap.add_argument("--title")
    ap.add_argument("--authority")
    ap.add_argument("--edition")
    ap.add_argument("--all", action="store_true",
                    help="ingest every PDF in corpus/inbox/ using registry.json")
    ap.add_argument("--reindex", action="store_true", help="rebuild corpus/index.json only")
    args = ap.parse_args()

    if args.reindex:
        rebuild_index()
        return

    if args.all:
        inbox = os.path.join(CORPUS, "inbox")
        reg_path = os.path.join(CORPUS, "registry.json")
        registry = {}
        if os.path.isfile(reg_path):
            with open(reg_path, encoding="utf-8") as fh:
                registry = {d["doc_id"]: d for d in json.load(fh)["documents"]}
        if not os.path.isdir(inbox):
            sys.exit(f"no inbox directory: {inbox}")
        pdfs = sorted(f for f in os.listdir(inbox) if f.lower().endswith(".pdf"))
        if not pdfs:
            sys.exit(f"no PDFs in {inbox} — drop the official files there first")
        for fn in pdfs:
            doc_id = os.path.splitext(fn)[0]
            meta = registry.get(doc_id, {})
            ingest(doc_id, os.path.join(inbox, fn), meta.get("title"),
                   meta.get("authority"), meta.get("edition"))
        return

    if not args.doc or not args.pdf:
        ap.error("--doc and --pdf are required (or use --all)")
    ingest(args.doc, args.pdf, args.title, args.authority, args.edition)


if __name__ == "__main__":
    main()
