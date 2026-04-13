using System.Text;
using ApiJiraTools.Models;

namespace ApiJiraTools.Services;

public class AnalyzeService
{
    private readonly JiraService _jira;
    private readonly IdeaSummaryService _ideaSummary;
    private readonly IssueTreeService _tree;
    private readonly GeminiService _gemini;
    private readonly ILogger<AnalyzeService> _logger;

    public AnalyzeService(JiraService jira, IdeaSummaryService ideaSummary, IssueTreeService tree,
        GeminiService gemini, ILogger<AnalyzeService> logger)
    {
        _jira = jira;
        _ideaSummary = ideaSummary;
        _tree = tree;
        _gemini = gemini;
        _logger = logger;
    }

    public async Task<string> AnalyzeIssueAsync(string issueKey)
    {
        _logger.LogInformation("AnalyzeService. issueKey={IssueKey}", issueKey);

        // Detectar si es una Idea o un issue regular
        var issue = await _jira.GetIssueByKeyAsync(issueKey);
        bool isIdea = string.Equals(issue.Fields?.IssueType?.Name, "Idea", StringComparison.OrdinalIgnoreCase);

        string context;
        if (isIdea)
        {
            // Recorrido completo: Idea → Épicas → Hijos → Comments → Attachments
            var ideaNode = await _ideaSummary.BuildIdeaNodeAsync(issueKey);
            context = BuildIdeaContext(ideaNode);
        }
        else
        {
            // Para issues normales o épicas, usar el tree + cargar comments del epic
            bool isEpic = string.Equals(issue.Fields?.IssueType?.Name, "Epic", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(issue.Fields?.IssueType?.Name, "Épica", StringComparison.OrdinalIgnoreCase);

            if (isEpic)
            {
                var epicNode = await _ideaSummary.BuildEpicNodeAsync(issueKey);
                context = BuildEpicContext(epicNode);
            }
            else
            {
                var report = await _tree.BuildAsync(issueKey);
                context = BuildIssueContext(report, issue);
            }
        }

        var prompt = BuildPrompt(context);
        return await _gemini.GenerateAsync(prompt);
    }

    private static string BuildIdeaContext(IdeaNode idea)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"IDEA: {idea.Key} - {idea.Summary}");
        sb.AppendLine($"  Estado: {idea.Status}");
        sb.AppendLine($"  Roadmap: {idea.Roadmap}");
        sb.AppendLine($"  Asignado: {idea.Assignee}");
        if (!string.IsNullOrWhiteSpace(idea.Description))
            sb.AppendLine($"  Descripción: {Truncate(idea.Description, 2000)}");
        AppendAttachments(sb, idea.Attachments, "  ");
        AppendComments(sb, idea.Comments, "  ");
        sb.AppendLine();

        foreach (var epic in idea.Epics)
        {
            sb.AppendLine($"ÉPICA: {epic.Key} - {epic.Summary}");
            sb.AppendLine($"  Estado: {epic.Status}");
            sb.AppendLine($"  Asignado: {epic.Assignee}");
            if (!string.IsNullOrWhiteSpace(epic.Description))
                sb.AppendLine($"  Descripción: {Truncate(epic.Description, 1000)}");
            AppendAttachments(sb, epic.Attachments, "  ");
            AppendComments(sb, epic.Comments, "  ");
            sb.AppendLine();

            int done = 0, inProgress = 0, toDo = 0;
            double totalSp = 0, doneSp = 0;

            sb.AppendLine($"  HIJOS ({epic.Issues.Count}):");
            foreach (var child in epic.Issues)
            {
                var spText = child.StoryPoints.HasValue ? $" [{child.StoryPoints.Value} SP]" : "";
                sb.AppendLine($"    {child.Key} | {child.IssueType} | {child.Status} | {child.Assignee}{spText}");
                sb.AppendLine($"      {child.Summary}");

                if (!string.IsNullOrWhiteSpace(child.Description))
                    sb.AppendLine($"      Desc: {Truncate(child.Description, 300)}");

                AppendComments(sb, child.Comments, "      ");

                totalSp += child.StoryPoints ?? 0;
                if (IsDone(child.Status)) { done++; doneSp += child.StoryPoints ?? 0; }
                else if (IsInProgress(child.Status)) inProgress++;
                else toDo++;
            }

            sb.AppendLine();
            sb.AppendLine($"  RESUMEN ÉPICA: {done} Done, {inProgress} En Progreso, {toDo} To Do | SP: {doneSp}/{totalSp}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildEpicContext(EpicNode epic)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"ÉPICA: {epic.Key} - {epic.Summary}");
        sb.AppendLine($"  Estado: {epic.Status}");
        sb.AppendLine($"  Asignado: {epic.Assignee}");
        if (!string.IsNullOrWhiteSpace(epic.Description))
            sb.AppendLine($"  Descripción: {Truncate(epic.Description, 1500)}");
        AppendAttachments(sb, epic.Attachments, "  ");
        AppendComments(sb, epic.Comments, "  ");
        sb.AppendLine();

        int done = 0, inProgress = 0, toDo = 0;
        double totalSp = 0, doneSp = 0;

        sb.AppendLine($"HIJOS ({epic.Issues.Count}):");
        foreach (var child in epic.Issues)
        {
            var spText = child.StoryPoints.HasValue ? $" [{child.StoryPoints.Value} SP]" : "";
            sb.AppendLine($"  {child.Key} | {child.IssueType} | {child.Status} | {child.Assignee}{spText}");
            sb.AppendLine($"    {child.Summary}");
            if (!string.IsNullOrWhiteSpace(child.Description))
                sb.AppendLine($"    Desc: {Truncate(child.Description, 300)}");
            AppendComments(sb, child.Comments, "    ");

            totalSp += child.StoryPoints ?? 0;
            if (IsDone(child.Status)) { done++; doneSp += child.StoryPoints ?? 0; }
            else if (IsInProgress(child.Status)) inProgress++;
            else toDo++;
        }

        sb.AppendLine();
        sb.AppendLine($"RESUMEN: {done} Done, {inProgress} En Progreso, {toDo} To Do | SP: {doneSp}/{totalSp}");
        return sb.ToString();
    }

    private static string BuildIssueContext(IssueTreeReport report, JiraIssue issue)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(report.EpicKey))
        {
            sb.AppendLine($"ÉPICA: {report.EpicKey} - {report.EpicSummary}");
            sb.AppendLine($"  Estado: {report.EpicStatus}");
            sb.AppendLine();
        }

        sb.AppendLine($"ISSUE: {report.IssueKey} - {report.IssueSummary}");
        sb.AppendLine($"  Tipo: {report.IssueType}");
        sb.AppendLine($"  Estado: {report.IssueStatus}");
        sb.AppendLine($"  Asignado: {report.IssueAssignee}");
        if (report.IssueStoryPoints > 0)
            sb.AppendLine($"  SP: {report.IssueStoryPoints}");
        if (!string.IsNullOrEmpty(report.IssueSprint))
            sb.AppendLine($"  Sprint: {report.IssueSprint}");

        var desc = issue.Fields?.GetDescriptionText();
        if (!string.IsNullOrWhiteSpace(desc))
            sb.AppendLine($"  Descripción: {Truncate(desc, 1500)}");

        if (report.BlockedBy.Count > 0)
        {
            sb.AppendLine("\n  BLOQUEADO POR:");
            foreach (var b in report.BlockedBy)
                sb.AppendLine($"    {b.Key} | {b.Status} | {b.Summary}");
        }

        if (report.Siblings.Count > 0)
        {
            sb.AppendLine($"\n  HERMANOS EN LA ÉPICA ({report.Siblings.Count}):");
            foreach (var s in report.Siblings)
                sb.AppendLine($"    {s.Key} | {s.Status} | {s.Assignee} | {s.Summary}");
        }

        return sb.ToString();
    }

    private static void AppendComments(StringBuilder sb, List<IssueCommentNode> comments, string indent)
    {
        if (comments.Count == 0) return;
        sb.AppendLine($"{indent}Comentarios ({comments.Count}):");
        foreach (var c in comments.Take(5))
            sb.AppendLine($"{indent}  [{c.Created:dd/MM}] {c.Author}: {Truncate(c.Body, 300)}");
        if (comments.Count > 5)
            sb.AppendLine($"{indent}  ...y {comments.Count - 5} más");
    }

    private static void AppendAttachments(StringBuilder sb, List<IssueAttachmentNode> attachments, string indent)
    {
        if (attachments.Count == 0) return;
        sb.AppendLine($"{indent}Adjuntos ({attachments.Count}):");
        foreach (var a in attachments.Take(5))
            sb.AppendLine($"{indent}  - {a.Filename} ({a.MimeType})");
    }

    private static string BuildPrompt(string context)
    {
        return $"""
            Sos un PM técnico senior analizando el estado de una iniciativa de desarrollo de software.
            Tenés acceso a toda la información de Jira: ideas, épicas, issues, comentarios del equipo y adjuntos.

            Analizá la siguiente información y respondé en español, de forma concisa y accionable:

            ---
            {context}
            ---

            Respondé con este formato exacto:

            📋 RESUMEN EJECUTIVO
            (2-3 líneas: qué es, para qué sirve, en qué estado está)

            📊 AVANCE
            (% de avance basado en SP y issues, desglose por estado)

            ⚠️ RIESGOS Y BLOCKERS
            (issues bloqueados, sin asignar, sin SP, dependencias, delays detectados en los comentarios)

            📌 QUÉ FALTA PARA CERRAR
            (lista priorizada de lo que queda, basada en los issues pendientes y los comentarios del equipo)

            👥 EQUIPO
            (quién está haciendo qué, carga de trabajo, issues sin dueño)

            🗓️ ESTIMACIÓN
            (basado en velocidad observada y lo que falta)

            💡 RECOMENDACIONES
            (3-5 acciones concretas que el PM debería tomar, basadas en la evidencia)

            Sé directo y específico. Mencioná issue keys. Usá los comentarios del equipo como evidencia.
            No inventes información que no esté en los datos.
            """;
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "...";

    private static bool IsDone(string status) =>
        status.Contains("Done", StringComparison.OrdinalIgnoreCase)
        || status.Contains("Finalizada", StringComparison.OrdinalIgnoreCase)
        || status.Contains("Resolved", StringComparison.OrdinalIgnoreCase)
        || status.Contains("Closed", StringComparison.OrdinalIgnoreCase);

    private static bool IsInProgress(string status) =>
        status.Contains("Curso", StringComparison.OrdinalIgnoreCase)
        || status.Contains("Progress", StringComparison.OrdinalIgnoreCase)
        || status.Contains("Revisar", StringComparison.OrdinalIgnoreCase)
        || status.Contains("Review", StringComparison.OrdinalIgnoreCase)
        || status.Contains("Test", StringComparison.OrdinalIgnoreCase);
}
