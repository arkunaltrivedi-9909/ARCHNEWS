# Verify list

This code has never been compiled against a live Revit install. It was written on Linux,
with no `RevitAPI.dll` to check against, so every member below is taken from documentation
and memory rather than from the assembly.

Work this list first. `CLAUDE.md` section 6 describes the loop: take the first compile
error, look the member up in `RevitAPI.xml` next to `RevitAPI.dll`, fix it, rebuild.

**The rule that governs every fix here:** never resolve a compile error by deleting a
safety check, widening a `catch`, or turning a `return true` (dirty) into `return false`
(clean). If the real API cannot answer a question this code asks, make the failure path
mark the sheet dirty and say so in the Why column.

Tick each row as you confirm it, and move the confirmed ones into `CLAUDE.md` section 5.

---

## Tier 1 — most likely to be wrong, and most damaging if it is

| ✔ | Member | Used at | Note |
|---|---|---|---|
| ☐ | `Document.GetDocumentVersion(Document)` → `DocumentVersion` | `Core/DocumentHistory.cs:40` | May be an instance method, and the class may live elsewhere |
| ☐ | `DocumentVersion.VersionGUID`, `.NumberOfSaves` | `Core/DocumentHistory.cs:41-42` | Casing of `GUID` is a common trip |
| ☐ | `Document.GetChangedElements(Guid)` | `Core/DocumentHistory.cs:68` | **If this takes a `DocumentVersion` rather than a `Guid`, the ledger cannot round-trip it as a string.** `LedgerData` already stores `LastNumberOfSaves` alongside the guid so a fix has both halves to work with. Bound as `ICollection<ElementId>` so a list or a set both compile |

A failure in this file is the one place where a wrong guess is *safe*: the catch sets
`EvidenceIncomplete`, every sheet with history becomes `Unknown`, and `Unknown` exports.
The tool becomes useless rather than wrong. Fix it, do not work around it.

## Tier 2 — export options, where names drift between releases

| ✔ | Member | Used at |
|---|---|---|
| ☐ | `PDFExportOptions.Combine`, `.FileName`, `.StopOnError` | `Core/ExportEngine.cs:97-107` |
| ☐ | `PDFExportOptions.HideCropBoundaries`, `.HideScopeBoxes`, `.HideReferencePlane`, `.HideUnreferencedViewTags` | `Core/ExportEngine.cs:102-105` |
| ☐ | `PDFExportOptions.PaperFormat`, `.PaperPlacement`, `.ZoomType`, `.ZoomPercentage` | `Core/ExportEngine.cs:136-139` |
| ☐ | `ExportPaperFormat` enum | `Core/ExportEngine.cs:128`, `UI/MainWindow.xaml.cs:91` |
| ☐ | `PaperPlacementType.Center` | `Core/ExportEngine.cs:137` |
| ☐ | `ZoomType.Zoom` | `Core/ExportEngine.cs:138` |
| ☐ | `Document.Export(string, IList<ElementId>, PDFExportOptions)` → `bool` | `Core/ExportEngine.cs:112` |
| ☐ | `DWGExportOptions.GetPredefinedOptions(Document, string)` | `Core/ExportEngine.cs:150` |
| ☐ | `DWGExportOptions.MergedViews` | `Core/ExportEngine.cs:160` |
| ☐ | `Document.Export(string, string, IList<ElementId>, DWGExportOptions)` → `bool` | `Core/ExportEngine.cs:162` |

The paper-format list is read from the running Revit with `Enum.GetNames`, so the combo box
cannot offer a member this build does not have. Only the enum's *type name* is guessed.

## Tier 3 — model reads

| ✔ | Member | Used at |
|---|---|---|
| ☐ | `ViewSheet.GetCurrentRevision()` → `ElementId` | `Core/Fingerprint.cs:97`, `Core/NameTemplate.cs:152` |
| ☐ | `ViewSheet.GetAllRevisionIds()` | `Core/Fingerprint.cs:100` |
| ☐ | `ViewSheet.GetAllViewports()` | `Core/Fingerprint.cs:129`, `Core/ChangeAnalyzer.cs` |
| ☐ | `Revision.SequenceNumber`, `.RevisionNumber`, `.RevisionDate`, `.Description`, `.Issued`, `.IssuedBy`, `.IssuedTo` | `Core/Fingerprint.cs:107-115` |
| ☐ | `Viewport.GetBoxCenter()`, `.GetBoxOutline()` → `Outline` | `Core/Fingerprint.cs:136-138` |
| ☐ | `ScheduleSheetInstance.ScheduleId`, `.Point`, `.Rotation` | `Core/Fingerprint.cs:251-256` |
| ☐ | `ElementOwnerViewFilter(ElementId)` | `Core/Fingerprint.cs:187` |
| ☐ | `BuiltInParameter.REVISION_CLOUD_REVISION` | `Core/Fingerprint.cs:218` |
| ☐ | `BuiltInParameter.ELEM_PARTITION_PARAM` | `Core/Fingerprint.cs:31` |
| ☐ | `View.Discipline`, `.DisplayStyle`, `.DetailLevel`, `.CropBoxActive`, `.CropBoxVisible`, `.ViewTemplateId` | `Core/Fingerprint.cs:151-160` |
| ☐ | `ProjectInformation.ClientName`, `.Number`, `.Name`, `.UniqueId` | `Core/NameTemplate.cs:36-38`, `UI/ExportHandler.cs` |
| ☐ | `Document.Application.Username` | `Core/NameTemplate.cs:57` |

## Tier 4 — host and shell

| ✔ | Member | Used at | Note |
|---|---|---|---|
| ☐ | `UIApplication.MainWindowHandle` | `UI/MainWindow.xaml.cs:42` | Already inside a try/catch; the window just goes unowned if it is wrong |
| ☐ | `Microsoft.Win32.OpenFolderDialog` (`.FolderName`, `.Multiselect`, `.InitialDirectory`) | `UI/MainWindow.xaml.cs:252` | .NET 8 WPF and later. If it does not resolve on your SDK, swap to `System.Windows.Forms.FolderBrowserDialog` and add `<UseWindowsForms>true</UseWindowsForms>` to the csproj |

---

## Things known to be right, so do not "fix" them

- `ElementId.Value` (long) rather than `IntegerValue`. Correct for 2024 and later.
- `<Private>False</Private>` on both Revit references. `build.ps1` fails the build if a
  `RevitAPI*.dll` reaches the output folder.
- `Document.Export` is called with no surrounding `Transaction`. Export is a read.
- `PDFExportOptions.Combine = true` with a single sheet id. With `Combine = false` Revit
  names files by its own rules and ignores `FileName`, and the ledger could not then match
  a sheet to a file.
- `System.Text.Json` comes from the shared framework. Do not add the NuGet package; a
  second copy beside a Revit add-in is a load failure waiting to happen.

## Not yet run

`TESTING.md` stages 0 to 5 have not been executed. Nothing in this repo has been run inside
Revit. Stage 0.1 is the first thing that will tell you anything.
