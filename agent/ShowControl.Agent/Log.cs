namespace ShowControl.Agent;

/// <summary>Tiny thread-safe daily file logger in %LOCALAPPDATA%\ShowControl\logs.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly LinkedList<string> Recent = new();
    private const int RecentMax = 500;

    /// <summary>Most recent log lines, oldest first, for the dashboard.</summary>
    public static List<string> Tail(int max = 200)
    {
        lock (Gate) return Recent.Skip(Math.Max(0, Recent.Count - max)).ToList();
    }

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShowControl", "logs");

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Recent.AddLast($"{DateTime.Now:HH:mm:ss} {level.Trim()} {message}");
                while (Recent.Count > RecentMax) Recent.RemoveFirst();
                Directory.CreateDirectory(Folder);
                string file = Path.Combine(Folder, $"agent-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the agent down.
        }
    }
}
