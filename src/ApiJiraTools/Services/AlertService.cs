using System.Text;
using System.Text.RegularExpressions;
using ApiJiraTools.Configuration;
using ApiJiraTools.Models;
using Microsoft.Extensions.Options;

namespace ApiJiraTools.Services;

public sealed class AlertService
{
    private readonly JiraService _jira;
    private readonly string _discoveryMapping;

    public AlertService(JiraService jira, IOptions<TelegramSettings> telegramOptions)
    {
        _jira = jira;
        _discoveryMapping = telegramOptions.Value.DiscoveryProjectMapping;
    }

    public async Task<string?> BuildDailyAlertAsync(string projectKey)
    {
        var sprints = await _jira.GetSprintsByProjectAsync(projectKey);
        var activeSprint = sprints.FirstOrDefault(s =>
            s.State.Equals("active", StringComparison.OrdinalIgnoreCase));

        if (activeSprint == null)
            return null;

        var issues = await _jira.GetSprintIssuesDetailedAsync(activeSprint.Id);
        var workIssues = issues.Where(i =>
            !(i.Fields?.Summary?.TrimStart() ?? "").StartsWith("STG", StringComparison.OrdinalIgnoreCase)).ToList();

        var alerts = new List<string>();

        // 1. Issues bloqueados: deshabilitado — demasiado ruido con dependencias STG/PROD.
        // Se detectan con `is blocked by` pero la mayoría son dependencias del flujo normal
        // de release, no bloqueos reales del equipo.

        // 2. Sprint por cerrar
        if (activeSprint.EndDate.HasValue)
        {
            var daysLeft = (activeSprint.EndDate.Value - DateTimeOffset.UtcNow).TotalDays;
            if (daysLeft <= 3 && daysLeft > 0)
            {
                var toDo = workIssues.Count(i => !IsDone(i) && !IsInProgress(i));
                var inProgress = workIssues.Count(i => IsInProgress(i));
                if (toDo > 0 || inProgress > 0)
                    alerts.Add($"⏰ *Sprint cierra en {EscapeMd($"{daysLeft:0.#}")} días* \\— {toDo} en To Do, {inProgress} en progreso");
            }
        }

        // 3. Issues sin assignee
        var unassigned = workIssues.Where(i => !IsDone(i) && !i.Fields.HasAssignee).ToList();
        if (unassigned.Count > 0)
        {
            alerts.Add($"👤 *{unassigned.Count} issue{(unassigned.Count > 1 ? "s" : "")} sin asignar:*");
            foreach (var i in unassigned.Take(5))
                alerts.Add($"   `{i.Key}` {EscapeMd(Truncate(i.Fields?.Summary ?? "", 40))}");
            if (unassigned.Count > 5)
                alerts.Add($"   _y {unassigned.Count - 5} más_");
        }

        // 4. Issues sin story points
        var noSp = workIssues.Where(i => !IsDone(i) && GetSp(i) == 0).ToList();
        if (noSp.Count > 0)
        {
            alerts.Add($"📊 *{noSp.Count} issue{(noSp.Count > 1 ? "s" : "")} sin story points:*");
            foreach (var i in noSp.Take(5))
                alerts.Add($"   `{i.Key}` {EscapeMd(Truncate(i.Fields?.Summary ?? "", 40))}");
            if (noSp.Count > 5)
                alerts.Add($"   _y {noSp.Count - 5} más_");
        }

        // 5. Naming mismatch idea ↔ épica
        var namingAlerts = await CheckNamingMismatchAsync(projectKey);
        if (namingAlerts.Count > 0)
        {
            alerts.Add($"📛 *{namingAlerts.Count} nombre{(namingAlerts.Count > 1 ? "s" : "")} idea ≠ épica:*");
            foreach (var nm in namingAlerts.Take(5))
                alerts.Add($"   `{nm.IdeaKey}` _{EscapeMd(Truncate(nm.IdeaName, 25))}_ → `{nm.EpicKey}` _{EscapeMd(Truncate(nm.EpicName, 25))}_");
            if (namingAlerts.Count > 5)
                alerts.Add($"   _y {namingAlerts.Count - 5} más_");
        }

        // 6. Velocidad actual
        var doneSp = workIssues.Where(IsDone).Sum(GetSp);
        var totalSp = workIssues.Sum(GetSp);
        double pct = totalSp > 0 ? doneSp / totalSp * 100 : 0;

        if (alerts.Count == 0)
            return null; // Sin alertas, no mandar nada

        var sb = new StringBuilder();
        sb.AppendLine($"📢 *Alertas diarias \\— {EscapeMd(projectKey)}*");
        sb.AppendLine($"_{EscapeMd(activeSprint.Name)}_\n");

        // Las alertas ya tienen formato MarkdownV2 — no escapar
        foreach (var alert in alerts)
            sb.AppendLine(alert);

        sb.AppendLine($"\n📈 Progreso: {EscapeMd($"{doneSp}/{totalSp}")} SP \\({EscapeMd($"{pct:0}")}%\\)");

        return sb.ToString();
    }

    private static List<(JiraIssue Issue, string BlockerKey)> FindBlockedIssues(List<JiraIssue> issues)
    {
        var result = new List<(JiraIssue, string)>();
        foreach (var issue in issues)
        {
            if (IsDone(issue)) continue;
            foreach (var link in issue.Fields?.IssueLinks ?? Enumerable.Empty<JiraIssueLink>())
            {
                var linkType = link.Type?.Name ?? "";
                if (!linkType.Contains("Block", StringComparison.OrdinalIgnoreCase)) continue;

                // "is blocked by" = inward
                if (link.InwardIssue != null &&
                    (link.Type?.Inward ?? "").Contains("blocked", StringComparison.OrdinalIgnoreCase))
                {
                    var blockerStatus = link.InwardIssue.Fields?.Status?.StatusCategory?.Key ?? "";
                    if (!blockerStatus.Equals("done", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add((issue, link.InwardIssue.Key));
                    }
                }
            }
        }
        return result;
    }

    private double GetSp(JiraIssue issue)
    {
        if (issue?.Fields == null) return 0d;
        var sp = issue.Fields.GetStoryPointsValue(_jira.StoryPointsFieldId);
        return sp > 0 ? sp : issue.Fields.GetStoryPointEstimateValue(_jira.StoryPointEstimateFieldId);
    }

    private static bool IsDone(JiraIssue issue)
    {
        var key = issue?.Fields?.Status?.StatusCategory?.Key;
        if (!string.IsNullOrWhiteSpace(key) && key.Equals("done", StringComparison.OrdinalIgnoreCase)) return true;
        var name = issue?.Fields?.Status?.Name?.Trim() ?? "";
        return name.Equals("Done", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Finalizada", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Resolved", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Closed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInProgress(JiraIssue issue)
        => (issue?.Fields?.Status?.StatusCategory?.Key ?? "").Equals("indeterminate", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "...";

    private async Task<List<NamingMismatch>> CheckNamingMismatchAsync(string projectKey)
    {
        var result = new List<NamingMismatch>();

        // Resolver proyecto discovery
        var discoveryProject = ResolveDiscoveryProject(projectKey, _discoveryMapping);
        if (string.Equals(discoveryProject, projectKey, StringComparison.OrdinalIgnoreCase))
            return result; // No hay mapping de discovery

        try
        {
            var ideasAhora = await _jira.GetDiscoveryIdeasByRoadmapAsync(discoveryProject, "Ahora");
            var ideasSiguiente = await _jira.GetDiscoveryIdeasByRoadmapAsync(discoveryProject, "Siguiente");
            var allIdeas = new List<JiraIssue>(ideasAhora);
            foreach (var i in ideasSiguiente)
                if (!allIdeas.Any(x => x.Key == i.Key))
                    allIdeas.Add(i);

            foreach (var idea in allIdeas)
            {
                var linkedEpics = JiraService.GetLinkedEpicsFromIdea(idea)
                    .Where(e => e.Key.StartsWith(projectKey + "-", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var (epicKey, epicSummary) in linkedEpics)
                {
                    if (!NamesMatch(idea.Fields?.Summary ?? "", epicSummary))
                    {
                        result.Add(new NamingMismatch
                        {
                            IdeaKey = idea.Key,
                            IdeaName = idea.Fields?.Summary ?? "",
                            EpicKey = epicKey,
                            EpicName = epicSummary
                        });
                    }
                }
            }
        }
        catch { /* no bloquear alertas por error en discovery */ }

        return result;
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

    private static bool NamesMatch(string ideaName, string epicName)
    {
        static string Normalize(string s)
        {
            s = Regex.Replace(s.Trim(), @"^[A-Za-z0-9]{1,3}[\)\-\.]\s*", "");
            s = s.Trim().ToLowerInvariant().Replace('-', ' ').Replace('_', ' ');
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s;
        }

        var n1 = Normalize(ideaName);
        var n2 = Normalize(epicName);

        if (string.IsNullOrWhiteSpace(n1) || string.IsNullOrWhiteSpace(n2)) return true;
        if (n1 == n2) return true;
        if (n1.Contains(n2) || n2.Contains(n1)) return true;

        var words1 = n1.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToHashSet();
        var words2 = n2.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToHashSet();
        if (words1.Count == 0) return true;
        int common = words1.Count(w => words2.Contains(w));
        return (double)common / words1.Count >= 0.5;
    }

    private record NamingMismatch
    {
        public string IdeaKey { get; init; } = "";
        public string IdeaName { get; init; } = "";
        public string EpicKey { get; init; } = "";
        public string EpicName { get; init; } = "";
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
