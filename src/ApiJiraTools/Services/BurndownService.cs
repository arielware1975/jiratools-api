using ApiJiraTools.Models;

namespace ApiJiraTools.Services;

public sealed class BurndownService
{
    private readonly JiraService _jira;

    public BurndownService(JiraService jira)
    {
        _jira = jira;
    }

    public BurndownData Build(List<JiraIssue> sprintIssues, DateTime sprintStart, DateTime sprintEnd)
    {
        var today = DateTime.UtcNow.Date;
        double totalSp = sprintIssues.Sum(GetSp);

        var days = new List<DateTime>();
        for (var d = sprintStart.Date; d <= sprintEnd.Date; d = d.AddDays(1))
            days.Add(d);

        var resolvedByDay = sprintIssues
            .Where(i => IsDone(i) && i.Fields?.ResolutionDateValue != null)
            .GroupBy(i => i.Fields!.ResolutionDateValue!.Value.Date)
            .ToDictionary(g => g.Key, g => g.Sum(GetSp));

        var doneNoResolution = sprintIssues
            .Where(i => IsDone(i) && i.Fields?.ResolutionDateValue == null && i.Fields?.UpdatedDate != null)
            .GroupBy(i => i.Fields!.UpdatedDate!.Value.Date)
            .ToDictionary(g => g.Key, g => g.Sum(GetSp));

        foreach (var kv in doneNoResolution)
        {
            if (resolvedByDay.ContainsKey(kv.Key))
                resolvedByDay[kv.Key] += kv.Value;
            else
                resolvedByDay[kv.Key] = kv.Value;
        }

        double cumulativeDone = 0;
        var dataPoints = new List<BurndownPoint>();
        foreach (var day in days)
        {
            if (resolvedByDay.TryGetValue(day, out var spDoneToday))
                cumulativeDone += spDoneToday;

            double remaining = Math.Max(0, totalSp - cumulativeDone);
            int totalDays = days.Count;
            int dayIndex = days.IndexOf(day);
            double ideal = totalDays <= 1 ? 0 : totalSp * (1.0 - (double)dayIndex / (totalDays - 1));

            dataPoints.Add(new BurndownPoint
            {
                Date = day,
                RemainingActual = day <= today ? remaining : null,
                RemainingIdeal = Math.Round(ideal, 1),
            });
        }

        return new BurndownData
        {
            SprintStart = sprintStart,
            SprintEnd = sprintEnd,
            TotalSp = totalSp,
            DataPoints = dataPoints,
        };
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

    private double GetSp(JiraIssue issue)
    {
        if (issue?.Fields == null) return 0d;
        var sp = issue.Fields.GetStoryPointsValue(_jira.StoryPointsFieldId);
        return sp > 0 ? sp : issue.Fields.GetStoryPointEstimateValue(_jira.StoryPointEstimateFieldId);
    }
}
