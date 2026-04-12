using System.Text;
using ApiJiraTools.Models;

namespace ApiJiraTools.Services;

public sealed class AlertService
{
    private readonly JiraService _jira;

    public AlertService(JiraService jira)
    {
        _jira = jira;
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

        // 1. Issues bloqueados (tienen link "is blocked by" con issue no-Done)
        var blocked = FindBlockedIssues(workIssues);
        if (blocked.Count > 0)
        {
            alerts.Add($"🚫 *{blocked.Count} issue{(blocked.Count > 1 ? "s" : "")} bloqueado{(blocked.Count > 1 ? "s" : "")}:*");
            foreach (var (issue, blocker) in blocked)
                alerts.Add($"   `{issue.Key}` bloqueado por `{blocker}`");
        }

        // 2. Sprint por cerrar
        if (activeSprint.EndDate.HasValue)
        {
            var daysLeft = (activeSprint.EndDate.Value - DateTimeOffset.UtcNow).TotalDays;
            if (daysLeft <= 3 && daysLeft > 0)
            {
                var toDo = workIssues.Count(i => !IsDone(i) && !IsInProgress(i));
                var inProgress = workIssues.Count(i => IsInProgress(i));
                if (toDo > 0 || inProgress > 0)
                    alerts.Add($"⏰ *Sprint cierra en {daysLeft:0.#} días* — {toDo} en To Do, {inProgress} en progreso");
            }
        }

        // 3. Issues sin assignee
        var unassigned = workIssues.Where(i => !IsDone(i) && !i.Fields.HasAssignee).ToList();
        if (unassigned.Count > 0)
        {
            alerts.Add($"👤 *{unassigned.Count} issue{(unassigned.Count > 1 ? "s" : "")} sin asignar:*");
            foreach (var i in unassigned.Take(5))
                alerts.Add($"   `{i.Key}` {Truncate(i.Fields?.Summary ?? "", 40)}");
            if (unassigned.Count > 5)
                alerts.Add($"   _y {unassigned.Count - 5} más_");
        }

        // 4. Issues sin story points
        var noSp = workIssues.Where(i => !IsDone(i) && GetSp(i) == 0).ToList();
        if (noSp.Count > 0)
            alerts.Add($"📊 *{noSp.Count} issue{(noSp.Count > 1 ? "s" : "")} sin story points*");

        // 5. Velocidad actual
        var doneSp = workIssues.Where(IsDone).Sum(GetSp);
        var totalSp = workIssues.Sum(GetSp);
        double pct = totalSp > 0 ? doneSp / totalSp * 100 : 0;

        if (alerts.Count == 0)
            return null; // Sin alertas, no mandar nada

        var sb = new StringBuilder();
        sb.AppendLine($"📢 *Alertas diarias — {projectKey}*");
        sb.AppendLine($"_{EscapeMd(activeSprint.Name)}_\n");

        foreach (var alert in alerts)
            sb.AppendLine(EscapeMd(alert));

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
