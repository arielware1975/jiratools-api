using ApiJiraTools.Configuration;
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

                if (message == null)
                {
                    _logger.LogInformation("Sin alertas para {Project}.", project);
                    continue;
                }

                foreach (var chatId in chatIds)
                {
                    await bot.SendMessage(chatId, message, parseMode: ParseMode.MarkdownV2, cancellationToken: ct);
                    _logger.LogInformation("Alerta enviada a chat {ChatId} para {Project}.", chatId, project);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enviando alerta para {Project}.", project);
            }
        }
    }
}
