#!/usr/bin/env python3
"""
BYCODES — amendment ingestion.

Amendment notifications are a different animal from regulation documents. They
contain almost no regulation text; they contain *instructions to change* text
that lives in another document:

    [Page 34, clause 4.4.2.4.3.2 (f), line 6] — Substitute '90 mm' for '115 mm'.

That single line is the reason this whole tool exists. Anyone answering from
memory of the base code says 115 mm, and has been wrong since March 2021. So
amendments are parsed into structured, citable edits and overlaid on the base
document, rather than being dumped into the clause tree as prose.

Output:
    corpus/docs/<doc_id>/amendments.json   structured edits with target clauses
    corpus/docs/<doc_id>/pages.json        verbatim source text
    corpus/docs/<doc_id>/manifest.json     provenance, type=amendment

Usage:
    python3 tools/ingest_amendment.py --doc nbc-2016-amd1-2021 \
        --pdf corpus/inbox/nbc-2016-amd1-2021.pdf --amends nbc-2016-part4
"""

import argparse
import json
import os
import re
import sys
from datetime import datetime, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import ingest  # reuse extract_pages / sha256_of / rebuild_index

ROOT = os.path.dirname(HERE)
DOCS = os.path.join(ROOT, "corpus", "docs")

# Quote characters used interchangeably in BIS/Gujarat notifications.
Q = "['‘’“”\"]"

# "[Page 34, clause 4.4.2.4.3.2 (f), line 6] — Substitute ..."
# "(Pages 114 and 115, List of standards under Sl. No. (17)) — Delete ..."
LOCATOR = re.compile(
    r"^\s*[\(\[]\s*Pages?\s+(.+?)\s*[\)\]]\s*[—–-]\s*(.*)$",
    re.IGNORECASE,
)

ACTIONS = ("substitute", "insert", "delete", "add", "renumber", "omit")


def parse_locator(inside):
    """
    Split "34, clause 4.4.2.4.3.2 (f), line 6" into pages and a target
    description. Pages come first, everything after the first comma is target.
    """
    parts = inside.split(",", 1)
    pages = [int(n) for n in re.findall(r"\d+", parts[0])]
    target = parts[1].strip() if len(parts) > 1 else ""
    return pages, target


def parse_target(target):
    """Pull a clause number and/or table number out of the target description."""
    clause = None
    m = re.search(r"clause\s+([A-Za-z]?-?\d+(?:\.\d+)*(?:\s*\([^)]{1,6}\))*)", target, re.I)
    if m:
        clause = re.sub(r"\s+", " ", m.group(1)).strip()
    table = None
    m = re.search(r"\bTable\s+(\d+)", target, re.I)
    if m:
        table = m.group(1)
    annex = None
    m = re.search(r"\bAnnex\s+([A-Z])\b", target, re.I)
    if m:
        annex = m.group(1).upper()
    return clause, table, annex


def parse_action(instruction, body):
    """
    Classify the edit and, where the notification states them inline, capture
    the exact old and new strings. Never infer them — if the amendment says
    "Substitute the following for the existing:" the new text is the quoted
    block that follows, and the old text is simply not stated here.
    """
    text = (instruction + " " + body).strip()
    first = instruction.strip().split()[0].lower().rstrip(":,.") if instruction.strip() else ""
    action = first if first in ACTIONS else None

    old = new = None

    # "Substitute '90 mm' for '115 mm'."  -> new='90 mm', old='115 mm'
    m = re.search(rf"substitute\s+{Q}(.+?){Q}\s+for\s+{Q}(.+?){Q}", text, re.I | re.S)
    if m:
        new = re.sub(r"\s+", " ", m.group(1)).strip()
        old = re.sub(r"\s+", " ", m.group(2)).strip()
        return "substitute", old, new

    # "Substitute the following for the existing:" -> new text is the quoted block
    if re.search(r"substitute\s+the\s+following", text, re.I):
        m = re.search(rf"{Q}(.+){Q}", body, re.S)
        if m:
            new = re.sub(r"\s+", " ", m.group(1)).strip()
        return "substitute", None, new

    # "Insert the following ... :" -> new text is the quoted block
    if action == "insert" or re.search(r"\binsert\b", text, re.I):
        m = re.search(rf"{Q}(.+){Q}", body, re.S)
        if m:
            new = re.sub(r"\s+", " ", m.group(1)).strip()
        return "insert", None, new

    # "Delete the words 'and fire load density'." -> old is what goes away
    if action == "delete" or re.search(r"\b(delete|omit)\b", text, re.I):
        quoted = re.findall(rf"{Q}(.+?){Q}", text, re.S)
        if quoted:
            old = "; ".join(re.sub(r"\s+", " ", q).strip() for q in quoted)
        return "delete", old, None

    return action, old, new


def parse_amendment(pages):
    """Walk the document line by line, cutting a new item at each locator."""
    items = []
    current = None

    def close(cur):
        if cur is None:
            return
        body = "\n".join(cur.pop("_body")).strip()
        action, old, new = parse_action(cur["instruction"], body)
        clause, table, annex = parse_target(cur["target"])
        cur.update({
            "action": action, "old_text": old, "new_text": new,
            "target_clause": clause, "target_table": table, "target_annex": annex,
            "body": body,
        })
        items.append(cur)

    for pg in pages:
        for line in pg["text"].splitlines():
            m = LOCATOR.match(line)
            if m:
                close(current)
                inside, instruction = m.group(1), m.group(2).strip()
                tpages, target = parse_locator(inside)
                current = {
                    "seq": len(items) + 1,
                    "pdf_page": pg["pdf_page"],
                    "target_pages": tpages,
                    "target": target,
                    "instruction": instruction,
                    "raw": line.strip(),
                    "_body": [],
                }
            elif current is not None:
                current["_body"].append(line)
    close(current)

    for i, it in enumerate(items, start=1):
        it["seq"] = i
        it["id"] = f"amd::{i}"
    return items


def detect_header(pages):
    """Pull the amendment's own identity out of its first page."""
    head = pages[0]["text"] if pages else ""
    info = {}
    m = re.search(r"AMENDMENT\s+NO\.?\s*(\d+)", head, re.I)
    if m:
        info["amendment_number"] = m.group(1)
    m = re.search(r"\b(January|February|March|April|May|June|July|August|September|"
                  r"October|November|December)\s+(\d{4})", head, re.I)
    if m:
        info["issued"] = f"{m.group(1).title()} {m.group(2)}"
    m = re.search(r"\bTO\s*\n?\s*(SP\s*\d+\s*:?\s*\d{4}[^\n]*)", head, re.I)
    if m:
        info["amends_title"] = re.sub(r"\s+", " ", m.group(1)).strip()
    m = re.search(r"amends the provisions of\s+(.+?)\s+of the Code", head, re.I | re.S)
    if m:
        info["amends_part"] = re.sub(r"\s+", " ", m.group(1)).strip()
    return info


def ingest_amendment(doc_id, pdf_path, amends=None, title=None, authority=None,
                     state=None, scope=None):
    if not os.path.isfile(pdf_path):
        sys.exit(f"not found: {pdf_path}")

    print(f"→ {doc_id}  (amendment)\n  source: {pdf_path}")
    pages = ingest.extract_pages(pdf_path)
    if not pages:
        sys.exit("  ! no pages extracted")

    header = detect_header(pages)
    items = parse_amendment(pages)
    empty = [p["pdf_page"] for p in pages if len(p["text"].strip()) < 20]

    out_dir = os.path.join(DOCS, doc_id)
    os.makedirs(out_dir, exist_ok=True)

    manifest = {
        "doc_id": doc_id,
        "type": "amendment",
        "title": title or (
            f"Amendment No. {header.get('amendment_number','?')} "
            f"({header.get('issued','date unknown')}) to {header.get('amends_title', 'base document')}"
        ),
        "short": f"Amd {header.get('amendment_number','?')} ({header.get('issued','')})".strip(),
        "authority": authority or "Bureau of Indian Standards",
        "state": state,
        "scope": scope,
        "edition": header.get("issued"),
        "amends_doc": amends,
        "amends_title": header.get("amends_title"),
        "amends_part": header.get("amends_part"),
        "amendment_number": header.get("amendment_number"),
        "source_file": os.path.basename(pdf_path),
        "sha256": ingest.sha256_of(pdf_path),
        "pdf_pages": len(pages),
        "amendment_items": len(items),
        "sections": 0,
        "ocr_gap_pages": empty,
        "ocr_gap_count": len(empty),
        "coverage": round(1 - len(empty) / len(pages), 4),
        "ingested_at": datetime.now(timezone.utc).isoformat(),
        "pipeline": "tools/ingest_amendment.py",
        "fixture": doc_id.startswith("fixture-"),
    }

    for name, payload in (
        ("pages.json", {"doc_id": doc_id, "pages": pages}),
        ("amendments.json", {"doc_id": doc_id, "amends_doc": amends, "items": items}),
        ("sections.json", {"doc_id": doc_id, "sections": []}),
        ("manifest.json", manifest),
    ):
        with open(os.path.join(out_dir, name), "w", encoding="utf-8") as fh:
            json.dump(payload, fh, ensure_ascii=False, separators=(",", ":"))

    print(f"  pages       {len(pages)}")
    print(f"  edits       {len(items)}")
    if header.get("amends_title"):
        print(f"  amends      {header['amends_title']}")
    if header.get("amends_part"):
        print(f"  part        {header['amends_part']}")
    unresolved = [i for i in items if not i["action"]]
    if unresolved:
        print(f"  ! {len(unresolved)} edit(s) had no recognised action verb — review them:")
        for i in unresolved[:5]:
            print(f"    seq {i['seq']}: {i['raw'][:80]}")
    if not amends:
        print("  ! --amends not set: these edits are not linked to a base document, so "
              "they will show in the Amendments view but cannot overlay clause pages.")
    ingest.rebuild_index()
    return manifest


def main():
    ap = argparse.ArgumentParser(description="Ingest an amendment notification.")
    ap.add_argument("--doc", required=True)
    ap.add_argument("--pdf", required=True)
    ap.add_argument("--amends", help="doc_id of the base document these edits apply to")
    ap.add_argument("--title")
    ap.add_argument("--authority")
    ap.add_argument("--state", help='jurisdiction, e.g. "Gujarat" or "India"')
    ap.add_argument("--scope", choices=["national", "state", "authority", "city"])
    args = ap.parse_args()
    ingest_amendment(args.doc, args.pdf, args.amends, args.title, args.authority,
                     args.state, args.scope)


if __name__ == "__main__":
    main()
