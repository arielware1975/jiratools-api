using ApiJiraTools.Configuration;
using ApiJiraTools.Models;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace ApiJiraTools.Services;

public class AlertSchedulerService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AlertSchedulerService> _logger;
    private readonly TelegramSettings _settings;

    public AlertSchedulerService(
        IServiceProvider services,
        IOptions<TelegramSettings> options,
        ILogger<AlertSchedulerService> logger)
    {
        _services = services;
        _logger = logger;
        _settings = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.BotToken) ||
            string.IsNullOrWhiteSpace(_settings.AlertChatIds) ||
            string.IsNullOrWhiteSpace(_settings.AlertProjects))
        {
            _logger.LogWarning("Alertas no configuradas (faltan AlertChatIds o AlertProjects). Scheduler deshabilitado.");
            return;
        }

        _logger.LogInformation("Alert scheduler iniciado. Hora UTC: {Hour}, Proyectos: {Projects}, Chats: {Chats}",
            _settings.AlertHourUtc, _settings.AlertProjects, _settings.AlertChatIds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var nextRun = now.Date.AddHours(_settings.AlertHourUtc).AddMinutes(_settings.AlertMinuteUtc);
                if (nextRun <= now)
                    nextRun = nextRun.AddDays(1);

                var delay = nextRun - now;
                _logger.LogInformation("Próxima alerta en {Delay}", delay);
                await Task.Delay(delay, stoppingToken);

                await SendDailyAlerts(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en alert scheduler.");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    private async Task SendDailyAlerts(CancellationToken ct)
    {
        var bot = new TelegramBotClient(_settings.BotToken);
        var chatIds = _settings.AlertChatIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(long.Parse)
            .ToList();
        var projects = _settings.AlertProjects
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        foreach (var project in projects)
        {
            try
            {
                using var scope = _services.CreateScope();
                var alertService = scope.ServiceProvider.GetRequiredService<AlertService>();
                var message = await alertService.BuildDailyAlertAsync(project);

                if (message != null)
                {
                    foreach (var chatId in chatIds)
                    {
                        await bot.SendMessage(chatId, message, parseMode: ParseMode.MarkdownV2, cancellationToken: ct);
                        _logger.LogInformation("Alerta enviada a chat {ChatId} para {Project}.", chatId, project);
                    }
                }
                else
                {
                    _logger.LogInformation("Sin alertas para {Project}.", project);
                }

                // STG check diario
                await SendStgCheckAsync(bot, chatIds, project, scope, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enviando alerta para {Project}.", project);
            }
        }
    }

    private async Task SendStgCheckAsync(TelegramBotClient bot, List<long> chatIds, string project, IServiceScope scope, CancellationToken ct)
    {
        try
        {
            var jira = scope.ServiceProvider.GetRequiredService<JiraService>();
            var stgService = scope.ServiceProvider.GetRequiredService<StgChecklistService>();

            var sprints = await jira.GetSprintsByProjectAsync(project);
            var activeSprint = sprints.FirstOrDefault(s => s.State.Equals("active", StringComparison.OrdinalIgnoreCase));
            if (activeSprint == null)
            {
                _logger.LogInformation("Sin sprint activo en {Project}, se omite STG check.", project);
                return;
            }

            var report = await stgService.BuildAsync(activeSprint.Id, activeSprint.Name);

            if (report.Epics.Count == 0)
                return;

            // Sólo enviar si hay desalineaciones o épicas sin STG
            if (report.Misaligned == 0 && report.WithoutStg == 0)
            {
                _logger.LogInformation("STG check OK para {Project}, sin problemas a reportar.", project);
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"*🧪 STG Check diario \\— {EscapeMd(activeSprint.Name)}*\n");
            sb.AppendLine($"⚠️ {report.Misaligned} épica\\(s\\) desalineada\\(s\\)");
            sb.AppendLine($"❌ {report.WithoutStg} épica\\(s\\) sin card STG\n");

            foreach (var epic in report.Epics.Where(e => e.Alignment != StgAlignment.Ok).Take(10))
            {
                string icon = epic.Alignment switch
                {
                    StgAlignment.Partial => "⚠️",
                    StgAlignment.Empty => "🟡",
                    StgAlignment.NoCard => "❌",
                    _ => "❓"
                };

                sb.AppendLine($"{icon} `{EscapeMd(epic.EpicKey)}` — {EscapeMd(Truncate(epic.EpicSummary, 45))}");
                if (epic.MissingFromStg.Count > 0)
                    sb.AppendLine($"   📛 {epic.MissingFromStg.Count} dev issue\\(s\\) no cubiert\\(os\\)");
                if (!epic.HasStgCard)
                    sb.AppendLine("   _Sin card STG_");
            }

            sb.AppendLine("\n_Usá `/checkstg` para ver el detalle completo_");

            foreach (var chatId in chatIds)
            {
                await bot.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.MarkdownV2, cancellationToken: ct);
                _logger.LogInformation("STG check diario enviado a chat {ChatId} para {Project}.", chatId, project);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generando STG check diario para {Project}.", project);
        }
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "...";

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
