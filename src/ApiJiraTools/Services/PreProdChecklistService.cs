using ApiJiraTools.Models;

namespace ApiJiraTools.Services;

public sealed class PreProdChecklistService
{
    private readonly JiraService _jiraService;
    private readonly ILogger<PreProdChecklistService> _logger;

    public PreProdChecklistService(JiraService jiraService, ILogger<PreProdChecklistService> logger)
    {
        _jiraService = jiraService;
        _logger = logger;
    }

    public async Task<PreProdChecklistReport> BuildAsync(string projectKey, int sprintId, string sprintName, DateTime sprintStart)
    {
        _logger.LogInformation("PreProdChecklistService.BuildAsync: sprint={SprintId} ({SprintName})", sprintId, sprintName);

        var report = new PreProdChecklistReport
        {
            SprintName = sprintName,
            DeployDate = sprintStart,
            GeneratedAt = DateTime.Now
        };

        var sprintIssues = await _jiraService.GetSprintIssuesDetailedAsync(sprintId);

        report.Checks.Add(BuildCheck1_ProxReleaseDone(sprintIssues));
        report.Checks.Add(await BuildCheck2_StgCardsDone(sprintIssues));
        report.Checks.Add(await BuildCheck3_EpicsProdDate(sprintIssues));
        report.Checks.Add(BuildCheck4_NoBlockedIssues(sprintIssues));
        report.Checks.Add(BuildCheck5_PasajeExists(sprintIssues));

        return report;
    }

    private static PreProdCheck BuildCheck1_ProxReleaseDone(List<JiraIssue> sprintIssues)
    {
        var proxReleaseIssues = sprintIssues
            .Where(x => HasLabel(x, "prox_release") && !IsStgIssue(x) && !IsPasajeIssue(x))
            .ToList();

        var notDone = proxReleaseIssues.Where(x => !IsDone(x)).ToList();

        return new PreProdCheck
        {
            Name = "Todos los issues prox_release están Done",
            Passed = notDone.Count == 0,
            Detail = notDone.Count == 0
                ? $"{proxReleaseIssues.Count} issue(s) prox_release verificados — todos Done."
                : $"{notDone.Count} de {proxReleaseIssues.Count} issue(s) prox_release no están Done.",
            FailedIssueKeys = notDone.Select(x => x.Key).ToList()
        };
    }

    private async Task<PreProdCheck> BuildCheck2_StgCardsDone(List<JiraIssue> sprintIssues)
    {
        var epics = sprintIssues
            .Where(x => string.Equals(x.Fields?.IssueType?.Name, "Epic", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var failedKeys = new List<string>();
        int checkedCount = 0;

        foreach (var epic in epics)
        {
            var children = await _jiraService.GetEpicChildIssuesAsync(epic.Key);

            var stgCard = children.FirstOrDefault(c =>
                c.Fields != null &&
                !string.IsNullOrWhiteSpace(c.Fields.Summary) &&
                IsStgIssue(c));

            if (stgCard == null)
                failedKeys.Add(epic.Key);
            else if (!IsDone(stgCard))
                failedKeys.Add(epic.Key);

            checkedCount++;
        }

        return new PreProdCheck
        {
            Name = "Todas las cards STG existen y están Done",
            Passed = failedKeys.Count == 0,
            Detail = failedKeys.Count == 0
                ? $"{checkedCount} épica(s) verificadas — todas tienen STG Done."
                : $"{failedKeys.Count} épica(s) sin STG o con STG no Done.",
            FailedIssueKeys = failedKeys
        };
    }

    private async Task<PreProdCheck> BuildCheck3_EpicsProdDate(List<JiraIssue> sprintIssues)
    {
        var epics = sprintIssues
            .Where(x => string.Equals(x.Fields?.IssueType?.Name, "Epic", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var failedKeys = new List<string>();

        foreach (var epic in epics)
        {
            var epicIssue = await _jiraService.GetIssueByKeyAsync(epic.Key);
            var dueDate = epicIssue.Fields?.DueDate;

            if (string.IsNullOrWhiteSpace(dueDate))
                failedKeys.Add(epic.Key);
        }

        return new PreProdCheck
        {
            Name = "Todas las épicas tienen fecha PROD",
            Passed = failedKeys.Count == 0,
            Detail = failedKeys.Count == 0
                ? $"{epics.Count} épica(s) verificadas — todas tienen fecha PROD."
                : $"{failedKeys.Count} épica(s) sin fecha PROD (duedate).",
            FailedIssueKeys = failedKeys
        };
    }

    private static PreProdCheck BuildCheck4_NoBlockedIssues(List<JiraIssue> sprintIssues)
    {
        var blocked = sprintIssues.Where(IsBlocked).ToList();

        return new PreProdCheck
        {
            Name = "No hay issues bloqueados",
            Passed = blocked.Count == 0,
            Detail = blocked.Count == 0
                ? "No se detectaron issues bloqueados."
                : $"{blocked.Count} issue(s) bloqueados detectados.",
            FailedIssueKeys = blocked.Select(x => x.Key).ToList()
        };
    }

    private static PreProdCheck BuildCheck5_PasajeExists(List<JiraIssue> sprintIssues)
    {
        var pasajeCard = sprintIssues.FirstOrDefault(IsPasajeIssue);

        return new PreProdCheck
        {
            Name = "Card Pasaje a Producción existe",
            Passed = pasajeCard != null,
            Detail = pasajeCard != null
                ? $"Card encontrada: {pasajeCard.Key} — {pasajeCard.Fields?.Summary}"
                : "No se encontró card de Pasaje a Producción en el sprint.",
            FailedIssueKeys = pasajeCard == null ? new List<string> { "(ninguna)" } : new List<string>()
        };
    }

    private static bool IsDone(JiraIssue issue)
    {
        var key = issue?.Fields?.Status?.StatusCategory?.Key;
        if (!string.IsNullOrWhiteSpace(key) && key.Equals("done", StringComparison.OrdinalIgnoreCase))
            return true;

        string name = issue?.Fields?.Status?.Name?.Trim() ?? string.Empty;
        return name.Equals("Done", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Resolved", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Closed", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Resuelto", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Cerrado", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBlocked(JiraIssue issue)
    {
        if (issue?.Fields != null)
        {
            var flagged = issue.Fields.GetCustomFieldRaw("customfield_10021");
            if (flagged.HasValue && flagged.Value.ValueKind != System.Text.Json.JsonValueKind.Null
                                 && flagged.Value.ValueKind != System.Text.Json.JsonValueKind.Undefined)
            {
                return true;
            }
        }

        string status = issue?.Fields?.Status?.Name ?? string.Empty;
        return HasLabel(issue, "blocked")
            || status.Contains("blocked", StringComparison.OrdinalIgnoreCase)
            || status.Contains("bloque", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStgIssue(JiraIssue issue)
    {
        var summary = issue?.Fields?.Summary ?? string.Empty;
        return summary.Contains("STG", StringComparison.OrdinalIgnoreCase)
            || summary.Contains("Staging", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPasajeIssue(JiraIssue issue)
    {
        var summary = issue?.Fields?.Summary?.TrimStart() ?? string.Empty;
        return summary.StartsWith("Pasaje a Producción", StringComparison.OrdinalIgnoreCase)
            || summary.StartsWith("Pasaje a Produccion", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasLabel(JiraIssue? issue, string label)
        => issue?.Fields?.Labels != null
           && issue.Fields.Labels.Any(x => string.Equals(x, label, StringComparison.OrdinalIgnoreCase));
}
