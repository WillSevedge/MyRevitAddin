# Nexus

Nexus connects running **Revit**, **AutoCAD**, **Civil 3D** and **Plant 3D** sessions to a **hub** that runs in
the background (tray icon, starts with Windows). Each host runs a small agent that answers requests
over a named pipe; the hub finds every running agent, lists the open documents, runs *readers*
against them, lets you edit values and write them back, and exports the results.

Later phases: Excel import/export, two-way sync and the MCP server.

## Solution layout

| Project | Target | What it is |
|---|---|---|
| `src/Nexus.Contracts` | net48; net8.0; net10.0 | *Shared.Contracts*: message envelope, DTOs (host, document, reader, item, property), pipe framing. No dependencies. |
| `src/Nexus.Agent.Shared` | net48; net8.0; net10.0 | *Shared.Agent*: pipe server, discovery file, request dispatch, host-thread work queue, reader registry, logging. No NuGet dependencies (loaded inside the hosts). |
| `src/Nexus.Agent.Revit` | per year (see *Program years*) | Revit add-in (`IExternalApplication`), ribbon status button, `ExternalEvent` request queue, readers. |
| `src/Nexus.Agent.Acad` | per year (see *Program years*) | AutoCAD-family core agent (`IExtensionApplication`), autoloader bundle, generic "Properties palette" reader, layouts reader, module loader. Loads in every AutoCAD-based product. |
| `src/Nexus.Agent.Acad.Civil3D` | per year (see *Program years*) | Civil 3D module. The only project that references the Civil 3D API. Loaded by the core only when Civil 3D is detected. |
| `src/Nexus.Agent.Acad.Plant3D` | per year (see *Program years*) | Plant 3D module. Reaches the Plant 3D API (project, DataLinksManager) by late binding, so it builds without Plant 3D. Loaded by the core only when Plant 3D is detected. |
| `src/Nexus.Agent.Acad.Console` | per year (see *Program years*) | Runs in AutoCAD's Core Console (`accoreconsole.exe`) for Print & PDF: PDFs of a drawing's layouts (`NEXUSJOB`). References only `accoremgd`/`acdbmgd` (the Core Console cannot load anything that uses AutoCAD's UI libraries). |
| `src/Nexus.Hub.Core` | net10.0 | UI-free hub logic: discovery, pipe client, result flattening, CSV/JSON export. |
| `src/Nexus.Hub` | net10.0-windows10.0.19041 (WPF) | The hub application (`Nexus.exe`). |
| `src/Nexus.Cli` | net10.0 | `nexus` command-line client, for testing agents without the GUI. |
| `tests/Nexus.Tests` | net10.0 | xUnit tests (no Autodesk product needed): pipe round trip with a fake host, threading, errors, busy-host handling, flattening, CSV. |

Build settings: `Directory.Build.props` (common), `build/HostVersions.props` (host version
matrix), `build/Revit.targets` and `build/AutoCad.targets` (API references and deployment).
Deployment templates are in `deploy/`.

## Prerequisites

- Windows, Visual Studio 2026 (includes the .NET 10 SDK; it builds the .NET Framework 4.8 and .NET 8 add-ins too).
- Any of Revit 2024-2027 and/or AutoCAD / Civil 3D / Plant 3D 2024-2027 installed in the default folders
  (`C:\Program Files\Autodesk\Revit 20xx\`, `C:\Program Files\Autodesk\AutoCAD 20xx\`). Years that are
  not installed still build, against Autodesk's reference packages.

Autodesk API DLLs are referenced from those install folders with `Private=false`, so they
are never copied or redistributed. If a product is **not** installed on the build machine (e.g. CI),
the build falls back to compile-only reference packages (`Nice3point.Revit.Api.*`,
`AutoCAD.NET`, `Civil3D.NET`) with `ExcludeAssets=runtime`. These are never copied either. The build
prints which one it used. To point at a different install folder, pass
`/p:RevitInstallDir=...\` or `/p:AutoCadInstallDir=...\`.

## Program years

The hub is one program for every year. The add-ins are built once per program year:

| Year | Runtime | Revit | AutoCAD / Civil 3D / Plant 3D |
|---|---|---|---|
| 2024 | .NET Framework 4.8 | 2024 | 2024 (R24.3) |
| 2025 | .NET 8 | 2025 | 2025 (R25.0) |
| 2026 | .NET 10 | 2026 (API 26.5+) | 2026 (R25.1) |
| 2027 | .NET 10 | 2027 | 2027 (R26.0) |

- **Visual Studio** builds 2026 (the default). To build another year from Visual Studio, pick the solution
  configuration `Debug.2024`, `Debug.2025` or `Debug.2027` (add them in Configuration Manager if missing), or use
  the script below.
- **`build\Build-AllYears.cmd`** builds every year, installs each year's add-in on this PC when that program year
  is installed here, and collects them all in `artifacts\addins\` with **`Install-Addins.cmd`**. Copy that folder
  to another PC and double-click `Install-Addins.cmd`: it installs Revit 2024-2027 and the AutoCAD / Civil 3D /
  Plant 3D bundle for whatever years that PC has. (Install the hub there with `Install-Hub.cmd` or the
  `artifacts\Nexus` folder.)
- Each add-in project builds into `bin\{year}\` and `obj\{year}\`, so years do not overwrite each other.
- Year-specific code uses the symbols `HOST2024`... and `HOST2025_OR_GREATER`... (`build/HostVersions.props`).
  .NET Framework builds get `src/Common/NetFrameworkPolyfills.cs` (newer library methods), PolySharp (newer C#
  features) and System.Text.Json, which ships next to the 2024 add-ins; a resolver loads those copies if the
  program has other versions loaded.
- The GitHub check builds every year's add-ins on every push.
- Adding a year (2028...): add a row to `build/HostVersions.props` (runtime, API package versions, AutoCAD
  R-number), a `<Components>` block to `deploy/autocad/PackageContents.xml`, and the year to the scripts' lists.

## Build and deploy

Close Revit, AutoCAD and Civil 3D first: they lock the DLLs. (If one is open, the build
still succeeds but prints copy warnings and the old version stays deployed.)

Visual Studio: open `Nexus.sln`, choose **Debug**, **Build > Build Solution**.

Command line:

```powershell
dotnet build Nexus.sln -c Debug
dotnet test tests\Nexus.Tests
```

On Windows, every build installs the add-ins automatically for the year being built, if that program year is
installed on this PC (`{year}` below):

| What | Where |
|---|---|
| Revit manifest | `%APPDATA%\Autodesk\Revit\Addins\{year}\Nexus.addin` |
| Revit add-in files | `%APPDATA%\Autodesk\Revit\Addins\{year}\Nexus\` |
| AutoCAD bundle manifest | `%APPDATA%\Autodesk\ApplicationPlugins\Nexus.bundle\PackageContents.xml` |
| AutoCAD core + Civil 3D and Plant 3D modules | `%APPDATA%\Autodesk\ApplicationPlugins\Nexus.bundle\Contents\{year}\` (one bundle, one folder per year) |
| Hub | not deployed by the build: it is a separate program, installed with `build\Publish-Hub.cmd install` (see *Hub*) into `%LOCALAPPDATA%\Nexus\Hub\Nexus.exe` |

Add `/p:DeployToHost=false` to build without deploying. `dotnet clean` removes the deployed files.

Runtime files (all under `%LOCALAPPDATA%\Nexus\`):

| Path | Contents |
|---|---|
| `agents\{pid}.json` | One registration per running agent (how the hub finds them). |
| `logs\revit-*.log`, `logs\acad-*.log`, `logs\hub-*.log` | Logs. Every error is logged here with a stack trace. |
| `exports\` | Default export folder. |
| `Hub\Nexus.exe`, `hub-settings.json` | The installed hub and its Start with Windows choice. |
| `acad-probe-*.txt` | AutoCAD crash guard for the generic property reader (see below). |

## Revit 2026

**Load**
1. Build (see above), then start Revit 2026.
2. Revit asks about the unsigned add-in "Nexus Agent": choose **Always Load**.
3. A **Nexus** ribbon tab appears with a **Hub Link** panel. The **Hub** button shows the connection state:
   - grey **Hub: Starting** until Revit is idle,
   - blue **Hub: Waiting** while listening for the hub,
   - green **Hub: Connected** while the hub is connected,
   - red **Hub: Error**.
   Click it for details (pipe name, requests handled, last error), **Show the Nexus hub**, or the log folder.

**Readers**
- `revit.sheets`: one item per sheet (placeholders optional).
  - Every sheet parameter (built-in, project and shared), grouped like the Properties palette (`Sheet · Graphics`, `Sheet · Text`, `Sheet · Identity Data`, `Sheet · Other`…), in palette order, including read-only ones. Parameters the palette does not show are in `Sheet · Not in Properties`.
  - `Revisions on Sheet` (in Identity Data, where the palette has its Edit... button): the revisions shown on the sheet. Change them with right-click › Revisions on sheet in the hub.
  - The hub's Sheets view opens on the palette's parameters for Revit sheets (with AutoCAD layouts in the view, also the standard Number/Title/Revision columns and the title block attributes). Everything else is under **Columns**.
  - Title block instance parameters (`Title Block (Instance) · …`) and type parameters (`Title Block (Type) · …`). If a sheet has more than one title block, each is listed separately and a warning is added.
  - `Current Revision`: number on the sheet, date, description, sequence, issued, issued by/to, plus the sheet's revision history.
  - `Element`: element id, unique id, workset, and whether the element can be edited.
- `revit.projectinfo`: every Project Information parameter.
- `revit.revisions`: every revision (Sheet Issues/Revisions) in sequence order: sequence, revision number, numbering
  sequence, issued, all revision parameters (date, description, issued to/by, visibility…; editable until issued),
  and the sheets it is on (and on which it comes from revision clouds).
- Revisions on sheets: `revit.sheets` has one **Yes/No** column per project revision (*Revisions on Sheet ›
  Seq. 3 - Revision 3*), like Revit's *Revisions on Sheet* dialog. Changing it adds or removes the revision on the
  sheet. A revision placed by revision clouds on the sheet is locked, as in Revit.
- `revit.fabrication.parts`: every MEP Fabrication part (see *Fabrication* below).
- `revit.fabrication.database`: the fabrication configuration loaded in the model (see *Fabrication* below).
- Placeholders (registered, return `NotImplemented`): `revit.elements`, `revit.schedules`, `revit.views`, `revit.rooms`, `revit.mepsystems`.

Every parameter carries:
- source (BuiltIn / Project / Shared / Family) and id (BuiltInParameter name, shared GUID, or parameter element id),
- storage type, spec (data type) and display units,
- display value and raw value (internal units),
- read-only flag and reason: read-only parameter, not user-modifiable, document read-only or linked, element borrowed by another user, changed in central, or workset owned by someone else.

**Editing.** The agent accepts edits from the hub (see *Hub › Editing*). All edits to one
document go into one transaction, `Nexus: edit N values`, so one **Undo** in Revit reverts the
batch. Per value:
- text: set as typed; whole numbers; Yes/No accepts yes/no, true/false, 1/0;
- numbers with units (lengths, areas, angles…): typed in project units, e.g. `10' 6"` or `3200`;
- values that refer to other elements (materials, types, levels…): not editable yet.

A value is skipped when the element is not editable (see the read-only reasons above) or when it
has changed in Revit since it was read. If Revit reports an error when committing, the whole batch
is rolled back. Revit warnings (e.g. a duplicate mark) are passed back to the hub instead of
showing a dialog. Editing a title block **type** parameter changes every sheet that uses that type.

**Fabrication (bridge to the Fabrication CADmep database).** Revit's MEP Fabrication parts come from a
fabrication configuration: the database (services, item files, materials, specifications, custom data…) that is
authored in Fabrication CADmep/ESTmep. The **Fabrication** panel on the Nexus tab connects the two:
- **Fabrication Database**: browse what the model's configuration contains (configuration name, version, location,
  profile; services with loaded/used state, palettes and buttons; materials, specifications, insulation
  specifications, custom data fields, part statuses, ancillaries, connectors, dampers), with filtering, and the
  actions below.
- **Reload**: reload the configuration from the database on disk, so changes made in CADmep reach the model
  (reports out-of-date parts, custom data changes and disconnections). One undo step.
- **Export MAJ**: save the selected fabrication parts (or every part in the active view) as a MAJ job that
  CADmep, ESTmep and CAMduct open.
- **Parts in Nexus**: open the hub on the *Fabrication parts* view.

`revit.fabrication.parts` reads each part's fabrication data: item number, notes, spool name, part status,
alias, item custom id, part type, service (name, abbreviation, type), specification, material, gauge, insulation
specification, insulation/lining/double wall, product list entry and product data (code, name, descriptions, finish,
install type, manufacturer, vendor), size, overall size, centerline length, weight, sheet metal area, and every
custom data field of the database. Options: filter by service, include the Revit parameters.

Editable from the hub (one transaction, one undo): **item number, notes, spool name, part status** (by its
description), **specification, material, insulation specification** (by name or `Group: Name`; Revit refuses
values that are not valid for the part) and **custom data** (text, or numbers parsed by the configuration's rules).

**Threading.** Pipe requests never touch the Revit API. They are queued and run by an
`IExternalEventHandler` on Revit's main thread. If Revit cannot run them within 30 s
(for example a modal dialog is open or you are in an edit mode), the hub gets a `HostBusy`
error instead of waiting forever.

**Debug**
- Set `Nexus.Agent.Revit` as the startup project and press F5. The "Revit 2026" launch profile starts Revit.
- Or attach to a running Revit: **Debug > Attach to Process…**, pick `Revit.exe`, code type **Managed (.NET Core, .NET 5+)**.

### Bulk Rename (Revit)

**Nexus tab › Tools › Bulk Rename** renames many things at once, like Bulk Rename Utility, inside Revit (2024–2027):

- **What**: views, sheet numbers, sheet names, schedules, view templates, levels, grids, scope boxes, named
  reference planes, room/space/area names and numbers, families, family types, system types (walls, floors,
  text, dimensions...), group types, materials, view filters, line and fill patterns, phases, worksets.
  Starts with what is selected in Revit (*Rename › Selected in Revit*), or views.
- **Panels**, applied in order: 1 RegEx · 2 Name (keep, start empty, fixed text, reverse) · 3 Replace (several
  pairs with `|`, match case, first only, whole word) · 4 Case (UPPER, lower, Title, Sentence, with words kept as
  typed) · 5 Remove (first/last, from–to, characters, words, crop before/after, digits, symbols, accents, spaces)
  · 6 Add (prefix, insert at, suffix, space between words) · 7 Numbering (start, step, digits, separator, letters,
  restart per value such as `{Level}`) · 8 Filter (wildcards or RegEx, not matching).
- **Revit values** (*Insert Revit value*): `{Level}`, `{Sheet Number}`, `{View Type}`, `{Family}`, `{Type}`,
  `{Scale}`... or any parameter name, in Name, Replace › With, Add and Numbering › Restart. Each item gets its own
  value: Add › Prefix `{Level} - ` turns "Floor Plan" into "Level 1 - Floor Plan".
- **Preview**: every new name and what will happen. Names Revit would refuse are flagged before anything changes:
  characters Revit does not allow, empty names, names already used (per view type, level, family...), and
  elements owned by other users. Numbering follows the list order: click a column to sort.
- **Rename**: one undo step (Ctrl+Z puts every name back). Swaps and shifts (A101→A102, A102→A103) work: names
  that are moving go through a temporary name first.
- **Presets**: type a name and Save; Load or Delete later (`%APPDATA%\Nexus\rename-presets`). The last settings
  are kept for next time.

## AutoCAD 2026 / Civil 3D 2026

**Load**
1. Build, then start AutoCAD 2026 or Civil 3D 2026.
2. The bundle in `%APPDATA%\Autodesk\ApplicationPlugins` loads automatically.
   - AutoCAD asks about loading `Nexus.Agent.Acad.dll` from a non-trusted location: choose **Always Load**.
   - Or add `%APPDATA%\Autodesk\ApplicationPlugins\Nexus.bundle\...` to `TRUSTEDPATHS`.
3. A **Nexus** ribbon tab appears with a **Hub Link** panel. Its **Hub** button shows the connection
   state with the same colors as in Revit. Click it (or type `NEXUS`) for details and **Show the Nexus hub**.
   The tab comes back after a workspace switch. `NEXUSSTATUS` prints the same details on the command line.

In Civil 3D the status shows `Modules: Civil3D`, and the hub lists the host as **Civil 3D 2026**.
In Plant 3D it shows `Modules: Plant3D`, and the hub lists the host as **Plant 3D 2026**.

The core detects Civil 3D after startup, on the first idle. It looks for any of:
- `/product C3D` on the command line,
- a loaded `AeccDbMgd` assembly,
- a loaded `aecc*` ObjectARX module.

Plant 3D is detected the same way: `/product PLNT3D`, a loaded `PnPProjectManagerMgd`/`PnP3dObjectsMgd`/`PnPDataLinks`
assembly, or a loaded `PnP*` module.

The detection rules are in `modules.json` next to the core DLL.

**Readers**
- `acad.sheets`: one row per layout (the sheet index): layout name (editable; renames the layout), page setup,
  plotter, paper size, plot scale, viewport scales, every attribute of the layout's title block (editable), and the
  drawing properties (DWGPROPS: title, subject, author, keywords, comments, custom properties; editable). The title
  block is the attributed block whose name looks like one (TITLE, TB, BORDER, SHEET…), else the one with the most
  attributes; set exact names with the `titleBlockNames` option (wildcards allowed).
- `acad.layouts`: one item per layout, in tab order, plus a `Drawing` item.
  - Drawing item: drawing settings and, for the active drawing, the system variables the palette shows with nothing selected. These cover current layer, color, linetype, lineweight, annotation scale, UCS, plot style, view center/size and similar settings.
  - Each layout: page setup, plotter, paper size, plot style table, plot settings including shade plot and DPI, all layout COM/.NET properties, and the paper space view (view center/height/width, visual style, UCS settings, annotation scale).
  - Children: every paper space object with all its properties, grouped by the palette's own categories (General, Geometry, Text, Misc, …). This covers title blocks, text/mtext, tables, dimensions, viewports and Civil 3D labels and tables.
    - Block references also get **Attributes** (tag = value, constant ones read-only) and **Custom** (dynamic block properties).
    - Viewports get scale and VP-frozen layers.
    - Tables get their cell text.
  - Option `modelThroughViewports` (off by default) adds, under each plan viewport, the model space objects whose extents fall inside that viewport's view. Objects on frozen, off and VP-frozen layers are skipped. Limits: `maxModelObjectsPerViewport`, `maxPaperSpaceObjects`.
- `civil3d.objects` (Civil 3D only):
  - alignments, with their profiles as children,
  - surfaces, with statistics from `GetGeneralProperties`/`GetTinProperties`/…,
  - pipe networks, with pipes and structures as children,
  - corridors.

- `plant.objects` (Plant 3D only): every Plant 3D object in model space (pipes, fittings, valves, equipment,
  instruments, supports; P&ID symbols and lines) with its project data, the properties Plant 3D shows in the
  Properties palette: tag, line number, size, spec, service, descriptions, and every custom property of the
  project. Gaskets, bolt sets and welds are left out unless the `connections` option is on; `drawingProperties`
  adds the AutoCAD properties (layer, colour…). Values are edited through the project's DataLinksManager, the
  same store Plant 3D's Data Manager edits; when Plant 3D keeps the old value (calculated properties, tags built
  from their parts) the hub says so. `PnP*` system properties are read-only. The drawing must be opened from the
  Plant 3D Project Manager.
- `plant.project` (Plant 3D only): the open project's settings, its parts (Piping, P&ID, Ortho, Iso) and every
  drawing registered in each part.

**How the generic property reader works** (`src/Nexus.Agent.Acad/PropertyEngine`)
1. **COM/ActiveX properties.** It enumerates the object's `ITypeInfo`, the same properties the Properties palette shows. Each property is put in the palette's own category by asking the object's `ICategorizeProperties` interface.
2. **.NET API properties.** It reflects the managed object's public properties. Any the COM layer did not already show go into `.NET · <Type>` groups.
3. **Fallback categories.** Properties without a category use `property-categories.json`, which you can edit.
4. **Read-only flags.** Each property is marked read-only if it has no setter, if its layer is locked, or if the drawing is read-only.

Properties that other plug-ins add to the palette through native code are not reachable from .NET.

**Crash guard.** A few native-backed getters can crash a host in a way .NET cannot catch.
The first time each property is read, its name is written to `acad-probe-pending.txt`.
If AutoCAD dies during a read, the next start moves that property to
`acad-probe-denied.txt` and never reads it again. The first read of a new object type is
slightly slower because of this; later reads are not.

**Editing.** The agent accepts edits from the hub (see *Hub › Editing*):
- block attributes (constant ones excepted), table cells, dynamic block properties (checked against the allowed values);
- Properties palette (COM) properties and .NET API properties with a setter: text, numbers, Yes/No, choices (enums),
  points as `x, y, z`, and colors (`ByLayer`, `ByBlock`, `1`-`255`, `Red`…, or `R,G,B`);
- drawing settings, and system variables of the active drawing.

Values that refer to other objects (layer/style ids, COM objects) are not editable yet; the `Layer`
property (text) is. Objects on locked layers (title blocks usually are) can be edited: the layer is unlocked
for the change and locked again. A value is skipped when the drawing is read-only or it changed in the
drawing since it was read. Edits run with the document locked from the application context, so they are
recorded in AutoCAD's undo history (`U`).

**Threading.** Requests are marshaled to AutoCAD's main thread and run only in the
application context, never while a command is running. If a command stays active for more
than 30 s the hub gets `HostBusy`. Reads lock the document and use read-only transactions.
The agent handles "no active document" and reads non-active drawings too.

**Debug**
- Set `Nexus.Agent.Acad` as the startup project. Pick the launch profile "AutoCAD 2026" or "Civil 3D 2026 (Imperial)" and press F5.
- Or attach to a running `acad.exe` (code type **Managed (.NET Core, .NET 5+)**).
- To debug the Civil 3D module, attach to or launch Civil 3D. Its PDB is deployed next to it.

## Hub

The hub is a separate program from the add-ins. It runs in the background like Autodesk Access:
one per Windows user, an **N** icon in the notification area (on Windows 11 new icons start in the
hidden area behind **^** next to the clock; drag it onto the taskbar to keep it visible), and it
starts when you sign in to Windows.

- **Easiest:** double-click **`Install-Hub.cmd`** in the repository folder (`C:\Users\<you>\source\repos\Nexus`).
  It builds the hub and installs or updates it; run it again after pulling hub changes.
- **Build it.** `build\Publish-Hub.cmd` makes one self-contained `artifacts\Nexus\Nexus.exe` (about 75 MB)
  plus `Install.cmd`. It runs on any 64-bit Windows 10/11 PC: no .NET, Visual Studio or Autodesk product needed.
  `build\Publish-Hub.cmd install` also installs it on this PC. (Visual Studio: right-click `Nexus.Hub` →
  **Publish** → *Standalone* does the same build.)
- **Install it.** Run `Install.cmd` (or `Nexus.exe --install`). No administrator rights: it copies
  `Nexus.exe` to `%LOCALAPPDATA%\Nexus\Hub`, adds **Nexus** to the Start Menu and to **Settings → Apps**, turns on
  Start with Windows, and opens it. Running it again upgrades in place (the running hub is stopped first).
- **Uninstall.** **Settings → Apps → Nexus → Uninstall** (or `Nexus.exe --uninstall`). Logs and settings in
  `%LOCALAPPDATA%\Nexus` are kept. The add-ins are separate (see *Build and deploy*).
- **Open it.** Click the tray icon, the Start Menu entry, or **Hub → Show the Nexus hub** in Revit/AutoCAD/Civil 3D.
- **Close vs exit.** Closing the window hides it; Nexus keeps running. Right-click the tray icon →
  **Exit Nexus** to stop it.
- **Start with Windows.** On by default; toggle it in the tray menu (a per-user `Run` entry that starts
  `%LOCALAPPDATA%\Nexus\Hub\Nexus.exe --background`).
- **Programs.** Revit, AutoCAD and Civil 3D sessions are picked up within a few seconds of starting or closing,
  even while the window is hidden. **Reload** (F5) also re-lists the open files.
- **Debugging.** Building the solution does not touch the installed hub. F5 on `Nexus.Hub` uses the
  `--replace` launch profile: the installed hub exits and the debugger's copy takes over until you stop
  debugging. Set `InstallHubOnBuild=true` to reinstall the hub on every build instead.
  Switches: `--background`, `--install`, `--uninstall`, `--shutdown`, `--replace`.

### Using the hub

```
┌ Nexus  Show [Sheets ▾] [Options] [Reload]   Search…        [Show in model] [Columns…] [Excel ▾] [⋯] ┐
│ CONNECTED            │ File        Number  Title         Revision  Drawn By …  │ DETAILS              │
│ Revit 2026           │ Tower.rvt   A-101   Floor Plan    2         WS          │ A-101                │
│  ☑ Tower.rvt active  │ Tower.rvt   A-102   Sections      1         WS          │ Sheet · Identity …   │
│ Civil 3D 2026        │ C-Grade.dwg C-201   GRADING PLAN            JD          │  Sheet Name [ … ]    │
│  ☑ C-Grade.dwg       │                                                         │ Title Block …        │
│ EXCEL  Index.xlsx    │                                                         │                      │
│  [Compare…]          │                                                         │                      │
└ 3 files · 48 rows · 1 warning        status…               [ 2 changes not yet applied  Review & apply… ] ┘
```

- **Connected** (left). Every running Revit, AutoCAD and Civil 3D with its open files. The active file of each
  program is ticked; tick the files you want in the grid. Changes reload automatically.
- **Show** (top). What to look at:
  - **Sheets** (all programs): one row per Revit sheet and per AutoCAD/Civil 3D layout, with the standard columns
    **Number, Title, Revision, Revision Date, Issue Date, Drawn/Checked/Designed/Approved By, Scale, Project Number**.
    In Revit they come from the sheet parameters; in AutoCAD from the title block attributes (`DWGNO`, `TITLE`,
    `REV`… with many common spellings). Add your own tags in `%LOCALAPPDATA%\Nexus\sheet-fields.json`
    (**⋯ → Sheet field matching**). Every other sheet, title block and drawing property is available too.
  - Per program: Revit *Project Information*, AutoCAD *Layouts (all objects)*, Civil 3D *objects*, …
  - **Options** changes what a reader includes (title block names, hidden parameters, …).
- **Grid** (center). Works like a spreadsheet:
  - double-click or type to edit; edited cells turn yellow (tooltip: old value); grey cells are locked (tooltip: why);
  - select several cells: **Ctrl+V** pastes a value or a block copied from Excel, **Ctrl+D** fills down,
    **F2** sets one value for all, **Ctrl+H** finds and replaces, **Delete** clears, **Ctrl+Z** reverts;
  - click a header to sort; **Ctrl+F** searches all visible columns; **Columns…** picks the columns (remembered per view).
- **Details** (right). Every property of the selected row, grouped, editable in place.
- **Revisions on sheets** (right-click, Revit sheets). Right-click a sheet, or select several (Shift/Ctrl+click) and
  right-click them: the menu lists every revision, ticked when it is on all of them and with a dash when it is on
  some. Click a revision to add it to (or, if all have it, remove it from) those sheets; the menu stays open so you
  can change several, plus *Show all* / *Remove all*. Revisions placed by revision clouds stay, as in Revit. Or select
  Yes/No cells and press **Space**. Changes are pending until *Review & apply*, like any edit.
- **Show in model** (toolbar, details or right-click). Revit opens the sheet (or selects and zooms to elements);
  AutoCAD/Civil 3D switches to the drawing and layout, selects the objects and zooms to them.
- **Changes bar** (bottom). **Review & apply…** lists every change (old → new), writes them, and shows each outcome:
  *Applied*, *Unchanged*, *Skipped* or *Failed*, with the reason. The grid then reloads from the model.
  **Discard** puts everything back. A sheet field and the parameter/attribute behind it change together.

### Sheet health

In the **Sheets** view, **Health** checks every sheet of every open Revit model and drawing, and re-checks as
you edit:

| Check | Severity | One-click fix |
|---|---|---|
| Duplicate sheet numbers (across all open files and programs) | Error | – |
| Sheet without a number | Error | – |
| Template placeholders never filled in (Drawn By = *Author*, Checked By = *Checker*, Designed By = *Designer*, Approved By = *Approver*…) | Warning | – |
| Sheet without a name | Warning | – |
| Project number differs from the other sheets of the drawing (AutoCAD title blocks) | Warning | the usual number |
| Revision without a date | Warning | – |
| Sheet number does not follow the pattern most sheets use (A-104 among A101…A105) | Suggestion | A104 (if not taken) |
| Sheet name not in capitals when most are | Suggestion | UPPERCASE |
| Extra spaces | Suggestion | trimmed |

The first column of the table shows each sheet's state (✓, or the worst finding; hover for the list; click to
open Health). Click a finding to jump to that cell. **Fix** and **Fix all** stage values like your own edits:
nothing changes in the files until **Review & apply**. The checks are in `Nexus.Hub.Core/Health/SheetHealth.cs`.

### Rename & Renumber

**Rename** (command bar, right-click, Ctrl+R) works on the selected rows, or every row shown, in the order shown.
Pick **Sheet Number** or **Sheet Name** (the Revit parameter or the AutoCAD title block attribute) and how to change it:

- **Renumber** from a pattern: `#` is the counter, padded to the number of `#`s (`A1##` → A101, A102…), with a
  start and a step. No `#` adds the number at the end.
- **Replace**, with `*` (any text) and `?` (one character), match case, whole value.
- **Capitals**: UPPER, lower, Title Case.
- **Add** or **Remove** text at the start or end.

Every result shows before anything changes, with a status: will change, unchanged, clash (sheet numbers and layout
names must stay unique within a file; swaps are fine), locked. **Add N changes** puts them in your pending changes.
In Revit, several sheet numbers set at once go through temporary numbers first, so swaps and shifted ranges work;
if one sheet cannot take its number the whole batch is rolled back.

### Review & apply

Pending changes are grouped by file and sheet: property, old value (struck through) → new value. Every change,
sheet and file has a checkbox. **Apply N changes** sends the ticked ones; each shows its outcome in place. Unticked
changes stay pending. The window closes by itself when everything applied.

### History

**History** keeps snapshots of the sheet set in `%LOCALAPPDATA%\Nexus\snapshots`: take one by hand (e.g.
"50% CD issue"); one is also saved automatically before every apply (the last 40 are kept). Pick a snapshot to see
what changed since: new, removed and changed sheets with each value's before → after (a renumbered sheet shows as
changed). Export the comparison to Excel. The **Change log** tab lists every change Nexus applied (who, when, file,
sheet, property, before, after), from `%LOCALAPPDATA%\Nexus\change-log.jsonl`.

### Go to anything (Ctrl+K)

Type a sheet number or title (from every open file), an action or a view; Enter runs it. On a sheet, Enter selects
it in the table and Shift+Enter also shows it in its program.

### Progress and messages

Reading and applying show a progress bar with **Cancel** under the table. Cancelling a read keeps the table as it
was; cancelling an apply stops before the next file (the file being written finishes, one undo step each).
Problems and results appear in an info bar above the table instead of pop-ups; questions (discard changes, write
to Excel) use the hub's own dialogs.

### Print & PDF

**Print & PDF** on the command bar (Sheets view) works on the selected sheets, or every sheet shown:

- **Create PDFs**: made by the programs themselves — Revit's PDF export of the open model (each sheet at its
  title block size, unsaved changes included) and, for drawings, AutoCAD's *DWG To PDF* with each layout's own
  page setup (plot area, scale, plot style; same paper size, or the closest). Drawings are plotted by AutoCAD's
  Core Console from a copy of the saved drawing, so AutoCAD keeps working meanwhile. Name files with
  `{Number}`, `{Name}`, `{File}`, and optionally combine everything into one PDF. A combined set is ready for
  Bluebeam Revu: each sheet's page is labelled with its sheet number and bookmarked "A101 - Floor Plan", so
  page navigation, hyperlinks and Batch Slip Sheet work by sheet number.
- **Preview**: each sheet is shown in the window (Windows' own PDF renderer; no PDF program needed). Click it to
  open the PDF.
- **Print…**: any printer; each sheet is turned to fit and scaled to the paper picked in the print dialog
  (half-size sets, check prints), or printed on its own paper size for plotters. Printing sends the sheet as a
  300 dpi image; for full vector plots send the PDFs from your plotter software.
- If AutoCAD asks whether to load the Nexus add-in (SECURELOAD), add the `Nexus.bundle\Contents\<year>` folder to
  *Options › Files › Trusted Locations*.

### Markups (Bluebeam Revu)

**Markups** on the command bar reads the markups from PDFs reviewed in Bluebeam Revu (Revu keeps them in the PDF
as standard annotations; the PDFs are only read):

- **Add PDFs…**: a set or single sheets. Each page is matched to its sheet by page label, else by bookmark
  ("A101 - Floor Plan"), else, for a one-sheet PDF, by its file name (`A101 - Floor Plan.pdf`). Sets made with
  Print & PDF have labels and bookmarks.
- Markups are grouped by sheet, with the sheet name from the loaded models: status, page, type (cloud, callout,
  text box, pen…), subject, comment, author, date and replies (hover to read them). *Show* filters open, closed
  or on sheets not in the model; the search box looks through comments, authors and replies.
- **Status**: the review status set in Revu (Accepted, Completed, Rejected, Cancelled… or a custom status). A
  markup without one, or with a status that doesn't close it, is *Open*.
- When Revu saves the PDF, the list reads it again by itself.
- **Show sheet in model** (or double-click) opens the sheet in Revit or AutoCAD; **Open PDF** opens it in your
  PDF program; **Export to Excel…** saves the markups shown.

### AI assistants in AutoCAD (MCP)

Claude (Desktop or Code), or any other MCP client, can work in AutoCAD, Civil 3D and Plant 3D through Nexus, like the
pyRevit MCP does in Revit. `nexus-mcp.exe` is a small MCP server (stdio) that the assistant starts; it talks to
the Nexus add-in in each running AutoCAD over the same pipe as the hub. Nothing is opened to the network.

**Set up** — easiest: close AutoCAD/Revit and double-click **`Setup-Claude.cmd`** in the repository folder. It
pulls the latest code, builds and installs the add-ins and the hub (with `nexus-mcp.exe`), adds `nexus` to Claude
Desktop (every config it may read; your other servers are kept, with a backup) and to Claude Code, and offers to
restart Claude Desktop. By hand:
1. Install the hub with `Install-Hub.cmd` (it builds and installs `nexus-mcp.exe` next to `Nexus.exe` in
   `%LOCALAPPDATA%\Nexus\Hub`). Build the AutoCAD add-in as usual (AutoCAD closed).
2. In the hub: **⋯ › Connect Claude (AI assistants)…** adds `nexus` to Claude Desktop's
   `claude_desktop_config.json`. Your other servers (pyRevit…) are kept, and a backup is saved next to it. It
   also puts the Claude Code command on the clipboard:
   `claude mcp add nexus --scope user -- "%LOCALAPPDATA%\Nexus\Hub\nexus-mcp.exe"`.
3. Restart Claude Desktop, open AutoCAD with a drawing, and ask away.

By hand, Claude Desktop's config entry is:
```json
{ "mcpServers": { "nexus": { "command": "C:\\Users\\<you>\\AppData\\Local\\Nexus\\Hub\\nexus-mcp.exe" } } }
```

**Tools** (every one works on the active drawing, or `document`: a drawing's title; `session`: the process id
when AutoCAD is open twice):

| Tool | What it does |
|---|---|
| `nexus_status` | Programs running with Nexus and their open drawings |
| `autocad_drawing_info` | File, units, extents, layouts, entity counts by type, layers, blocks, xrefs |
| `autocad_list_layers` / `autocad_list_blocks` | Layers (colour, linetype, states, usage); block definitions (attributes, dynamic, xref, references) |
| `autocad_query_entities` | Find entities by type, layer, block, text, area; paged, compact geometry |
| `autocad_get_entities` | Full details by handle: vertices, bulges, attributes, dynamic properties, styles |
| `autocad_get_selection` | What the user has selected in AutoCAD |
| `autocad_view_image` | A picture of the drawing area, so the assistant can check its work |
| `autocad_create_entities` | Draw lines, polylines, rectangles, circles, arcs, points, text, mtext, blocks (with attributes), aligned dimensions |
| `autocad_modify_entities` | Layer, colour, linetype, move, rotate, scale, text, attribute values |
| `autocad_delete_entities` | Erase by handle (locked layers are skipped) |
| `autocad_set_layer` | Create or change a layer; make it current |
| `autocad_zoom` | Frame handles, an area or the extents |
| `autocad_run_command` | Type a command or AutoLISP on the command line (for anything the tools don't cover) |

**Civil 3D** adds (when Civil 3D is running):

| Tool | What it does |
|---|---|
| `autocad_civil3d_overview` | Alignments, profiles, surfaces, pipe networks, corridors, sites, point groups, COGO point count |
| `autocad_civil3d_alignments` | Alignments (type, length, stations, site, style) and profiles (range, min/max elevation; elevation and grade at a station) |
| `autocad_civil3d_station_offset` | Points → station/offset along an alignment, or stations (+offsets) → points |
| `autocad_civil3d_surfaces` / `autocad_civil3d_surface_elevation` | Surface statistics (points, elevations, extents, 2D/3D area, slopes); elevation at many points |
| `autocad_civil3d_pipe_networks` | Pipes (size, length, slope, cover, structures) and structures (rim, sump, position) |
| `autocad_civil3d_points` / `autocad_civil3d_add_points` | COGO points by group, description or number (paged); add points with descriptions |
| `autocad_civil3d_corridors` | Corridors, baselines (alignment, profile, stations), out of date |

**Plant 3D** adds (with a project open):

| Tool | What it does |
|---|---|
| `autocad_plant3d_project` | The project, its parts (Piping, P&ID, Ortho, Iso) and their drawings |
| `autocad_plant3d_objects` | Plant 3D objects with tag, line number, size, spec, service… (by class, tag, line number; all properties on request) |
| `autocad_plant3d_set_properties` | Change Plant 3D data (as in the Properties palette) |

When AutoCAD, Civil 3D and Plant 3D are open together, each tool goes to the program that has it.

Each drawing or change is one undo step in AutoCAD (U). Coordinates are drawing units (WCS), angles degrees.
The assistant marks read-only tools as safe; `delete_entities` and `run_command` are flagged destructive, so
clients ask before using them. Programs can add tools later (Revit, Civil 3D…) without changing the server: it
lists whatever each running add-in offers.

### Deliverables list and Model Export (Excel)

The workbook (e.g. the Budget Tool) is the **source of truth**. Nexus reads its deliverables list and writes only
one place in it: the **Model Export** tab.

1. **Choose…** on the Excel card (bottom left) picks the workbook. Use it again to switch to another workbook.
2. **Compare** opens the deliverables list next to the model, lined up by drawing number:
   - the list's Title, Rev, Date and Status sit beside the model's Sheet Name, Rev, Rev Date and Status, with
     the program and file each sheet comes from. Values that differ are highlighted on both sides;
   - each drawing is *Match*, *Differs*, *Not in model* or *Not on list* (a model sheet missing from the list).
     *Show* filters them.
3. **Match model to list**: select drawings in the window (click, Ctrl/Shift+click, Ctrl+A), or select them in
   Excel and click **Excel selection**. Excel selection takes rows on the Deliverables List, or any cells
   holding drawing numbers. Then click **Match model to list**:
   - **Not in model**: each becomes a new sheet in the Revit model chosen under *New sheets go in*. It gets the
     number, name and status from the list, and the title block the model's sheets use most. Nexus asks first.
     It is one undo step in Revit;
   - **Differs**: the list's values go into the main table (yellow), to write with *Review & apply*. In Revit,
     Rev and its date come from the sheet's revisions, so Nexus says when it can't set them. An empty cell on
     the list never clears a model value;
   - **Not on list**: left alone. Add the drawing to the list in Excel if it belongs there.

   The list is found by its headers (*Dwg/Doc No.*, *Rev.*, *Date*, *Status*, *Drawing Title…*), on the
   *Deliverables List* tab when there is one. Lists built by formulas read as Excel last saved them. Items
   without a drawing number are not compared.
4. **Export to Model** fills the `ModelExport` table: one row per sheet, sorted by program then sheet number.
   Source is the program, and the other columns are matched by name (Sheet Number, Sheet Name, Rev, Rev Date,
   Status). Formula columns (*On Deliverables List?*) are left to Excel, and the table grows as needed.
   - If the workbook is open in Excel, Nexus writes into Excel itself; save it there. Otherwise the file is
     written after a backup in `%LOCALAPPDATA%\Nexus\excel-backups`, and only those cells change. The edited
     copy is checked against the original (macros, controls, validation, formatting, every other cell) before it
     replaces it.
   - Files that are not open keep their rows, so opening one model doesn't erase the others' sheets.
   - The first export asks before replacing rows Nexus did not put there.
5. Nexus watches the workbook. When it is saved, an open comparison refreshes. Otherwise the info bar says how
   many drawings differ and offers *Compare…*.

**Excel ▾ → Export this view to Excel…** saves the grid as shown (columns, filter and sort) to a new formatted workbook.
**⋯** has the CSV and JSON exports.

### CLI

```powershell
cd src\Nexus.Cli\bin\Debug\net10.0
.\nexus agents
.\nexus docs <pid>
.\nexus readers <pid>
.\nexus read <pid> revit.sheets --csv sheets.csv
.\nexus read <pid> acad.layouts --opt modelThroughViewports=true --long layouts.csv --json layouts.json
```

## Troubleshooting

- **The hub shows no hosts.** Check the host loaded the add-in (Revit and AutoCAD: Nexus tab, or `NEXUSSTATUS`), check that Nexus is running (tray icon), and check `%LOCALAPPDATA%\Nexus\agents\`. The hub and the hosts must run as the same Windows user, and both elevated or both not elevated.
- **`HostBusy`.** Close dialogs, finish the active command or edit mode, then run again.
- **An error in the hub.** The full stack trace is in the host's log (`%LOCALAPPDATA%\Nexus\logs`).
- **A property disappeared from AutoCAD results.** Check `acad-probe-denied.txt`. Delete the line (or the file) to try that property again.

## Extending

**Add a reader.**
1. Implement `IHostDataReader<Document>`: a `ReaderDescriptor` with id, options and description, and a `Read(document, context)` method that adds `DataItem`s.
2. Register it in `RevitReaders.CreateRegistry()` or `AgentExtension.Initialize()`.

The hub, IPC and export code do not change.

**Add an AutoCAD vertical (e.g. Map 3D).**
1. Create `Nexus.Agent.Acad.<Vertical>` like the Civil 3D project and implement `IAcadAgentModule`.
2. Add an entry with detection rules to `modules.json`.

The core never references the vertical's API.

**Add a host version (e.g. 2027).**
1. Add a row to `build/HostVersions.props` (runtime, API package versions, AutoCAD R-number); see *Program years*.
2. Add `Debug.2027;Release.2027` to `Directory.Build.props` and to the solution configurations.
3. Add a `<Components>` block for R26.0 in `deploy/autocad/PackageContents.xml`.

Build with `-c Debug.2027`. Each year deploys to its own folder (`Addins\2027`, `Contents\2027`).

## Security and data

- `samples/` and `*.xlsm` are git-ignored. The budget workbooks contain client financial data and must never be committed.
- Named pipes are restricted to the current Windows user (`PipeOptions.CurrentUserOnly`).
