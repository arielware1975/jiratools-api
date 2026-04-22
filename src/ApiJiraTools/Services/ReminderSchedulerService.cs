using ApiJiraTools.Configuration;
using ApiJiraTools.Models;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace ApiJiraTools.Services;

/// <summary>
/// Chequea cada minuto qué recordatorios corresponde disparar ahora y los envía por Telegram.
/// </summary>
public sealed class ReminderSchedulerService : BackgroundService
{
    private readonly ReminderStore _store;
    private readonly ILogger<ReminderSchedulerService> _logger;
    private readonly string _botToken;
    private static readonly TimeZoneInfo ArgentinaTz = GetArgentinaTz();

    private static TimeZoneInfo GetArgentinaTz()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"); }
        catch { return TimeZoneInfo.CreateCustomTimeZone("ARG", TimeSpan.FromHours(-3), "Argentina", "Argentina"); }
    }

    public ReminderSchedulerService(
        ReminderStore store,
        IOptions<TelegramSettings> opts,
        ILogger<ReminderSchedulerService> logger)
    {
        _store = store;
        _logger = logger;
        _botToken = opts.Value.BotToken;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_botToken))
        {
            _logger.LogWarning("Reminder scheduler: Bot token vacío, deshabilitado.");
            return;
        }

        _logger.LogInformation("Reminder scheduler iniciado.");
        var bot = new TelegramBotClient(_botToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndFire(bot, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en reminder scheduler loop");
            }

            // Revisar cada minuto
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task CheckAndFire(ITelegramBotClient bot, CancellationToken ct)
    {
        var nowArg = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ArgentinaTz);
        var reminders = _store.LoadAll();

        foreach (var r in reminders.Where(x => x.Enabled))
        {
            if (!ShouldFireNow(r, nowArg)) continue;

            try
            {
                var text = $"⏰ *Recordatorio*\n\n{EscapeMd(r.Message)}";

                // Botón "✅ Ya lo hice" para poder silenciar el ciclo (útil sobre todo en interval).
                var keyboard = new InlineKeyboardMarkup(new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ Ya lo hice", $"reminder_done:{r.Id}"),
                });

                await bot.SendMessage(
                    r.ChatId,
                    text,
                    parseMode: ParseMode.MarkdownV2,
                    replyMarkup: keyboard,
                    cancellationToken: ct);

                _store.UpdateLastFired(r.Id, nowArg);

                // Recordatorios de una sola vez: deshabilitar
                if (r.Schedule.Type.Equals("once", StringComparison.OrdinalIgnoreCase))
                {
                    var list = _store.LoadAll();
                    var inList = list.FirstOrDefault(x => x.Id == r.Id);
                    if (inList != null)
                    {
                        inList.Enabled = false;
                        _store.SaveAll(list);
                    }
                }

                _logger.LogInformation("Recordatorio {Id} enviado a chat {ChatId}", r.Id, r.ChatId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enviando recordatorio {Id}", r.Id);
            }
        }
    }

    /// <summary>
    /// Devuelve true si el recordatorio debe dispararse "ahora" (dentro de la misma minuto-hora
    /// del día en horario argentino) y no fue disparado todavía hoy.
    /// </summary>
    public static bool ShouldFireNow(Reminder r, DateTime nowArg)
    {
        // Respetar DoneUntil (lo setea /hecho o el botón inline)
        if (r.DoneUntil.HasValue && nowArg < r.DoneUntil.Value) return false;

        if (r.Schedule.Type.Equals("interval", StringComparison.OrdinalIgnoreCase))
            return ShouldFireInterval(r, nowArg);

        if (!TryParseTime(r.Schedule.Time, out int hh, out int mm)) return false;

        var targetDate = ComputeTargetDate(r.Schedule, nowArg);
        if (targetDate == null) return false;

        var fireTime = new DateTime(targetDate.Value.Year, targetDate.Value.Month, targetDate.Value.Day, hh, mm, 0);

        if (nowArg.Date != fireTime.Date) return false;
        if (nowArg < fireTime) return false;

        if (r.LastFired.HasValue && r.LastFired.Value.Date == nowArg.Date)
            return false;

        return true;
    }

    /// <summary>
    /// Para recordatorios type=interval: dispara cada intervalHours entre Time y EndTime.
    /// Si LastFired es del mismo "slot" del día (startTime + N*interval), no redispara.
    /// </summary>
    private static bool ShouldFireInterval(Reminder r, DateTime nowArg)
    {
        if (!TryParseTime(r.Schedule.Time, out int startH, out int startM)) return false;
        int interval = Math.Max(1, r.Schedule.IntervalHours);

        int endH = 23, endM = 59;
        if (!string.IsNullOrWhiteSpace(r.Schedule.EndTime))
            TryParseTime(r.Schedule.EndTime, out endH, out endM);

        var windowStart = new DateTime(nowArg.Year, nowArg.Month, nowArg.Day, startH, startM, 0);
        var windowEnd = new DateTime(nowArg.Year, nowArg.Month, nowArg.Day, endH, endM, 0);

        if (nowArg < windowStart || nowArg > windowEnd) return false;

        // Slot "esperado" = el último punto start + k*interval <= now
        var slotsPassed = (int)((nowArg - windowStart).TotalHours / interval);
        var expectedFire = windowStart.AddHours(slotsPassed * interval);

        // Si el último disparo fue en este slot o posterior, no redisparar
        if (r.LastFired.HasValue && r.LastFired.Value >= expectedFire) return false;

        return true;
    }

    /// <summary>Calcula la fecha (sin hora) en que debe dispararse el recordatorio según el schedule.</summary>
    private static DateTime? ComputeTargetDate(ReminderSchedule s, DateTime nowArg)
    {
        switch (s.Type.ToLowerInvariant())
        {
            case "once":
                if (DateTime.TryParse(s.Date, out var d)) return d.Date;
                return null;

            case "daily":
                return nowArg.Date;

            case "weekly":
                if (!TryParseDayOfWeek(s.DayOfWeek, out var dow)) return null;
                if (nowArg.DayOfWeek == dow) return nowArg.Date;
                return null;

            case "monthly":
                if (s.DayOfMonth == null) return null;
                return ResolveMonthlyDate(s, nowArg);

            case "yearly":
                if (s.DayOfMonth == null || s.Month == null) return null;
                if (nowArg.Month == s.Month.Value && nowArg.Day == s.DayOfMonth.Value)
                    return nowArg.Date;
                return null;

            default:
                return null;
        }
    }

    /// <summary>Para monthly con/sin offsetBusinessDays, devuelve si hoy es el día de fire.</summary>
    private static DateTime? ResolveMonthlyDate(ReminderSchedule s, DateTime nowArg)
    {
        int targetDay = Math.Clamp(s.DayOfMonth!.Value, 1, DateTime.DaysInMonth(nowArg.Year, nowArg.Month));
        var anchor = new DateTime(nowArg.Year, nowArg.Month, targetDay);
        var fireDate = ApplyBusinessDayOffset(anchor, s.OffsetBusinessDays);

        // Si el fire del mes actual ya pasó, no se dispara hasta el mes que viene
        if (fireDate.Date == nowArg.Date) return nowArg.Date;

        // Caso borde: si el offset empuja la fecha al mes anterior (ej: offset -5 y día 2)
        // y hoy estamos en el mes anterior, hay que chequear también el "mes próximo"
        // para no perder el disparo. Calculamos el fire para el mes siguiente también.
        var nextMonthAnchor = anchor.AddMonths(1);
        var nextMonthTargetDay = Math.Clamp(s.DayOfMonth.Value, 1,
            DateTime.DaysInMonth(nextMonthAnchor.Year, nextMonthAnchor.Month));
        nextMonthAnchor = new DateTime(nextMonthAnchor.Year, nextMonthAnchor.Month, nextMonthTargetDay);
        var nextFireDate = ApplyBusinessDayOffset(nextMonthAnchor, s.OffsetBusinessDays);
        if (nextFireDate.Date == nowArg.Date) return nowArg.Date;

        return null;
    }

    /// <summary>
    /// Aplica un offset de días hábiles (lunes a viernes) a la fecha dada.
    /// offset negativo = antes, positivo = después. 0 = misma fecha.
    /// </summary>
    public static DateTime ApplyBusinessDayOffset(DateTime anchor, int offset)
    {
        if (offset == 0) return anchor;
        int step = offset > 0 ? 1 : -1;
        int remaining = Math.Abs(offset);
        var d = anchor;
        while (remaining > 0)
        {
            d = d.AddDays(step);
            if (d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday)
                remaining--;
        }
        return d;
    }

    private static bool TryParseTime(string s, out int hh, out int mm)
    {
        hh = 0; mm = 0;
        var parts = (s ?? "").Split(':');
        if (parts.Length != 2) return false;
        return int.TryParse(parts[0], out hh) && int.TryParse(parts[1], out mm);
    }

    private static bool TryParseDayOfWeek(string? s, out DayOfWeek dow)
    {
        dow = DayOfWeek.Monday;
        if (string.IsNullOrWhiteSpace(s)) return false;
        return s.Trim().ToLowerInvariant() switch
        {
            "mon" or "monday" or "lunes" => (dow = DayOfWeek.Monday) == DayOfWeek.Monday,
            "tue" or "tuesday" or "martes" => (dow = DayOfWeek.Tuesday) == DayOfWeek.Tuesday,
            "wed" or "wednesday" or "miercoles" or "miércoles" => (dow = DayOfWeek.Wednesday) == DayOfWeek.Wednesday,
            "thu" or "thursday" or "jueves" => (dow = DayOfWeek.Thursday) == DayOfWeek.Thursday,
            "fri" or "friday" or "viernes" => (dow = DayOfWeek.Friday) == DayOfWeek.Friday,
            "sat" or "saturday" or "sabado" or "sábado" => (dow = DayOfWeek.Saturday) == DayOfWeek.Saturday,
            "sun" or "sunday" or "domingo" => (dow = DayOfWeek.Sunday) == DayOfWeek.Sunday,
            _ => false
        };
    }

    private static string EscapeMd(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var chars = new[] { '_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!' };
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (chars.Contains(c)) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
