using Nexus.Contracts;

namespace Nexus.Hub.Core.Files;

/// <summary>
/// PDFs of a drawing's layouts, made by AutoCAD's Core Console (AutoCAD without a window) from a private copy
/// of the saved drawing, with each layout's own page setup. Used by Print &amp; PDF for drawings open in AutoCAD.
/// </summary>
public sealed class DrawingPdfs
{
    // Several drawings at once (each is its own Core Console process).
    private readonly SemaphoreSlim _consoles = new(Math.Clamp(Environment.ProcessorCount / 2, 1, 4));

    public async Task<ExportPdfResult> ExportAsync(string drawing, ExportPdfRequest request, CancellationToken ct)
    {
        var install = CoreConsole.Find().FirstOrDefault()
                      ?? throw new AgentRequestException(new ErrorInfo(ErrorCodes.NotImplemented, CoreConsole.NotInstalled));
        await _consoles.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var copy = await WorkCopy.CreateAsync(drawing, ct).ConfigureAwait(false);
            request.Path = copy.Path;
            var job = new HeadlessJob { Pdf = request };
            var done = await CoreConsole.RunAsync(install, copy.Path, job, copy.Folder, loadCivil3D: true, ct).ConfigureAwait(false);
            return done.Pdf ?? throw new AgentRequestException(new ErrorInfo(ErrorCodes.InternalError, "The add-in made no PDFs."));
        }
        finally
        {
            _consoles.Release();
        }
    }
}
