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


def ocr_missing_pages(pdf_path, pages, lang="eng"):
    """
    Recover text for pages that have no text layer, by running tesseract over
    the page's embedded image.

    OCR is a genuine downgrade in trustworthiness and is recorded as such. It
    misreads exactly the things that matter here: in testing, clause numbers in
    the left margin came back as 'aL0' for 3.10 and 'Sud' for 3.17, and digits
    inside dimensions are equally vulnerable. Pages recovered this way are
    flagged all the way through to the reader, who must check them against the
    source before relying on any figure.

    Returns the number of pages recovered.
    """
    import shutil
    import subprocess
    import tempfile

    if not shutil.which("tesseract"):
        print("  ! tesseract not installed — cannot OCR. "
              "Install tesseract-ocr (and a language pack) and re-run with --ocr.")
        return 0

    try:
        from pypdf import PdfReader
    except ImportError:
        return 0

    reader = PdfReader(pdf_path)
    recovered = 0
    for pg in pages:
        if len(pg["text"].strip()) >= 20:
            continue
        page_obj = reader.pages[pg["pdf_page"] - 1]
        try:
            images = list(page_obj.images)
        except Exception:
            images = []
        if not images:
            continue
        chunks = []
        for img in images:
            with tempfile.NamedTemporaryFile(suffix=os.path.splitext(img.name)[1] or ".png",
                                             delete=False) as fh:
                fh.write(img.data)
                tmp = fh.name
            try:
                out = subprocess.run(["tesseract", tmp, "stdout", "-l", lang],
                                     capture_output=True, text=True, timeout=180)
                if out.returncode == 0 and out.stdout.strip():
                    chunks.append(out.stdout)
            except Exception as exc:
                print(f"  ! OCR failed on page {pg['pdf_page']}: {exc}")
            finally:
                os.unlink(tmp)
        if chunks:
            pg["text"] = "\n".join(chunks)
            pg["ocr"] = True          # provenance: this text was not in the file
            recovered += 1
    return recovered


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
        seen = set()
        for cand in (lines[:2] + lines[-2:]):
            # Two shapes, both common: the number alone on its own line, and the
            # number trailing a running header ("…Govt. of Gujarat      19").
            m = (re.fullmatch(r"[^\d]{0,12}?(\d{1,4})[^\d]{0,12}?", cand)
                 or re.search(r"(?:^|\s)(\d{1,4})\s*$", cand))
            if not m:
                continue
            printed = int(m.group(1))
            if not 0 < printed < 5000:
                continue
            delta = printed - pg["pdf_page"]
            if delta in seen:      # one vote per page, not per matching line
                continue
            seen.add(delta)
            votes[delta] = votes.get(delta, 0) + 1
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
# The identifier must be a number, a roman numeral, or a single letter. Allowing
# arbitrary letters turns ordinary prose — "PART OF any notified water body" —
# into a heading.
STRUCTURAL = re.compile(
    r"^\s{0,8}((?:CHAPTER|PART|SCHEDULE|ANNEXURE|APPENDIX|TABLE)\s+"
    r"(?:\d{1,3}(?:\.\d{1,3}){0,3}|[IVXLC]{1,6}|[A-Z]))\b\.?[\s\-:]*(.{0,150})$",
    re.IGNORECASE,
)
# Table-of-contents lines: "12.4 Parking .......... 87" or trailing bare page no.
TOC_LINE = re.compile(r"(\.\s*){4,}\s*\d{1,4}\s*$|\s{4,}\d{1,4}\s*$")


def looks_like_heading(line, chapters=None):
    """
    Return (number, title) if the line opens a clause, else None.

    `chapters` is the set of top-level chapter numbers actually found in this
    document. When supplied, a dotted number is only accepted if it belongs to a
    real chapter — which is what separates clause 6.3.1 from the FSI value "1.8"
    sitting at the start of a table row.
    """
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
        # "1.5 m" / "2.5 metres" / "9.0 mts" are dimensions inside prose or table
        # cells, not headings.
        if re.match(r"^(m|mm|cm|mt|mts|mtr|mtrs|metre|meter|metres|meters|"
                    r"sq|sqm|sqmt|m2|no|nos|%|to|and|or|of)\b", title, re.I):
            return None
        # Table rows open with a serial number and are mostly figures:
        #   "1 Gamtal GM 2.0 Nil 2.0"   "1. Dwelling-1 0.1 5% 8mts"
        # Two or more standalone numeric tokens means tabular data, not a heading.
        if len(re.findall(r"(?<![\w.])\d+(?:\.\d+)?(?![\w.])", title)) >= 2:
            return None
        # A heading's title is words. Table debris ("*", "& Maximum", "(for RAH1",
        # "3.6**") starts with punctuation or a figure.
        if not title[:1].isalpha():
            return None
        # A dotted number must belong to a chapter this document actually has.
        # Without this, every decimal at the start of a table row ("1.8", "13.0")
        # is indistinguishable from a clause number.
        if "." in num and chapters:
            if num.split(".", 1)[0] not in chapters:
                return None
        # A bare top-level number is only a heading when it introduces a chapter,
        # and chapters shout: "6 GENERAL PLANNING AND DEVELOPMENT REGULATIONS".
        # Numbered list items inside a clause ("1. No development shall be…")
        # are prose and must stay with their parent clause.
        if "." not in num:
            letters = [c for c in title if c.isalpha()]
            if not letters:
                return None
            upper_ratio = sum(c.isupper() for c in letters) / len(letters)
            if upper_ratio < 0.6:
                return None
            # Once the real chapters are known, a capitalised list item that is
            # not one of them stays inside its parent clause.
            if chapters and num not in chapters:
                return None
        return num, title
    return None


def is_contents_page(text):
    """
    Front matter (contents, list of tables/figures) is full of clause numbers
    that are references, not the clauses themselves. Detect those pages and skip
    heading extraction on them; the text still lands in pages.json.
    """
    lines = [ln for ln in text.splitlines() if ln.strip()]
    if len(lines) < 5:
        return False
    hits = sum(1 for ln in lines if TOC_LINE.search(ln))
    return hits >= 5 or hits / len(lines) > 0.5


def build_sections(pages, doc_id, offset):
    """
    Walk pages in order, cut at headings, attach verbatim body text.
    Every section records the page it starts on -- that is the citation.
    """
    # Pass 1: establish which chapters this document actually contains.
    #
    # An ALL-CAPS bare integer is only a candidate — regulations are full of
    # capitalised numbered list items ("4. LANDUSE ZONING IN HAZARD PRONE AREAS")
    # that look identical to a chapter heading. A real chapter is corroborated by
    # having at least one dotted clause beneath it.
    candidates = set()
    dotted_children = {}
    for pg in pages:
        if is_contents_page(pg["text"]):
            continue
        for line in pg["text"].splitlines():
            head = looks_like_heading(line)
            if not head:
                continue
            num = head[0]
            if "." not in num and num.isdigit():
                candidates.add(num)
            elif "." in num:
                prefix = num.split(".", 1)[0]
                if prefix.isdigit():
                    dotted_children.setdefault(prefix, set()).add(num)
    chapters = {c for c in candidates if dotted_children.get(c)}
    if not chapters:
        chapters = candidates   # flat document with no sub-clauses

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
        front_matter = is_contents_page(pg["text"])
        for line in pg["text"].splitlines():
            head = None if front_matter else looks_like_heading(line, chapters)
            if head:
                num, title = head
                # A table or annex belongs to the clause it interrupts. Without
                # this it floats to the root of the tree, detached from the rule
                # it actually carries — and in these regulations the table IS
                # the rule (Table 6.49 holds the common plot percentages).
                under = current["id"] if (current and not num[:1].isdigit()) else None
                close(current, pdf_page)
                current = {
                    "id": f"{doc_id}::{num}",
                    "number": num,
                    "title": title,
                    "pdf_page": pdf_page,
                    "printed_page": pdf_page + offset if offset else None,
                    "depth": num.count(".") if num[0].isdigit() else 0,
                    "text": "",
                    "_under": under,
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
    by_id = {s["id"]: s for s in sections}
    for s in sections:
        num = s["number"]
        parent = None
        if "." in num and num[:1].isdigit():
            head = num.rsplit(".", 1)[0]
            if head in by_number:
                parent = by_number[head]["id"]
        if parent is None:
            parent = s.get("_under")
        s["parent"] = parent
        s.pop("_under", None)
    # Indent tables and annexes one level below the clause they sit under.
    for s in sections:
        if not s["number"][:1].isdigit() and s["parent"] in by_id:
            s["depth"] = by_id[s["parent"]]["depth"] + 1

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


def ingest(doc_id, pdf_path, title=None, authority=None, edition=None,
           ocr=False, ocr_lang="eng"):
    if not os.path.isfile(pdf_path):
        sys.exit(f"not found: {pdf_path}")

    print(f"→ {doc_id}\n  source: {pdf_path}")
    pages = extract_pages(pdf_path)
    if not pages:
        sys.exit("  ! no pages extracted")

    if ocr:
        n = ocr_missing_pages(pdf_path, pages, ocr_lang)
        if n:
            print(f"  OCR recovered {n} page(s) — flagged as lower confidence")

    ocr_pages_list = [p["pdf_page"] for p in pages if p.get("ocr")]
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
        "ocr_recovered_pages": ocr_pages_list,
        "ocr_lang": ocr_lang if ocr_pages_list else None,
        "coverage": round(1 - len(empty) / len(pages), 4),
        "ingested_at": datetime.now(timezone.utc).isoformat(),
        "pipeline": "tools/ingest.py",
        # Fixture documents are invented test data. This flag follows the document
        # everywhere: the browser paints it red, and the Q&A layer refuses to
        # treat it as authority. Never remove it from a synthetic document.
        "fixture": doc_id.startswith("fixture-"),
    }

    ocr_set = set(ocr_pages_list)
    for sec in sections:
        if sec["pdf_page"] in ocr_set:
            sec["ocr"] = True

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
    # Deliberately no generated_at: a timestamp here changes on every reindex and
    # leaves the tracked file permanently dirty, which trains you to ignore its
    # diff — the one file whose diff you actually want to read, since it decides
    # which documents the deployed app will try to fetch. Per-document
    # provenance lives in each manifest's ingested_at.
    with open(os.path.join(CORPUS, "index.json"), "w", encoding="utf-8") as fh:
        json.dump({
            "_note": ("Generated by tools/ingest.py from corpus/docs/. Commit this "
                      "alongside corpus/docs/ — if they disagree, the app lists a "
                      "document it cannot fetch. Documents excluded from version "
                      "control (see corpus/.gitignore) are absent here by design."),
            "documents": docs,
        }, fh, ensure_ascii=False, indent=2)
        fh.write("\n")
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
    ap.add_argument("--ocr", action="store_true",
                    help="OCR pages that have no text layer (requires tesseract). "
                         "Recovered text is flagged as lower confidence throughout.")
    ap.add_argument("--ocr-lang", default="eng",
                    help="tesseract language(s), e.g. eng or eng+guj (default: eng)")
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
                   meta.get("authority"), meta.get("edition"),
                   args.ocr, args.ocr_lang)
        return

    if not args.doc or not args.pdf:
        ap.error("--doc and --pdf are required (or use --all)")
    ingest(args.doc, args.pdf, args.title, args.authority, args.edition,
           args.ocr, args.ocr_lang)


if __name__ == "__main__":
    main()
