namespace ApiJiraTools.Models;

public class Reminder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public long ChatId { get; set; }
    public string Message { get; set; } = string.Empty;
    public ReminderSchedule Schedule { get; set; } = new();
    public DateTime? LastFired { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool Enabled { get; set; } = true;
    /// <summary>Texto original del usuario (para mostrar en /recordatorios).</summary>
    public string OriginalText { get; set; } = string.Empty;
}

public class ReminderSchedule
{
    /// <summary>once | daily | weekly | monthly | yearly</summary>
    public string Type { get; set; } = "once";

    /// <summary>YYYY-MM-DD — solo para type=once</summary>
    public string? Date { get; set; }

    /// <summary>mon|tue|wed|thu|fri|sat|sun — solo weekly</summary>
    public string? DayOfWeek { get; set; }

    /// <summary>1..31 — solo monthly / yearly</summary>
    public int? DayOfMonth { get; set; }

    /// <summary>1..12 — solo yearly</summary>
    public int? Month { get; set; }

    /// <summary>Offset en días hábiles para monthly. Ej: -2 = 2 días hábiles antes del día 25.</summary>
    public int OffsetBusinessDays { get; set; }

    /// <summary>HH:mm — hora en huso local (Argentina). Default 09:00.</summary>
    public string Time { get; set; } = "09:00";
}

public class UserNote
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
