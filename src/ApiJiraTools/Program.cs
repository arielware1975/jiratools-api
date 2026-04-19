using ApiJiraTools.Configuration;
using ApiJiraTools.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JiraSettings>(builder.Configuration.GetSection("Jira"));
builder.Services.Configure<TelegramSettings>(builder.Configuration.GetSection("Telegram"));
builder.Services.Configure<GeminiSettings>(builder.Configuration.GetSection("Gemini"));
builder.Services.AddScoped<JiraService>();
builder.Services.AddScoped<SprintClosureService>();
builder.Services.AddScoped<BurndownService>();
builder.Services.AddScoped<IssueTreeService>();
builder.Services.AddScoped<AlertService>();
builder.Services.AddScoped<ReleaseAuditService>();
builder.Services.AddScoped<GeminiService>();
builder.Services.AddScoped<IdeaSummaryService>();
builder.Services.AddScoped<AnalyzeService>();
builder.Services.AddScoped<ScopeAnalysisService>();
builder.Services.AddScoped<StgChecklistService>();
builder.Services.AddScoped<PreProdChecklistService>();
builder.Services.AddHostedService<TelegramBotService>();
builder.Services.AddHostedService<AlertSchedulerService>();
builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();

app.Run();
