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
    private readonly string _jiraBaseUrl;

    public TelegramBotService(
        IServiceProvider services,
        IOptions<TelegramSettings> options,
        IOptions<JiraSettings> jiraOptions,
        ILogger<TelegramBotService> logger)
    {
        _services = services;
        _logger = logger;
        _botToken = options.Value.BotToken;
        _jiraBaseUrl = jiraOptions.Value.BaseUrl.TrimEnd('/');
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
                "/start" or "/help" => GetHelpText(chatId),
                "/projects" => await HandleProjects(ct),
                "/sprints" => await HandleSprints(arg, ct),
                "/sprint" => await HandleSprintIssues(arg, ct),
                "/ticket" => await HandleTicket(arg, ct),
                "/alerts" => await HandleAlerts(arg, ct),
                "/status" => await HandleStatus(arg, ct),
                "/velocity" => await HandleVelocity(arg, ct),
                "/burndown" => await HandleBurndown(arg, ct),
                "/tree" => await HandleTree(arg, ct),
                "/recent" => await HandleRecent(arg, ct),
                "/epic" => await HandleEpic(arg, ct),
                "/release" => await HandleRelease(arg, ct),
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

    private static string GetHelpText(long chatId)
    {
        return $"""
            *JiraTools API Bot* 🛠️

            *📋 Proyectos y Sprints*
            `/projects` — Lista todos los proyectos
            `/sprints CTA` — Sprints del proyecto CTA
            `/sprint 285` — Issues del sprint 285

            *🔍 Tickets*
            `/ticket CTA\-922` — Detalle de un ticket
            `/tree CTA\-922` — Contexto completo: épica, hermanos, blockers
            `/epic CTA\-355` — Todos los hijos de una épica

            *📊 Sprint Activo*
            `/status CTA` — Resumen: velocidad, carry\-over, alertas
            `/velocity CTA` — SP completados por persona
            `/burndown CTA` — Progreso diario del sprint

            *🆕 Actividad*
            `/recent CTA` — Issues creados en los últimos 7 días

            *🚀 Release*
            `/release CTA` — Auditoría del release actual

            *🔔 Alertas*
            `/alerts CTA` — Chequeo de alertas ahora

            _Tu chat ID: `{chatId}`_
            _Reemplazá CTA por la key de tu proyecto_
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
                var spText = sp.HasValue ? $" \\({EscapeMd(sp.Value.ToString())}sp\\)" : "";
                sb.AppendLine($"{icon} {LinkMd(issue.Key)} {EscapeMd(issue.Fields.Summary)}{spText}");
            }
        }

        // Resumen
        var totalSp = issues.Sum(i => i.Fields.StoryPoints ?? i.Fields.StoryPointEstimate ?? 0);
        var doneSp = issues
            .Where(i => i.Fields.Status.StatusCategory.Key == "done")
            .Sum(i => i.Fields.StoryPoints ?? i.Fields.StoryPointEstimate ?? 0);

        sb.AppendLine($"\n*SP:* {EscapeMd($"{doneSp}/{totalSp}")} completados");

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
        sb.AppendLine($"*{LinkMd(issue.Key)}* — {EscapeMd(issue.Fields.Summary)}");
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
            sb.AppendLine($"*Parent:* {LinkMd(issue.Fields.Parent.Key)} {EscapeMd(issue.Fields.Parent.Fields.Summary)}");

        return sb.ToString();
    }

    private async Task<string> HandleRelease(string? projectKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectKey))
            return "Uso: /release CTA";

        using var scope = _services.CreateScope();
        var jira = scope.ServiceProvider.GetRequiredService<JiraService>();

        var pk = projectKey.ToUpperInvariant();
        var sprint = await FindActiveSprint(pk, jira);
        if (sprint == null)
            return $"No hay sprint activo en `{pk}`\\.";

        var issues = await jira.GetSprintIssuesDetailedAsync(sprint.Id);

        // Issues con label prox_release
        var proxRelease = issues.Where(i =>
            i.Fields?.Labels?.Any(l => string.Equals(l, "prox_release", StringComparison.OrdinalIgnoreCase)) == true).ToList();

        // Card de Pasaje a Producción
        var prodCard = issues.FirstOrDefault(i =>
            (i.Fields?.Summary?.TrimStart() ?? "").StartsWith("Pasaje a Producción", StringComparison.OrdinalIgnoreCase));

        // STG cards
        var stgCards = issues.Where(i =>
            (i.Fields?.Summary ?? "").Contains("STG", StringComparison.OrdinalIgnoreCase) &&
            (i.Fields?.Summary ?? "").Contains("Orquestar", StringComparison.OrdinalIgnoreCase) ||
            (i.Fields?.Summary?.TrimStart() ?? "").StartsWith("STG", StringComparison.OrdinalIgnoreCase)).ToList();

        // Clasificar prox_release
        var done = proxRelease.Where(i => (i.Fields?.Status?.StatusCategory?.Key ?? "").Equals("done", StringComparison.OrdinalIgnoreCase)).ToList();
        var pending = proxRelease.Where(i => !(i.Fields?.Status?.StatusCategory?.Key ?? "").Equals("done", StringComparison.OrdinalIgnoreCase)).ToList();
        var inStg = stgCards.Where(i => !(i.Fields?.Status?.StatusCategory?.Key ?? "").Equals("done", StringComparison.OrdinalIgnoreCase)).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"🚀 *RELEASE \\- {EscapeMd(sprint.Name)}*\n");
        sb.AppendLine($"Done: {done.Count}/{proxRelease.Count}");
        sb.AppendLine($"Pendientes: {pending.Count}");
        sb.AppendLine($"En STG: {inStg.Count}");

        if (prodCard != null)
            sb.AppendLine($"Card PROD: {LinkMd(prodCard.Key)} \\[{EscapeMd(prodCard.Fields?.Status?.Name ?? "")}\\]");
        else if (proxRelease.Count > 0)
            sb.AppendLine("⚠️ Sin card de Pasaje a Producción");

        if (done.Count > 0)
        {
            sb.AppendLine("\n*✅ Done*");
            foreach (var i in done)
            {
                var date = i.Fields?.ResolutionDateValue?.ToString("dd/MM") ?? i.Fields?.UpdatedDate?.ToString("dd/MM") ?? "";
                sb.AppendLine($"{LinkMd(i.Key)} \\| {EscapeMd(i.Fields?.Assignee?.DisplayName ?? "Sin asignar")} \\| {EscapeMd(date)} \\| {EscapeMd(i.Fields?.Summary ?? "")}");
            }
        }

        if (pending.Count > 0)
        {
            sb.AppendLine("\n*⏳ Pendientes*");
            foreach (var i in pending)
                sb.AppendLine($"{LinkMd(i.Key)} \\| {EscapeMd(i.Fields?.Status?.Name ?? "")} \\| {EscapeMd(i.Fields?.Assignee?.DisplayName ?? "Sin asignar")} \\| {EscapeMd(i.Fields?.Summary ?? "")}");
        }

        if (inStg.Count > 0)
        {
            sb.AppendLine("\n*🔄 En STG*");
            foreach (var i in inStg)
                sb.AppendLine($"{LinkMd(i.Key)} \\| {EscapeMd(i.Fields?.Status?.Name ?? "")} \\| {EscapeMd(i.Fields?.Summary ?? "")}");
        }
        else
        {
            sb.AppendLine($"\nℹ️ Sin issues prox\\_release en STG\\.");
        }

        return sb.ToString();
    }

    private static string ResolveDiscoveryProject(string projectKey, string mapping)
    {
        if (string.IsNullOrWhiteSpace(mapping)) return projectKey;
        foreach (var pair in mapping.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split(':', 2);
            if (parts.Length == 2 && parts[0].Equals(projectKey, StringComparison.OrdinalIgnoreCase))
                return parts[1].ToUpperInvariant();
        }
        return projectKey;
    }

    private async Task<string> HandleAlerts(string? projectKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectKey))
            return "Uso: /alerts CTA";

        using var scope = _services.CreateScope();
        var alertService = scope.ServiceProvider.GetRequiredService<AlertService>();

        var message = await alertService.BuildDailyAlertAsync(projectKey.ToUpperInvariant());
        return message ?? $"✅ Sin alertas para `{projectKey.ToUpperInvariant()}`\\. Todo bien\\.";
    }

    // ── Nuevos comandos ──────────────────────────────────────────────────

    private async Task<JiraSprint?> FindActiveSprint(string projectKey, JiraService jira)
    {
        var sprints = await jira.GetSprintsByProjectAsync(projectKey.ToUpperInvariant());
        return sprints.FirstOrDefault(s => s.State.Equals("active", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> HandleStatus(string? projectKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectKey))
            return "Uso: /status CTA";

        using var scope = _services.CreateScope();
        var jira = scope.ServiceProvider.GetRequiredService<JiraService>();
        var closure = scope.ServiceProvider.GetRequiredService<SprintClosureService>();

        var sprint = await FindActiveSprint(projectKey, jira);
        if (sprint == null)
            return $"No hay sprint activo en `{projectKey.ToUpperInvariant()}`\\.";

        var project = new JiraProject { Key = projectKey.ToUpperInvariant() };
        var report = await closure.BuildAsync(project, sprint);

        var sb = new StringBuilder();
        sb.AppendLine($"*{EscapeMd(sprint.Name)}*\n");

        double pct = report.CommittedSp > 0 ? report.DoneSp / report.CommittedSp * 100 : 0;
        sb.AppendLine($"*Velocidad:* {EscapeMd($"{report.DoneSp}/{report.CommittedSp}")} SP \\({EscapeMd($"{pct:0}")}%\\)");
        sb.AppendLine($"*Issues:* {report.DoneIssues}/{report.TotalIssues} completados");
        sb.AppendLine($"*Bugs abiertos:* {report.OpenBugsAtClose}");
        sb.AppendLine($"*Carry\\-over:* {report.CarryOverToDo.Count + report.CarryOverInProgress.Count} issues \\({EscapeMd($"{report.CarryOverTotalSp}")} SP\\)");

        if (report.Alerts.Count > 0)
        {
            sb.AppendLine("\n*Alertas:*");
            foreach (var alert in report.Alerts)
                sb.AppendLine($"⚠️ {EscapeMd(alert)}");
        }

        return sb.ToString();
    }

    private async Task<string> HandleVelocity(string? projectKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectKey))
            return "Uso: /velocity CTA";

        using var scope = _services.CreateScope();
        var jira = scope.ServiceProvider.GetRequiredService<JiraService>();
        var closure = scope.ServiceProvider.GetRequiredService<SprintClosureService>();

        var sprint = await FindActiveSprint(projectKey, jira);
        if (sprint == null)
            return $"No hay sprint activo en `{projectKey.ToUpperInvariant()}`\\.";

        var project = new JiraProject { Key = projectKey.ToUpperInvariant() };
        var report = await closure.BuildAsync(project, sprint);

        var sb = new StringBuilder($"*Velocidad por persona \\- {EscapeMd(sprint.Name)}*\n\n");
        foreach (var a in report.ByAssignee.OrderByDescending(x => x.DoneSp))
            sb.AppendLine($"`{a.DoneSp}/{a.TotalSp}` SP — {EscapeMd(a.Name)} \\({a.DoneIssues}/{a.TotalIssues}\\)");

        return sb.ToString();
    }

    private async Task<string> HandleBurndown(string? projectKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectKey))
            return "Uso: /burndown CTA";

        using var scope = _services.CreateScope();
        var jira = scope.ServiceProvider.GetRequiredService<JiraService>();
        var burndown = scope.ServiceProvider.GetRequiredService<BurndownService>();

        var sprint = await FindActiveSprint(projectKey, jira);
        if (sprint == null)
            return $"No hay sprint activo en `{projectKey.ToUpperInvariant()}`\\.";

        var issues = await jira.GetSprintIssuesDetailedAsync(sprint.Id);
        var start = sprint.StartDate?.DateTime ?? DateTime.UtcNow.AddDays(-14);
        var end = sprint.EndDate?.DateTime ?? DateTime.UtcNow;

        var data = burndown.Build(issues, start, end);

        var sb = new StringBuilder($"*Burndown \\- {EscapeMd(sprint.Name)}*\n");
        sb.AppendLine($"Total: {EscapeMd($"{data.TotalSp}")} SP\n");

        // Mostrar solo los puntos con datos reales
        foreach (var p in data.DataPoints.Where(x => x.RemainingActual.HasValue))
        {
            var bar = new string('█', (int)(p.RemainingActual!.Value / Math.Max(1, data.TotalSp) * 20));
            sb.AppendLine($"`{p.Date:dd/MM}` {bar} {EscapeMd($"{p.RemainingActual:0.#}")}");
        }

        var last = data.DataPoints.LastOrDefault(x => x.RemainingActual.HasValue);
        if (last != null)
            sb.AppendLine($"\n*Restante:* {EscapeMd($"{last.RemainingActual:0.#}")} SP \\(ideal: {EscapeMd($"{last.RemainingIdeal}")}\\)");

        return sb.ToString();
    }

    private async Task<string> HandleTree(string? issueKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(issueKey))
            return "Uso: /tree CTA\\-456";

        using var scope = _services.CreateScope();
        var tree = scope.ServiceProvider.GetRequiredService<IssueTreeService>();

        var report = await tree.BuildAsync(issueKey.ToUpperInvariant());

        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(report.IdeaKey))
        {
            sb.AppendLine($"💡 *Idea:* {LinkMd(report.IdeaKey)} {EscapeMd(report.IdeaSummary ?? "")}");
            sb.AppendLine($"   Estado: {EscapeMd(report.IdeaStatus ?? "")}");
            if (!string.IsNullOrEmpty(report.IdeaRoadmap))
                sb.AppendLine($"   Roadmap: {EscapeMd(report.IdeaRoadmap)}");
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(report.EpicKey))
            sb.AppendLine($"📦 *Épica:* {LinkMd(report.EpicKey!)} {EscapeMd(report.EpicSummary ?? "")} \\[{EscapeMd(report.EpicStatus ?? "")}\\]");

        sb.AppendLine($"📋 *Issue:* {LinkMd(report.IssueKey)} {EscapeMd(report.IssueSummary)}");
        sb.AppendLine($"   Estado: {EscapeMd(report.IssueStatus)} \\| {EscapeMd(report.IssueAssignee)}");

        if (report.IssueStoryPoints > 0)
            sb.AppendLine($"   SP: {EscapeMd($"{report.IssueStoryPoints}")}");

        if (report.BlockedBy.Count > 0)
        {
            sb.AppendLine("\n🚫 *Bloqueado por:*");
            foreach (var b in report.BlockedBy)
                sb.AppendLine($"   {LinkMd(b.Key)} {EscapeMd(b.Summary)} \\[{EscapeMd(b.Status)}\\]");
        }

        if (report.Blocks.Count > 0)
        {
            sb.AppendLine("\n🔒 *Bloquea:*");
            foreach (var b in report.Blocks)
                sb.AppendLine($"   {LinkMd(b.Key)} {EscapeMd(b.Summary)} \\[{EscapeMd(b.Status)}\\]");
        }

        if (report.Siblings.Count > 0)
        {
            sb.AppendLine($"\n👥 *Hermanos en épica:* \\({report.Siblings.Count}\\)");
            foreach (var s in report.Siblings.Take(10))
            {
                var icon = s.Status.Contains("Done", StringComparison.OrdinalIgnoreCase) ? "✅" : "🔄";
                sb.AppendLine($"   {icon} {LinkMd(s.Key)} {EscapeMd(s.Summary)}");
            }
        }

        if (report.Children.Count > 0)
        {
            sb.AppendLine($"\n📎 *Hijos:* \\({report.Children.Count}\\)");
            foreach (var c in report.Children.Take(10))
            {
                var icon = c.Status.Contains("Done", StringComparison.OrdinalIgnoreCase) ? "✅" : "🔄";
                sb.AppendLine($"   {icon} {LinkMd(c.Key)} {EscapeMd(c.Summary)}");
            }
        }

        return sb.ToString();
    }

    private async Task<string> HandleRecent(string? projectKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectKey))
            return "Uso: /recent CTA";

        using var scope = _services.CreateScope();
        var jira = scope.ServiceProvider.GetRequiredService<JiraService>();

        string jql = $"project = \"{projectKey.ToUpperInvariant()}\" AND created >= -7d ORDER BY created DESC";
        var issues = await jira.SearchIssuesByJqlAsync(jql, 50);

        if (issues.Count == 0)
            return $"No hay issues nuevos en los últimos 7 días en `{projectKey.ToUpperInvariant()}`\\.";

        var sb = new StringBuilder($"*Issues nuevos \\(7d\\) \\- {projectKey.ToUpperInvariant()}:*\n\n");
        foreach (var issue in issues.Take(20))
        {
            var type = issue.Fields?.IssueType?.Name ?? "";
            sb.AppendLine($"{LinkMd(issue.Key)} {EscapeMd(issue.Fields?.Summary ?? "")} \\[{EscapeMd(type)}\\]");
        }

        if (issues.Count > 20)
            sb.AppendLine($"\n_{issues.Count - 20} más\\.\\.\\._");

        return sb.ToString();
    }

    private async Task<string> HandleEpic(string? epicKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(epicKey))
            return "Uso: /epic CTA\\-355";

        using var scope = _services.CreateScope();
        var jira = scope.ServiceProvider.GetRequiredService<JiraService>();

        var children = await jira.GetEpicChildIssuesAsync(epicKey.ToUpperInvariant());
        if (children.Count == 0)
            return $"No se encontraron hijos para `{epicKey.ToUpperInvariant()}`\\.";

        var sb = new StringBuilder($"*Épica {epicKey.ToUpperInvariant()}* — {children.Count} issues\n\n");

        foreach (var child in children)
        {
            var cat = child.Fields?.Status?.StatusCategory?.Key ?? "";
            var icon = cat switch { "done" => "✅", "indeterminate" => "🔄", _ => "📋" };
            var sp = child.Fields?.StoryPoints ?? child.Fields?.StoryPointEstimate;
            var spText = sp.HasValue ? $" \\({EscapeMd(sp.Value.ToString())}sp\\)" : "";
            sb.AppendLine($"{icon} {LinkMd(child.Key)} {EscapeMd(child.Fields?.Summary ?? "")}{spText}");
        }

        var totalSp = children.Sum(c => c.Fields?.StoryPoints ?? c.Fields?.StoryPointEstimate ?? 0);
        var doneSp = children
            .Where(c => (c.Fields?.Status?.StatusCategory?.Key ?? "").Equals("done", StringComparison.OrdinalIgnoreCase))
            .Sum(c => c.Fields?.StoryPoints ?? c.Fields?.StoryPointEstimate ?? 0);

        sb.AppendLine($"\n*SP:* {EscapeMd($"{doneSp}/{totalSp}")} completados");

        return sb.ToString();
    }

    /// <summary>Creates a clickable Jira link in MarkdownV2 format.</summary>
    private string LinkMd(string issueKey)
    {
        var url = $"{_jiraBaseUrl}/browse/{issueKey}";
        // In MarkdownV2 links: [text](url) — escape only inside text, URL needs ) and \ escaped
        var escapedUrl = url.Replace("\\", "\\\\").Replace(")", "\\)");
        return $"[{EscapeMd(issueKey)}]({escapedUrl})";
    }

    private static string EscapeMd(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
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
