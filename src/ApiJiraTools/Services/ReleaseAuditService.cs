using System.Text.Json;
using ApiJiraTools.Models;

namespace ApiJiraTools.Services;

public class ReleaseAuditService
{
    private readonly JiraService _jira;
    private readonly ILogger<ReleaseAuditService> _logger;

    public ReleaseAuditService(JiraService jira, ILogger<ReleaseAuditService> logger)
    {
        _jira = jira;
        _logger = logger;
    }

    public async Task<ReleaseAuditReport> BuildAsync(string projectKey, string discoveryProjectKey, int sprintId,
        string sprintName, DateTimeOffset? sprintStart = null, DateTimeOffset? sprintEnd = null)
    {
        _logger.LogInformation("ReleaseAuditService.BuildAsync. project={Project}, discovery={Discovery}, sprint={Sprint}",
            projectKey, discoveryProjectKey, sprintId);

        var report = new ReleaseAuditReport
        {
            ProjectKey = projectKey,
            SprintName = sprintName,
            GeneratedAt = DateTime.UtcNow,
        };

        if (sprintStart.HasValue && sprintEnd.HasValue)
        {
            report.SprintStart = sprintStart.Value.DateTime.Date;
            report.SprintEnd = sprintEnd.Value.DateTime.Date;
            report.FechaStg = CalcularMiercolesSegundaSemana(sprintStart.Value.DateTime.Date);
            report.FechaProd = sprintEnd.Value.DateTime.Date.AddDays(1);
        }

        // Card de Pasaje a Producción
        var sprintIssues = await _jira.GetSprintIssuesDetailedAsync(sprintId);
        var prodCard = sprintIssues.FirstOrDefault(i =>
            (i.Fields?.Summary?.TrimStart() ?? "").StartsWith("Pasaje a Producción", StringComparison.OrdinalIgnoreCase));

        report.HasProdCard = prodCard != null;
        report.ProdCardKey = prodCard?.Key ?? "";
        report.ProdCardStatus = prodCard?.Fields?.Status?.Name ?? "";

        // Ideas de roadmap Ahora + Siguiente
        var ideasAhora = await _jira.GetDiscoveryIdeasByRoadmapAsync(discoveryProjectKey, "Ahora");
        var ideasSiguiente = await _jira.GetDiscoveryIdeasByRoadmapAsync(discoveryProjectKey, "Siguiente");

        var allIdeas = new List<JiraIssue>(ideasAhora);
        foreach (var idea in ideasSiguiente)
            if (!allIdeas.Any(x => x.Key == idea.Key))
                allIdeas.Add(idea);

        int totalEpics = 0, totalIssues = 0, totalProxRelease = 0;

        foreach (var idea in allIdeas)
        {
            var detail = await BuildIdeaDetail(idea, projectKey);
            report.IdeaDetails.Add(detail);
            totalEpics += detail.EpicCount;
            foreach (var epic in detail.Epics)
            {
                totalIssues += epic.WorkIssueCount;
                totalProxRelease += epic.ProxReleaseCount;
            }
        }

        report.TotalIdeas = allIdeas.Count;
        report.TotalEpics = totalEpics;
        report.TotalIssues = totalIssues;
        report.TotalProxRelease = totalProxRelease;

        GenerateAlerts(report);
        return report;
    }

    private async Task<ReleaseAuditIdeaDetail> BuildIdeaDetail(JiraIssue idea, string projectKey)
    {
        var detail = new ReleaseAuditIdeaDetail
        {
            IdeaKey = idea.Key,
            IdeaSummary = idea.Fields?.Summary ?? "",
            IdeaStatus = idea.Fields?.Status?.Name ?? "",
            IdeaRoadmap = _jira.GetDiscoveryRoadmapValue(idea),
            HasDescription = !string.IsNullOrWhiteSpace(idea.Fields?.GetDescriptionText()),
        };

        // Épicas vinculadas en el proyecto de desarrollo
        var linkedEpics = (idea.Fields?.IssueLinks ?? Enumerable.Empty<JiraIssueLink>())
            .Where(link =>
            {
                var linked = link.LinkedIssue;
                return linked != null &&
                       string.Equals(linked.Fields?.IssueType?.Name, "Epic", StringComparison.OrdinalIgnoreCase) &&
                       linked.Key.StartsWith(projectKey + "-", StringComparison.OrdinalIgnoreCase);
            })
            .Select(link => link.LinkedIssue!)
            .ToList();

        detail.EpicCount = linkedEpics.Count;

        foreach (var epicRef in linkedEpics.OrderBy(e => e.Key))
        {
            try
            {
                var epicDetail = await BuildEpicDetail(epicRef.Key);
                detail.Epics.Add(epicDetail);

                if (string.IsNullOrWhiteSpace(detail.IdeaTargetDate) && epicDetail.TargetDate.HasValue)
                    detail.IdeaTargetDate = epicDetail.TargetDate.Value.ToString("yyyy-MM-dd");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error procesando épica {EpicKey}", epicRef.Key);
            }
        }

        return detail;
    }

    private async Task<ReleaseAuditEpicDetail> BuildEpicDetail(string epicKey)
    {
        var epic = await _jira.GetIssueByKeyAsync(epicKey);
        var children = await _jira.GetEpicChildIssuesAsync(epicKey);

        DateTime? targetDate = null;
        if (!string.IsNullOrWhiteSpace(epic.Fields?.DueDate) && DateTime.TryParse(epic.Fields.DueDate, out var parsed))
            targetDate = parsed;

        var stgCard = children.FirstOrDefault(c =>
            (c.Fields?.Summary ?? "").Contains("STG", StringComparison.OrdinalIgnoreCase));

        var workIssues = children.Where(c => !IsOperationalIssue(c)).ToList();
        var proxRelease = workIssues.Where(c =>
            c.Fields?.Labels?.Any(l => string.Equals(l, "prox_release", StringComparison.OrdinalIgnoreCase)) ?? false).ToList();

        return new ReleaseAuditEpicDetail
        {
            EpicKey = epicKey,
            EpicSummary = epic.Fields?.Summary ?? "",
            EpicStatus = epic.Fields?.Status?.Name ?? "",
            HasDescription = !string.IsNullOrWhiteSpace(epic.Fields?.GetDescriptionText()),
            TargetDate = targetDate,
            HasStgCard = stgCard != null,
            StgCardKey = stgCard?.Key ?? "",
            StgCardStatus = stgCard?.Fields?.Status?.Name ?? "",
            ProxReleaseCount = proxRelease.Count,
            ProxReleaseDone = proxRelease.Count(c => IsDone(c.Fields?.Status)),
            ProxReleaseInProgress = proxRelease.Count(c => IsInProgress(c.Fields?.Status)),
            ProxReleaseToDo = proxRelease.Count(c => IsToDo(c.Fields?.Status)),
            WorkIssueCount = workIssues.Count,
            WorkIssueDone = workIssues.Count(c => IsDone(c.Fields?.Status)),
            WorkIssueInProgress = workIssues.Count(c => IsInProgress(c.Fields?.Status)),
            WorkIssueToDo = workIssues.Count(c => IsToDo(c.Fields?.Status)),
        };
    }

    private static void GenerateAlerts(ReleaseAuditReport report)
    {
        var epicAlerted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var idea in report.IdeaDetails)
        {
            if (idea.EpicCount == 0)
                report.Alerts.Add(new ReleaseAuditAlert { Severity = ReleaseAuditSeverity.Error, Category = "Trazabilidad", IssueKey = idea.IdeaKey, Message = "Idea sin épica de desarrollo vinculada." });

            if (!idea.HasDescription)
                report.Alerts.Add(new ReleaseAuditAlert { Severity = ReleaseAuditSeverity.Warning, Category = "Descripción", IssueKey = idea.IdeaKey, Message = "Idea sin descripción." });

            foreach (var epic in idea.Epics)
            {
                if (!epicAlerted.Add(epic.EpicKey)) continue;

                if (!epic.TargetDate.HasValue)
                    report.Alerts.Add(new ReleaseAuditAlert { Severity = ReleaseAuditSeverity.Error, Category = "Fecha PROD", IssueKey = epic.EpicKey, Message = "Epic sin fecha de producción (duedate)." });
                else if (epic.TargetDate.Value.Date < DateTime.Today)
                    report.Alerts.Add(new ReleaseAuditAlert { Severity = ReleaseAuditSeverity.Warning, Category = "Fecha PROD", IssueKey = epic.EpicKey, Message = $"Fecha PROD vencida ({epic.TargetDate.Value:dd/MM/yyyy})." });

                if (epic.ProxReleaseCount > 0 && !epic.HasStgCard)
                    report.Alerts.Add(new ReleaseAuditAlert { Severity = ReleaseAuditSeverity.Warning, Category = "STG", IssueKey = epic.EpicKey, Message = $"{epic.ProxReleaseCount} issues prox_release sin card STG." });

                if (epic.ProxReleaseToDo > 0)
                    report.Alerts.Add(new ReleaseAuditAlert { Severity = ReleaseAuditSeverity.Info, Category = "prox_release", IssueKey = epic.EpicKey, Message = $"{epic.ProxReleaseToDo} issue(s) prox_release en To Do." });
            }
        }

        if (!report.HasProdCard && report.TotalProxRelease > 0)
            report.Alerts.Add(new ReleaseAuditAlert { Severity = ReleaseAuditSeverity.Error, Category = "PROD", IssueKey = report.SprintName, Message = "No existe card de Pasaje a Producción en el sprint." });

        report.Alerts = report.Alerts.OrderBy(a => a.Severity).ThenBy(a => a.IssueKey).ToList();
    }

    private static bool IsOperationalIssue(JiraIssue issue)
    {
        var summary = issue.Fields?.Summary ?? "";
        return summary.Contains("STG", StringComparison.OrdinalIgnoreCase)
            || summary.TrimStart().StartsWith("Pasaje", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDone(JiraStatus? s) => string.Equals(s?.StatusCategory?.Key, "done", StringComparison.OrdinalIgnoreCase);
    private static bool IsInProgress(JiraStatus? s) => string.Equals(s?.StatusCategory?.Key, "indeterminate", StringComparison.OrdinalIgnoreCase);
    private static bool IsToDo(JiraStatus? s) => string.Equals(s?.StatusCategory?.Key, "new", StringComparison.OrdinalIgnoreCase);

    private static DateTime CalcularMiercolesSegundaSemana(DateTime sprintStart)
    {
        var inicio = sprintStart.AddDays(7);
        int diasDesdeLunes = ((int)inicio.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return inicio.AddDays(-diasDesdeLunes + 2);
    }
}
