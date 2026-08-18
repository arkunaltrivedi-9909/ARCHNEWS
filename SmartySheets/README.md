# KTA Smarty Sheets

Re-export only the sheets that changed.

A 150-sheet set takes roughly 15 minutes to publish. One sheet changes and the standard answer is to publish all 150 again, then hunt through a folder to work out which files are current. Smarty Sheets compares every sheet against a ledger of the last export, re-exports what moved, and writes a CSV checklist proving what landed.

Revit 2025, 2026 and 2027. Windows, per-user install, no elevation.

> **Status: not yet compiled or run.** This code was written without a Revit install to
> reference against, so the Revit API member names have not been confirmed. Work through
> `VERIFY.md` first, then `TESTING.md` stage 0. Nothing below has been measured.

---

## How it decides what changed

Three independent sources of evidence, combined with a logical OR.

**Fingerprint.** A content hash per sheet covering sheet parameters, titleblock instance parameters, revisions and revision clouds, every viewport's position and outline, each placed view's scale, detail level, template, crop and display style, schedule instances, and a digest of the annotation inside each view.

**Session tracking.** Revit's `DocumentChanged` event, listened to from the moment Revit starts. This is what catches edits made before you saved. `Element.VersionGuid` cannot do this: Autodesk's documentation states it only moves between saves, synchronise-to-central and reload-latest, so between saves it cannot tell you whether anything changed.

**Document history.** `Document.GetChangedElements`, comparing against the document version recorded at the end of your last run. This covers the case where you closed Revit, a colleague edited the central model, and you reopened.

Changed elements that carry an `OwnerViewId` (tags, dimensions, detail components) are mapped straight to their view for free. Only model elements trigger the deeper per-view visibility scan, and results are cached per view.

### The rule the whole tool is built on

**False positives are free. False negatives are unacceptable.**

Re-exporting an unchanged sheet costs eight seconds. Skipping a changed sheet puts a stale drawing in a tender set. Every ambiguity resolves toward exporting. If the tool cannot prove a sheet is unchanged, it exports it and tells you why in the Why column.

---

## Install

Requires the .NET SDK: 8 for Revit 2025 and 2026, 10 for Revit 2027.

```powershell
git clone <your-repo> SmartySheets
cd SmartySheets
.\build.ps1 -Versions 2026
```

Start Revit. Look for the **KTA** tab.

Multiple versions at once:

```powershell
.\build.ps1 -Versions 2025,2026,2027 -Config Release
```

Remove it:

```powershell
.\build.ps1 -Versions 2026 -Uninstall
```

Installs to `%APPDATA%\Autodesk\Revit\Addins\<version>\`. Revit 2027 moved *machine-wide* add-ins from `ProgramData` to `Program Files`; per-user paths are unchanged, which is why this installer uses them.

---

## Use

1. **KTA** tab, **Smarty Sheets**.
2. Choose an output folder. The folder is the unit of memory: Smarty Sheets remembers what it last sent there.
3. Set a name template. Tokens: `{SheetNumber}` `{SheetName}` `{Revision}` `{RevisionDate}` `{RevisionDescription}` `{ProjectNumber}` `{ProjectName}` `{ClientName}` `{Discipline}` `{ModelName}` `{User}` `{Date:yyyy-MM-dd}` `{Param:Any Parameter Name}`. Empty tokens collapse cleanly, so an unrevised sheet gives `A101`, not `A101 - `.
4. **Analyse.** Every sheet gets a state and a plain-English reason.
5. **Export.** Tick or untick any row first; you always have the final say.

| State | Meaning |
|---|---|
| New | Never sent to this folder |
| Changed | Something moved. The Why column says what |
| MissingOnDisk | Ledger says exported, file is gone |
| Unknown | Could not be verified, so it exports |
| Unchanged | Provably identical. Skipped |

The run writes `SmartySheets_Manifest.csv` next to the output: one row per sheet, a tick for what is current, the reason for every decision, and a run summary. Opens directly in Excel or Google Sheets.

State lives in a hidden `.smartysheets\ledger.json` inside the output folder, not in AppData, so it travels with the deliverable and a colleague exporting to the same network folder inherits your history.

---

## Known limits

1. Deleted elements cannot be traced to the views they appeared in, so when deletions are detected the affected sheets are conservatively marked Changed.
2. Deep scan enumerates the visible elements of every placed view. Accurate, and slower on very large models. It can be turned off.
3. Linked model edits register once the link reloads and the host records the change.
4. Placeholder sheets are listed and never exported.
5. A new output folder means a full first export, by design.

---

## Performance

Fill this in from Stage 4 of `TESTING.md` once you have run it on a real set.

| Task | Revit's own dialog | Smarty Sheets |
|---|---|---|
| Full publish, 150 sheets | | |
| Re-publish after one sheet changes | | |

---

## Troubleshooting

**No KTA tab.** The add-in did not load. Check `%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit <version>\Journals\` for the most recent journal and search for `SmartySheets`. Ninety percent of the time it is a .NET target mismatch or a stray `RevitAPI.dll` in the deployed folder.

**Everything reads Changed after a save.** Correct behaviour only if you actually edited something. If a save alone dirties every sheet, the fingerprint is including a parameter that Revit rewrites on save. Log which one and exclude it in `Fingerprint.AppendSheetParameters`.

**Export produces no file and no error.** Almost always the PDF options. Set Paper Format away from `Default` and retry. There is a documented case of `Default` combined with other layout options silently doing nothing.

**Runtime log:** `%APPDATA%\KTA\SmartySheets\logs\`

---

Built by Kunal Trivedi Atelier, Ahmedabad.
