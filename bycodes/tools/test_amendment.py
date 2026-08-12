#!/usr/bin/env python3
"""
Tests for the amendment parser.

Amendments are where a regulation reference most easily becomes silently wrong:
the base text still reads plausibly after it has been superseded. These tests
pin the parse of the real BIS notification held in this repo, plus a synthetic
one covering the instruction forms the real document does not exercise.

Run:  python3 tools/test_amendment.py
"""

import json
import os
import shutil
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)

import ingest                    # noqa: E402
import ingest_amendment as amd   # noqa: E402
from fixtures import make_fixture  # noqa: E402

PASS = FAIL = 0


def check(label, cond, detail=""):
    global PASS, FAIL
    if cond:
        PASS += 1
        print(f"  ok    {label}")
    else:
        FAIL += 1
        print(f"  FAIL  {label}" + (f"\n        {detail}" if detail else ""))


SYNTHETIC = [[
    "AMENDMENT NO. 4 JANUARY 2024",
    "TO",
    "SAMPLE REGULATIONS 2017",
    "This amendment amends the provisions of Part II 'Planning' of the Code, as follows:",
    "(Page 12, clause 5.2.1, line 4) - Substitute '2.25' for '1.80'.",
    "[Page 20, clause 6.1.3 (b)] - Delete the words 'subject to prior approval'.",
    "(Page 31, Table 9, Note 2) - Insert the following at the end:",
    "'For plots abutting a road of 18 m or more, see 6.4.2.'",
    "[Pages 40 and 41, clause 7.7] - Substitute the following for the existing:",
    "'7.7 Common Plot - A common plot of not less than 10 percent shall be provided.'",
]]


def main():
    tmp = tempfile.mkdtemp(prefix="bycodes-amd-")

    # ---- synthetic notification -------------------------------------------
    pdf = os.path.join(tmp, "amd.pdf")
    make_fixture.build_pdf(SYNTHETIC, pdf)
    pages = ingest.extract_pages(pdf)
    items = amd.parse_amendment(pages)

    check("parses one item per locator", len(items) == 4, f"got {len(items)}")

    by_clause = {i.get("target_clause"): i for i in items if i.get("target_clause")}

    it = by_clause.get("5.2.1")
    check("inline substitution captures old and new in the right order",
          it and it["action"] == "substitute" and it["new_text"] == "2.25" and it["old_text"] == "1.80",
          json.dumps(it, ensure_ascii=False)[:200] if it else "clause 5.2.1 not found")
    check("substitution records the target page", it and it["target_pages"] == [12],
          str(it.get("target_pages")) if it else "")

    it = by_clause.get("6.1.3 (b)")
    check("clause with a parenthetical sub-item is captured whole",
          it is not None, f"clauses seen: {list(by_clause)}")
    check("deletion captures the removed words",
          it and it["action"] == "delete" and it["old_text"] == "subject to prior approval",
          json.dumps(it, ensure_ascii=False)[:200] if it else "")

    tbl = [i for i in items if i.get("target_table") == "9"]
    check("table target is recognised", len(tbl) == 1, f"got {len(tbl)}")
    check("insertion captures the added text",
          tbl and tbl[0]["action"] == "insert" and "18 m or more" in (tbl[0]["new_text"] or ""),
          json.dumps(tbl[0], ensure_ascii=False)[:200] if tbl else "")

    multi = [i for i in items if i.get("target_pages") == [40, 41]]
    check("multi-page locator captures both pages", len(multi) == 1,
          str([i.get("target_pages") for i in items]))
    check("'substitute the following' captures the replacement block",
          multi and "10 percent" in (multi[0]["new_text"] or ""),
          json.dumps(multi[0], ensure_ascii=False)[:200] if multi else "")

    check("every parsed item resolves an action verb",
          all(i["action"] for i in items),
          str([(i["seq"], i["action"]) for i in items]))

    hdr = amd.detect_header(pages)
    check("reads the amendment number", hdr.get("amendment_number") == "4", str(hdr))
    check("reads the issue date", hdr.get("issued") == "January 2024", str(hdr))

    shutil.rmtree(tmp, ignore_errors=True)

    # ---- the real BIS notification held in this repo -----------------------
    real = os.path.join(ROOT, "corpus", "docs", "nbc-2016-amd1-2021", "amendments.json")
    if os.path.isfile(real):
        print("\n  -- real document: NBC 2016 Amendment No. 1 --")
        items = json.load(open(real, encoding="utf-8"))["items"]
        check("parses all 19 edits from the real notification", len(items) == 19,
              f"got {len(items)}")

        # The headline case: memory says 115 mm, the amendment says 90 mm.
        hit = [i for i in items if i.get("old_text") == "115 mm"]
        check("captures the 115 mm → 90 mm substitution", len(hit) == 1,
              f"got {len(hit)}")
        if hit:
            i = hit[0]
            check("that edit targets clause 4.4.2.4.3.2 (f)",
                  i["target_clause"] == "4.4.2.4.3.2 (f)", i["target_clause"])
            check("that edit points at base document page 34",
                  i["target_pages"] == [34], str(i["target_pages"]))
            check("that edit is classified as a substitution",
                  i["action"] == "substitute" and i["new_text"] == "90 mm",
                  f"{i['action']} {i['new_text']}")

        check("every real edit records a target page",
              all(i["target_pages"] for i in items),
              str([i["seq"] for i in items if not i["target_pages"]]))
        check("every real edit resolves an action verb",
              all(i["action"] for i in items),
              str([(i["seq"], i["action"]) for i in items if not i["action"]]))
        check("real edits carry either changed text or an instruction",
              all(i["new_text"] or i["old_text"] or i["instruction"] for i in items))
    else:
        print("\n  -- real notification not ingested, skipping those checks --")

    print(f"\n{PASS} passed, {FAIL} failed")
    return 1 if FAIL else 0


if __name__ == "__main__":
    sys.exit(main())
