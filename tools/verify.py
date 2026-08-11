#!/usr/bin/env python3
"""
CODEX — corpus integrity check.

Answers the question "what does this tool actually know, and where is it lying
by omission?" Run it after every ingest and before every deploy.

Checks:
  * every ingested document is complete and readable
  * OCR gaps are reported loudly (a scanned page looks like an absent rule)
  * printed-page offsets were established (else citations show PDF page only)
  * every feasibility rule carries a citation that resolves to a real clause

Exit code is non-zero if anything would cause the app to present unsupported
information. Wire it into CI if you like.
"""

import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CORPUS = os.path.join(ROOT, "corpus")
DOCS = os.path.join(CORPUS, "docs")

RED = "\033[31m"; YEL = "\033[33m"; GRN = "\033[32m"; DIM = "\033[2m"; OFF = "\033[0m"


def load(path):
    with open(path, encoding="utf-8") as fh:
        return json.load(fh)


def main():
    errors, warnings = [], []

    if not os.path.isdir(DOCS) or not os.listdir(DOCS):
        print(f"{YEL}corpus is empty — no documents ingested.{OFF}")
        print("The app will show its 'not loaded' state and refuse every question.")
        print("That is correct behaviour, not a failure. Ingest with:")
        print("  python3 tools/ingest.py --all")
        return 0

    print(f"{DIM}{'DOCUMENT':<28}{'PAGES':>7}{'CLAUSES':>9}{'COVERAGE':>10}{'PRINTED PG':>12}{OFF}")
    print("-" * 66)

    doc_sections = {}
    total_gap = 0

    for doc_id in sorted(os.listdir(DOCS)):
        d = os.path.join(DOCS, doc_id)
        if not os.path.isdir(d):
            continue
        try:
            man = load(os.path.join(d, "manifest.json"))
            secs = load(os.path.join(d, "sections.json"))["sections"]
            pages = load(os.path.join(d, "pages.json"))["pages"]
        except Exception as exc:
            errors.append(f"{doc_id}: unreadable corpus files ({exc})")
            continue

        doc_sections[doc_id] = {s["number"] for s in secs}
        cov = man.get("coverage", 0)
        gaps = man.get("ocr_gap_count", 0)
        total_gap += gaps
        offset = man.get("printed_page_offset")

        flag = ""
        if man.get("fixture"):
            flag = f"  {RED}FIXTURE — NOT LAW{OFF}"
        colour = GRN if cov >= 0.98 else (YEL if cov >= 0.8 else RED)
        print(f"{doc_id[:27]:<28}{len(pages):>7}{len(secs):>9}"
              f"{colour}{cov*100:>9.1f}%{OFF}"
              f"{(f'{offset:+d}' if offset is not None else 'unknown'):>12}{flag}")

        if gaps:
            warnings.append(
                f"{doc_id}: {gaps} page(s) yielded no text — likely scanned. "
                f"Those pages are INVISIBLE to search and Q&A. Run "
                f"`ocrmypdf --force-ocr` and re-ingest.")
        if offset is None:
            warnings.append(
                f"{doc_id}: printed page number could not be established; citations "
                f"will show PDF page only. Check the document has page numbers in a "
                f"consistent position.")
        if not secs:
            errors.append(f"{doc_id}: no clauses parsed — the heading pattern did not "
                          f"match this document's numbering. Nothing is citable.")
        if man.get("fixture"):
            warnings.append(f"{doc_id}: fixture data is loaded and will appear in the UI "
                            f"(marked in red). Remove corpus/docs/{doc_id}/ before real use.")

    # ---- rules ----
    print()
    rules_path = os.path.join(CORPUS, "rules.json")
    rules = []
    if os.path.isfile(rules_path):
        try:
            rules = load(rules_path).get("rules", [])
        except Exception as exc:
            errors.append(f"rules.json is not valid JSON ({exc})")

    if not rules:
        print(f"{DIM}rules.json: empty — feasibility calculator reports NOT DERIVABLE "
              f"for every field.{OFF}")
    else:
        ok = 0
        for i, r in enumerate(rules):
            tag = f"rule[{i}] {r.get('output','?')}"
            c = r.get("citation") or {}
            if not c.get("doc_id") or not c.get("number"):
                errors.append(f"{tag}: missing citation — will be discarded by the calculator.")
                continue
            if c["doc_id"] not in doc_sections:
                errors.append(f"{tag}: cites document '{c['doc_id']}' which is not ingested.")
                continue
            if c["number"] not in doc_sections[c["doc_id"]]:
                errors.append(f"{tag}: cites clause {c['number']} which does not exist in "
                              f"{c['doc_id']}. Citation would not resolve.")
                continue
            if r.get("verified") is False:
                warnings.append(f"{tag}: marked unverified — shown with a warning badge.")
            ok += 1
        print(f"rules.json: {ok}/{len(rules)} rules have resolvable citations")

    # ---- report ----
    print()
    for w in warnings:
        print(f"{YEL}WARN{OFF}  {w}")
    for e in errors:
        print(f"{RED}FAIL{OFF}  {e}")

    if not errors and not warnings:
        print(f"{GRN}All checks passed.{OFF}")
    print()
    if total_gap:
        print(f"{YEL}{total_gap} page(s) across the corpus have no extractable text. "
              f"Until those are OCR'd, absence of a rule in this tool does not mean "
              f"absence of the rule in law.{OFF}")

    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
