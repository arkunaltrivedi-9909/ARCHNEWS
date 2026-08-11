# CODEX — Gujarat building regulation reference

A UpCodes-style clause browser, cited Q&A and feasibility calculator for
CGDCR, NBC and Ahmedabad / Gandhinagar development regulations.

Open at **`/codes.html`**.

---

## The one thing to understand first

**This tool ships with no regulation text, and that is the design.**

It knows nothing until you load official PDFs into it. Every clause it shows,
every page number it cites and every number the calculator produces is lifted
from a document you supplied and can open yourself.

The reason is narrow and practical. A language model can produce a clause number
and a page reference that look completely correct and are simply invented —
"CGDCR Regulation 12.4, page 87" reads the same whether it is real or not. On a
drawing going to AMC or AUDA scrutiny, that is the difference between an approval
and a redesign. So the architecture removes the possibility rather than trying to
prompt around it:

| Layer | What it may do | What it cannot do |
|---|---|---|
| Ingest | Extract verbatim text, page by page, from your PDF | Rewrite, summarise or supply text |
| Search | Rank the extracted clauses (BM25, deterministic) | Invent a match |
| Ask | Phrase what retrieved clauses say, with citations | Answer from model memory, or answer uncited |
| Feasibility | Apply rules you transcribed from cited clauses | Fall back on a "typical" value |

When the corpus cannot answer, every surface says so explicitly. **A refusal is a
working result, not a failure.**

---

## Loading the documents

### 1. Get the PDFs

`corpus/registry.json` lists the documents this is built for and where they were
published. Those URLs came from a web search and **were not opened** — the build
environment blocked outbound access to every Gujarat and BIS host. Verify each
one, and more importantly verify you have the **current amendment**.

Priority order, if you want the fastest route to something useful:

1. **CGDCR 2017 Part II (Planning Regulations)** — FSI, margins, common plot,
   parking, height. Most feasibility questions resolve here.
2. **Every CGDCR amendment notification since 2017.** An unamended base text will
   give wrong answers on FSI and height. This is the most common way a tool like
   this becomes confidently wrong.
3. CGDCR Parts I and III.
4. NBC 2016 Parts 3, 4 and 8.

### 2. Ingest

```bash
pip install pypdf

# name each file by its document id from registry.json
cp ~/Downloads/cgdcr-part2.pdf corpus/inbox/cgdcr-2017-part2.pdf

python3 tools/ingest.py --all
```

Or one at a time:

```bash
python3 tools/ingest.py --doc cgdcr-2017-part2 --pdf corpus/inbox/cgdcr-2017-part2.pdf
```

### 3. Verify before you trust it

```bash
python3 tools/verify.py
```

This reports page coverage, OCR gaps, whether printed page numbers were
established, and whether every feasibility rule cites a clause that actually
exists. Non-zero exit means something would be presented without support.

### 4. Deploy

Commit `corpus/docs/` and redeploy. The document list, tree, search, Q&A and
calculator all populate from it.

---

## Scanned PDFs — read this

Many Indian government PDFs are page images with no text layer. Those pages
extract as empty, and **an invisible page looks exactly like an absent rule**.

Ingest reports them as OCR gaps and `verify.py` warns loudly. Fix before use:

```bash
ocrmypdf --force-ocr corpus/inbox/doc.pdf corpus/inbox/doc-ocr.pdf
python3 tools/ingest.py --doc <id> --pdf corpus/inbox/doc-ocr.pdf
```

Until a document reads 100% coverage, absence of a rule in this tool does not
mean absence of the rule in law.

---

## Page numbers

Every citation shows both:

- **printed page** — the number printed on the page, which is what you quote
- **PDF page** — where it sits in the file, which is how you find it

Ingest infers the offset between them by sampling headers and footers. If it
cannot establish the offset confidently, citations show the PDF page only rather
than guessing a printed number. `verify.py` flags those documents.

---

## The feasibility calculator

Reads `corpus/rules.json`. Every rule must name the document, clause and page it
came from; the calculator **discards any rule without a citation** and reports
NOT DERIVABLE instead.

NOT DERIVABLE means *no cited rule is loaded* — never that the requirement is
zero or unrestricted.

Format and workflow: `corpus/rules.README.md`. Transcribe rules from clauses you
have read on the page; mark them `verified: false` until a second read confirms
them, and they will display with a warning badge until you do.

### What it structurally cannot do

Plot-specific answers depend on **DP zoning and your TP scheme final plot** —
road widening lines, reservations, the actual zone of the final plot. That
information lives in maps and F-forms, not in regulation text, and no amount of
clause ingestion produces it. Confirm your plot's zone and abutting road width
from the sanctioned TP scheme and DP map before trusting any output here.

---

## Optional: narrated answers

The Ask tab retrieves clauses with or without an API key. The key only adds a
narration layer on top of them.

```
ANTHROPIC_API_KEY=sk-ant-...      # in Vercel project env vars
CODEX_MODEL=claude-opus-5         # optional override
```

Without it, Ask still retrieves and displays the source clauses — which are the
authoritative thing anyway. With it, `api/code/ask.js` enforces, **after the
model responds**:

- citation markers pointing at passages that were not supplied are stripped
- an answer left with no valid citation is withheld entirely
- an explicit refusal is passed through as a refusal
- upstream errors fail closed, never into an uncited answer

Answers are always rendered above the source clauses, never instead of them.

---

## Tests

```bash
python3 tools/test_pipeline.py   # extraction, page anchoring, verbatim fidelity
node    tools/test_ask.js        # citation enforcement and refusal paths
node    tools/test_ui.js         # full page in a real browser (needs playwright)
```

`test_ask.js` pins the property the tool rests on: a confident, well-formed,
uncited answer is withheld. If you change `api/code/ask.js`, that test is the one
that matters.

---

## Fixture data

A synthetic document ships in `corpus/docs/fixture-sample/` so the UI is
explorable before you load anything. **Its content is invented and is not law.**
It renders with a red banner throughout, never outranks real documents in search,
and the Q&A layer excludes it once any real document is loaded.

Remove it before real use:

```bash
rm -rf corpus/docs/fixture-sample && python3 tools/ingest.py --reindex
```

---

## Copyright

CGDCR and Development Plan documents are Government of Gujarat notifications.

**NBC 2016 is copyright Bureau of Indian Standards and is sold, not freely
licensed.** Use a licensed copy. `corpus/.gitignore` excludes `corpus/docs/nbc-*`
so extracted NBC text is not published from a public repository — keep it that
way unless the repository is private and you hold a licence.

---

## Status

Built and tested; **corpus empty pending document load**. Phase 2 — AUDA/GUDA
Development Plan zoning and TP scheme data — needs a different approach from
clause ingestion, since that data is spatial rather than textual.
