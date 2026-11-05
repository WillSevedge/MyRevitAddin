using Nexus.Agent;
using Nexus.Contracts;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Files;

namespace Nexus.Tests;

[Collection("NexusHome")]
public sealed class PrintAndPdfTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-pdf-" + Guid.NewGuid().ToString("N"));

    public PrintAndPdfTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("NEXUS_HOME", Path.Combine(_dir, "home"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignored */ }
    }

    [Fact]
    public void Pdf_names_are_safe_and_unique()
    {
        Assert.Equal("A101 - Plan_ Level 1", PdfNames.For(PdfNames.DefaultPattern, "A101", "Plan: Level 1", "Tower"));
        Assert.Equal("Layout1", PdfNames.For(PdfNames.DefaultPattern, "", "Layout1", "Site"));
        Assert.Equal("Site - C-1 - Grading", PdfNames.For("{File} - {Number} - {Name}", "C-1", "Grading", "Site"));
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.EndsWith("A101.pdf", PdfNames.Unique(_dir, "A101", taken));
        Assert.EndsWith("A101 (2).pdf", PdfNames.Unique(_dir, "A101", taken));
    }

    [Fact]
    public async Task Drawing_copies_leave_the_original_alone_and_are_removed()
    {
        string path = Path.Combine(_dir, "Model.dwg");
        File.WriteAllText(path, "original");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        string copyPath;
        using (var copy = await WorkCopy.CreateAsync(path, CancellationToken.None))
        {
            copyPath = copy.Path;
            Assert.StartsWith(NexusPaths.OfflineWorkDir, copy.Path);
            File.WriteAllText(copy.Path, "changed by a program"); // the copy is writable
        }
        Assert.False(File.Exists(copyPath));
        Assert.Equal("original", File.ReadAllText(path));
        File.SetAttributes(path, FileAttributes.Normal);
    }

    [Fact]
    public void Sheet_pdfs_combine_into_one()
    {
        string MakePdf(string name, int pages)
        {
            using var doc = new PdfSharp.Pdf.PdfDocument();
            for (int i = 0; i < pages; i++) doc.AddPage();
            string p = Path.Combine(_dir, name);
            doc.Save(p);
            return p;
        }
        string target = Path.Combine(_dir, "Set.pdf");
        PdfTools.Combine(new[] { MakePdf("A101.pdf", 1), MakePdf("A102.pdf", 2) }, target);
        using var combined = PdfSharp.Pdf.IO.PdfReader.Open(target, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        Assert.Equal(3, combined.PageCount);
    }

    [Fact]
    public async Task Agent_makes_pdfs_of_open_documents_only()
    {
        var log = new AgentLog("test");
        using var host = new FakeHost(log);
        var readers = new ReaderRegistry<FakeDoc>();
        readers.Register(new FakeSheetsReader());
        var info = HostInfoFactory.Create("Fake", "FakeCAD", "2026", "test", log);
        info.HostKind = "Fake" + Guid.NewGuid().ToString("N")[..8];
        using var server = new AgentServer<FakeDoc>(info, host.Queue, new FakeDocs(), readers, log,
            new AgentServerOptions { WriteRegistration = false }, pdf: new FakePdf());
        server.Start();
        await using var client = await AgentClient.ConnectAsync(server.PipeName, TimeSpan.FromSeconds(5));

        var hello = await client.HelloAsync();
        Assert.Contains(AgentFeatures.ExportPdf, hello.Host.Features);

        var pdf = await client.ExportPdfAsync(new ExportPdfRequest { DocumentId = "d1", OutputFolder = Path.Combine(_dir, "pdf"), ItemIds = { "s1" } });
        Assert.Equal("Tower.rvt/s1", Assert.Single(pdf.Sheets).Name);

        // A path instead of an open document is refused: agents never open files.
        var refused = await Assert.ThrowsAsync<AgentRequestException>(() =>
            client.ExportPdfAsync(new ExportPdfRequest { Path = Path.Combine(_dir, "Closed.rvt"), OutputFolder = _dir }));
        Assert.Equal(ErrorCodes.BadRequest, refused.Code);
    }

    [Fact]
    public async Task Agent_creates_sheets_when_it_can()
    {
        var log = new AgentLog("test");
        using var host = new FakeHost(log);
        var readers = new ReaderRegistry<FakeDoc>();
        readers.Register(new FakeSheetsReader());
        var info = HostInfoFactory.Create("Fake", "FakeCAD", "2026", "test", log);
        info.HostKind = "Fake" + Guid.NewGuid().ToString("N")[..8];
        using var server = new AgentServer<FakeDoc>(info, host.Queue, new FakeDocs(), readers, log,
            new AgentServerOptions { WriteRegistration = false }, sheets: new FakeSheets());
        server.Start();
        await using var client = await AgentClient.ConnectAsync(server.PipeName, TimeSpan.FromSeconds(5));

        Assert.Contains(AgentFeatures.CreateSheets, (await client.HelloAsync()).Host.Features);
        var result = await client.CreateSheetsAsync(new CreateSheetsRequest
        {
            DocumentId = "d1",
            Sheets = { new NewSheet { Number = "C-101", Name = "SITE PLAN", Values = { ["Sheet Status|Status"] = "IFR" } } },
        });
        var sheet = Assert.Single(result.Sheets);
        Assert.True(sheet.Created);
        Assert.Equal("Tower.rvt/C-101/SITE PLAN/IFR", sheet.Id);
        Assert.Equal("Tower.rvt", result.DocumentTitle);

        var empty = await Assert.ThrowsAsync<AgentRequestException>(() => client.CreateSheetsAsync(new CreateSheetsRequest { DocumentId = "d1" }));
        Assert.Equal(ErrorCodes.BadRequest, empty.Code);
    }

    private sealed class FakeSheets : ISheetCreator<FakeDoc>
    {
        public CreateSheetsResult Create(FakeDoc document, CreateSheetsRequest request, AgentLog log, CancellationToken ct) => new()
        {
            Sheets = request.Sheets.Select(s => new NewSheetResult
            {
                Number = s.Number, Created = true, Id = $"{document.Title}/{s.Number}/{s.Name}/{s.Values.Values.FirstOrDefault()}",
            }).ToList(),
        };
    }

    private sealed class FakePdf : IPdfExporter<FakeDoc>
    {
        public ExportPdfResult Export(FakeDoc document, ExportPdfRequest request, AgentLog log, CancellationToken ct) => new()
        {
            Sheets = request.ItemIds.Select(id => new ExportedSheet { ItemId = id, Name = $"{document.Title}/{id}", PdfPath = Path.Combine(request.OutputFolder, id + ".pdf") }).ToList(),
        };
    }
}
