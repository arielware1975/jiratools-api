using ApiJiraTools.Models;

namespace ApiJiraTools.Services;

public sealed class SprintClosureService
{
    private readonly JiraService _jira;

    public SprintClosureService(JiraService jira)
    {
        _jira = jira;
    }

    public async Task<SprintClosureReport> BuildAsync(JiraProject project, JiraSprint sprint)
    {
        var issuesTask = _jira.GetSprintIssuesDetailedAsync(sprint.Id);
        var epicsTask = _jira.GetEpicsBySprintAsync(sprint.Id);
        await Task.WhenAll(issuesTask, epicsTask);

        var issues = await issuesTask;
        var epics = await epicsTask;

        var report = new SprintClosureReport
        {
            ProjectKey = project.Key,
            SprintName = sprint.Name,
            SprintStart = sprint.StartDate,
            SprintEnd = sprint.EndDate,
            GeneratedAt = DateTime.UtcNow,
        };

        var stgTasks = issues.Where(IsStgTask).ToList();
        var workIssues = issues.Where(x => !IsStgTask(x)).ToList();

        BuildVelocity(workIssues, report);
        BuildByType(workIssues, report);
        BuildCarryOver(workIssues, report);
        BuildEpics(epics, workIssues, stgTasks, report);
        BuildQuality(issues, report);
        BuildByAssignee(workIssues, report);
        BuildHighlights(workIssues, report);
        BuildAlerts(report);

        return report;
    }

    private void BuildVelocity(List<JiraIssue> workIssues, SprintClosureReport r)
    {
        r.TotalSp = Math.Round(workIssues.Sum(GetSp), 1);
        r.TotalIssues = workIssues.Count;
        r.DoneSp = Math.Round(workIssues.Where(IsDone).Sum(GetSp), 1);
        r.DoneIssues = workIssues.Count(IsDone);
        r.IssuesWithoutSp = workIssues.Count(x => GetSp(x) == 0);
        r.CommittedSp = r.TotalSp;
        r.CommittedIssues = r.TotalIssues;
    }

    private void BuildByType(List<JiraIssue> workIssues, SprintClosureReport r)
    {
        foreach (var g in workIssues.GroupBy(x => x.Fields?.IssueType?.Name ?? "Sin tipo").OrderByDescending(g => g.Count()))
        {
            var done = g.Where(IsDone).ToList();
            r.ByType.Add(new SprintClosureTypeRow
            {
                TypeName = g.Key,
                Total = g.Count(),
                Done = done.Count,
                TotalSp = Math.Round(g.Sum(GetSp), 1),
                DoneSp = Math.Round(done.Sum(GetSp), 1),
            });
        }
    }

    private void BuildCarryOver(List<JiraIssue> workIssues, SprintClosureReport r)
    {
        var notDone = workIssues.Where(x => !IsDone(x)).ToList();
        var toDo = notDone.Where(x => !IsInProgress(x)).ToList();
        var inProgress = notDone.Where(IsInProgress).ToList();

        r.CarryOverToDo = toDo.Select(ToCarryItem).ToList();
        r.CarryOverInProgress = inProgress.Select(ToCarryItem).ToList();
        r.CarryOverToDoSp = Math.Round(toDo.Sum(GetSp), 1);
        r.CarryOverInProgressSpRaw = Math.Round(inProgress.Sum(GetSp), 1);
        r.CarryOverTotalSp = Math.Round(r.CarryOverToDoSp + r.CarryOverInProgressSpRaw, 1);
    }

    private void BuildEpics(List<JiraEpicSummary> epics, List<JiraIssue> workIssues,
        List<JiraIssue> stgTasks, SprintClosureReport r)
    {
        foreach (var epic in epics)
        {
            var epicWork = workIssues.Where(x =>
                string.Equals(x.Fields?.Parent?.Key, epic.EpicKey, StringComparison.OrdinalIgnoreCase)).ToList();
            var epicStg = stgTasks.Where(x =>
                string.Equals(x.Fields?.Parent?.Key, epic.EpicKey, StringComparison.OrdinalIgnoreCase)).ToList();
            var done = epicWork.Where(IsDone).ToList();

            r.Epics.Add(new SprintClosureEpicRow
            {
                EpicKey = epic.EpicKey,
                EpicSummary = epic.EpicSummary,
                TotalIssues = epicWork.Count,
                DoneIssues = done.Count,
                TotalSp = Math.Round(epicWork.Sum(GetSp), 1),
                DoneSp = Math.Round(done.Sum(GetSp), 1),
                HasStgTask = epicStg.Count > 0 || epic.HasStgChild,
                IsStgNotRequired = epic.IsStgNotRequired,
                StgTaskKey = epicStg.FirstOrDefault()?.Key ?? epic.StgIssueKey ?? string.Empty,
                StgTaskDone = epicStg.Any(IsDone),
            });
        }
    }

    private void BuildQuality(List<JiraIssue> allIssues, SprintClosureReport r)
    {
        var bugs = allIssues.Where(IsBug).ToList();
        r.OpenBugsAtClose = bugs.Count(x => !IsDone(x));
        r.BugsClosedInSprint = bugs.Count(IsDone);
        r.QaQueueAtClose = allIssues.Count(x => !IsDone(x) && IsQaItem(x));
        r.OpenBugRefs = bugs.Where(x => !IsDone(x)).Select(ToIssueRef).ToList();
    }

    private void BuildByAssignee(List<JiraIssue> workIssues, SprintClosureReport r)
    {
        foreach (var g in workIssues.GroupBy(x => x.Fields?.Assignee?.DisplayName ?? "Sin asignar").OrderByDescending(g => g.Count()))
        {
            var done = g.Where(IsDone).ToList();
            r.ByAssignee.Add(new SprintClosureAssigneeRow
            {
                Name = g.Key,
                TotalIssues = g.Count(),
                TotalSp = Math.Round(g.Sum(GetSp), 1),
                DoneIssues = done.Count,
                DoneSp = Math.Round(done.Sum(GetSp), 1),
            });
        }
    }

    private void BuildHighlights(List<JiraIssue> workIssues, SprintClosureReport r)
    {
        var done = workIssues.Where(IsDone).ToList();
        r.ProxReleaseDone = done
            .Where(x => x.Fields?.Labels?.Any(l => string.Equals(l, "prox_release", StringComparison.OrdinalIgnoreCase)) == true)
            .OrderByDescending(GetSp).Select(ToIssueRef).ToList();
        r.TopSpDone = done.OrderByDescending(GetSp).Take(5).Select(ToIssueRef).ToList();
    }

    private static void BuildAlerts(SprintClosureReport r)
    {
        if (r.CommittedSp > 0)
        {
            double pct = r.DoneSp / r.CommittedSp * 100;
            if (pct < 70) r.Alerts.Add($"Velocidad baja: {pct:0}% del commitment ({r.DoneSp:0.##}/{r.CommittedSp:0.##} SP).");
        }
        if (r.CarryOverTotalSp > 0)
            r.Alerts.Add($"Carry-over: {r.CarryOverToDo.Count + r.CarryOverInProgress.Count} issues ({r.CarryOverTotalSp:0.##} SP) pasan al siguiente sprint.");
        if (r.OpenBugsAtClose > 0)
            r.Alerts.Add($"{r.OpenBugsAtClose} bug{(r.OpenBugsAtClose > 1 ? "s" : "")} abierto{(r.OpenBugsAtClose > 1 ? "s" : "")} al cierre.");
        if (r.Alerts.Count == 0)
            r.Alerts.Add("Sin alertas críticas.");
    }

    // ── Helpers ──

    private static bool IsDone(JiraIssue issue)
    {
        var key = issue?.Fields?.Status?.StatusCategory?.Key;
        if (!string.IsNullOrWhiteSpace(key) && key.Equals("done", StringComparison.OrdinalIgnoreCase)) return true;
        var name = issue?.Fields?.Status?.Name?.Trim() ?? string.Empty;
        return name.Equals("Done", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Finalizada", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Resolved", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Closed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInProgress(JiraIssue issue)
        => (issue?.Fields?.Status?.StatusCategory?.Key ?? "").Equals("indeterminate", StringComparison.OrdinalIgnoreCase);

    private static bool IsStgTask(JiraIssue issue)
        => (issue?.Fields?.Summary?.TrimStart() ?? "").StartsWith("STG", StringComparison.OrdinalIgnoreCase);

    private static bool IsBug(JiraIssue issue)
        => string.Equals(issue?.Fields?.IssueType?.Name, "Bug", StringComparison.OrdinalIgnoreCase);

    private static bool IsQaItem(JiraIssue issue)
    {
        var status = issue?.Fields?.Status?.Name ?? "";
        return status.Contains("qa", StringComparison.OrdinalIgnoreCase)
            || status.Contains("test", StringComparison.OrdinalIgnoreCase);
    }

    private double GetSp(JiraIssue issue)
    {
        if (issue?.Fields == null) return 0d;
        var sp = issue.Fields.GetStoryPointsValue(_jira.StoryPointsFieldId);
        return sp > 0 ? sp : issue.Fields.GetStoryPointEstimateValue(_jira.StoryPointEstimateFieldId);
    }

    private SprintClosureCarryItem ToCarryItem(JiraIssue issue) => new()
    {
        Key = issue.Key,
        Summary = issue.Fields?.Summary ?? "",
        StoryPoints = GetSp(issue),
        Status = issue.Fields?.Status?.Name ?? "",
        Assignee = issue.Fields?.Assignee?.DisplayName ?? "Sin asignar",
    };

    private SprintClosureIssueRef ToIssueRef(JiraIssue issue) => new()
    {
        Key = issue.Key,
        Summary = issue.Fields?.Summary ?? "",
        StoryPoints = GetSp(issue),
        Status = issue.Fields?.Status?.Name ?? "",
        Assignee = issue.Fields?.Assignee?.DisplayName ?? "Sin asignar",
    };
}
