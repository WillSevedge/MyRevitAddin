using Nexus.Agent;
using Nexus.Contracts;

namespace Nexus.Agent.Acad.PropertyEngine;

/// <summary>
/// Crash guard for generic property reading. Some native-backed getters can crash
/// the host in ways .NET cannot catch (access violations). Before a property is
/// read for the first time its key is written to a "pending" file; after the read
/// request finishes the file is cleared. If AutoCAD dies mid-read, the next start
/// finds the pending key and adds it to the deny list, so the same property is
/// never read again. Delete acad-probe-denied.txt to reset.
/// </summary>
public sealed class ProbeJournal
{
    private readonly object _gate = new();
    private readonly string _pendingFile = Path.Combine(NexusPaths.Root, "acad-probe-pending.txt");
    private readonly string _deniedFile = Path.Combine(NexusPaths.Root, "acad-probe-denied.txt");
    private readonly string _verifiedFile = Path.Combine(NexusPaths.Root, "acad-probe-verified.txt");
    private readonly HashSet<string> _verified = new(StringComparer.Ordinal);
    private readonly HashSet<string> _denied = new(StringComparer.Ordinal);
    private readonly List<string> _newlyVerified = new();
    private readonly AgentLog _log;
    private bool _pendingWritten;

    public ProbeJournal(AgentLog log)
    {
        _log = log;
        try
        {
            Directory.CreateDirectory(NexusPaths.Root);
            if (File.Exists(_verifiedFile))
                foreach (var l in File.ReadAllLines(_verifiedFile)) if (l.Length > 0) _verified.Add(l);
            if (File.Exists(_deniedFile))
                foreach (var l in File.ReadAllLines(_deniedFile)) if (l.Length > 0 && !l.StartsWith('#')) _denied.Add(l);

            if (File.Exists(_pendingFile))
            {
                string crashed = File.ReadAllText(_pendingFile).Trim();
                File.Delete(_pendingFile);
                if (crashed.Length > 0 && _denied.Add(crashed))
                {
                    File.AppendAllLines(_deniedFile, new[] { crashed });
                    _verified.Remove(crashed);
                    log.Warn($"The previous session ended while reading '{crashed}'. It will be skipped from now on (see {_deniedFile}).");
                }
            }
        }
        catch (Exception ex)
        {
            log.Warn("Could not load the property probe journal.", ex);
        }
    }

    public bool IsDenied(string key)
    {
        lock (_gate) return _denied.Contains(key);
    }

    public void Before(string key)
    {
        lock (_gate)
        {
            if (_verified.Contains(key)) return;
            try
            {
                File.WriteAllText(_pendingFile, key);
                _pendingWritten = true;
            }
            catch { /* best effort */ }
        }
    }

    public void After(string key)
    {
        lock (_gate)
        {
            if (_verified.Add(key)) _newlyVerified.Add(key);
        }
    }

    /// <summary>Call at the end of every read request (in a finally block).</summary>
    public void Flush()
    {
        lock (_gate)
        {
            try
            {
                if (_newlyVerified.Count > 0)
                {
                    File.AppendAllLines(_verifiedFile, _newlyVerified);
                    _newlyVerified.Clear();
                }
                if (_pendingWritten)
                {
                    File.Delete(_pendingFile);
                    _pendingWritten = false;
                }
            }
            catch (Exception ex)
            {
                _log.Warn("Could not update the property probe journal.", ex);
            }
        }
    }
}
