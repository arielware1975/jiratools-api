using System.Text.Json;
using ApiJiraTools.Models;

namespace ApiJiraTools.Services;

/// <summary>
/// Convierte texto natural del usuario en un <see cref="Reminder"/> usando Gemini.
/// </summary>
public sealed class ReminderParser
{
    private readonly GeminiService _gemini;
    private readonly ILogger<ReminderParser> _logger;

    public ReminderParser(GeminiService gemini, ILogger<ReminderParser> logger)
    {
        _gemini = gemini;
        _logger = logger;
    }

    public async Task<(Reminder? reminder, string? error)> ParseAsync(string text, long chatId)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var schema = @"{
  ""message"": ""mensaje del recordatorio sin la parte del cuándo"",
  ""schedule"": {
    ""type"": ""once|daily|weekly|monthly|yearly"",
    ""date"": ""YYYY-MM-DD"",
    ""dayOfWeek"": ""mon|tue|wed|thu|fri|sat|sun"",
    ""dayOfMonth"": 25,
    ""month"": 3,
    ""offsetBusinessDays"": -2,
    ""time"": ""HH:mm""
  }
}";
        var prompt =
            "Parseá este pedido de recordatorio a JSON. Hoy es " + today + " (zona horaria Argentina, UTC-3).\n\n" +
            "Pedido: \"" + text + "\"\n\n" +
            "Respondé EXCLUSIVAMENTE un JSON con este schema (sin markdown, sin explicaciones):\n" +
            schema + "\n\n" +
            "Reglas:\n" +
            "- Si no especifica hora, usar \"09:00\".\n" +
            "- Para \"una sola vez\": type=once, date obligatorio.\n" +
            "- Para \"todos los días\": type=daily, time.\n" +
            "- Para \"todos los lunes/martes/...\": type=weekly, dayOfWeek.\n" +
            "- Para \"día X de cada mes\": type=monthly, dayOfMonth=X.\n" +
            "- Para \"N días hábiles antes/después del día X de cada mes\": type=monthly, dayOfMonth=X, offsetBusinessDays=±N (negativo=antes, positivo=después).\n" +
            "- Para \"todos los X de mes Y\": type=yearly, dayOfMonth=X, month=Y.\n" +
            "- Omití los campos que no apliquen al tipo.\n" +
            "- Si el texto está confuso o no podés inferir, respondé {\"error\": \"no entendí\"}.\n";

        string raw;
        try { raw = await _gemini.GenerateAsync(prompt); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error llamando a Gemini");
            return (null, "No pude procesar el pedido (error con IA).");
        }

        if (string.IsNullOrWhiteSpace(raw))
            return (null, "No pude procesar el pedido.");

        // Sanitizar markdown code fences si vinieron
        var json = raw.Trim();
        if (json.StartsWith("```"))
        {
            var firstNl = json.IndexOf('\n');
            if (firstNl > 0) json = json[(firstNl + 1)..];
            if (json.EndsWith("```")) json = json[..^3];
            json = json.Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errEl))
                return (null, errEl.GetString() ?? "No entendí el pedido.");

            var message = root.GetProperty("message").GetString() ?? "";
            var schedEl = root.GetProperty("schedule");
            var sched = new ReminderSchedule
            {
                Type = schedEl.GetProperty("type").GetString() ?? "once",
                Time = schedEl.TryGetProperty("time", out var t) ? (t.GetString() ?? "09:00") : "09:00",
            };

            if (schedEl.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String)
                sched.Date = d.GetString();
            if (schedEl.TryGetProperty("dayOfWeek", out var dw) && dw.ValueKind == JsonValueKind.String)
                sched.DayOfWeek = dw.GetString();
            if (schedEl.TryGetProperty("dayOfMonth", out var dm) && dm.ValueKind == JsonValueKind.Number)
                sched.DayOfMonth = dm.GetInt32();
            if (schedEl.TryGetProperty("month", out var m) && m.ValueKind == JsonValueKind.Number)
                sched.Month = m.GetInt32();
            if (schedEl.TryGetProperty("offsetBusinessDays", out var ob) && ob.ValueKind == JsonValueKind.Number)
                sched.OffsetBusinessDays = ob.GetInt32();

            return (new Reminder
            {
                ChatId = chatId,
                Message = message,
                Schedule = sched,
                OriginalText = text,
            }, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parseando JSON de Gemini: {Raw}", raw);
            return (null, "No pude entender la respuesta de la IA.");
        }
    }
}
