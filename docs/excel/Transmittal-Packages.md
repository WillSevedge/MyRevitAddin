# Transmittal by package — instructions for Claude in Excel

Paste the prompt below into Claude in Excel with the Budget Tool open. It adds an issue log, a package
builder and a package dropdown on the Transmittal. It does not change any existing formula or value.
Do it on a copy first.

Excel 365 (dynamic arrays) is required.

---

```text
I want to issue drawings by package (30% CD, 60% CD, 90% CD, Review Set, IFC…) and fill the Transmittal
from a package dropdown, without picking sheets one at a time. A drawing can be in several packages over
time, so issues are kept as history (one row per drawing per package), not as a single column.

Rules for every step:
- Do NOT change, move, delete or overwrite any existing cell, formula, format, named range, table or macro.
  Only add new sheets, new tables, new named ranges, and formulas in cells that are empty now.
- The tabs "Deliverables List" and the discipline tabs (PM, Structural, Civil, Mechanical, Process,
  Electrical, Programming, RealityCapture) are formula-driven: never type into them, except the one new
  column in step 4, which must go in an empty column to the right of everything already there.
- Before writing anything, look at each sheet you will touch and tell me exactly which cells you will
  add, then wait for my OK.
- Use the table, column and range names below exactly (another tool reads them).

1) Packages (new sheet "Packages")
   - Create an Excel table named Packages with columns: Package, Order, Description.
   - Rows: 30% CD (1), 60% CD (2), 90% CD (3), Review Set (4), IFC (5). I will add more rows later.

2) Issue log (new sheet "Issue Log")
   - Create an Excel table named IssueLog starting at A1 with columns, in this order:
     Package | Issue Date | Dwg/Doc No. | Rev | Status | Transmittal No. | Notes
   - Package: data validation list =INDIRECT("Packages[Package]").
   - Issue Date: date format (m/d/yyyy). Rev and Dwg/Doc No.: text format (so 001 stays 001).
   - Leave it empty: rows are added in step 3.

3) Package builder (new sheet "Package Builder")
   Inputs (label in column A, value in column B; give each value cell the name in brackets):
   - B2 Package [PB_Package]: data validation list =INDIRECT("Packages[Package]").
   - B3 Issue date [PB_Date]: date, default =TODAY() as a typed value (not a formula).
   - B4 Disciplines [PB_Disciplines]: text, "All" or a comma list such as Civil, Structural.
   - B5 Type [PB_Type]: data validation list All,Drawing,Document; default All.
   - B6 Number contains [PB_NumberLike]: text, optional (e.g. C- or A1).
   - B7 Transmittal No. [PB_Transmittal]: text, optional.
   Source: the "Deliverables List" sheet. Find its header row (the row with "Discipline", "Type",
   "Dwg/Doc No.", "Rev.", "Date", "Status", "Drawing Title / Document Description"); it is row 9 in my
   copy. Data starts on the next row and runs to the last used row (about 2240). Use whole fixed ranges
   down to row 2500 so new rows are included.
   Output at A10 (header labels in A9:H9: Package, Issue Date, Dwg/Doc No., Rev, Status, Transmittal No.,
   Notes, Already logged?): one spilling LET/FILTER formula that returns, for every list row where
     - Dwg/Doc No. is not blank,
     - Discipline is in PB_Disciplines (or PB_Disciplines is "All"; compare trimmed, case-insensitive;
       split the comma list with TEXTSPLIT),
     - Type equals PB_Type (or PB_Type is "All"),
     - Dwg/Doc No. contains PB_NumberLike (or it is blank),
   these columns: PB_Package, PB_Date, Dwg/Doc No., Rev., Status, PB_Transmittal, "" (Notes), and
   "yes"/"" for whether IssueLog already has a row with the same Package and Dwg/Doc No. (COUNTIFS).
   Sort the result by Dwg/Doc No. If nothing matches, show "No drawings match".
   Above it, in A8, a count: ="Drawings in this package: "&ROWS(...) (0 when none).
   Add a short note in D2:D7: "To log the package: copy A10:G (the rows without 'yes' in H), then paste
   values only at the first empty row of IssueLog. Repeat for the next package."

4) Issued In (one new column on "Deliverables List")
   - In the first completely empty column to the right of everything on that sheet, header row cell:
     "Issued In". In the data rows, a formula that lists the packages each drawing was issued in, in
     package order, e.g.
     =IF($C10="","",TEXTJOIN(", ",TRUE,SORTBY(FILTER(IssueLog[Package],IssueLog[Dwg/Doc No.]=$C10,""),
       XLOOKUP(FILTER(IssueLog[Package],IssueLog[Dwg/Doc No.]=$C10,""),Packages[Package],Packages[Order],99))))
     (use the actual Dwg/Doc No. column; fill it down to the last data row).
   - Do not touch any other column of that sheet.

5) Transmittal by package ("Transmittal" sheet)
   - Look at the sheet first: the header area (No:, Date:, To:, From:…) and the attachment table that
     starts at the row with "Attached Are:", "Ea.", "These Are:", "Issued For:", "Please:" (row 20 in my
     copy). Tell me which rows below it are free for the drawing list, and how many.
   - Add a "Package:" label and a dropdown cell named TR_Package in an empty cell of the header area
     (data validation =INDIRECT("Packages[Package]")). Optionally a "Transmittal No." filter cell named
     TR_Number: when it is filled, only IssueLog rows with that Transmittal No. are listed.
   - In the first free row of the attachment table, one spilling formula that lists every IssueLog row
     with Package = TR_Package (and Transmittal No. = TR_Number when that is filled), sorted by
     Dwg/Doc No., laid out in the table's existing columns:
       Ea. = 1, These Are = Dwg/Doc No. & " - " & the drawing title (XLOOKUP of the number in the
       Deliverables List's Dwg/Doc No. column, returning "Drawing Title / Document Description"),
       a Rev column if the table has one (else append "  Rev " & Rev), Issued For = the package.
     Use HSTACK/CHOOSECOLS so each value lands in the right column of the existing layout. If the
     table's columns are not adjacent (merged or spaced cells), use one spilling formula per column
     with the same FILTER so the rows stay aligned.
   - If the table has fewer free rows than the package, let it spill down and tell me so I can extend
     the print area; do not insert rows.
   - Add a count next to the Package dropdown: =ROWS(FILTER(...)) "drawing(s)".

6) Check
   - Pick the first package that has rows in IssueLog (or tell me the log is empty) and show me what the
     Transmittal lists.
   - List every cell you added, per sheet.
```

---

## Using it

1. **Make a package:** on Package Builder pick the package, the issue date and the disciplines (or All).
   The list fills. Copy the rows not marked "yes", then use Paste Special › Values at the first empty row
   of the Issue Log. That logs 700 drawings in one paste.
2. **Transmittal:** pick the package in the Transmittal's Package dropdown. The drawing list fills itself.
3. **History:** the Deliverables List's new "Issued In" column shows every package each drawing was
   issued in (30% CD, IFC…).

Nexus can read the IssueLog and Packages tables by these names: Print & PDF could select a package's
sheets, and the Deliverables window could push a package into the models.
