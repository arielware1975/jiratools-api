using System.Text.Json;

namespace ApiJiraTools.Helpers;

public static class JiraAdfTextFormatter
{
    public static string ToPlainText(JsonElement? description)
    {
        if (description == null || description.Value.ValueKind == JsonValueKind.Undefined || description.Value.ValueKind == JsonValueKind.Null)
            return string.Empty;

        var parts = new List<string>();
        AppendNode(description.Value, parts, 0);

        var text = string.Join(string.Empty, parts);
        return Normalize(text);
    }

    private static void AppendNode(JsonElement node, List<string> parts, int depth)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
                AppendNode(item, parts, depth);
            return;
        }

        if (node.ValueKind != JsonValueKind.Object)
            return;

        string type = node.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
            ? typeProp.GetString() ?? string.Empty
            : string.Empty;

        switch (type)
        {
            case "text":
                if (node.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
                    parts.Add(textProp.GetString() ?? string.Empty);
                break;

            case "hardBreak":
                parts.Add(Environment.NewLine);
                break;

            case "paragraph":
            case "heading":
                AppendChildren(node, parts, depth);
                parts.Add(Environment.NewLine + Environment.NewLine);
                break;

            case "bulletList":
            case "orderedList":
                AppendChildren(node, parts, depth + 1);
                parts.Add(Environment.NewLine);
                break;

            case "listItem":
                parts.Add(new string(' ', Math.Max(0, depth - 1) * 2));
                parts.Add("- ");
                AppendChildren(node, parts, depth);
                parts.Add(Environment.NewLine);
                break;

            case "rule":
                parts.Add(Environment.NewLine + "---" + Environment.NewLine);
                break;

            default:
                AppendChildren(node, parts, depth);
                break;
        }
    }

    private static void AppendChildren(JsonElement node, List<string> parts, int depth)
    {
        if (node.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in content.EnumerateArray())
                AppendNode(child, parts, depth);
        }
    }

    private static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');

        while (normalized.Contains("\n\n\n"))
            normalized = normalized.Replace("\n\n\n", "\n\n");

        return normalized.Trim().Replace("\n", Environment.NewLine);
    }
}
