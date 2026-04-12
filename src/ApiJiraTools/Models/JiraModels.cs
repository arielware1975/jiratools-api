using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApiJiraTools.Models;

// ── Project ────────────────────────────────────────────────────────────────

public class JiraProjectSearchResult
{
    [JsonPropertyName("isLast")]
    public bool IsLast { get; set; }

    [JsonPropertyName("nextPage")]
    public string? NextPage { get; set; }

    [JsonPropertyName("startAt")]
    public int StartAt { get; set; }

    [JsonPropertyName("maxResults")]
    public int MaxResults { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("values")]
    public List<JiraProject> Values { get; set; } = new();
}

public class JiraProject
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("projectTypeKey")]
    public string? ProjectTypeKey { get; set; }

    [JsonPropertyName("simplified")]
    public bool? Simplified { get; set; }

    [JsonPropertyName("style")]
    public string? Style { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

// ── Board ──────────────────────────────────────────────────────────────────

public class JiraBoardSearchResult
{
    [JsonPropertyName("values")]
    public List<JiraBoard> Values { get; set; } = new();
}

public class JiraBoard
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}

// ── Sprint ─────────────────────────────────────────────────────────────────

public class JiraSprintSearchResult
{
    [JsonPropertyName("values")]
    public List<JiraSprint> Values { get; set; } = new();
}

public class JiraSprint
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("startDate")]
    public DateTimeOffset? StartDate { get; set; }

    [JsonPropertyName("endDate")]
    public DateTimeOffset? EndDate { get; set; }

    [JsonPropertyName("completeDate")]
    public DateTimeOffset? CompleteDate { get; set; }
}

// ── JQL Search ─────────────────────────────────────────────────────────────

public class JiraJqlSearchRequest
{
    [JsonPropertyName("jql")]
    public string Jql { get; set; } = string.Empty;

    [JsonPropertyName("maxResults")]
    public int MaxResults { get; set; } = 100;

    [JsonPropertyName("fields")]
    public List<string> Fields { get; set; } = new();

    [JsonPropertyName("fieldsByKeys")]
    public bool FieldsByKeys { get; set; } = false;

    [JsonPropertyName("nextPageToken")]
    public string? NextPageToken { get; set; }
}

public class JiraJqlSearchResult
{
    [JsonPropertyName("issues")]
    public List<JiraIssue> Issues { get; set; } = new();

    [JsonPropertyName("isLast")]
    public bool IsLast { get; set; }

    [JsonPropertyName("nextPageToken")]
    public string? NextPageToken { get; set; }
}

// ── Issue ──────────────────────────────────────────────────────────────────

public class JiraIssue
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("fields")]
    public JiraIssueFields Fields { get; set; } = new();

    [JsonPropertyName("changelog")]
    public JiraIssueChangelog? Changelog { get; set; }

    [JsonPropertyName("archived")]
    public bool Archived { get; set; }

    [JsonPropertyName("archivedBy")]
    public JsonElement? ArchivedBy { get; set; }

    [JsonIgnore]
    public bool IsArchived => Archived || (ArchivedBy.HasValue && ArchivedBy.Value.ValueKind == JsonValueKind.Object);
}

public class JiraIssueFields
{
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("issuetype")]
    public JiraIssueType IssueType { get; set; } = new();

    [JsonPropertyName("parent")]
    public JiraIssue? Parent { get; set; }

    [JsonPropertyName("status")]
    public JiraStatus Status { get; set; } = new();

    [JsonPropertyName("assignee")]
    public JiraUser? Assignee { get; set; }

    [JsonPropertyName("labels")]
    public List<string> Labels { get; set; } = new();

    [JsonPropertyName("priority")]
    public JiraPriority Priority { get; set; } = new();

    [JsonPropertyName("updated")]
    public string? Updated { get; set; }

    [JsonPropertyName("created")]
    public string? Created { get; set; }

    [JsonPropertyName("duedate")]
    public string? DueDate { get; set; }

    [JsonPropertyName("resolutiondate")]
    public string? ResolutionDate { get; set; }

    [JsonPropertyName("issuelinks")]
    public List<JiraIssueLink> IssueLinks { get; set; } = new();

    [JsonPropertyName("description")]
    public JsonElement? Description { get; set; }

    [JsonPropertyName("customfield_10024")]
    public double? StoryPoints { get; set; }

    [JsonPropertyName("customfield_10016")]
    public double? StoryPointEstimate { get; set; }

    [JsonPropertyName("attachment")]
    public List<JiraAttachment> Attachments { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }

    [JsonIgnore]
    public DateTimeOffset? UpdatedDate => ParseJiraDate(Updated);

    [JsonIgnore]
    public DateTimeOffset? CreatedDate => ParseJiraDate(Created);

    [JsonIgnore]
    public DateTimeOffset? ResolutionDateValue => ParseJiraDate(ResolutionDate);

    [JsonIgnore]
    public bool HasAssignee => Assignee != null && !string.IsNullOrWhiteSpace(Assignee.DisplayName);

    public string GetDescriptionText()
    {
        return Helpers.JiraAdfTextFormatter.ToPlainText(Description);
    }

    public double GetStoryPointsValue(string fieldId)
    {
        if (!string.IsNullOrWhiteSpace(fieldId))
        {
            var configured = GetCustomFieldAsDouble(fieldId);
            if (configured.HasValue)
                return configured.Value;
        }
        return StoryPoints ?? 0d;
    }

    public double GetStoryPointEstimateValue(string fieldId)
    {
        if (!string.IsNullOrWhiteSpace(fieldId))
        {
            var configured = GetCustomFieldAsDouble(fieldId);
            if (configured.HasValue)
                return configured.Value;
        }
        return StoryPointEstimate ?? 0d;
    }

    public double? GetCustomFieldAsDouble(string fieldId)
    {
        var raw = GetCustomFieldRaw(fieldId);
        if (raw == null) return null;
        var value = raw.Value;
        try
        {
            return value.ValueKind switch
            {
                JsonValueKind.Number => value.GetDouble(),
                JsonValueKind.String => TryParseDouble(value.GetString()),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => null
            };
        }
        catch { return null; }
    }

    public JsonElement? GetCustomFieldRaw(string fieldId)
    {
        if (string.IsNullOrWhiteSpace(fieldId) || ExtraFields == null)
            return null;
        return ExtraFields.TryGetValue(fieldId, out var value) ? value : null;
    }

    public string? GetSprintName(string sprintFieldId = "customfield_10020")
    {
        var raw = GetCustomFieldRaw(sprintFieldId);
        if (raw == null) return null;
        var el = raw.Value;
        try
        {
            if (el.ValueKind == JsonValueKind.Array)
            {
                string? activeName = null, anyName = null;
                foreach (var item in el.EnumerateArray())
                {
                    string? name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? state = item.TryGetProperty("state", out var s) ? s.GetString() : null;
                    if (name == null) continue;
                    anyName = name;
                    if (string.Equals(state, "active", StringComparison.OrdinalIgnoreCase))
                    {
                        activeName = name;
                        break;
                    }
                }
                return activeName ?? anyName;
            }
            if (el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty("name", out var n2)) return n2.GetString();
            }
        }
        catch { }
        return null;
    }

    private static double? TryParseDouble(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var v1)) return v1;
        if (double.TryParse(text, NumberStyles.Any, CultureInfo.GetCultureInfo("es-AR"), out var v2)) return v2;
        return null;
    }

    private static DateTimeOffset? ParseJiraDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
            return dto;
        if (DateTimeOffset.TryParse(value, out dto))
            return dto;
        return null;
    }
}

// ── Issue sub-types ────────────────────────────────────────────────────────

public class JiraIssueType
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("subtask")]
    public bool? Subtask { get; set; }
}

public class JiraStatus
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("statusCategory")]
    public JiraStatusCategory StatusCategory { get; set; } = new();
}

public class JiraStatusCategory
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public class JiraUser
{
    [JsonPropertyName("accountId")]
    public string? AccountId { get; set; }

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("emailAddress")]
    public string? EmailAddress { get; set; }
}

public class JiraPriority
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

// ── Links ──────────────────────────────────────────────────────────────────

public class JiraIssueLink
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public JiraIssueLinkType? Type { get; set; }

    [JsonPropertyName("inwardIssue")]
    public JiraLinkedIssue? InwardIssue { get; set; }

    [JsonPropertyName("outwardIssue")]
    public JiraLinkedIssue? OutwardIssue { get; set; }

    [JsonIgnore]
    public JiraLinkedIssue? LinkedIssue => OutwardIssue ?? InwardIssue;
}

public class JiraIssueLinkType
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("inward")]
    public string? Inward { get; set; }

    [JsonPropertyName("outward")]
    public string? Outward { get; set; }
}

public class JiraLinkedIssue
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("fields")]
    public JiraLinkedIssueFields Fields { get; set; } = new();
}

public class JiraLinkedIssueFields
{
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public JiraStatus Status { get; set; } = new();

    [JsonPropertyName("issuetype")]
    public JiraIssueType IssueType { get; set; } = new();
}

// ── Epic Summary ───────────────────────────────────────────────────────────

public class JiraEpicSummary
{
    public string EpicId { get; set; } = string.Empty;
    public string EpicKey { get; set; } = string.Empty;
    public string EpicSummary { get; set; } = string.Empty;
    public bool HasStgChild { get; set; }
    public string StgIssueKey { get; set; } = string.Empty;
    public string StgIssueSummary { get; set; } = string.Empty;
    public int ChildCount { get; set; }
    public int SprintChildCount { get; set; }
    public List<string> Labels { get; set; } = new();
    public bool IsStgNotRequired { get; set; }
    public List<JiraEpicChildInfo> Children { get; set; } = new();
}

public class JiraEpicChildInfo
{
    public string Key { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string IssueTypeName { get; set; } = string.Empty;
    public bool IsInSelectedSprint { get; set; }
}

// ── Changelog ──────────────────────────────────────────────────────────────

public class JiraIssueChangelog
{
    [JsonPropertyName("histories")]
    public List<JiraIssueHistory> Histories { get; set; } = new();
}

public class JiraIssueHistory
{
    [JsonPropertyName("created")]
    public string? Created { get; set; }

    [JsonPropertyName("items")]
    public List<JiraIssueHistoryItem> Items { get; set; } = new();

    [JsonIgnore]
    public DateTimeOffset? CreatedDate => DateTimeOffset.TryParse(Created, out var dto) ? dto : null;
}

public class JiraIssueHistoryItem
{
    [JsonPropertyName("field")]
    public string? Field { get; set; }

    [JsonPropertyName("fromString")]
    public string? FromString { get; set; }

    [JsonPropertyName("toString")]
    public string? ToStringValue { get; set; }
}

// ── Attachment ─────────────────────────────────────────────────────────────

public class JiraAttachment
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public JiraUser? Author { get; set; }

    [JsonPropertyName("created")]
    public string? Created { get; set; }
}
