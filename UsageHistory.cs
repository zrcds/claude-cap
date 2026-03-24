using System.Text.Json;

namespace ClaudeCap;

record DailyUsage(string Timestamp, double UsedDollars, double TotalDollars, int Percent);
record DailyUsageLegacy(string Date, double UsedDollars, double TotalDollars, int Percent);

static class UsageHistory
{
    const int MaxEntries      = 250;  // ~62 days × 4/day
    const int MinHoursBetween = 6;    // at most 4 readings per day

    static readonly string HistoryFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "usage_history.json");

    public static List<DailyUsage> Load()
    {
        if (!File.Exists(HistoryFile)) return new();
        var text = File.ReadAllText(HistoryFile);
        try
        {
            var list = JsonSerializer.Deserialize<List<DailyUsage>>(text);
            if (list is { Count: > 0 } && list[0].Timestamp != null) return list;
        }
        catch { }
        // Migrate legacy format ("Date": "yyyy-MM-dd" → "Timestamp": "yyyy-MM-ddT12:00:00")
        try
        {
            var legacy = JsonSerializer.Deserialize<List<DailyUsageLegacy>>(text);
            if (legacy != null)
                return legacy
                    .Select(e => new DailyUsage(e.Date + "T12:00:00", e.UsedDollars, e.TotalDollars, e.Percent))
                    .ToList();
        }
        catch { }
        return new();
    }

    public static void Record(double usedDollars, double totalDollars, int percent)
    {
        var history = Load();
        var now = DateTime.Now;

        if (history.Count > 0)
        {
            var lastTs = DateTime.Parse(history[^1].Timestamp);
            if ((now - lastTs).TotalHours < MinHoursBetween) return;
        }

        history.Add(new DailyUsage(now.ToString("yyyy-MM-ddTHH:mm:ss"), usedDollars, totalDollars, percent));
        history = history.TakeLast(MaxEntries).ToList();
        File.WriteAllText(HistoryFile, JsonSerializer.Serialize(history,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
