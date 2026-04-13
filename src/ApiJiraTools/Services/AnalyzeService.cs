using System.Text;
using ApiJiraTools.Models;

namespace ApiJiraTools.Services;

public class AnalyzeService
{
    private readonly JiraService _jira;
    private readonly IssueTreeService _tree;
    private readonly GeminiService _gemini;
    private readonly ILogger<AnalyzeService> _logger;

    public AnalyzeService(JiraService jira, IssueTreeService tree, GeminiService gemini, ILogger<AnalyzeService> logger)
    {
        _jira = jira;
        _tree = tree;
        _gemini = gemini;
        _logger = logger;
    }

    public async Task<string> AnalyzeIssueAsync(string issueKey)
    {
        _logger.LogInformation("AnalyzeService. issueKey={IssueKey}", issueKey);

        // 1. Armar el árbol de contexto
        var report = await _tree.BuildAsync(issueKey);

        // 2. Construir el prompt con toda la info
        var context = BuildContext(report);
        var prompt = BuildPrompt(context);

        // 3. Llamar a Gemini
        var analysis = await _gemini.GenerateAsync(prompt);

        return analysis;
    }

    private string BuildContext(IssueTreeReport report)
    {
        var sb = new StringBuilder();

        // Idea
        if (!string.IsNullOrEmpty(report.IdeaKey))
        {
            sb.AppendLine($"IDEA: {report.IdeaKey} - {report.IdeaSummary}");
            sb.AppendLine($"  Estado: {report.IdeaStatus}");
            if (!string.IsNullOrEmpty(report.IdeaRoadmap))
                sb.AppendLine($"  Roadmap: {report.IdeaRoadmap}");
            sb.AppendLine();
        }

        // Épica
        if (!string.IsNullOrEmpty(report.EpicKey))
        {
            sb.AppendLine($"ÉPICA: {report.EpicKey} - {report.EpicSummary}");
            sb.AppendLine($"  Estado: {report.EpicStatus}");
            sb.AppendLine();
        }

        // Issue principal
        sb.AppendLine($"ISSUE PRINCIPAL: {report.IssueKey} - {report.IssueSummary}");
        sb.AppendLine($"  Tipo: {report.IssueType}");
        sb.AppendLine($"  Estado: {report.IssueStatus}");
        sb.AppendLine($"  Asignado: {report.IssueAssignee}");
        if (report.IssueStoryPoints > 0)
            sb.AppendLine($"  Story Points: {report.IssueStoryPoints}");
        if (!string.IsNullOrEmpty(report.IssueSprint))
            sb.AppendLine($"  Sprint: {report.IssueSprint}");
        if (report.IssueLabels.Count > 0)
            sb.AppendLine($"  Labels: {string.Join(", ", report.IssueLabels)}");
        sb.AppendLine();

        // Hijos
        if (report.Children.Count > 0)
        {
            sb.AppendLine($"HIJOS DE LA ÉPICA ({report.Children.Count}):");
            int done = 0, inProgress = 0, toDo = 0;
            double totalSp = 0, doneSp = 0;

            foreach (var child in report.Children)
            {
                var status = child.Status;
                var spText = child.StoryPoints > 0 ? $" [{child.StoryPoints} SP]" : "";
                var sprintText = !string.IsNullOrEmpty(child.Sprint) ? $" | {child.Sprint}" : "";
                sb.AppendLine($"  {child.Key} | {child.IssueType} | {status} | {child.Assignee}{spText}{sprintText}");
                sb.AppendLine($"    {child.Summary}");

                totalSp += child.StoryPoints;
                if (IsDone(status)) { done++; doneSp += child.StoryPoints; }
                else if (IsInProgress(status)) { inProgress++; }
                else { toDo++; }
            }

            sb.AppendLine();
            sb.AppendLine($"RESUMEN: {done} Done, {inProgress} En Progreso, {toDo} To Do");
            sb.AppendLine($"  SP: {doneSp}/{totalSp} completados ({(totalSp > 0 ? doneSp / totalSp * 100 : 0):0}%)");
            sb.AppendLine();
        }

        // Hermanos
        if (report.Siblings.Count > 0)
        {
            sb.AppendLine($"HERMANOS EN LA ÉPICA ({report.Siblings.Count}):");
            foreach (var s in report.Siblings)
                sb.AppendLine($"  {s.Key} | {s.Status} | {s.Assignee} | {s.Summary}");
            sb.AppendLine();
        }

        // Blockers
        if (report.BlockedBy.Count > 0)
        {
            sb.AppendLine("BLOQUEADO POR:");
            foreach (var b in report.BlockedBy)
                sb.AppendLine($"  {b.Key} | {b.Status} | {b.Summary}");
            sb.AppendLine();
        }

        if (report.Blocks.Count > 0)
        {
            sb.AppendLine("BLOQUEA A:");
            foreach (var b in report.Blocks)
                sb.AppendLine($"  {b.Key} | {b.Status} | {b.Summary}");
            sb.AppendLine();
        }

        // Links
        if (report.Links.Count > 0)
        {
            sb.AppendLine($"LINKS ({report.Links.Count}):");
            foreach (var l in report.Links)
                sb.AppendLine($"  {l.Relation}: {l.Key} | {l.IssueType} | {l.Status} | {l.Summary}");
        }

        return sb.ToString();
    }

    private static string BuildPrompt(string context)
    {
        return $"""
            Sos un PM técnico senior analizando el estado de una iniciativa de desarrollo de software.

            Analizá la siguiente información de Jira y respondé en español, de forma concisa y accionable:

            ---
            {context}
            ---

            Respondé con este formato exacto:

            📋 RESUMEN EJECUTIVO
            (2-3 líneas sobre qué es esta iniciativa y en qué estado está)

            📊 AVANCE
            (% de avance real basado en issues y SP, no solo conteo)

            ⚠️ RIESGOS Y BLOCKERS
            (issues bloqueados, sin asignar, sin SP, dependencias, issues STG/PROD pendientes)

            📌 QUÉ FALTA PARA CERRAR
            (lista concreta de lo que queda por hacer, priorizado)

            🗓️ ESTIMACIÓN
            (basado en la velocidad observada y lo que falta, cuándo se podría entregar)

            💡 RECOMENDACIONES
            (1-3 acciones concretas que el PM debería tomar ahora)

            Sé directo y específico. Mencioná los issue keys cuando hables de issues puntuales.
            No inventes información que no esté en los datos.
            """;
    }

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
