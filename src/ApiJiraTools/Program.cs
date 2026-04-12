using ApiJiraTools.Configuration;
using ApiJiraTools.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JiraSettings>(builder.Configuration.GetSection("Jira"));
builder.Services.AddScoped<JiraService>();
builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();

app.Run();
