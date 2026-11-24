using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace SnowRunnerApex5;

internal sealed class DiagnosticLog : IDisposable
{
    private const long MaximumBytes = 25_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    private readonly string path;
    private readonly string session = Guid.NewGuid().ToString("N");
    private readonly Channel<Entry> queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(4096)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false
    });
    private readonly Task writerTask;
    private long sequence;
    private long dropped;
    private string? error;

    private sealed record Entry(long Sequence, DateTimeOffset Timestamp, long MonotonicMs,
        string Kind, object Data);

    public string? Error => Volatile.Read(ref error);

    public DiagnosticLog(string fileName = "diagnostics.jsonl")
    {
        path = Path.Combine(Program.DataFolder, fileName);
        writerTask = Task.Run(WriteAsync);
    }

    public void Write(string kind, object data)
    {
        Entry entry = new(Interlocked.Increment(ref sequence), DateTimeOffset.Now,
            Environment.TickCount64, kind, data);
        if (!queue.Writer.TryWrite(entry))
            Interlocked.Increment(ref dropped);
    }

    private async Task WriteAsync()
    {
        StreamWriter? writer = null;
        try
        {
            long bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
            long lastFlush = Environment.TickCount64;
            await foreach (Entry entry in queue.Reader.ReadAllAsync())
            {
                if (bytes >= MaximumBytes)
                {
                    if (writer is not null)
                    {
                        await writer.DisposeAsync();
                        writer = null;
                    }
                    for (int index = 3; index >= 1; index--)
                    {
                        string source = index == 1 ? path : $"{path}.{index - 1}";
                        if (File.Exists(source))
                            File.Move(source, $"{path}.{index}", true);
                    }
                    bytes = 0;
                }
                writer ??= new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous),
                    new UTF8Encoding(false), 65536);
                string line = JsonSerializer.Serialize(new
                {
                    Session = session,
                    entry.Sequence,
                    entry.Timestamp,
                    entry.MonotonicMs,
                    entry.Kind,
                    DroppedSinceLastEntry = Interlocked.Exchange(ref dropped, 0),
                    entry.Data
                }, JsonOptions);
                await writer.WriteLineAsync(line);
                bytes += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                if (Environment.TickCount64 - lastFlush >= 1000)
                {
                    await writer.FlushAsync();
                    lastFlush = Environment.TickCount64;
                }
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or
            JsonException or NotSupportedException)
        {
            Volatile.Write(ref error, failure.ToString());
            queue.Writer.TryComplete();
        }
        finally
        {
            if (writer is not null)
            {
                try { await writer.DisposeAsync(); }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
                {
                    Volatile.Write(ref error, failure.ToString());
                }
            }
        }
    }

    public void Dispose()
    {
        queue.Writer.TryComplete();
        writerTask.GetAwaiter().GetResult();
    }
}
