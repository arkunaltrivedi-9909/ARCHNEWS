# Feasibility rules — format and workflow

The calculator does not know any building regulation. It only applies rules
listed in `rules.json`, and it refuses to apply a rule that cannot name the
clause and page it came from.

This is deliberate. A calculator that silently falls back on a plausible default
FSI is more dangerous than one that says nothing, because you cannot see it
being wrong.

## Format

```jsonc
{
  "rules": [
    {
      "output": "base_fsi",              // one of the keys listed below
      "value": "1.8",                    // EXACTLY as printed in the clause
      "unit": "",                        // "m", "sq m", "%", "ECS/100 sq m", ...
      "when": {                          // all present conditions must match
        "city": "ahmedabad",             // ahmedabad | gandhinagar
        "zone": "R1",                    // R1 | R2 | C | I
        "use": "residential",            // residential | commercial | mixed | institutional
        "min_road_width": 9,             // metres, inclusive
        "max_road_width": 18,
        "min_plot_area": 0,              // sq m, inclusive
        "max_plot_area": 500
      },
      "citation": {                      // REQUIRED — rule is discarded without it
        "doc_id": "cgdcr-2017-part2",    // must exist in corpus/docs/
        "number": "12.4",                // clause number as ingested
        "page_label": "p. 87",           // what the citation chip displays
        "quote": "…the base FSI shall be 1.8…"   // verbatim, for cross-checking
      },
      "verified": true,                  // false ⇒ shown with an UNVERIFIED warning
      "note": "Applies only where the plot abuts a road of 9 m or more."
    }
  ]
}
```

### Output keys

`base_fsi`, `chargeable_fsi`, `max_height`, `front_margin`, `side_margin`,
`rear_margin`, `common_plot`, `parking`, `ground_coverage`

Any output key with no matching rule renders as **NOT DERIVABLE**. That is the
correct result when nothing is loaded — it does not mean "unrestricted".

## Workflow

1. **Ingest first.** `python3 tools/ingest.py --all`. A rule may only cite a
   clause that exists in `corpus/docs/`; `tools/verify.py` checks this.
2. **Find the governing clause** in the Search tab. Read the actual page.
3. **Transcribe, don't recall.** Copy the value and the verbatim quote from the
   clause text. If you find yourself typing a number you remember rather than one
   you can see on the page, stop.
4. **Set `verified: false`** until a second read confirms it against the PDF
   page. Unverified rules still compute, but display a warning — so a
   half-finished ruleset is visibly half-finished.
5. **Run `python3 tools/verify.py`** — it fails on rules whose `doc_id` or clause
   number is not present in the corpus, and on rules missing a citation.

## Conditions and precedence

More specific rules win: the calculator scores a rule by how many `when` keys it
sets and picks the highest. If two equally specific rules match, it shows both
and asks you to resolve it manually rather than picking one silently.

## What this cannot do

Plot-specific answers depend on the **Development Plan zoning and the TP scheme
final plot** — road widening lines, reservations, the actual zone of your final
plot. Those live in maps and F-forms, not in regulation text, and no amount of
clause ingestion resolves them. Confirm your plot's zone and abutting road width
from the sanctioned TP scheme and DP map before trusting any output here.
