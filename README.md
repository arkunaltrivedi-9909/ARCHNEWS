# ARCHNEWS

Architecture intelligence, deployed on Vercel.

| | |
|---|---|
| **`/`** | Live architecture news aggregated from ArchDaily, Dezeen, Archinect, Architectural Record and others |
| **`/codes.html`** | **CODEX** — Gujarat building regulation reference: clause browser, cited Q&A, feasibility calculator |

## CODEX

A UpCodes-style reference for CGDCR, NBC and Ahmedabad / Gandhinagar development
regulations. Every clause, page number and computed value traces to a source PDF
you loaded yourself — and when the loaded corpus cannot answer, it says so
instead of guessing.

It ships with **no regulation text**. See **[CODEX.md](CODEX.md)** for the load
procedure, the accuracy design and the tests.

```bash
pip install pypdf
cp your-regulation.pdf corpus/inbox/cgdcr-2017-part2.pdf
python3 tools/ingest.py --all
python3 tools/verify.py
```
