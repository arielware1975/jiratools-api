namespace ApiJiraTools.Models;

public enum StgAlignment { Ok, Partial, Empty, NoCard }

public class StgChecklistReport
{
    public string SprintName { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; } = DateTime.Now;
    public List<StgEpicRow> Epics { get; set; } = new();
    public List<ProdCardRow> ProdCards { get; set; } = new();

    public int TotalEpics => Epics.Count;
    public int WithStg => Epics.Count(e => e.HasStgCard);
    public int WithoutStg => Epics.Count(e => !e.HasStgCard);
    public int Aligned => Epics.Count(e => e.Alignment == StgAlignment.Ok);
    public int Misaligned => Epics.Count(e => e.HasStgCard && e.Alignment != StgAlignment.Ok);
}

public class StgEpicRow
{
    public string EpicKey { get; set; } = string.Empty;
    public string EpicSummary { get; set; } = string.Empty;
    public string EpicStatus { get; set; } = string.Empty;

    public bool HasStgCard { get; set; }
    public string StgKey { get; set; } = string.Empty;
    public string StgSummary { get; set; } = string.Empty;
    public string StgStatus { get; set; } = string.Empty;
    public bool StgInSprint { get; set; }

    public List<StgIssueRow> DevIssues { get; set; } = new();
    public List<StgIssueRow> StgSubtasks { get; set; } = new();

    public StgAlignment Alignment { get; set; }
    public List<StgIssueRow> MissingFromStg { get; set; } = new();
    public List<StgIssueRow> ExtraInStg { get; set; } = new();
}

public class ProdCardRow
{
    public string Key { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public List<StgIssueRow> LinkedStgCards { get; set; } = new();
}

public class StgIssueRow
{
    public string Key { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Assignee { get; set; } = string.Empty;
    public string IssueType { get; set; } = string.Empty;
    public double StoryPoints { get; set; }
    public bool IsInSprint { get; set; }
    public bool IsMatched { get; set; }
}

public class PreProdChecklistReport
{
    public string SprintName { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; } = DateTime.Now;
    public DateTime? DeployDate { get; set; }
    public bool IsGo => Checks.Count > 0 && Checks.All(c => c.Passed);
    public string Verdict => IsGo ? "GO" : "NO-GO";
    public List<PreProdCheck> Checks { get; set; } = new();
}

public class PreProdCheck
{
    public string Name { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string Detail { get; set; } = string.Empty;
    public List<string> FailedIssueKeys { get; set; } = new();
}
