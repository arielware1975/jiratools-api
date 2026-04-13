using ApiJiraTools.Models;

namespace ApiJiraTools.Services;

public sealed class ScopeAnalysisService
{
    private readonly JiraService _jiraService;
    private readonly ILogger<ScopeAnalysisService> _logger;

    public ScopeAnalysisService(JiraService jiraService, ILogger<ScopeAnalysisService> logger)
    {
        _jiraService = jiraService;
        _logger = logger;
    }

    public async Task<ScopeAnalysisResult> BuildAsync(
        string projectKey,
        JiraSprint currentSprint,
        JiraSprint nextSprint,
        double carryOverInProgressFactor = 0.3)
    {
        carryOverInProgressFactor = Math.Max(0.0, Math.Min(1.0, carryOverInProgressFactor));

        var currentIssues = await _jiraService.GetSprintIssuesDetailedAsync(currentSprint.Id);
        var nextIssues = await _jiraService.GetSprintIssuesDetailedAsync(nextSprint.Id);
        var allSprints = await _jiraService.GetSprintsByProjectAsync(projectKey);

        // Carry over: no done, no STG
        var carryOver = currentIssues
            .Where(x => !IsDone(x) && !IsStg(x))
            .ToList();

        // Split carry over: To Do (full SP) vs In Progress/Review (factor applied)
        var carryOverToDo = carryOver.Where(IsToDoStatus).ToList();
        var carryOverInProgress = carryOver.Where(x => !IsToDoStatus(x)).ToList();

        // Next sprint issues no done
        var nextPending = nextIssues
            .Where(x => !IsDone(x))
            .ToList();

        // Velocity: current sprint + last 2 closed sprints
        var currentDone = currentIssues.Where(IsDone).Sum(GetSp);
        var velocityData = new List<SprintVelocityData>
        {
            new() { SprintName = currentSprint.Name, DoneSp = currentDone }
        };

        var closedSprints = allSprints
            .Where(x => string.Equals(x.State, "closed", StringComparison.OrdinalIgnoreCase)
                     && x.Id != currentSprint.Id
                     && x.Id != nextSprint.Id)
            .OrderByDescending(x => x.CompleteDate ?? x.EndDate ?? DateTimeOffset.MinValue)
            .Take(2)
            .ToList();

        foreach (var sp in closedSprints)
        {
            var issues = await _jiraService.GetSprintIssuesDetailedAsync(sp.Id);
            var done = issues.Where(IsDone).Sum(GetSp);
            velocityData.Add(new SprintVelocityData { SprintName = sp.Name, DoneSp = done });
        }

        double avgVelocity = velocityData.Count > 0
            ? Math.Round(velocityData.Average(x => x.DoneSp), 1)
            : 0;

        // Business days del próximo sprint (simplificado, sin feriados argentinos)
        int businessDays = 0;
        if (nextSprint.StartDate.HasValue && nextSprint.EndDate.HasValue)
        {
            var start = nextSprint.StartDate.Value.Date;
            var end = nextSprint.EndDate.Value.Date;
            for (var d = start; d <= end; d = d.AddDays(1))
                if (d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday)
                    businessDays++;
        }

        double carryOverToDoSp = carryOverToDo.Sum(GetSp);
        double carryOverInProgressSpRaw = carryOverInProgress.Sum(GetSp);
        double carryOverInProgressSpEffective = Math.Round(carryOverInProgressSpRaw * carryOverInProgressFactor, 1);
        double carryOverSp = Math.Round(carryOverToDoSp + carryOverInProgressSpEffective, 1);
        double nextSp = nextPending.Sum(GetSp);
        double totalCommitted = carryOverSp + nextSp;
        double diff = avgVelocity - totalCommitted;

        var carryOverNoSp = carryOver.Where(x => GetSp(x) == 0).ToList();
        var nextNoSp = nextPending.Where(x => GetSp(x) == 0).ToList();
        var nextUnassigned = nextPending.Where(x => x.Fields == null || !x.Fields.HasAssignee).ToList();
        var carryOverUnassigned = carryOver.Where(x => x.Fields == null || !x.Fields.HasAssignee).ToList();

        // Fechas clave
        DateTime? stgTargetDate = nextSprint.StartDate.HasValue
            ? CalculateStgDate(nextSprint.StartDate.Value)
            : null;

        DateTime? prodPlanningDate = null;
        if (nextSprint.EndDate.HasValue)
            prodPlanningDate = nextSprint.EndDate.Value.LocalDateTime.Date.AddDays(1);
        else if (nextSprint.StartDate.HasValue)
            prodPlanningDate = nextSprint.StartDate.Value.LocalDateTime.Date.AddDays(14);

        // Semáforo
        ScopeStatus status;
        if (avgVelocity == 0)
            status = ScopeStatus.SinDatos;
        else if (carryOverNoSp.Count + nextNoSp.Count > 3)
            status = ScopeStatus.Amarillo; // scope oculto
        else if (diff >= 0)
            status = ScopeStatus.Verde;
        else if (diff >= -5)
            status = ScopeStatus.Amarillo;
        else
            status = ScopeStatus.Rojo;

        // Scope pendiente vía JQL (ambos sprints, estados pendientes)
        string pendingJql = $"sprint IN ({currentSprint.Id}, {nextSprint.Id}) AND status IN (\"To Do\", Review, \"En Curso\") ORDER BY status ASC, parent ASC, rank ASC";
        var pendingJqlIssues = new List<JiraIssue>();
        try { pendingJqlIssues = await _jiraService.SearchIssuesByJqlAsync(pendingJql); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error en JQL de scope pendiente"); }

        double pendingJqlToDoSp = 0, pendingJqlInProgressSpRaw = 0;
        int pendingJqlToDoCount = 0, pendingJqlInProgressCount = 0;
        foreach (var iss in pendingJqlIssues)
        {
            var sp2 = GetSp(iss);
            if (IsToDoStatus(iss))
            {
                pendingJqlToDoSp += sp2;
                pendingJqlToDoCount++;
            }
            else
            {
                pendingJqlInProgressSpRaw += sp2;
                pendingJqlInProgressCount++;
            }
        }
        double pendingJqlInProgressSpEffective = Math.Round(pendingJqlInProgressSpRaw * carryOverInProgressFactor, 1);
        double pendingJqlTotalSp = Math.Round(pendingJqlToDoSp + pendingJqlInProgressSpEffective, 1);

        return new ScopeAnalysisResult
        {
            CurrentSprintName = currentSprint.Name,
            NextSprintName = nextSprint.Name,
            NextSprintStart = nextSprint.StartDate,
            NextSprintEnd = nextSprint.EndDate,
            StgTargetDate = stgTargetDate,
            ProdPlanningDate = prodPlanningDate,
            BusinessDays = businessDays,
            VelocityData = velocityData,
            AvgVelocity = avgVelocity,
            CarryOverIssues = carryOver,
            CarryOverToDoIssues = carryOverToDo,
            CarryOverInProgressIssues = carryOverInProgress,
            CarryOverToDoSp = carryOverToDoSp,
            CarryOverInProgressSpRaw = carryOverInProgressSpRaw,
            CarryOverInProgressSpEffective = carryOverInProgressSpEffective,
            CarryOverInProgressFactor = carryOverInProgressFactor,
            NextSprintIssues = nextPending,
            CarryOverSp = carryOverSp,
            NextSprintSp = nextSp,
            TotalCommitted = totalCommitted,
            Diff = diff,
            CarryOverNoSp = carryOverNoSp,
            NextNoSp = nextNoSp,
            NextUnassigned = nextUnassigned,
            CarryOverUnassigned = carryOverUnassigned,
            Status = status,
            PendingJqlToDoSp = pendingJqlToDoSp,
            PendingJqlInProgressSpRaw = pendingJqlInProgressSpRaw,
            PendingJqlInProgressSpEffective = pendingJqlInProgressSpEffective,
            PendingJqlTotalSp = pendingJqlTotalSp,
            PendingJqlToDoCount = pendingJqlToDoCount,
            PendingJqlInProgressCount = pendingJqlInProgressCount,
            PendingJqlIssues = pendingJqlIssues,
        };
    }

    /// <summary>
    /// STG date = miércoles de la semana 2 del sprint.
    /// </summary>
    private static DateTime CalculateStgDate(DateTimeOffset sprintStart)
    {
        var start = sprintStart.LocalDateTime.Date;
        var week2Start = start.AddDays(7);
        int daysUntilWednesday = ((int)DayOfWeek.Wednesday - (int)week2Start.DayOfWeek + 7) % 7;
        return week2Start.AddDays(daysUntilWednesday);
    }

    private static bool IsDone(JiraIssue issue)
    {
        var key = issue?.Fields?.Status?.StatusCategory?.Key;
        if (!string.IsNullOrWhiteSpace(key) && key.Equals("done", StringComparison.OrdinalIgnoreCase))
            return true;
        var name = issue?.Fields?.Status?.Name?.Trim() ?? string.Empty;
        return name.Equals("Done", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Finalizada", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Resolved", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Closed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStg(JiraIssue issue)
    {
        var summary = issue?.Fields?.Summary?.TrimStart() ?? string.Empty;
        return summary.StartsWith("STG", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true when the issue is in To Do (status category = "new") → full SP.
    /// Everything "indeterminate" (In Progress, Test, Review, etc.) → factor applied.
    /// </summary>
    private static bool IsToDoStatus(JiraIssue issue)
    {
        var key = issue?.Fields?.Status?.StatusCategory?.Key ?? string.Empty;
        return !key.Equals("indeterminate", StringComparison.OrdinalIgnoreCase);
    }

    private double GetSp(JiraIssue issue)
    {
        if (issue?.Fields == null) return 0d;
        var sp = issue.Fields.GetStoryPointsValue(_jiraService.StoryPointsFieldId);
        if (sp > 0) return sp;
        return issue.Fields.GetStoryPointEstimateValue(_jiraService.StoryPointEstimateFieldId);
    }
}

public sealed class ScopeAnalysisResult
{
    public string CurrentSprintName { get; set; } = string.Empty;
    public string NextSprintName { get; set; } = string.Empty;
    public DateTimeOffset? NextSprintStart { get; set; }
    public DateTimeOffset? NextSprintEnd { get; set; }
    public DateTime? StgTargetDate { get; set; }
    public DateTime? ProdPlanningDate { get; set; }
    public int BusinessDays { get; set; }
    public List<SprintVelocityData> VelocityData { get; set; } = new();
    public double AvgVelocity { get; set; }
    public List<JiraIssue> CarryOverIssues { get; set; } = new();
    public List<JiraIssue> CarryOverToDoIssues { get; set; } = new();
    public List<JiraIssue> CarryOverInProgressIssues { get; set; } = new();
    public double CarryOverToDoSp { get; set; }
    public double CarryOverInProgressSpRaw { get; set; }
    public double CarryOverInProgressSpEffective { get; set; }
    public double CarryOverInProgressFactor { get; set; }
    public List<JiraIssue> NextSprintIssues { get; set; } = new();
    public double CarryOverSp { get; set; }
    public double NextSprintSp { get; set; }
    public double TotalCommitted { get; set; }
    public double Diff { get; set; }
    public List<JiraIssue> CarryOverNoSp { get; set; } = new();
    public List<JiraIssue> NextNoSp { get; set; } = new();
    public List<JiraIssue> NextUnassigned { get; set; } = new();
    public List<JiraIssue> CarryOverUnassigned { get; set; } = new();
    public ScopeStatus Status { get; set; }

    // Scope pendiente vía JQL
    public double PendingJqlToDoSp { get; set; }
    public double PendingJqlInProgressSpRaw { get; set; }
    public double PendingJqlInProgressSpEffective { get; set; }
    public double PendingJqlTotalSp { get; set; }
    public int PendingJqlToDoCount { get; set; }
    public int PendingJqlInProgressCount { get; set; }
    public List<JiraIssue> PendingJqlIssues { get; set; } = new();
}

public sealed class SprintVelocityData
{
    public string SprintName { get; set; } = string.Empty;
    public double DoneSp { get; set; }
}

public enum ScopeStatus { Verde, Amarillo, Rojo, SinDatos }
