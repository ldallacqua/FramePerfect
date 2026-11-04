using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace XivDlssFix;

/// <summary>
/// Plain-text log at pluginConfigs\XivDlssFix\dlssfix.log (plus a copy in dalamud.log).
/// Lines from other threads (e.g. the render thread) are queued and flushed on the framework thread.
/// </summary>
internal sealed class FileLog : IDisposable
{
    private readonly object gate = new();
    private readonly StreamWriter writer;
    private readonly ConcurrentQueue<string> pending = new();

    public string FilePath { get; }

    public FileLog(string directory)
    {
        Directory.CreateDirectory(directory);
        this.FilePath = Path.Combine(directory, "dlssfix.log");
        try
        {
            var info = new FileInfo(this.FilePath);
            if (info.Exists && info.Length > 4 * 1024 * 1024)
                File.Move(this.FilePath, this.FilePath + ".old", true);
        }
        catch
        {
            // rotation is best effort
        }

        var stream = new FileStream(this.FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        this.writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public void Info(string message) => this.Write("INF", message);

    public void Warn(string message) => this.Write("WRN", message);

    public void Error(string message) => this.Write("ERR", message);

    /// <summary>Thread-safe and cheap: just enqueues. Use from hooks that may run on the render thread.</summary>
    public void Enqueue(string message) => this.pending.Enqueue($"{DateTime.Now:HH:mm:ss.fff} [RT ] {message}");

    /// <summary>Like <see cref="Enqueue"/> but stamped with the time the event happened (e.g. a GPU readback of an older frame).</summary>
    public void EnqueueAt(DateTime time, string message) => this.pending.Enqueue($"{time:HH:mm:ss.fff} [RT ] {message}");

    public void Flush()
    {
        while (this.pending.TryDequeue(out var line))
        {
            lock (this.gate)
                this.writer.WriteLine(line);
            Plugin.PluginLog.Information(line);
        }
    }

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        lock (this.gate)
            this.writer.WriteLine(line);

        switch (level)
        {
            case "ERR": Plugin.PluginLog.Error(message); break;
            case "WRN": Plugin.PluginLog.Warning(message); break;
            default: Plugin.PluginLog.Information(message); break;
        }
    }

    public void Dispose()
    {
        this.Flush();
        lock (this.gate)
            this.writer.Dispose();
    }
}
