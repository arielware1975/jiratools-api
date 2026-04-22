namespace ApiJiraTools.Models;

public class Reminder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public long ChatId { get; set; }
    public string Message { get; set; } = string.Empty;
    public ReminderSchedule Schedule { get; set; } = new();
    /// <summary>Último disparo (horario Argentina). Para interval: es el minuto exacto del último fire.</summary>
    public DateTime? LastFired { get; set; }
    /// <summary>Si está seteado, no se dispara hasta esta fecha/hora. Lo usa /hecho para cortar un ciclo interval por hoy.</summary>
    public DateTime? DoneUntil { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool Enabled { get; set; } = true;
    /// <summary>Texto original del usuario (para mostrar en /recordatorios).</summary>
    public string OriginalText { get; set; } = string.Empty;
}

public class ReminderSchedule
{
    /// <summary>once | daily | weekly | monthly | yearly | interval</summary>
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

    /// <summary>HH:mm — hora en huso local (Argentina). Default 09:00. Para interval = hora de inicio.</summary>
    public string Time { get; set; } = "09:00";

    // ── interval ─────────────────────────────────────────────────────────

    /// <summary>HH:mm — hora de fin de ventana (solo interval). Si null, corre hasta 23:59.</summary>
    public string? EndTime { get; set; }

    /// <summary>Cada cuántas horas dispara dentro de la ventana (solo interval).</summary>
    public int IntervalHours { get; set; }
}

public class UserNote
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
