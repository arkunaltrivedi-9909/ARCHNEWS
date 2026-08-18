# CLAUDE.md — working rules for this repo

You are working on **KTA Smarty Sheets**, a Revit add-in that re-exports only the sheets that actually changed.

Read this file fully before editing anything.

---

## 1. What the product is

A 150-sheet set takes about 15 minutes to publish. One sheet changes and the current answer is to publish all 150 again. Smarty Sheets compares every sheet against a ledger of the last export and re-exports only what moved. It writes a CSV checklist proving what landed.

Target user is an architect under deadline. The tool must be boring, fast, and never wrong.

---

## 2. The one invariant you may never break

> **False positives are free. False negatives are unacceptable.**

Re-exporting an unchanged sheet costs eight seconds. Skipping a changed sheet puts a stale drawing in a tender set. Every ambiguity resolves toward exporting.

Concretely:
- Every `catch` in `Fingerprint.cs` appends a `Guid.NewGuid()` so a failure produces a *different* hash and forces a re-export. Do not "clean this up".
- `ChangeAnalyzer` fuses three evidence sources with a logical OR. Never turn this into a confidence score, a heuristic, or a vote.
- If you cannot enumerate something, return `true` (dirty). See `SheetUsesAnyView`, `SheetShowsChangedModel`.
- A failed export **deletes** that sheet's ledger entry so the next run retries it. Never make failures silent-clean.

If a change you are asked to make would create a path where a changed sheet is skipped, stop and say so before writing code.

---

## 3. Revit API rules that are not negotiable

1. **Single thread.** Every `Document` and `Element` call happens on Revit's API thread. From the modeless window, that means going through `ExternalEvent` (`UI/ExportHandler.cs`). Never `Task.Run` anything that touches the API. Never `async void` around a Revit call.
2. **Batch and yield.** `RunExportBatch` exports `BatchSize` sheets then calls `_event.Raise()` and returns. That is what keeps Revit responsive and makes Cancel work. Do not "optimise" it into one long loop.
3. **No transaction for export.** `Document.Export` is a read. Wrapping it in a `Transaction` adds failure modes and nothing else.
4. **Never throw out of an event handler.** `DirtyTracker.OnDocumentChanged` runs inside Revit's commit pipeline on every transaction. It swallows everything and does one cheap thing.
5. **`Private=False` on Revit references.** Copying `RevitAPI.dll` next to your add-in is the classic cause of a silent load failure.

---

## 4. Version targets

| Revit | TFM | Configuration |
|---|---|---|
| 2025 | `net8.0-windows` | `Debug R2025` / `Release R2025` |
| 2026 | `net8.0-windows` | `Debug R2026` / `Release R2026` |
| 2027 | `net10.0-windows` | `Debug R2027` / `Release R2027` |

Revit 2027 runs on .NET 10 and Autodesk state that all add-ins must be built against the .NET 10 SDK. Also in 2027, machine-wide add-ins moved out of `ProgramData` into `Program Files`. We install per-user, so that change does not affect us, but do not write an installer that targets `ProgramData`.

`ElementId.Value` (long) is used throughout. That is correct for 2024+. Do not reintroduce `IntegerValue`.

---

## 5. Verified vs assumed

**Verified against current Autodesk documentation and support articles:**
- `Element.VersionGuid` only changes between saves, syncs and reload-latest. It cannot detect in-session edits. This is why `DirtyTracker` exists.
- `Document.GetChangedElements(Guid)` exists from Revit 2023 and returns created, modified and deleted ids since a base document version.
- With `PDFExportOptions.Combine = false`, files are named by Revit's naming rules and `FileName` is ignored. Hence the one-sheet-at-a-time `Combine = true` approach.
- With `PaperFormat = Default`, setting other layout options can make the export silently do nothing and throw no exception. `ApplyPdfOptions` guards this.
- .NET targets per Revit version, as in the table above.

**Assumed and NOT yet verified against a live Revit install. Verify these first:**
- Exact property names and enum members on `PDFExportOptions`, `DWGExportOptions`, `ACADVersion`, `PDFExportQualityType`, `ColorDepthType`, `ZoomType`, `PaperPlacementType`. Names drift between releases.
- `DWGExportOptions.GetPredefinedOptions(doc, name)` signature.
- `ViewSheet.GetCurrentRevision()` and `GetAllRevisionIds()` return types.
- `Revision.SequenceNumber`, `IssuedBy`, `IssuedTo`.
- `ScheduleSheetInstance.ScheduleId` and `.Point`.
- `Viewport.GetBoxOutline()` returning `Outline`.
- `UIApplication.MainWindowHandle`.
- Whether `Microsoft.Win32.OpenFolderDialog` resolves on your exact SDK. If not, swap to a `System.Windows.Forms.FolderBrowserDialog` with `<UseWindowsForms>true</UseWindowsForms>`.

These are now tracked in **`VERIFY.md`**, one row per call, with the file and line that
uses it. Work that list before anything else and tick each row off as you confirm it.

**Do not guess at these.** Open `RevitAPI.xml` next to `RevitAPI.dll`, or decompile the DLL, and confirm.

---

## 6. Your first job: compile clean

```powershell
cd <repo>
dotnet build src\KTA.SmartySheets\KTA.SmartySheets.csproj -c "Debug R2026"
```

Then loop:

1. Take the **first** error only.
2. Look up the real member name in `RevitAPI.xml` at `C:\Program Files\Autodesk\Revit 2026\RevitAPI.xml`, or by decompiling. Do not guess from memory.
3. Fix it. Rebuild.
4. Repeat until zero errors and zero warnings other than the suppressed ones.

Report every API name you had to correct, in a list, at the end. That list is the record of what my documentation research got wrong and it goes into this file's section 5.

**Never** fix a compile error by deleting a safety check, widening a catch to swallow more, or changing a `return true` (dirty) into `return false` (clean).

---

## 7. Debugging inside Revit

- Build `Debug R2026`, run `.\build.ps1 -Versions 2026`, start Revit.
- In Visual Studio: Debug, Attach to Process, `Revit.exe`. Breakpoints in `ChangeAnalyzer.Analyze` and `ExportEngine.ExportSheet` are the two that matter.
- Runtime log: `%APPDATA%\KTA\SmartySheets\logs\smartysheets-<date>.log`.
- If the ribbon tab never appears, the add-in failed to load. Read the journal at `%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit 2026\Journals\`, search for `SmartySheets` and for `ExternalApplication`.

---

## 8. Code style

- Comments explain **why**, never what. If a comment restates the line below it, delete it.
- Every `catch` either logs or has a one-line comment saying why swallowing is correct.
- No `async`. No DI container. No MVVM framework. This is a single-window tool and plain code-behind is the right size.
- Public methods that touch the Revit API get an XML doc line stating the threading requirement.

---

## 9. What is deliberately not built yet

Do not add these unprompted. They are v1.1 and later.

- Sheet-set and view-set filtering, and saved selection sets
- Per-sheet paper size resolved from the titleblock family
- Combined multi-sheet PDF alongside the per-sheet files
- IFC, DWF, NWC, image formats
- A scheduled or watched-folder export runner
- Writing status back into a Revit sheet parameter after export
- Direct Google Sheets sync in place of the CSV

---

## 10. Where things are

```
SmartySheets/
  build.ps1                       build + per-user install, all three Revit versions
  VERIFY.md                       every API call not yet confirmed against a live install
  src/KTA.SmartySheets/
    App.cs                        IExternalApplication: ribbon, DocumentChanged wiring
    Commands/ShowSmartySheetsCommand.cs
    Core/
      ChangeAnalyzer.cs           the OR of three evidence sources. Analyze() is the breakpoint
      Fingerprint.cs              content hash per sheet. Read the catch blocks first
      DirtyTracker.cs             DocumentChanged listener, live from Revit startup
      DocumentHistory.cs          the only file that touches the document-version API
      Ledger.cs                   .smartysheets/ledger.json, per output folder
      ExportEngine.cs             one sheet at a time. ExportSheet() is the other breakpoint
      NameTemplate.cs             tokens, empty-token collapsing, collision suffixes
      ManifestWriter.cs           SmartySheets_Manifest.csv
      ExportSettings.cs           persisted to %APPDATA%\KTA\SmartySheets\settings.json
      PathSafety.cs               filename sanitising, write probe, lock detection
      Log.cs
    UI/
      ExportHandler.cs            the ExternalEvent bridge. RunExportBatch lives here
      MainWindow.xaml(.cs)        the single window
      IconFactory.cs              ribbon icon drawn at runtime, no binary assets
```

The evidence sources meet in exactly one place, `ChangeAnalyzer.Analyze`. If you are
changing how a sheet is judged, that method and the two helpers below it are the whole
surface. Nothing else decides.
