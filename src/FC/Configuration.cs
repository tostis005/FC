using System.Globalization;
using System.Text.Json;

namespace FC;

public sealed class ThreadSettings
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class BotSettings
{
    public bool Enabled { get; set; }
    public string ForumUsername { get; set; } = "";
    public int MinMinutes { get; set; } = 75;
    public int MaxMinutes { get; set; } = 90;
    public string StartTime { get; set; } = "08:00";
    public string EndTime { get; set; } = "00:00";
    public string TimeZone { get; set; } = "Europe/Madrid";
    public List<ThreadSettings> Threads { get; set; } = new();
}

public sealed class ConfigurationStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private BotSettings _current;

    public ConfigurationStore()
    {
        var dir = Environment.GetEnvironmentVariable("FC_DATA_DIR") ?? "/data";
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "config.json");
        _current = File.Exists(_path)
            ? JsonSerializer.Deserialize<BotSettings>(File.ReadAllText(_path), _json) ?? new()
            : new();
        Validate(_current);
        if (!File.Exists(_path)) Save(_current);
    }

    public BotSettings Read()
    {
        lock (_gate)
            return JsonSerializer.Deserialize<BotSettings>(JsonSerializer.Serialize(_current, _json), _json)!;
    }

    public void Save(BotSettings config)
    {
        Validate(config);
        lock (_gate)
        {
            var clone = JsonSerializer.Deserialize<BotSettings>(JsonSerializer.Serialize(config, _json), _json)!;
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(clone, _json));
            File.Move(tmp, _path, overwrite: true);
            _current = clone;
        }
    }

    public static void Validate(BotSettings c)
    {
        if (c.MinMinutes < 1 || c.MaxMinutes > 1440 || c.MaxMinutes < c.MinMinutes)
            throw new ArgumentException("Intervalos inválidos: 1-1440 minutos y máximo >= mínimo.");
        if (!TimeOnly.TryParseExact(c.StartTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
            !TimeOnly.TryParseExact(c.EndTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new ArgumentException("Horario inválido (HH:mm).");
        try { TimeZoneInfo.FindSystemTimeZoneById(c.TimeZone); }
        catch { throw new ArgumentException("Zona horaria inválida, p. ej. Europe/Madrid."); }
        if (c.Enabled && string.IsNullOrWhiteSpace(c.ForumUsername))
            throw new ArgumentException("Configura tu nombre de usuario de ForoCoches antes de activar.");
        if (c.Threads == null || c.Threads.Count > 100)
            throw new ArgumentException("Máximo 100 hilos.");
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in c.Threads)
        {
            if (string.IsNullOrWhiteSpace(t.Name) || t.Name.Length > 120 || !IsForumThreadUrl(t.Url))
                throw new ArgumentException("Todos los hilos necesitan nombre y URL válida de ForoCoches.");
            if (!unique.Add(t.Url))
                throw new ArgumentException("Hay URLs duplicadas.");
        }
    }

    public static bool IsForumThreadUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
            return false;
        return u.Host.Equals("forocoches.com", StringComparison.OrdinalIgnoreCase)
            || u.Host.EndsWith(".forocoches.com", StringComparison.OrdinalIgnoreCase);
    }
}

public static class TimeWindow
{
    public static bool IsOpen(BotSettings c, DateTimeOffset utcNow)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(c.TimeZone);
        var local = TimeZoneInfo.ConvertTime(utcNow, zone).TimeOfDay;
        var start = TimeSpan.ParseExact(c.StartTime, @"hh\:mm", CultureInfo.InvariantCulture);
        var end = TimeSpan.ParseExact(c.EndTime, @"hh\:mm", CultureInfo.InvariantCulture);
        if (start == end) return true; // 24h
        if (start < end) return local >= start && local < end;
        return local >= start || local < end; // atraviesa medianoche
    }
}
