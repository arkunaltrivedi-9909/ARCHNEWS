# Test protocol

A plugin that skips the wrong sheet is worse than no plugin. Run every test in order. Do not ship past a red row.

Use a copy of a real project with 40 or more sheets. A sample model will not surface the failures that matter.

---

## Stage 0 — it loads

| # | Do | Expect |
|---|---|---|
| 0.1 | `.\build.ps1 -Versions 2026` | Green, no errors |
| 0.2 | Start Revit, open a project | A **KTA** tab with **Smarty Sheets** |
| 0.3 | Click it with no project open | Polite message, no crash |
| 0.4 | Click it in a family document | Polite message, no crash |
| 0.5 | Click it twice | One window, brought forward |

If 0.2 fails, the add-in did not load. Journal first: `%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit 2026\Journals\`.

---

## Stage 1 — first export is correct

| # | Do | Expect |
|---|---|---|
| 1.1 | Pick an empty folder, Analyse | Every sheet reads **New** |
| 1.2 | Export all | One PDF per sheet, named by the template |
| 1.3 | Open three PDFs | Correct sheet, correct scale, titleblock intact |
| 1.4 | Compare against a manual Revit PDF export of the same sheet | Visually identical |
| 1.5 | Open `SmartySheets_Manifest.csv` in Excel | A tick against every exported sheet, tick renders correctly |
| 1.6 | Check the folder | A hidden `.smartysheets\ledger.json` exists |

**1.4 is the one people skip.** If the plugin's output differs from Revit's own dialog, every downstream test is meaningless.

---

## Stage 2 — the delta, which is the whole product

| # | Do | Expect |
|---|---|---|
| 2.1 | Analyse again, changing nothing | Every sheet **Unchanged**, nothing selected |
| 2.2 | Export | Zero files written, run finishes in seconds |
| 2.3 | Rename one sheet, Analyse | That sheet **Changed**, everything else Unchanged |
| 2.4 | Move a tag in a view placed on one sheet, Analyse | That sheet **Changed** |
| 2.5 | Move a wall visible on three sheets, Analyse, Deep scan ON | Exactly those three **Changed** |
| 2.6 | Same edit, Deep scan OFF | Detection is weaker. Note what it misses; this is the documented trade-off |
| 2.7 | Add a revision to two sheets, Analyse | Those two **Changed** |
| 2.8 | Edit a titleblock field, Analyse | That sheet **Changed** |
| 2.9 | Delete one exported PDF from disk, Analyse | That sheet **MissingOnDisk** |
| 2.10 | Save, close Revit, reopen, edit a wall, Analyse | Affected sheets **Changed**. This is the `GetChangedElements` path |
| 2.11 | Change nothing, save, Analyse | Still **Unchanged**. A save alone must not dirty everything |

**2.11 is the trap.** A naive implementation built on `VersionGuid` marks every sheet dirty after any save, and the tool becomes a slow way to export everything.

---

## Stage 3 — it does not lie

| # | Do | Expect |
|---|---|---|
| 3.1 | Start a 40-sheet export, click Cancel at sheet 5 | Stops within one sheet. Files 1 to 5 exist and are valid |
| 3.2 | Analyse after that cancel | Sheets 6 onward still read as needing export |
| 3.3 | Open an exported PDF in a viewer, re-export that sheet | Clear message naming the locked file, a `_NEW` copy is written, run continues |
| 3.4 | Point the output at a read-only folder | Refuses up front with a readable message |
| 3.5 | Corrupt `ledger.json` with a text editor, Analyse | Treats it as empty, everything reads New, no crash |
| 3.6 | Export a sheet named `A/B: test` | Filename sanitised, file created |
| 3.7 | Two sheets that resolve to the same filename | Second gets `_02`, neither is lost |
| 3.8 | Point the output at a folder that holds another model's ledger | Detects the mismatch, starts fresh, does not skip anything |
| 3.9 | During a long export, click around in Revit | The UI responds. Frozen Revit means the batching broke |

---

## Stage 4 — the number that justifies the build

Take a real set of 100 or more sheets.

1. Time a full publish through Revit's own PDF dialog. Record it.
2. Full export through Smarty Sheets. Record it. Should be comparable, this is not the win.
3. Change **one** sheet.
4. Analyse and export through Smarty Sheets. Record it.

**Acceptance:** step 4 finishes in under 60 seconds on a 150-sheet set, against 15 minutes for step 1.

Write the three numbers into the README. That table is what turns this from a personal utility into a product other people pay for.

---

## Stage 5 — before anyone else touches it

- Run stages 1 to 3 on Revit 2025, 2026 and 2027 builds.
- Run on a workshared central model, not just a local file.
- Run with the output on a network drive, not just local.
- Have one colleague run it on their machine with no build tools installed.
- Confirm no `RevitAPI.dll` sits in the deployed folder.

---

## Known limits to state plainly in the README

Say these out loud rather than letting a user discover them.

1. **Deletions cannot be attributed.** A deleted element cannot be asked which views it appeared in. When deletions are detected the affected sheets are conservatively marked Changed. Safe, sometimes over-eager.
2. **Deep scan costs time** on very large models, because it enumerates the visible elements of every placed view.
3. **Linked model edits** are only caught once the link is reloaded and the host document records the change.
4. **Placeholder sheets** are listed and never exported. They have no content.
5. **The ledger is per output folder.** Export to a different folder and the first run there is a full export, by design.
