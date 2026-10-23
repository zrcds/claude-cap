namespace ClaudeCap;

static class Logger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "tools", "claudecap", "debug.log");

    private static readonly object Lock = new();

    public static void Log(string message)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            lock (Lock)
                File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch { }
    }

    // Keep history across launches so a failed autostart leaves a trace; start fresh once past 1 MB.
    public static void TrimIfLarge()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > 1_000_000) File.WriteAllText(LogPath, "");
        }
        catch { }
    }

    public static void Clear()
    {
        try { File.WriteAllText(LogPath, ""); } catch { }
    }

    public static void Open()
    {
        if (File.Exists(LogPath))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = LogPath,
                UseShellExecute = true
            });
    }
}
