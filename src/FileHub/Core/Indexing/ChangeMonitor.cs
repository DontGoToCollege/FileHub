using System.IO;
using System.Threading.Channels;
using FileHub.Platform;

namespace FileHub.Core.Indexing;

public enum FsChangeKind { Created, Deleted, Changed, Renamed, Overflow }

public sealed record FsChange(FsChangeKind Kind, string Path, string? OldPath = null);

/// <summary>Watches each indexed root with a FileSystemWatcher and funnels events into a channel.</summary>
public sealed class ChangeMonitor : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Channel<FsChange> _channel = Channel.CreateUnbounded<FsChange>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<FsChange> Changes => _channel.Reader;

    public void Watch(IEnumerable<string> roots)
    {
        Stop();
        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes,
                };
                w.Created += (_, e) => Post(new FsChange(FsChangeKind.Created, e.FullPath));
                w.Deleted += (_, e) => Post(new FsChange(FsChangeKind.Deleted, e.FullPath));
                w.Changed += (_, e) => Post(new FsChange(FsChangeKind.Changed, e.FullPath));
                w.Renamed += (_, e) => Post(new FsChange(FsChangeKind.Renamed, e.FullPath, e.OldFullPath));
                w.Error += (_, e) => { Log.Warn("Watcher error on " + root + ": " + e.GetException().Message); Post(new FsChange(FsChangeKind.Overflow, root)); };
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex) { Log.Warn("Cannot watch " + root + ": " + ex.Message); }
        }
    }

    private static readonly string OwnDataDir = Paths.DataDir.TrimEnd('\\') + "\\";

    private void Post(FsChange c)
    {
        // FileHub's own database / thumbnail cache / log writes would otherwise feed back into the indexer
        if (c.Path.StartsWith(OwnDataDir, StringComparison.OrdinalIgnoreCase)) return;
        _channel.Writer.TryWrite(c);
    }

    public void Stop()
    {
        foreach (var w in _watchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
        _watchers.Clear();
    }

    public void Dispose() => Stop();
}
