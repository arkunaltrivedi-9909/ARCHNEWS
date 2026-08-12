#!/usr/bin/env python3
"""
Tests for the ingestion pipeline.

These are not decorative. The properties asserted here are what allow the app to
claim a page number is real:
  * clause text is byte-identical to the PDF, never rewritten
  * page anchors point at the page the clause actually appeared on
  * contents listings never masquerade as clauses
  * prose mentioning "Schedule A" never becomes a heading

Run:  python3 tools/test_pipeline.py
"""

import json
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)

import ingest  # noqa: E402
from fixtures import make_fixture  # noqa: E402

PASS, FAIL = 0, 0


def check(label, cond, detail=""):
    global PASS, FAIL
    if cond:
        PASS += 1
        print(f"  ok    {label}")
    else:
        FAIL += 1
        print(f"  FAIL  {label}" + (f"\n        {detail}" if detail else ""))


def main():
    tmp = tempfile.mkdtemp(prefix="bycodes-test-")
    pdf = os.path.join(tmp, "fixture.pdf")
    make_fixture.build_pdf(make_fixture.PAGES, pdf)

    pages = ingest.extract_pages(pdf)
    check("extracts every page", len(pages) == len(make_fixture.PAGES),
          f"got {len(pages)} want {len(make_fixture.PAGES)}")

    offset, conf = ingest.detect_printed_offset(pages)
    check("detects printed-page offset", offset == -2 and conf >= 0.5,
          f"offset={offset} confidence={conf}")

    secs = ingest.build_sections(pages, "t", offset)
    nums = [s["number"] for s in secs]

    check("parses top-level and sub clauses",
          {"1", "1.1", "1.2", "2", "2.1", "2.2", "3", "4"}.issubset(set(nums)),
          f"got {nums}")

    check("contents page does not become clauses",
          sum(1 for s in secs if s["pdf_page"] == 2) == 0,
          f"page-2 sections: {[s['number'] for s in secs if s['pdf_page']==2]}")

    check("prose mentioning 'Schedule A' is not a heading",
          not any(n.startswith("SCHEDULE") for n in nums),
          f"got {[n for n in nums if n.startswith('SCHEDULE')]}")

    check("dotted table heading keeps its decimal",
          "TABLE 4.1" in nums, f"got {[n for n in nums if 'TABLE' in n]}")

    by = {s["number"]: s for s in secs}

    check("clause anchored to the correct PDF page", by["2.1"]["pdf_page"] == 4,
          f"got {by['2.1']['pdf_page']}")
    check("printed page computed from offset", by["2.1"]["printed_page"] == 2,
          f"got {by['2.1']['printed_page']}")

    # Verbatim: the clause body must appear character-for-character in the page.
    page4 = pages[3]["text"]
    check("clause text is verbatim from the page",
          by["2.1"]["text"].strip().splitlines()[0].strip() in page4,
          "clause body was altered during parsing")
    check("clause text preserves the exact printed figure",
          "1.8" in by["2.1"]["text"] and "9.0 m" in by["2.1"]["text"],
          f"got: {by['2.1']['text'][:120]!r}")

    check("sub-clause linked to its parent",
          by["2.1"]["parent"] == by["2"]["id"],
          f"parent={by['2.1']['parent']}")

    # Full ingest writes provenance we can trust.
    out = os.path.join(ROOT, "corpus", "docs", "fixture-selftest")
    try:
        man = ingest.ingest("fixture-selftest", pdf, "selftest", "test", "fixture")
        check("records sha256 of the exact file read", len(man["sha256"]) == 64)
        check("flags synthetic documents as fixtures", man["fixture"] is True)
        check("reports full coverage on a text PDF", man["coverage"] == 1.0,
              f"coverage={man['coverage']}")
        written = json.load(open(os.path.join(out, "sections.json"), encoding="utf-8"))
        check("writes sections to disk", len(written["sections"]) == len(secs))
    finally:
        shutil.rmtree(out, ignore_errors=True)
        ingest.rebuild_index()

    # An OCR'd-but-empty page must be reported, never silently skipped.
    blank = os.path.join(tmp, "blank.pdf")
    make_fixture.build_pdf([["This page carries enough text to count as extracted."], [], []], blank)
    bpages = ingest.extract_pages(blank)
    empties = [p["pdf_page"] for p in bpages if len(p["text"].strip()) < 20]
    check("detects pages with no extractable text", empties == [2, 3], f"got {empties}")

    shutil.rmtree(tmp, ignore_errors=True)

    print(f"\n{PASS} passed, {FAIL} failed")
    return 1 if FAIL else 0


if __name__ == "__main__":
    sys.exit(main())
