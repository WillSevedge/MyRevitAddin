using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Excel;

namespace Nexus.Hub.Excel;

/// <summary>
/// A linked workbook that is open in Excel: Excel locks the file, so Nexus writes into the open workbook through
/// Excel itself (found in Windows' running object table, like Excel's GetObject). The values appear straight away;
/// the user saves in Excel. Excel calculates formulas and extends tables itself.
/// </summary>
internal static class ExcelLive
{
    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable rot);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx ctx);

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string progId, out Guid clsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object obj);

    /// <summary>The workbook object when <paramref name="path"/> is open in a running Excel; otherwise null.</summary>
    public static object? FindOpenWorkbook(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return null; }
        IRunningObjectTable? rot = null;
        IEnumMoniker? monikers = null;
        try
        {
            if (GetRunningObjectTable(0, out rot) != 0 || CreateBindCtx(0, out var ctx) != 0) return null;
            rot.EnumRunning(out monikers);
            var one = new IMoniker[1];
            // A synced file (OneDrive, SharePoint, Desktop Connector) is registered under its web address: matched by
            // file name when no local path matches and only one open workbook has that name.
            string fileName = Path.GetFileName(full);
            var byName = new List<object>();
            while (monikers.Next(1, one, IntPtr.Zero) == 0)
            {
                try
                {
                    one[0].GetDisplayName(ctx, null, out string name);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (string.Equals(name, full, StringComparison.OrdinalIgnoreCase))
                    {
                        rot.GetObject(one[0], out object workbook);
                        foreach (var other in byName) Marshal.ReleaseComObject(other);
                        return workbook;
                    }
                    if (name.Contains("://", StringComparison.Ordinal)
                        && Uri.UnescapeDataString(name).EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        rot.GetObject(one[0], out object candidate);
                        byName.Add(candidate);
                    }
                }
                catch (COMException) { /* not ours */ }
                finally
                {
                    Marshal.ReleaseComObject(one[0]);
                }
            }
            if (byName.Count == 1) return byName[0];
            foreach (var other in byName) Marshal.ReleaseComObject(other);
            // Not registered under a name we know (some synced or cloud-opened workbooks): ask Excel itself.
            if (FromExcelApplication(full) is { } fromExcel) return fromExcel;
        }
        catch (Exception ex)
        {
            HubLog.Warn("Looking for the workbook in Excel failed.", ex);
        }
        finally
        {
            if (monikers is not null) Marshal.ReleaseComObject(monikers);
            if (rot is not null) Marshal.ReleaseComObject(rot);
        }
        return null;
    }

    /// <summary>
    /// The running Excel's open workbook with this path, or with this file name when its path is a web address
    /// (OneDrive, SharePoint, Teams) and only one open workbook has that name. Null when Excel is not running.
    /// </summary>
    private static object? FromExcelApplication(string full)
    {
        object? app = null;
        try
        {
            if (CLSIDFromProgID("Excel.Application", out var clsid) != 0 || GetActiveObject(ref clsid, IntPtr.Zero, out app) != 0 || app is null)
                return null;
            dynamic excel = app;
            dynamic books = excel.Workbooks;
            int count = (int)books.Count;
            string fileName = Path.GetFileName(full);
            object? byName = null;
            int sameName = 0;
            for (int i = 1; i <= count; i++)
            {
                object book = books.Item(i);
                dynamic wb = book;
                string fullName = (string)wb.FullName;
                if (string.Equals(fullName, full, StringComparison.OrdinalIgnoreCase))
                {
                    if (byName is not null) Marshal.ReleaseComObject(byName);
                    return book;
                }
                if (string.Equals((string)wb.Name, fileName, StringComparison.OrdinalIgnoreCase) && fullName.Contains("://", StringComparison.Ordinal))
                {
                    sameName++;
                    if (byName is null) { byName = book; continue; }
                }
                Marshal.ReleaseComObject(book);
            }
            if (sameName == 1) return byName;
            if (byName is not null) Marshal.ReleaseComObject(byName);
        }
        catch (Exception ex)
        {
            HubLog.Info("Asking Excel for its open workbooks failed: " + ex.Message);
        }
        finally
        {
            if (app is not null) Marshal.ReleaseComObject(app);
        }
        return null;
    }

    /// <summary>
    /// Signs that Excel has the workbook open even though <see cref="FindOpenWorkbook"/> could not reach it: Excel's
    /// owner file (~$name) next to it, or an Excel window titled with its name. Writing the file then would be
    /// overwritten by Excel's copy (AutoSave, or the next save), so the export stops and says so.
    /// </summary>
    public static string? OpenElsewhereReason(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            string name = Path.GetFileName(path);
            string folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            // Excel's owner file: "~$" + the name (long names lose their first two characters).
            foreach (var owner in new[] { "~$" + name, name.Length > 2 ? "~$" + name[2..] : null })
                if (owner is not null && File.Exists(Path.Combine(folder, owner)))
                    return "Excel has it open (its lock file is next to it)";
            string stem = Path.GetFileNameWithoutExtension(name);
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("EXCEL"))
                using (p)
                    if (p.MainWindowTitle.Contains(stem, StringComparison.OrdinalIgnoreCase))
                        return "it is open in an Excel window Nexus cannot reach";
        }
        catch (Exception ex)
        {
            HubLog.Info("Checking whether Excel has the workbook open failed: " + ex.Message);
        }
        return null;
    }

    /// <summary>
    /// Runs <paramref name="work"/> on its own STA thread: Excel's objects are used from one thread, and the UI
    /// stays responsive while Excel is busy.
    /// </summary>
    public static Task<T> RunSta<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true, Name = "Nexus Excel" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    /// <summary>
    /// Brings <paramref name="data"/> (read from the saved file) up to date with the open workbook: values typed but
    /// not saved, values Nexus put there earlier, and the table's current size. Plans are then made against what
    /// Excel shows now.
    /// </summary>
    public static void Refresh(object workbook, ExcelLink link, ExcelData data)
    {
        dynamic wb = workbook;
        dynamic ws = Retry(() => wb.Worksheets[data.Worksheet]);
        int first = data.FirstDataRow;
        int last = data.LastRow;
        if (link.Table is { Length: > 0 } tableName)
        {
            data.TableLastRow = Retry(() =>
            {
                dynamic table = ws.ListObjects[tableName];
                dynamic range = table.Range;
                return (int)range.Row + (int)range.Rows.Count - 1 - ((bool)table.ShowTotals ? 1 : 0);
            });
            last = Math.Max(last, data.TableLastRow);
        }
        else
        {
            int used = Retry(() => { dynamic u = ws.UsedRange; return (int)u.Row + (int)u.Rows.Count - 1; });
            last = Math.Max(last, Math.Min(used, data.LastRow + 500));
        }
        if (last < first) return;

        foreach (var map in link.Columns.Where(c => c.ColumnId.Length > 0 && data.HeaderColumns.ContainsKey(c.Header)))
        {
            int column = data.HeaderColumns[map.Header];
            // One call per column: Value gives dates as dates (Value2 gives serial numbers).
            object? values = Retry(() => (object?)ws.Range[ws.Cells[first, column], ws.Cells[last, column]].Value);
            for (int r = first; r <= last; r++)
            {
                object? value = values is object[,] array ? array[array.GetLowerBound(0) + r - first, array.GetLowerBound(1)] : values;
                if (!data.Area.TryGetValue(r, out var row)) data.Area[r] = row = new ExcelRow { Row = r };
                bool formula = row.Cells.TryGetValue(map.Header, out var old) ? old.HasFormula : data.FormulaHeaders.Contains(map.Header);
                row.Cells[map.Header] = new ExcelCell { Row = r, Column = column, Text = TextOf(value), HasFormula = formula };
            }
        }

        // Keys and the last used row as Excel has them now.
        string? keyHeader = link.KeyHeader;
        data.Rows.Clear();
        data.LastRow = data.FirstDataRow - 1;
        foreach (var r in data.Area.Keys.Order().ToList())
        {
            var row = data.Area[r];
            string key = keyHeader is not null && row.Cells.TryGetValue(keyHeader, out var k) ? k.Text.Trim() : "";
            if (key != row.Key)
            {
                var keyed = new ExcelRow { Row = r, Key = key };
                foreach (var (h, c) in row.Cells) keyed.Cells[h] = c;
                data.Area[r] = row = keyed;
            }
            if (row.Cells.Values.Any(c => !c.HasFormula && c.Text.Trim().Length > 0)) data.LastRow = r;
            if (key.Length > 0) data.Rows.Add(row);
        }
    }

    /// <summary>What is selected in Excel, when it is in this workbook: the worksheet, the selected rows, and the texts of the selected cells.</summary>
    public sealed record ExcelSelection(string Worksheet, HashSet<int> Rows, HashSet<string> Texts);

    /// <summary>The current selection in Excel if it is in <paramref name="workbook"/>; null when Excel's selection is elsewhere or not cells.</summary>
    public static ExcelSelection? Selection(object workbook)
    {
        dynamic wb = workbook;
        return Retry(() =>
        {
            dynamic app = wb.Application;
            dynamic selection = app.Selection;
            dynamic? sheet;
            try { sheet = selection.Worksheet; }
            catch (Exception ex) when (ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                                       || (ex is COMException com && com.HResult != CallRejected && com.HResult != Busy))
            {
                return null; // a shape or chart is selected, not cells
            }
            if (!string.Equals((string)sheet.Parent.FullName, (string)wb.FullName, StringComparison.OrdinalIgnoreCase)) return null;

            var rows = new HashSet<int>();
            var texts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int cells = 0;
            foreach (dynamic area in selection.Areas)
            {
                int top = (int)area.Row, count = Math.Min((int)area.Rows.Count, 5000);
                for (int r = top; r < top + count; r++) rows.Add(r);
                // Whole-column selections are not read cell by cell.
                if (cells > 5000 || (long)area.Count > 5000) continue;
                cells += (int)area.Count;
                object? values = area.Value;
                if (values is object[,] array)
                {
                    foreach (var v in array)
                        if (TextOf(v).Trim() is { Length: > 0 } t) texts.Add(t);
                }
                else if (TextOf(values).Trim() is { Length: > 0 } one) texts.Add(one);
            }
            return (ExcelSelection?)new ExcelSelection((string)sheet.Name, rows, texts);
        });
    }

    private static string TextOf(object? value) => value switch
    {
        null => "",
        DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("d", CultureInfo.CurrentCulture) : d.ToString(CultureInfo.CurrentCulture),
        double n => n.ToString(CultureInfo.CurrentCulture),
        int e when e < 0 => "", // an error value (#N/A...)
        _ => Convert.ToString(value, CultureInfo.CurrentCulture) ?? "",
    };

    /// <summary>
    /// Applies a plan to the open workbook: makes the table longer first (Excel copies formulas and formats),
    /// then sets the cells. Retries while Excel is busy (a cell being edited, a dialog open).
    /// </summary>
    public static void Apply(object workbook, ExcelFillPlan plan)
    {
        dynamic wb = workbook;
        dynamic app = wb.Application;
        dynamic ws = Retry(() => wb.Worksheets[plan.Worksheet]);
        bool screen = true;
        try
        {
            screen = Retry(() => (bool)app.ScreenUpdating);
            Retry(() => { app.ScreenUpdating = false; return 0; });
            if (plan.GrowToRow > 0 && plan.Table is { } tableName)
            {
                Retry(() =>
                {
                    dynamic table = ws.ListObjects[tableName];
                    dynamic range = table.Range;
                    int top = (int)range.Row, left = (int)range.Column, width = (int)range.Columns.Count;
                    if (top + (int)range.Rows.Count - 1 >= plan.GrowToRow) return 0; // already that long
                    table.Resize(ws.Range[ws.Cells[top, left], ws.Cells[plan.GrowToRow, left + width - 1]]);
                    return 0;
                });
            }
            foreach (var u in plan.Updates)
            {
                Retry(() =>
                {
                    dynamic cell = ws.Cells[u.Row, u.Column];
                    string format = cell.NumberFormat is string f ? f : "";
                    cell.Value2 = ToExcel(u.Value, IsDateFormat(format));
                    return 0;
                });
            }
        }
        finally
        {
            try { app.ScreenUpdating = screen; } catch { /* Excel closed meanwhile */ }
        }
    }

    /// <summary>A date number format ("m/d/yyyy", "dd-mmm-yy"...), as opposed to General, text or a number format.</summary>
    private static bool IsDateFormat(string format)
    {
        if (format.Length == 0 || format.Contains('@')) return false;
        // Ignore quoted text and [colour]/[$-locale] sections.
        var plain = System.Text.RegularExpressions.Regex.Replace(format, "\"[^\"]*\"|\\[[^\\]]*\\]", "");
        return plain.IndexOfAny(new[] { 'd', 'D', 'y', 'Y' }) >= 0;
    }

    /// <summary>
    /// Numbers as numbers; dates as dates in date-formatted cells; everything else as text exactly as given (no
    /// conversion by Excel). The same rules as writing the file.
    /// </summary>
    private static object ToExcel(string text, bool dateCell)
    {
        if (text.Length == 0) return "";
        bool leadingZero = text.Length > 1 && text[0] == '0' && char.IsDigit(text[1]);
        if (!leadingZero && double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double number)
            && !text.Contains(',', StringComparison.Ordinal))
            return number;
        if (dateCell && DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var date))
            return date.ToOADate();
        // Text that Excel would turn into a number or date on its own ("001", "1-2") stays text.
        bool looksConvertible = double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out _)
                                || DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out _);
        return looksConvertible ? "'" + text : text;
    }

    private const int CallRejected = unchecked((int)0x80010001);
    private const int Busy = unchecked((int)0x8001010A);

    private static T Retry<T>(Func<T> call)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return call(); }
            catch (COMException ex) when ((ex.HResult == CallRejected || ex.HResult == Busy) && attempt < 40)
            {
                Thread.Sleep(250);
            }
            catch (COMException ex) when (ex.HResult == CallRejected || ex.HResult == Busy)
            {
                throw new InvalidOperationException("Excel is busy (a cell is being edited or a dialog is open). Press Enter or Esc in Excel and try again.", ex);
            }
        }
    }
}
