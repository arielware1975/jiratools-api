namespace ApiJiraTools.Models;

public sealed class SprintClosureReport
{
    public string ProjectKey { get; set; } = string.Empty;
    public string SprintName { get; set; } = string.Empty;
    public DateTimeOffset? SprintStart { get; set; }
    public DateTimeOffset? SprintEnd { get; set; }
    public DateTime GeneratedAt { get; set; }
    public double CommittedSp { get; set; }
    public int CommittedIssues { get; set; }
    public double DoneSp { get; set; }
    public int DoneIssues { get; set; }
    public double TotalSp { get; set; }
    public int TotalIssues { get; set; }
    public int IssuesWithoutSp { get; set; }
    public List<SprintClosureTypeRow> ByType { get; set; } = new();
    public List<SprintClosureCarryItem> CarryOverToDo { get; set; } = new();
    public List<SprintClosureCarryItem> CarryOverInProgress { get; set; } = new();
    public double CarryOverToDoSp { get; set; }
    public double CarryOverInProgressSpRaw { get; set; }
    public double CarryOverTotalSp { get; set; }
    public List<SprintClosureEpicRow> Epics { get; set; } = new();
    public int OpenBugsAtClose { get; set; }
    public int BugsClosedInSprint { get; set; }
    public int QaQueueAtClose { get; set; }
    public List<SprintClosureIssueRef> OpenBugRefs { get; set; } = new();
    public List<SprintClosureAssigneeRow> ByAssignee { get; set; } = new();
    public List<SprintClosureIssueRef> ProxReleaseDone { get; set; } = new();
    public List<SprintClosureIssueRef> TopSpDone { get; set; } = new();
    public List<string> Alerts { get; set; } = new();
}

public sealed class SprintClosureTypeRow
{
    public string TypeName { get; set; } = string.Empty;
    public int Done { get; set; }
    public int Total { get; set; }
    public double DoneSp { get; set; }
    public double TotalSp { get; set; }
}

public sealed class SprintClosureCarryItem
{
    public string Key { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public double StoryPoints { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Assignee { get; set; } = string.Empty;
}

public sealed class SprintClosureEpicRow
{
    public string EpicKey { get; set; } = string.Empty;
    public string EpicSummary { get; set; } = string.Empty;
    public int DoneIssues { get; set; }
    public int TotalIssues { get; set; }
    public double DoneSp { get; set; }
    public double TotalSp { get; set; }
    public bool HasStgTask { get; set; }
    public bool IsStgNotRequired { get; set; }
    public string StgTaskKey { get; set; } = string.Empty;
    public bool StgTaskDone { get; set; }
}

public sealed class SprintClosureAssigneeRow
{
    public string Name { get; set; } = string.Empty;
    public int DoneIssues { get; set; }
    public int TotalIssues { get; set; }
    public double DoneSp { get; set; }
    public double TotalSp { get; set; }
}

public sealed class SprintClosureIssueRef
{
    public string Key { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public double StoryPoints { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Assignee { get; set; } = string.Empty;
}
