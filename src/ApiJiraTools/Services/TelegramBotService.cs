using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
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
    private readonly string _discoveryMapping;
    private static readonly ConcurrentDictionary<long, string> _defaultProject = new();
    private const string DefaultProjectFile = "data/default_projects.json";
    private static readonly object _fileLock = new();

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
        _discoveryMapping = options.Value.DiscoveryProjectMapping;

        LoadDefaultProjects();
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

        // Para comandos que usan projectKey, resolver con el default del chat
        var projectArg = ResolveProjectArg(arg, chatId);

        try
        {
            var response = command switch
            {
                "/start" or "/help" => GetHelpText(chatId),
                "/project" => HandleSetProject(arg, chatId),
                "/projects" => await HandleProjects(ct),
                "/sprints" => await HandleSprints(projectArg, ct),
                "/sprint" => await HandleSprintIssues(arg, ct), // recibe sprint ID, no project
                "/ticket" => await HandleTicket(arg, ct),        // recibe issue key
                "/alerts" => await HandleAlerts(projectArg, ct),
                "/status" => await HandleStatus(projectArg, ct),
                "/velocity" => await HandleVelocity(projectArg, ct),
                "/burndown" => await HandleBurndown(projectArg, ct),
                "/tree" => await HandleTree(arg, ct),             // recibe issue key
                "/recent" => await HandleRecent(projectArg, ct),
                "/epic" => await HandleEpic(arg, ct),             // recibe issue key
                "/release" => await HandleRelease(projectArg, ct),
                "/analyze" => await HandleAnalyze(arg, chatId, bot, ct), // recibe issue key
                "/review" => await HandleReview(arg, chatId, bot, ct),  // recibe issue key
                "/scope" => await HandleScope(projectArg, ct),
                "/resumen" => await HandleResumen(arg, chatId, bot, ct),
                "/ideas" => await HandleIdeas(projectArg, ct),
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

    private static string NeedProjectMsg(string cmd)
        => $"Usá `{cmd} CTA` o seteá un proyecto por defecto con `/project CTA`";

    private static string? ResolveProjectArg(string? arg, long chatId)
    {
        // Si el usuario pasó argumento, usarlo
        if (!string.IsNullOrWhiteSpace(arg)) return arg;
        // Si no, usar el proyecto por defecto del chat
        return _defaultProject.TryGetValue(chatId, out var pk) ? pk : null;
    }

    private static string HandleSetProject(string? arg, long chatId)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            if (_defaultProject.TryGetValue(chatId, out var current))
                return $"📌 Proyecto actual: *{EscapeMd(current)}*\nUsá `/project OTRO` para cambiarlo\\.";
            return "Uso: `/project CTA`\nEstablece el proyecto por defecto para no tener que escribirlo en cada comando\\.";
        }

        var pk = arg.ToUpperInvariant();
        _defaultProject[chatId] = pk;
        SaveDefaultProjects();
        return $"✅ Proyecto por defecto: *{EscapeMd(pk)}*\nTodos los comandos usarán este proyecto si no especificás otro\\.";
    }

    private static string GetHelpText(long chatId)
    {
        var currentProject = _defaultProject.TryGetValue(chatId, out var cp) ? cp : null;
        var projectInfo = currentProject != null
            ? $"_📌 Proyecto activo: *{EscapeMd(currentProject)}* \\(podés omitir la key en los comandos\\)_"
            : "_Sin proyecto por defecto\\. Usá `/project CTA` para configurarlo\\._";

        return $"""
            *JiraTools API Bot* 🛠️

            {projectInfo}

            *⚙️ Configuración*
            `/project CTA` — Setea proyecto por defecto
            `/project` — Muestra el proyecto actual

            *📋 Proyectos y Sprints*
            `/projects` — Lista todos los proyectos
            `/sprints` — Sprints del proyecto
            `/sprint 285` — Issues del sprint 285

            *🔍 Tickets*
            `/ticket CTA\-922` — Detalle de un ticket
            `/tree CTA\-922` — Contexto completo: épica, hermanos, blockers
            `/epic CTA\-355` — Todos los hijos de una épica

            *📊 Sprint Activo*
            `/status` — Resumen: velocidad, carry\-over, alertas
            `/velocity` — SP completados por persona
            `/burndown` — Progreso diario del sprint
            `/scope` — Análisis de scope del próximo sprint

            *🆕 Actividad*
            `/recent` — Issues creados en los últimos 7 días

            *🚀 Release*
            `/release` — Auditoría del release actual

            *💡 Discovery*
            `/ideas` — Ideas en Ahora y Siguiente
            `/resumen debin` — Resumen ejecutivo de ideas por tema

            *🤖 Análisis IA*
            `/analyze PC\-255` — Análisis inteligente de una idea o issue
            `/review PC\-255` — Revisar formato de idea \(estándar Finket\)

            *🔔 Alertas*
            `/alerts` — Chequeo de alertas ahora

            _Tu chat ID: `{chatId}`_
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
            return NeedProjectMsg("/sprints");

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
            return NeedProjectMsg("/release");

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

    private async Task<string?> HandleResumen(string? arg, long chatId, ITelegramBotClient bot, CancellationToken ct)
    {
        // arg puede ser "debin", "debin CTA", o solo keyword si hay default project
        if (string.IsNullOrWhiteSpace(arg))
        {
            await bot.SendMessage(chatId, "Uso: `/resumen debin`\nBusca ideas que contengan esa palabra y genera un resumen ejecutivo\\.", parseMode: ParseMode.MarkdownV2, cancellationToken: ct);
            return null;
        }

        // Parsear: si hay 2 palabras y la segunda parece project key, usarla
        var argParts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string keyword = argParts[0].ToLowerInvariant();
        string? projectKey = argParts.Length > 1 ? argParts[1].Trim() : null;
        projectKey = ResolveProjectArg(projectKey, chatId);

        if (string.IsNullOrWhiteSpace(projectKey))
        {
            await bot.SendMessage(chatId, "Primero seteá el proyecto con `/project CTA` y después usá `/resumen debin`", parseMode: ParseMode.MarkdownV2, cancellationToken: ct);
            return null;
        }

        await bot.SendMessage(chatId, $"🔍 Buscando ideas con *{EscapeMd(keyword)}* en {EscapeMd(projectKey.ToUpperInvariant())}\\.\\.\\.", parseMode: ParseMode.MarkdownV2, cancellationToken: ct);

        using var scope = _services.CreateScope();
        var jira = scope.ServiceProvider.GetRequiredService<JiraService>();
        var gemini = scope.ServiceProvider.GetRequiredService<GeminiService>();

        var pk = projectKey.ToUpperInvariant();
        var discoveryProject = ResolveDiscoveryProject(pk, _discoveryMapping);

        // Traer ideas de Ahora y Siguiente
        var ideasAhora = await jira.GetDiscoveryIdeasByRoadmapAsync(discoveryProject, "Ahora");
        var ideasSiguiente = await jira.GetDiscoveryIdeasByRoadmapAsync(discoveryProject, "Siguiente");

        var allIdeas = new List<JiraIssue>(ideasAhora);
        foreach (var i in ideasSiguiente)
            if (!allIdeas.Any(x => x.Key == i.Key))
                allIdeas.Add(i);

        // Filtrar por keyword en el nombre
        var matchedIdeas = allIdeas
            .Where(i => (i.Fields?.Summary ?? "").Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matchedIdeas.Count == 0)
        {
            await bot.SendMessage(chatId, $"No encontré ideas con *{EscapeMd(keyword)}* en {EscapeMd(discoveryProject)}\\.", parseMode: ParseMode.MarkdownV2, cancellationToken: ct);
            return null;
        }

        await bot.SendMessage(chatId, $"📊 Encontré {matchedIdeas.Count} idea{(matchedIdeas.Count > 1 ? "s" : "")}\\. Analizando épicas y generando resumen\\.\\.\\.", parseMode: ParseMode.MarkdownV2, cancellationToken: ct);

        // Fetch épicas + children en paralelo
        var epicKeys = matchedIdeas
            .SelectMany(idea => JiraService.GetLinkedEpicsFromIdea(idea)
                .Select(e => e.Key)
                .Where(ek => ek.StartsWith(pk + "-", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var semaphore = new SemaphoreSlim(4);
        var epicInfoMap = new Dictionary<string, EpicInfo>(StringComparer.OrdinalIgnoreCase);

        var epicTasks = epicKeys.Select(async ek =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var epic = await jira.GetIssueByKeyAsync(ek);
                var children = await jira.GetEpicChildIssuesAsync(ek);

                var workIssues = children.Where(c =>
                    !(c.Fields?.Summary ?? "").Contains("STG", StringComparison.OrdinalIgnoreCase) &&
                    !(c.Fields?.Summary?.TrimStart() ?? "").StartsWith("Pasaje", StringComparison.OrdinalIgnoreCase)).ToList();

                DateTime? dt = null;
                if (!string.IsNullOrWhiteSpace(epic.Fields?.DueDate) && DateTime.TryParse(epic.Fields.DueDate, out var parsed))
                    dt = parsed;

                int done = workIssues.Count(c => string.Equals(c.Fields?.Status?.StatusCategory?.Key, "done", StringComparison.OrdinalIgnoreCase));
                int inProg = workIssues.Count(c => string.Equals(c.Fields?.Status?.StatusCategory?.Key, "indeterminate", StringComparison.OrdinalIgnoreCase));
                int toDo = workIssues.Count - done - inProg;

                // SP
                double doneSp = workIssues.Where(c => string.Equals(c.Fields?.Status?.StatusCategory?.Key, "done", StringComparison.OrdinalIgnoreCase)).Sum(c => GetSpValue(c, jira));
                double totalSp = workIssues.Sum(c => GetSpValue(c, jira));

                return new EpicInfo
                {
                    Key = ek,
                    Summary = epic.Fields?.Summary ?? "",
                    TargetDate = dt,
                    TotalIssues = workIssues.Count,
                    Done = done,
                    InProgress = inProg,
                    ToDo = toDo,
                    DoneSp = doneSp,
                    TotalSp = totalSp
                };
            }
            finally { semaphore.Release(); }
        });

        foreach (var info in await Task.WhenAll(epicTasks))
            epicInfoMap[info.Key] = info;

        // Obtener fieldMap para fecha objetivo
        var fieldDefs = await jira.GetFieldsAsync();
        var fieldMap = jira.BuildDiscoveryFieldMap(fieldDefs);

        // ── Construir datos estructurados ──
        var sb = new StringBuilder();
        sb.AppendLine($"*📋 Resumen: {EscapeMd(keyword.ToUpperInvariant())}*");
        sb.AppendLine($"_{matchedIdeas.Count} idea{(matchedIdeas.Count > 1 ? "s" : "")} encontrada{(matchedIdeas.Count > 1 ? "s" : "")}_\n");

        int globalTotal = 0, globalDone = 0, globalInProg = 0;
        double globalTotalSp = 0, globalDoneSp = 0;
        var risks = new List<string>();
        DateTime? earliestDate = null, latestDate = null;

        // Datos para los prompts de Gemini
        var geminiData = new StringBuilder();
        geminiData.AppendLine($"TEMA: {keyword.ToUpperInvariant()}");
        geminiData.AppendLine($"PROYECTO: {pk}");
        geminiData.AppendLine($"FECHA HOY: {DateTime.Today:dd/MM/yyyy}");
        geminiData.AppendLine();

        // Descripciones de las ideas para generar brief funcional
        var ideaDescriptions = new StringBuilder();

        foreach (var idea in matchedIdeas)
        {
            var linkedEpicKeys = JiraService.GetLinkedEpicsFromIdea(idea)
                .Select(e => e.Key)
                .Where(ek => ek.StartsWith(pk + "-", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Fecha de la idea
            DateTime? bestDate = null;
            var ideaTarget = jira.GetDiscoveryTargetDate(idea, fieldMap);
            if (!string.IsNullOrWhiteSpace(ideaTarget) && DateTime.TryParse(ideaTarget, out var ideaDt))
                bestDate = ideaDt;

            int totalIssues = 0, done = 0, inProg = 0, toDo = 0;
            double totalSp = 0, doneSp = 0;
            var epicSummaries = new List<string>();

            foreach (var ek in linkedEpicKeys)
            {
                if (!epicInfoMap.TryGetValue(ek, out var ei)) continue;
                if (!bestDate.HasValue && ei.TargetDate.HasValue) bestDate = ei.TargetDate;
                totalIssues += ei.TotalIssues;
                done += ei.Done;
                inProg += ei.InProgress;
                toDo += ei.ToDo;
                totalSp += ei.TotalSp;
                doneSp += ei.DoneSp;
                epicSummaries.Add($"{ei.Key} ({ei.Done}/{ei.TotalIssues} done, {ei.DoneSp}/{ei.TotalSp} SP)");
            }

            if (bestDate.HasValue)
            {
                if (!earliestDate.HasValue || bestDate < earliestDate) earliestDate = bestDate;
                if (!latestDate.HasValue || bestDate > latestDate) latestDate = bestDate;
            }

            globalTotal += totalIssues;
            globalDone += done;
            globalInProg += inProg;
            globalTotalSp += totalSp;
            globalDoneSp += doneSp;

            // Control icon
            string icon;
            if (linkedEpicKeys.Count == 0) icon = "❓";
            else if (totalIssues > 0 && done == totalIssues) icon = "✅";
            else if (inProg > 0 || done > 0) icon = "🔵";
            else icon = "⬜";

            double pct = totalIssues > 0 ? (double)done / totalIssues * 100 : 0;
            var dateStr = bestDate.HasValue ? bestDate.Value.ToString("dd/MM/yyyy") : "sin fecha";
            var roadmap = ideasAhora.Any(x => x.Key == idea.Key) ? "Ahora" : "Siguiente";

            sb.AppendLine($"{icon} {LinkMd(idea.Key)} — {EscapeMd(idea.Fields?.Summary ?? "")}");
            sb.AppendLine($"   📅 {EscapeMd(dateStr)} \\| 🗂️ {EscapeMd(roadmap)} \\| {done}/{totalIssues} issues \\({EscapeMd($"{pct:0}")}%\\) \\| {EscapeMd($"{doneSp}/{totalSp}")} SP");
            sb.AppendLine();

            // Riesgos
            if (!bestDate.HasValue)
                risks.Add($"`{idea.Key}` sin fecha objetivo");
            else if (bestDate.Value.Date < DateTime.Today && done < totalIssues)
                risks.Add($"`{idea.Key}` fecha vencida \\({EscapeMd(bestDate.Value.ToString("dd/MM"))}\\) y no completada");
            if (linkedEpicKeys.Count == 0)
                risks.Add($"`{idea.Key}` sin épica vinculada");

            // Datos para Gemini
            geminiData.AppendLine($"IDEA: {idea.Key} — {idea.Fields?.Summary}");
            geminiData.AppendLine($"  Roadmap: {roadmap}");
            geminiData.AppendLine($"  Fecha objetivo: {dateStr}");
            geminiData.AppendLine($"  Issues: {done}/{totalIssues} done ({pct:0}%), {inProg} en curso, {toDo} pendientes");
            geminiData.AppendLine($"  Story Points: {doneSp}/{totalSp} completados");
            if (epicSummaries.Count > 0)
                geminiData.AppendLine($"  Épicas: {string.Join("; ", epicSummaries)}");
            geminiData.AppendLine();

            // Recolectar descripción para brief funcional
            var ideaDesc = idea.Fields?.GetDescriptionText() ?? "";
            if (!string.IsNullOrWhiteSpace(ideaDesc))
            {
                ideaDescriptions.AppendLine($"--- IDEA: {idea.Key} — {idea.Fields?.Summary} ---");
                ideaDescriptions.AppendLine(ideaDesc.Length > 3000 ? ideaDesc[..3000] + "\n[...]" : ideaDesc);
                ideaDescriptions.AppendLine();
            }
        }

        // Consolidado
        double globalPct = globalTotal > 0 ? (double)globalDone / globalTotal * 100 : 0;
        sb.AppendLine("*📈 Consolidado:*");
        sb.AppendLine($"  Issues: {globalDone}/{globalTotal} \\({EscapeMd($"{globalPct:0}")}%\\)");
        sb.AppendLine($"  SP: {EscapeMd($"{globalDoneSp}/{globalTotalSp}")}");
        if (earliestDate.HasValue && latestDate.HasValue)
            sb.AppendLine($"  📅 Timeline: {EscapeMd(earliestDate.Value.ToString("dd/MM/yyyy"))} → {EscapeMd(latestDate.Value.ToString("dd/MM/yyyy"))}");

        if (risks.Count > 0)
        {
            sb.AppendLine("\n*🚩 Riesgos:*");
            foreach (var r in risks)
                sb.AppendLine($"  {r}");
        }

        // Enviar datos duros primero
        await bot.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.MarkdownV2, cancellationToken: ct);

        // ── Resumen ejecutivo por idea (un solo call a Gemini) ──
        if (ideaDescriptions.Length > 0)
        {
            try
            {
                var perIdeaPrompt = $"""
                Sos un PM senior. Para cada idea listada abajo, generá un resumen ejecutivo de exactamente 2-3 líneas
                que explique QUÉ hace esa idea desde el punto de vista de negocio y cuál es su estado actual.

                Formato de salida (respetalo exactamente, una idea tras otra):
                [KEY] TÍTULO
                Resumen de 2-3 líneas.

                Reglas:
                - Español, lenguaje de negocio (no técnico)
                - No uses markdown, bullets ni emojis
                - Mencioná el estado (avance, pendiente, en curso) basándote en los datos de progreso
                - Si tiene fecha objetivo, mencionala
                - Sé directo y concreto

                DATOS DE LAS IDEAS:
                {geminiData}

                DESCRIPCIONES:
                {ideaDescriptions}
                """;

                var perIdeaResult = await gemini.GenerateAsync(perIdeaPrompt);
                if (!string.IsNullOrWhiteSpace(perIdeaResult) && !perIdeaResult.StartsWith("Error"))
                {
                    var perIdeaFormatted = $"📝 *Resumen por idea:*\n\n{EscapeMd(perIdeaResult.Trim())}";
                    await SendLongMessage(bot, chatId, perIdeaFormatted, ct, ParseMode.MarkdownV2);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error generando resumen por idea para /resumen");
            }
        }

        // Generar resumen IA
        geminiData.AppendLine($"CONSOLIDADO: {globalDone}/{globalTotal} issues ({globalPct:0}%), {globalDoneSp}/{globalTotalSp} SP");
        if (earliestDate.HasValue)
            geminiData.AppendLine($"TIMELINE: {earliestDate.Value:dd/MM/yyyy} a {latestDate?.ToString("dd/MM/yyyy") ?? "?"}");
        if (risks.Count > 0)
            geminiData.AppendLine($"RIESGOS: {risks.Count}");

        var aiPrompt = $"""
        Sos un PM senior. Generá un resumen ejecutivo breve (máximo 6-8 líneas) de este grupo de ideas relacionadas para presentar a la dirección.

        El resumen debe incluir:
        1. Estado general del tema (una línea)
        2. Qué está más avanzado y qué está más atrasado
        3. Riesgos o puntos de atención
        4. Próximos hitos / qué se espera a corto plazo
        5. Si hay algo bloqueado o preocupante

        Sé conciso, directo y en español. No uses markdown. No repitas datos que ya se muestran en el reporte.

        DATOS:
        {geminiData}
        """;

        try
        {
            var aiResult = await gemini.GenerateAsync(aiPrompt);
            if (!string.IsNullOrWhiteSpace(aiResult) && !aiResult.StartsWith("Error"))
            {
                var aiFormatted = $"🤖 *Análisis IA:*\n\n{EscapeMd(aiResult.Trim())}";
                await bot.SendMessage(chatId, aiFormatted, parseMode: ParseMode.MarkdownV2, cancellationToken: ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error generando resumen IA para /resumen");
        }

        // ── Brief funcional de negocio ──
        if (ideaDescriptions.Length > 0)
        {
            try
            {
                var briefPrompt = $"""
                Sos un analista funcional senior. A partir de las descripciones de estas ideas de producto relacionadas entre sí,
                generá una descripción funcional de negocio unificada del proyecto/tema "{keyword.ToUpperInvariant()}".

                La descripción debe:
                1. Explicar en 1-2 párrafos QUÉ es el proyecto y QUÉ problema de negocio resuelve
                2. Describir brevemente las funcionalidades principales (no listar ideas, sino capacidades de negocio)
                3. Mencionar integraciones con otros sistemas si las hay
                4. Identificar el valor de negocio / beneficio para el usuario final
                5. Ser comprensible para alguien NO técnico (directivos, stakeholders)

                Máximo 10-12 líneas. Español. Sin markdown. Sin bullets. Redacción fluida tipo documento ejecutivo.
                No menciones keys de Jira ni términos técnicos de gestión de proyecto.

                DESCRIPCIONES DE LAS IDEAS:
                {ideaDescriptions}
                """;

                var briefResult = await gemini.GenerateAsync(briefPrompt);
                if (!string.IsNullOrWhiteSpace(briefResult) && !briefResult.StartsWith("Error"))
                {
                    var briefFormatted = $"📄 *Descripción funcional:*\n\n{EscapeMd(briefResult.Trim())}";
                    await bot.SendMessage(chatId, briefFormatted, parseMode: ParseMode.MarkdownV2, cancellationToken: ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error generando brief funcional para /resumen");
            }
        }

        return null;
    }

    private static double GetSpValue(JiraIssue issue, JiraService jira)
    {
        if (issue?.Fields == null) return 0d;
        var sp = issue.Fields.GetStoryPointsValue(jira.StoryPointsFieldId);
        return sp > 0 ? sp : issue.Fields.GetStoryPointEstimateValue(jira.StoryPointEstimateFieldId);
    }

    private async Task<string> HandleIdeas(string? projectKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectKey))
            return NeedProjectMsg("/ideas");

        using var scope = _services.CreateScope();
        var jira = scope.ServiceProvider.GetRequiredService<JiraService>();

        var pk = projectKey.ToUpperInvariant();
        var discoveryProject = ResolveDiscoveryProject(pk, _discoveryMapping);

        // Traer ideas de Ahora y Siguiente
        var ideasAhora = await jira.GetDiscoveryIdeasByRoadmapAsync(discoveryProject, "Ahora");
        var ideasSiguiente = await jira.GetDiscoveryIdeasByRoadmapAsync(discoveryProject, "Siguiente");

        if (ideasAhora.Count == 0 && ideasSiguiente.Count == 0)
            return $"No hay ideas en *Ahora* ni *Siguiente* en *{EscapeMd(discoveryProject)}*\\.";

        // Recolectar épicas linkeadas únicas
        var allIdeas = new List<JiraIssue>(ideasAhora);
        foreach (var i in ideasSiguiente)
            if (!allIdeas.Any(x => x.Key == i.Key))
                allIdeas.Add(i);

        var epicKeys = allIdeas
            .SelectMany(idea => JiraService.GetLinkedEpicsFromIdea(idea)
                .Select(e => e.Key)
                .Where(ek => ek.StartsWith(pk + "-", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Fetch épicas + children en paralelo (máx 4 concurrent)
        var semaphore = new SemaphoreSlim(4);
        var epicInfoMap = new Dictionary<string, EpicInfo>(StringComparer.OrdinalIgnoreCase);

        var epicTasks = epicKeys.Select(async ek =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var epic = await jira.GetIssueByKeyAsync(ek);
                var children = await jira.GetEpicChildIssuesAsync(ek);

                // Filtrar cards operativas (STG, Pasaje)
                var workIssues = children.Where(c =>
                    !(c.Fields?.Summary ?? "").Contains("STG", StringComparison.OrdinalIgnoreCase) &&
                    !(c.Fields?.Summary?.TrimStart() ?? "").StartsWith("Pasaje", StringComparison.OrdinalIgnoreCase)).ToList();

                DateTime? dt = null;
                if (!string.IsNullOrWhiteSpace(epic.Fields?.DueDate) && DateTime.TryParse(epic.Fields.DueDate, out var parsed))
                    dt = parsed;

                int done = workIssues.Count(c => string.Equals(c.Fields?.Status?.StatusCategory?.Key, "done", StringComparison.OrdinalIgnoreCase));
                int inProg = workIssues.Count(c => string.Equals(c.Fields?.Status?.StatusCategory?.Key, "indeterminate", StringComparison.OrdinalIgnoreCase));
                int toDo = workIssues.Count - done - inProg;

                return new EpicInfo
                {
                    Key = ek,
                    Summary = epic.Fields?.Summary ?? "",
                    TargetDate = dt,
                    TotalIssues = workIssues.Count,
                    Done = done,
                    InProgress = inProg,
                    ToDo = toDo
                };
            }
            finally { semaphore.Release(); }
        });

        foreach (var info in await Task.WhenAll(epicTasks))
            epicInfoMap[info.Key] = info;

        // Obtener fieldMap para fecha objetivo del custom field de la idea
        var fieldDefs = await jira.GetFieldsAsync();
        var fieldMap = jira.BuildDiscoveryFieldMap(fieldDefs);

        // Construir info por idea (agregar datos de todas sus épicas)
        var ideaInfoMap = new Dictionary<string, IdeaInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var idea in allIdeas)
        {
            var linkedEpicKeys = JiraService.GetLinkedEpicsFromIdea(idea)
                .Select(e => e.Key)
                .Where(ek => ek.StartsWith(pk + "-", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Prioridad 1: custom field "Objetivo del proyecto" de la idea
            DateTime? bestDate = null;
            var ideaTarget = jira.GetDiscoveryTargetDate(idea, fieldMap);
            if (!string.IsNullOrWhiteSpace(ideaTarget) && DateTime.TryParse(ideaTarget, out var ideaDt))
                bestDate = ideaDt;

            int totalIssues = 0, done = 0, inProg = 0, toDo = 0;
            bool hasMissingDate = false;
            bool hasOverdueDate = false;
            var nameMismatches = new List<string>();

            foreach (var ek in linkedEpicKeys)
            {
                if (!epicInfoMap.TryGetValue(ek, out var ei)) continue;

                // Verificar nombre idea vs épica
                if (!NamesMatch(idea.Fields?.Summary ?? "", ei.Summary))
                    nameMismatches.Add($"{ek}: {ei.Summary}");

                // Prioridad 2: DueDate de la épica (solo si no hay fecha de la idea)
                if (ei.TargetDate.HasValue)
                {
                    if (!bestDate.HasValue || ei.TargetDate.Value > bestDate.Value)
                        bestDate = ei.TargetDate;
                    if (ei.TargetDate.Value.Date < DateTime.Today)
                        hasOverdueDate = true;
                }
                else
                    hasMissingDate = true;

                totalIssues += ei.TotalIssues;
                done += ei.Done;
                inProg += ei.InProgress;
                toDo += ei.ToDo;
            }

            // Si la fecha viene del custom field de la idea, verificar si está vencida
            if (bestDate.HasValue && bestDate.Value.Date < DateTime.Today)
                hasOverdueDate = true;

            // Determinar icono de control
            string icon;
            if (linkedEpicKeys.Count == 0)
                icon = "❓"; // sin épica
            else if (totalIssues > 0 && done == totalIssues)
                icon = "✅"; // todo done
            else if (hasMissingDate || hasOverdueDate)
                icon = "⚠️"; // problemas
            else if (inProg > 0 || done > 0)
                icon = "🔵"; // en progreso, sin problemas
            else
                icon = "⬜"; // sin avance

            ideaInfoMap[idea.Key] = new IdeaInfo
            {
                TargetDate = bestDate,
                TotalIssues = totalIssues,
                Done = done,
                InProgress = inProg,
                ToDo = toDo,
                Icon = icon,
                EpicCount = linkedEpicKeys.Count,
                NameMismatches = nameMismatches
            };
        }

        var sb = new StringBuilder();
        sb.AppendLine($"*💡 Ideas \\- {EscapeMd(discoveryProject)}*\n");

        void AppendBucket(string label, string emoji, List<JiraIssue> list)
        {
            if (list.Count == 0) return;
            sb.AppendLine($"*{emoji} {EscapeMd(label)}* \\({list.Count}\\)");
            foreach (var idea in list)
            {
                var info = ideaInfoMap.GetValueOrDefault(idea.Key);
                var icon = info?.Icon ?? "❓";
                var status = idea.Fields?.Status?.Name ?? "";
                var targetText = "";
                if (info?.TargetDate != null)
                    targetText = $" \\| 🎯 {EscapeMd(info.TargetDate.Value.ToString("dd/MM/yyyy"))}";

                var progressText = "";
                if (info != null && info.TotalIssues > 0)
                    progressText = $" \\| {info.Done}/{info.TotalIssues}";

                sb.AppendLine($"{icon} {LinkMd(idea.Key)} \\| {EscapeMd(status)}{targetText}{progressText}");
                sb.AppendLine($"    {EscapeMd(idea.Fields?.Summary ?? "")}");

                if (info?.NameMismatches?.Count > 0)
                {
                    foreach (var mm in info.NameMismatches)
                        sb.AppendLine($"    📛 _{EscapeMd(mm)}_");
                }
            }
            sb.AppendLine();
        }

        AppendBucket("Ahora", "🔴", ideasAhora);
        AppendBucket("Siguiente", "🟡", ideasSiguiente);

        sb.AppendLine("_Leyenda: ✅ Done \\| ⚠️ Sin fecha o vencida \\| 🔵 En curso \\| ⬜ Sin avance \\| ❓ Sin épica_");
        sb.AppendLine("_🎯 Fecha target \\| done/total \\| 📛 Nombre idea ≠ épica_");

        return sb.ToString();
    }

    /// <summary>
    /// Compara nombre de idea con nombre de épica. Tolera prefijos (A), B), etc.) y normaliza.
    /// </summary>
    private static bool NamesMatch(string ideaName, string epicName)
    {
        static string Normalize(string s)
        {
            // Quitar prefijos tipo "A) ", "B) ", "C) ", "1) ", "A- ", etc.
            s = System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"^[A-Za-z0-9]{1,3}[\)\-\.]\s*", "");
            // Quitar caracteres especiales y espacios extra
            s = s.Trim().ToLowerInvariant();
            // Normalizar guiones, underscores a espacio
            s = s.Replace('-', ' ').Replace('_', ' ');
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s;
        }

        var n1 = Normalize(ideaName);
        var n2 = Normalize(epicName);

        if (string.IsNullOrWhiteSpace(n1) || string.IsNullOrWhiteSpace(n2))
            return true; // no podemos comparar, no alertar

        // Match exacto después de normalizar
        if (n1 == n2) return true;

        // Uno contiene al otro
        if (n1.Contains(n2) || n2.Contains(n1)) return true;

        // Palabras significativas en común (>= 50% de las palabras de la idea)
        var words1 = n1.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToHashSet();
        var words2 = n2.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToHashSet();
        if (words1.Count == 0) return true;
        int common = words1.Count(w => words2.Contains(w));
        return (double)common / words1.Count >= 0.5;
    }

    private record EpicInfo
    {
        public string Key { get; init; } = "";
        public string Summary { get; init; } = "";
        public DateTime? TargetDate { get; init; }
        public int TotalIssues { get; init; }
        public int Done { get; init; }
        public int InProgress { get; init; }
        public int ToDo { get; init; }
        public double DoneSp { get; init; }
        public double TotalSp { get; init; }
    }

    private record IdeaInfo
    {
        public DateTime? TargetDate { get; init; }
        public int TotalIssues { get; init; }
        public int Done { get; init; }
        public int InProgress { get; init; }
        public int ToDo { get; init; }
        public string Icon { get; init; } = "";
        public int EpicCount { get; init; }
        public List<string> NameMismatches { get; init; } = new();
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

    private async Task<string?> HandleAnalyze(string? issueKey, long chatId, ITelegramBotClient bot, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(issueKey))
        {
            await bot.SendMessage(chatId, "Uso: /analyze PC\\-255 o /analyze CTA\\-922", parseMode: ParseMode.MarkdownV2, cancellationToken: ct);
            return null; // ya mandamos respuesta
        }

        // Mandar mensaje de "procesando" porque tarda
        await bot.SendMessage(chatId, $"🤖 Analizando `{EscapeMd(issueKey.ToUpperInvariant())}`\\.\\.\\. esto puede tardar unos segundos\\.", parseMode: ParseMode.MarkdownV2, cancellationToken: ct);

        using var scope = _services.CreateScope();
        var analyze = scope.ServiceProvider.GetRequiredService<AnalyzeService>();

        var result = await analyze.AnalyzeIssueAsync(issueKey.ToUpperInvariant());

        // Partir en chunks de 4000 chars (Telegram limit = 4096)
        // Post-procesar: convertir issue keys en links clickeables
        var linkedResult = FormatGeminiOutput(result);
        await SendLongMessage(bot, chatId, linkedResult, ct);
        return null; // ya mandamos la respuesta directamente
    }

    private async Task<string?> HandleReview(string? issueKey, long chatId, ITelegramBotClient bot, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(issueKey))
        {
            await bot.SendMessage(chatId, "Uso: `/review PC\\-255`\nRevisa el formato de una idea contra el estándar Finket\\.", parseMode: ParseMode.MarkdownV2, cancellationToken: ct);
            return null;
        }

        await bot.SendMessage(chatId, $"📝 Revisando formato de `{EscapeMd(issueKey.ToUpperInvariant())}`\\.\\.\\.", parseMode: ParseMode.MarkdownV2, cancellationToken: ct);

        using var scope = _services.CreateScope();
        var jira = scope.ServiceProvider.GetRequiredService<JiraService>();
        var gemini = scope.ServiceProvider.GetRequiredService<GeminiService>();

        var issue = await jira.GetIssueByKeyAsync(issueKey.ToUpperInvariant());
        var description = issue.Fields?.GetDescriptionText() ?? "";
        var summary = issue.Fields?.Summary ?? "";

        if (string.IsNullOrWhiteSpace(description))
        {
            await bot.SendMessage(chatId, "⚠️ La idea no tiene descripción\\. No se puede revisar\\.", parseMode: ParseMode.MarkdownV2, cancellationToken: ct);
            return null;
        }

        // Truncar descripción si es muy larga para no exceder límites de Gemini
        if (description.Length > 6000)
            description = description[..6000] + "\n\n[... descripción truncada ...]";

        var prompt = BuildReviewPrompt(issueKey.ToUpperInvariant(), summary, description);
        var result = await gemini.GenerateAsync(prompt);

        var formatted = FormatGeminiOutput(result);
        await SendLongMessage(bot, chatId, formatted, ct);
        return null;
    }

    private static string BuildReviewPrompt(string key, string summary, string description)
    {
        return $"""
        Sos un analista de producto senior. Revisá esta idea de Jira contra el estándar de formato de ideas Finket.

        ## Idea: {key} — {summary}

        ### Descripción actual:
        {description}

        ## Estándar de formato Finket

        Cada idea DEBE tener estas 5 secciones:

        **1. Problema** (máx 5 líneas)
        - ¿Qué está pasando hoy y por qué es un problema?
        - Incluir impacto (negocio / operación / riesgo)

        **2. Solución** (máx 3 líneas)
        - ¿Qué queremos lograr o construir?
        - Sin detalles técnicos

        **3. ¿Qué vamos a necesitar hacer?** (4-6 puntos máx)
        - Bloques de trabajo conceptuales (NO tareas)
        - Ejemplos: recepción de solicitudes, validaciones de negocio, integración con sistema externo
        - Si hay más de 6 puntos → está mal (nivel épica)
        - Subpuntos solo para reglas importantes (ej: "1.a considerar ventana de 30 días")
        - NO incluir: endpoints, tokens, APIs, tablas, código, implementación

        **4. Factibilidad** (máx 5 líneas)
        - Nivel: Alta / Media / Baja
        - Justificación breve
        - Riesgos si aplica

        **5. Recursos**
        - Links a PRD/spec, Loom, Design files

        ## Reglas clave
        - La idea debe leerse en 30-60 segundos
        - Debe permitir estimación preliminar
        - Describir QUÉ construir, no CÓMO
        - Si un dev puede discutirlo en detalle técnico → no va en la idea
        - Idea = qué construir | Épica = cómo hacerlo | Task = cómo se implementa

        ## Tu respuesta debe tener:

        **1. Checklist** (✅ cumple / ❌ no cumple):
        - [ ] ¿Se entiende en menos de 1 minuto?
        - [ ] ¿Tiene las 5 secciones?
        - [ ] ¿Máximo 6 puntos en "qué hacer"?
        - [ ] ¿Sin detalle técnico?
        - [ ] ¿Agrupado y no fragmentado?
        - [ ] ¿Se puede estimar con esto?

        **2. Observaciones**: qué falta, qué sobra, qué está en nivel incorrecto (debería ir en épica/task)

        **3. Texto propuesto**: La idea completa reescrita en el formato correcto. Poné el texto listo para copiar y pegar en Jira. Usá la info existente de la descripción y completá lo que falte con sugerencias razonables marcadas con [COMPLETAR].

        Respondé en español. Sé directo y concreto.
        """;
    }

    private async Task<string> HandleAlerts(string? projectKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectKey))
            return NeedProjectMsg("/alerts");

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
            return NeedProjectMsg("/status");

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
            return NeedProjectMsg("/velocity");

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
            return NeedProjectMsg("/burndown");

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

    private async Task<string> HandleScope(string? projectKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectKey))
            return NeedProjectMsg("/scope");

        using var scope = _services.CreateScope();
        var jira = scope.ServiceProvider.GetRequiredService<JiraService>();
        var scopeService = scope.ServiceProvider.GetRequiredService<ScopeAnalysisService>();

        var pk = projectKey.ToUpperInvariant();
        var sprints = await jira.GetSprintsByProjectAsync(pk);

        var activeSprint = sprints.FirstOrDefault(s =>
            s.State.Equals("active", StringComparison.OrdinalIgnoreCase));
        if (activeSprint == null)
            return $"No hay sprint activo en `{EscapeMd(pk)}`\\.";

        // Buscar el próximo sprint (future, o el que le sigue al activo por fecha)
        var nextSprint = sprints
            .Where(s => s.State.Equals("future", StringComparison.OrdinalIgnoreCase)
                     || (s.StartDate.HasValue && activeSprint.EndDate.HasValue && s.StartDate > activeSprint.EndDate))
            .OrderBy(s => s.StartDate ?? DateTimeOffset.MaxValue)
            .FirstOrDefault();

        if (nextSprint == null)
            return $"No se encontró un sprint futuro en `{EscapeMd(pk)}`\\. Creá el próximo sprint en Jira\\.";

        var result = await scopeService.BuildAsync(pk, activeSprint, nextSprint);

        var sb = new StringBuilder();

        // Semáforo
        var statusEmoji = result.Status switch
        {
            ScopeStatus.Verde => "🟢",
            ScopeStatus.Amarillo => "🟡",
            ScopeStatus.Rojo => "🔴",
            _ => "⚪"
        };

        sb.AppendLine($"{statusEmoji} *Scope Analysis \\— {EscapeMd(result.NextSprintName)}*\n");

        // Fechas clave
        if (result.NextSprintStart.HasValue)
            sb.AppendLine($"📅 *Inicio:* {EscapeMd(result.NextSprintStart.Value.LocalDateTime.ToString("dd/MM/yyyy"))}");
        if (result.StgTargetDate.HasValue)
            sb.AppendLine($"🧪 *STG:* {EscapeMd(result.StgTargetDate.Value.ToString("dd/MM/yyyy"))}");
        if (result.ProdPlanningDate.HasValue)
            sb.AppendLine($"🚀 *PROD/Planning:* {EscapeMd(result.ProdPlanningDate.Value.ToString("dd/MM/yyyy"))}");
        if (result.BusinessDays > 0)
            sb.AppendLine($"🗓️ *Días hábiles:* {result.BusinessDays}");

        sb.AppendLine();

        // Velocidad
        sb.AppendLine("*📊 Velocidad \\(últimos 3 sprints\\):*");
        foreach (var v in result.VelocityData)
            sb.AppendLine($"  {EscapeMd(v.SprintName)}: `{v.DoneSp}` SP");
        sb.AppendLine($"  *Promedio: `{result.AvgVelocity}` SP*");

        sb.AppendLine();

        // Carry-over
        sb.AppendLine($"*♻️ Carry\\-over del sprint actual* \\({EscapeMd(result.CurrentSprintName)}\\)*:*");
        sb.AppendLine($"  To Do: {result.CarryOverToDoIssues.Count} issues \\(`{result.CarryOverToDoSp}` SP — 100%\\)");
        sb.AppendLine($"  En curso: {result.CarryOverInProgressIssues.Count} issues \\(`{result.CarryOverInProgressSpRaw}` SP × {EscapeMd($"{result.CarryOverInProgressFactor:0.#}")} \\= `{result.CarryOverInProgressSpEffective}` SP\\)");
        sb.AppendLine($"  *Total carry\\-over: `{result.CarryOverSp}` SP*");

        if (result.CarryOverIssues.Count > 0)
        {
            sb.AppendLine();
            foreach (var i in result.CarryOverIssues.Take(10))
            {
                var sp = GetSpDisplay(i, jira);
                var statusIcon = IsToDoStatusStatic(i) ? "⬜" : "🔵";
                sb.AppendLine($"  {statusIcon} {LinkMd(i.Key)} {sp}SP — {EscapeMd(Truncate(i.Fields?.Summary ?? "", 35))}");
            }
            if (result.CarryOverIssues.Count > 10)
                sb.AppendLine($"  _\\.\\.\\.y {result.CarryOverIssues.Count - 10} más_");
        }

        sb.AppendLine();

        // Próximo sprint
        sb.AppendLine($"*🆕 Sprint próximo* \\({EscapeMd(result.NextSprintName)}\\)*:*");
        sb.AppendLine($"  {result.NextSprintIssues.Count} issues — `{result.NextSprintSp}` SP");

        if (result.NextSprintIssues.Count > 0)
        {
            sb.AppendLine();
            foreach (var i in result.NextSprintIssues.Take(10))
            {
                var sp = GetSpDisplay(i, jira);
                sb.AppendLine($"  ▪️ {LinkMd(i.Key)} {sp}SP — {EscapeMd(Truncate(i.Fields?.Summary ?? "", 35))}");
            }
            if (result.NextSprintIssues.Count > 10)
                sb.AppendLine($"  _\\.\\.\\.y {result.NextSprintIssues.Count - 10} más_");
        }

        sb.AppendLine();

        // Resumen / proyección
        sb.AppendLine("*📐 Proyección:*");
        sb.AppendLine($"  Carry\\-over: `{result.CarryOverSp}` SP");
        sb.AppendLine($"  Sprint nuevo: `{result.NextSprintSp}` SP");
        sb.AppendLine($"  *Total comprometido: `{result.TotalCommitted}` SP*");
        sb.AppendLine($"  *Velocidad promedio: `{result.AvgVelocity}` SP*");

        var diffSign = result.Diff >= 0 ? "\\+" : "";
        sb.AppendLine($"  *Diferencia: {diffSign}{EscapeMd($"{result.Diff:0.#}")} SP* {(result.Diff >= 0 ? "✅" : "⚠️")}");

        // Alertas
        var alerts = new List<string>();
        if (result.CarryOverNoSp.Count > 0)
            alerts.Add($"⚠️ {result.CarryOverNoSp.Count} issue{(result.CarryOverNoSp.Count > 1 ? "s" : "")} carry\\-over sin SP");
        if (result.NextNoSp.Count > 0)
            alerts.Add($"⚠️ {result.NextNoSp.Count} issue{(result.NextNoSp.Count > 1 ? "s" : "")} próximo sprint sin SP");
        if (result.CarryOverUnassigned.Count > 0)
            alerts.Add($"👤 {result.CarryOverUnassigned.Count} carry\\-over sin asignar");
        if (result.NextUnassigned.Count > 0)
            alerts.Add($"👤 {result.NextUnassigned.Count} próximo sprint sin asignar");

        if (alerts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("*⚡ Alertas:*");
            foreach (var a in alerts)
                sb.AppendLine($"  {a}");
        }

        return sb.ToString();
    }

    private static string GetSpDisplay(JiraIssue issue, JiraService jira)
    {
        if (issue?.Fields == null) return "0";
        var sp = issue.Fields.GetStoryPointsValue(jira.StoryPointsFieldId);
        if (sp <= 0) sp = issue.Fields.GetStoryPointEstimateValue(jira.StoryPointEstimateFieldId);
        return $"`{sp}`";
    }

    private static bool IsToDoStatusStatic(JiraIssue issue)
    {
        var key = issue?.Fields?.Status?.StatusCategory?.Key ?? string.Empty;
        return !key.Equals("indeterminate", StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "...";

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
            return NeedProjectMsg("/recent");

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

    /// <summary>Converts Gemini markdown output to Telegram HTML with clickable issue links.</summary>
    private string FormatGeminiOutput(string text)
    {
        // Convert **bold** to <b>bold</b>
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*\*(.+?)\*\*", "<b>$1</b>");

        // Convert markdown list bullets (* item) to clean bullets (• item)
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^\s*\*\s+", "• ");

        // Convert remaining *italic* to <i>italic</i>
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<![<])\*(.+?)\*", "<i>$1</i>");

        // Replace issue keys (CTA-123, PC-255) with clickable links
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"\b([A-Z]{2,6}-\d+)\b",
            m => $"<a href=\"{_jiraBaseUrl}/browse/{m.Value}\">{m.Value}</a>");

        return text;
    }

    private static async Task SendLongMessage(ITelegramBotClient bot, long chatId, string text, CancellationToken ct, ParseMode parseMode = ParseMode.Html)
    {
        const int maxLen = 4000;
        if (text.Length <= maxLen)
        {
            await bot.SendMessage(chatId, text, parseMode: parseMode, cancellationToken: ct);
            return;
        }

        var lines = text.Split('\n');
        var chunk = new StringBuilder();
        foreach (var line in lines)
        {
            if (chunk.Length + line.Length + 1 > maxLen)
            {
                await bot.SendMessage(chatId, chunk.ToString(), parseMode: parseMode, cancellationToken: ct);
                chunk.Clear();
            }
            chunk.AppendLine(line);
        }
        if (chunk.Length > 0)
            await bot.SendMessage(chatId, chunk.ToString(), parseMode: parseMode, cancellationToken: ct);
    }

    /// <summary>Creates a clickable Jira link in MarkdownV2 format.</summary>
    private string LinkMd(string issueKey)
    {
        var url = $"{_jiraBaseUrl}/browse/{issueKey}";
        // In MarkdownV2 links: [text](url) — escape only inside text, URL needs ) and \ escaped
        var escapedUrl = url.Replace("\\", "\\\\").Replace(")", "\\)");
        return $"[{EscapeMd(issueKey)}]({escapedUrl})";
    }

    private void LoadDefaultProjects()
    {
        try
        {
            if (!File.Exists(DefaultProjectFile)) return;
            var json = File.ReadAllText(DefaultProjectFile);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (dict == null) return;
            foreach (var kv in dict)
            {
                if (long.TryParse(kv.Key, out var chatId))
                    _defaultProject[chatId] = kv.Value;
            }
            _logger.LogInformation("Cargados {Count} proyectos por defecto desde {File}", dict.Count, DefaultProjectFile);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo cargar {File}", DefaultProjectFile);
        }
    }

    private static void SaveDefaultProjects()
    {
        try
        {
            var dir = Path.GetDirectoryName(DefaultProjectFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var dict = _defaultProject.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
            var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            lock (_fileLock)
            {
                File.WriteAllText(DefaultProjectFile, json);
            }
        }
        catch { /* no bloquear el bot por error de persistencia */ }
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
