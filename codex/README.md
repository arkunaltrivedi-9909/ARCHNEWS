# CODEX — Gujarat building regulation reference

Clause browser, cited Q&A, amendment tracking and feasibility calculator for
**GDCR** (primary), with **NBC** as back-end reference, for Ahmedabad and
Gandhinagar work.

**Standalone.** This folder shares nothing with the ARCHNEWS news site — its own
`index.html`, its own `api/`, its own `vercel.json`. Deploy it as a separate
Vercel project with **Root Directory = `codex`**. Nothing here can affect the
news site, and nothing there can affect this.

---

## Why it works this way

**It ships with almost no regulation text, and that is the design.**

A language model will produce "CGDCR Regulation 12.4, page 87" that reads exactly
the same whether it is real or invented. On a drawing going to AMC or AUDA
scrutiny that is the difference between approval and redesign. So the
architecture removes the possibility instead of prompting around it:

| Layer | May do | Cannot do |
|---|---|---|
| Ingest | Extract verbatim text, page by page | Rewrite, summarise or supply text |
| Search | Rank extracted clauses (BM25) | Invent a match |
| Ask | Phrase what retrieved clauses say, cited | Answer from memory, or answer uncited |
| Amendments | Show structured edits against base clauses | Silently rewrite the base text |
| Feasibility | Apply rules transcribed from cited clauses | Fall back on a "typical" value |

When the corpus cannot answer, every surface says so. **A refusal is a working
result.**

### The example that proves the point

**Amendment No. 1 (March 2021) to SP 7:2016**, the National Building Code, was
ingested during development — 19 edits, all parsed. One of them reads:

> *[Page 34, clause 4.4.2.4.3.2 (f), line 6] — Substitute '90 mm' for '115 mm'.*

Ask any model from memory and you get **115 mm**, because that is what the 2016
base text says and that is what the training data is full of. It has been wrong
since March 2021. CODEX shows the change, the clause, the base page, and — once
NBC Part 4 is loaded — flags it directly on the clause you are reading.

CGDCR has been amended repeatedly since 2017. This is not an edge case.

**That document is not committed.** This repository is public and NBC is BIS
copyright, so `corpus/.gitignore` excludes `corpus/docs/nbc-*`. Re-ingest it
locally in one command:

```bash
python3 tools/ingest_amendment.py --doc nbc-2016-amd1-2021 \
    --pdf corpus/inbox/nbc-2016-amd1-2021.pdf --amends nbc-2016-part4
```

If you make this repository private, delete the `docs/nbc-*/` line from
`corpus/.gitignore` and the ingested data will deploy with the app.

---

## Loading documents

### Priority

1. **CGDCR 2017 Part II** (Planning Regulations) — FSI, margins, common plot,
   parking, height. Most feasibility questions resolve here. Start here.
2. **Every CGDCR amendment notification since 2017.**
3. CGDCR Parts I and III.
4. NBC 2016 Parts 3, 4, 8 — supporting reference.

`corpus/registry.json` lists these with published locations. Those URLs came
from a web search and **were not opened** — the build environment blocked every
Gujarat and BIS host. Verify each, and verify the amendment status.

### Regulations

```bash
pip install pypdf
cp cgdcr-part2.pdf corpus/inbox/cgdcr-2017-part2.pdf
python3 tools/ingest.py --all
```

### Amendments — different command, on purpose

An amendment contains instructions to change another document, not regulation
text. Ingesting it as prose would produce a clause tree of nonsense.

```bash
python3 tools/ingest_amendment.py \
    --doc cgdcr-amd-2023-01 \
    --pdf corpus/inbox/cgdcr-amd-2023-01.pdf \
    --amends cgdcr-2017-part2
```

`--amends` is what links edits to the base document. Without it the edits are
listed but cannot overlay the clauses they change, and `verify.py` warns.

### Verify before trusting

```bash
python3 tools/verify.py
```

Reports page coverage, OCR gaps, whether printed page numbers were established,
whether amendments are linked to loaded base documents, and whether every
feasibility rule cites a clause that exists. Non-zero exit means something would
be presented without support.

---

## Scanned PDFs

Many Indian government PDFs are page images with no text layer. Those pages
extract empty, and **an invisible page looks exactly like an absent rule**.

```bash
ocrmypdf --force-ocr corpus/inbox/doc.pdf corpus/inbox/doc-ocr.pdf
python3 tools/ingest.py --doc <id> --pdf corpus/inbox/doc-ocr.pdf
```

Until a document reads 100% coverage, absence of a rule here does not mean
absence of the rule in law.

---

## Page numbers

Every citation shows **printed page** (what you quote) and **PDF page** (how you
find it). Ingest infers the offset by sampling headers and footers; if it cannot
establish it confidently it shows the PDF page only rather than guessing.

---

## Feasibility calculator

Reads `corpus/rules.json`. Every rule must name the document, clause and page it
came from. The calculator **discards any rule without a citation** and reports
NOT DERIVABLE — which means *no cited rule is loaded*, never that the requirement
is zero or unrestricted.

Format and workflow: `corpus/rules.README.md`.

**What it structurally cannot do:** your final plot's zone, abutting road width,
widening lines and reservations live in DP maps and TP scheme F-forms, not in
regulation text. The calculator takes those as inputs you supply from the
sanctioned TP scheme — it cannot derive them, and does not pretend to.

---

## Optional: narrated answers

Ask retrieves clauses with or without an API key; the key only adds narration.

```
ANTHROPIC_API_KEY=sk-ant-...      # Vercel project env vars
CODEX_MODEL=claude-opus-5         # optional
```

`api/ask.js` enforces, **after** the model responds: citation markers pointing at
passages that were not supplied are stripped; an answer left with no valid
citation is withheld entirely; explicit refusals pass through; upstream errors
fail closed. Answers always render above the source clauses, never instead.

---

## Tests

```bash
python3 tools/test_pipeline.py    # 16 — extraction, page anchoring, verbatim fidelity
python3 tools/test_amendment.py   # 20 — amendment parsing, incl. the real BIS notification
node    tools/test_ask.js         # 10 — citation enforcement and refusal paths
node    tools/test_ui.js          # 33 — full page in a real browser
```

`test_ask.js` case 5 pins the property everything rests on: a confident,
well-formed, **uncited** answer is withheld. `test_amendment.py` pins the
115 mm → 90 mm parse against the real document.

The UI test builds its own fixture, uses it, and deletes it — so shipped corpus
data is only ever real.

---

## Copyright

CGDCR and Development Plan documents are Government of Gujarat notifications.

**NBC 2016 is copyright Bureau of Indian Standards** and is sold, not freely
licensed. `corpus/.gitignore` excludes `corpus/docs/nbc-*` so extracted NBC text
is not published from a public repository. The amendment notification held here
is a short public errata document, not the code itself.

---

## Status

| | |
|---|---|
| App | Built, tested, deployable — 82 tests passing |
| Corpus | **Empty in git** — NBC amendment ingests locally, excluded from this public repo |
| GDCR | **Not loaded** — needs the PDFs |
| Phase 2 | AUDA/GUDA DP zoning + TP schemes — spatial data, different approach |
