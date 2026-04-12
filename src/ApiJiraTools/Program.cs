using ApiJiraTools.Configuration;
using ApiJiraTools.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JiraSettings>(builder.Configuration.GetSection("Jira"));
builder.Services.Configure<TelegramSettings>(builder.Configuration.GetSection("Telegram"));
builder.Services.AddScoped<JiraService>();
builder.Services.AddScoped<SprintClosureService>();
builder.Services.AddScoped<BurndownService>();
builder.Services.AddScoped<IssueTreeService>();
builder.Services.AddHostedService<TelegramBotService>();
builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();

app.Run();
