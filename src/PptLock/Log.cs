using System.Collections.Concurrent;
using System.Text;

namespace PptLock;

/// <summary>
/// Log em arquivo ao lado do .exe. A escrita acontece numa thread própria para
/// que o hook de teclado nunca espere por disco.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 1024 * 1024; // 1 MB por arquivo

    private static readonly BlockingCollection<string> Queue = new(boundedCapacity: 10_000);
    private static readonly Thread Writer;

    public static string FilePath { get; } = Path.Combine(AppPaths.BaseDirectory, "ppt-lock.log");

    static Log()
    {
        Writer = new Thread(WriteLoop) { IsBackground = true, Name = "PptLock.Log" };
        Writer.Start();
    }

    public static void Info(string message) => Enqueue("INFO ", message);
    public static void Warn(string message) => Enqueue("WARN ", message);
    public static void Error(string message, Exception? ex = null) =>
        Enqueue("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Enqueue(string level, string message)
    {
        // TryAdd: se a fila lotar, descarta a linha em vez de travar quem chamou.
        Queue.TryAdd($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}");
    }

    public static void Flush(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Queue.Count > 0 && DateTime.UtcNow < deadline) Thread.Sleep(20);
    }

    private static void WriteLoop()
    {
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try
            {
                RotateIfNeeded();
                File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Pendrive protegido contra escrita, removido etc.: segue sem log.
            }
        }
    }

    private static void RotateIfNeeded()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists || info.Length < MaxBytes) return;
        var old = FilePath + ".1";
        File.Delete(old);
        File.Move(FilePath, old);
    }
}
