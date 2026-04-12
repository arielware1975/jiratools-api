using System.Text;
using ApiJiraTools.Configuration;
using ApiJiraTools.Models;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace ApiJiraTools.Services;

public class TelegramBotService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<TelegramBotService> _logger;
    private readonly string _botToken;

    public TelegramBotService(
        IServiceProvider services,
        IOptions<TelegramSettings> options,
        ILogger<TelegramBotService> logger)
    {
        _services = services;
        _logger = logger;
        _botToken = options.Value.BotToken;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_botToken))
        {
            _logger.LogWarning("Telegram BotToken no configurado. Bot deshabilitado.");
            return;
        }

        var bot = new TelegramBotClient(_botToken);
        var me = await bot.GetMe(stoppingToken);
        _logger.LogInformation("Telegram bot iniciado: @{BotUsername}", me.Username);

        bot.StartReceiving(
            updateHandler: (client, update, ct) => HandleUpdateAsync(client, update, ct),
            errorHandler: (client, ex, source, ct) =>
            {
                _logger.LogError(ex, "Error en Telegram bot polling.");
                return Task.CompletedTask;
            },
            receiverOptions: new ReceiverOptions
            {
                AllowedUpdates = [UpdateType.Message]
            },
            cancellationToken: stoppingToken
        );

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        if (update.Message?.Text is not { } text)
            return;

        var chatId = update.Message.Chat.Id;
        var parts = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant().Split('@')[0]; // remove @botname
        var arg = parts.Length > 1 ? parts[1].Trim() : null;

        try
        {
            var response = command switch
            {
                "/start" or "/help" => GetHelpText(),
                "/projects" => await HandleProjects(ct),
                "/sprints" => await HandleSprints(arg, ct),
                "/sprint" => await HandleSprintIssues(arg, ct),
                "/ticket" => await HandleTicket(arg, ct),
                _ => null
            };

            if (response != null)
                await bot.SendMessage(chatId, response, parseMode: ParseMode.MarkdownV2, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error procesando comando {Command}", command);
            await bot.SendMessage(chatId, $"Error: {ex.Message}", cancellationToken: ct);
        }
    }

    private static string GetHelpText()
    {
        return """
            *JiraTools API Bot*

            Comandos disponibles:
            /projects — Lista de proyectos
            /sprints CTA — Sprints de un proyecto
            /sprint 123 — Issues de un sprint
            /ticket CTA\-456 — Detalle de un ticket
            """;
    }

    private async Task<string> HandleProjects(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var jira = scope.ServiceProvider.GetRequiredService<JiraService>();

        var projects = await jira.GetProjectsAsync();
        if (projects.Count == 0)
            return "No se encontraron proyectos.";

        var sb = new StringBuilder("*Proyectos:*\n\n");
        foreach (var p in projects)
            sb.AppendLine($"`{p.Key}` — {EscapeMd(p.Name)}");

        return sb.ToString();
    }

    private async Task<string> HandleSprints(string? projectKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectKey))
            return "Uso: /sprints CTA";

        using var scope = _services.CreateScope();
        var jira = scope.ServiceProvider.GetRequiredService<JiraService>();

        var sprints = await jira.GetSprintsByProjectAsync(projectKey.ToUpperInvariant());
        if (sprints.Count == 0)
            return $"No se encontraron sprints para `{projectKey.ToUpperInvariant()}`.";

        // Mostrar los últimos 5 sprints
        var sb = new StringBuilder($"*Sprints de {projectKey.ToUpperInvariant()}:*\n\n");
        foreach (var s in sprints.Take(5))
        {
            var state = s.State switch
            {
                "active" => "🟢",
                "closed" => "⚪",
                _ => "🔵"
            };
            sb.AppendLine($"{state} `{s.Id}` — {EscapeMd(s.Name)}");
        }

        if (sprints.Count > 5)
            sb.AppendLine($"\n_{sprints.Count - 5} sprints más\\.\\.\\._");

        return sb.ToString();
    }

    private async Task<string> HandleSprintIssues(string? sprintIdStr, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sprintIdStr) || !int.TryParse(sprintIdStr, out var sprintId))
            return "Uso: /sprint 123";

        using var scope = _services.CreateScope();
        var jira = scope.ServiceProvider.GetRequiredService<JiraService>();

        var issues = await jira.GetSprintIssuesDetailedAsync(sprintId);
        if (issues.Count == 0)
            return $"No se encontraron issues en sprint `{sprintId}`.";

        var sb = new StringBuilder($"*Sprint {sprintId}* — {issues.Count} issues\n\n");

        var grouped = issues
            .GroupBy(i => i.Fields.Status.StatusCategory.Key)
            .OrderBy(g => g.Key switch { "done" => 0, "indeterminate" => 1, _ => 2 });

        foreach (var group in grouped)
        {
            var icon = group.Key switch
            {
                "done" => "✅",
                "indeterminate" => "🔄",
                _ => "📋"
            };

            foreach (var issue in group)
            {
                var sp = issue.Fields.StoryPoints ?? issue.Fields.StoryPointEstimate;
                var spText = sp.HasValue ? $" \\({sp.Value}sp\\)" : "";
                sb.AppendLine($"{icon} `{issue.Key}` {EscapeMd(issue.Fields.Summary)}{spText}");
            }
        }

        // Resumen
        var totalSp = issues.Sum(i => i.Fields.StoryPoints ?? i.Fields.StoryPointEstimate ?? 0);
        var doneSp = issues
            .Where(i => i.Fields.Status.StatusCategory.Key == "done")
            .Sum(i => i.Fields.StoryPoints ?? i.Fields.StoryPointEstimate ?? 0);

        sb.AppendLine($"\n*SP:* {doneSp}/{totalSp} completados");

        return sb.ToString();
    }

    private async Task<string> HandleTicket(string? issueKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(issueKey))
            return "Uso: /ticket CTA\\-456";

        using var scope = _services.CreateScope();
        var jira = scope.ServiceProvider.GetRequiredService<JiraService>();

        var issue = await jira.GetIssueByKeyAsync(issueKey.ToUpperInvariant());

        var sb = new StringBuilder();
        sb.AppendLine($"*{EscapeMd(issue.Key)}* — {EscapeMd(issue.Fields.Summary)}");
        sb.AppendLine();
        sb.AppendLine($"*Tipo:* {EscapeMd(issue.Fields.IssueType.Name)}");
        sb.AppendLine($"*Estado:* {EscapeMd(issue.Fields.Status.Name)}");
        sb.AppendLine($"*Prioridad:* {EscapeMd(issue.Fields.Priority.Name)}");

        if (issue.Fields.HasAssignee)
            sb.AppendLine($"*Asignado:* {EscapeMd(issue.Fields.Assignee!.DisplayName)}");

        var sp = issue.Fields.StoryPoints ?? issue.Fields.StoryPointEstimate;
        if (sp.HasValue)
            sb.AppendLine($"*Story Points:* {sp.Value}");

        if (issue.Fields.Labels.Count > 0)
            sb.AppendLine($"*Labels:* {EscapeMd(string.Join(", ", issue.Fields.Labels))}");

        var sprint = issue.Fields.GetSprintName();
        if (!string.IsNullOrEmpty(sprint))
            sb.AppendLine($"*Sprint:* {EscapeMd(sprint)}");

        if (issue.Fields.Parent != null)
            sb.AppendLine($"*Parent:* `{issue.Fields.Parent.Key}` {EscapeMd(issue.Fields.Parent.Fields.Summary)}");

        return sb.ToString();
    }

    private static string EscapeMd(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // MarkdownV2 requires escaping these characters
        var chars = new[] { '_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!' };
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (chars.Contains(c))
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
