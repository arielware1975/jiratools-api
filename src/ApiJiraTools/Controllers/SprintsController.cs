using ApiJiraTools.Services;
using Microsoft.AspNetCore.Mvc;

namespace ApiJiraTools.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SprintsController : ControllerBase
{
    private readonly JiraService _jira;

    public SprintsController(JiraService jira)
    {
        _jira = jira;
    }

    /// <summary>Lista los issues de un sprint.</summary>
    [HttpGet("{id:int}/issues")]
    public async Task<IActionResult> GetSprintIssues(int id)
    {
        var issues = await _jira.GetSprintIssuesDetailedAsync(id);
        return Ok(issues);
    }
}
