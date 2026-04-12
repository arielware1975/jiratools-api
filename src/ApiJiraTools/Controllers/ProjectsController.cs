using ApiJiraTools.Services;
using Microsoft.AspNetCore.Mvc;

namespace ApiJiraTools.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProjectsController : ControllerBase
{
    private readonly JiraService _jira;

    public ProjectsController(JiraService jira)
    {
        _jira = jira;
    }

    /// <summary>Lista todos los proyectos de Jira.</summary>
    [HttpGet]
    public async Task<IActionResult> GetProjects()
    {
        var projects = await _jira.GetProjectsAsync();
        return Ok(projects);
    }

    /// <summary>Lista los sprints de un proyecto.</summary>
    [HttpGet("{key}/sprints")]
    public async Task<IActionResult> GetSprints(string key)
    {
        var sprints = await _jira.GetSprintsByProjectAsync(key);
        return Ok(sprints);
    }
}
