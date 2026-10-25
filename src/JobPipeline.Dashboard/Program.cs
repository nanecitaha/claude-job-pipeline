using System.Text.Json;
using JobPipeline.Core;
using Microsoft.AspNetCore.Http.HttpResults;

// Local dashboard for the job pipeline: browse, edit, comment on, add and delete jobs in the same jobs.db the
// worker and Claude use. All writes go through JobStore, so it's safe to run alongside them.
//
// Settings: appsettings.json next to the exe, overridden by appsettings.Local.json (not in the repo) or the
// command line, e.g.  JobPipeline.Dashboard.exe --Dashboard:DataDir=D:\jobs --urls=http://localhost:5090

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,   // find appsettings.json and wwwroot wherever it's started from
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
builder.Configuration.AddCommandLine(args);

var dataDir = builder.Configuration["Dashboard:DataDir"];
var store = new JobStore(string.IsNullOrWhiteSpace(dataDir) ? JobStore.DefaultDataDir() : dataDir);
builder.Services.AddSingleton(store);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

var app = builder.Build();

// Business-rule errors become 400s with a readable message for the page to show.
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Exception ex) when (ex is PipelineException or ArgumentException or FormatException)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/info", () => new { dataDir = store.DataDir, database = store.DatabasePath, statuses = Enum.GetNames<JobStatus>() });

api.MapGet("/stats", () => store.Stats());

api.MapGet("/jobs", (string? status, string? q, int? limit, int? offset) =>
    store.QueryJobs(ParseStatus(status), q, limit ?? 500, offset ?? 0));

api.MapGet("/jobs/{id:guid}", Results<Ok<JobDetail>, NotFound> (Guid id) =>
    store.GetJob(id) is { } job ? TypedResults.Ok(new JobDetail(job, store.ReadJd(job))) : TypedResults.NotFound());

api.MapPost("/jobs", (NewJob input) =>
{
    if (string.IsNullOrWhiteSpace(input.Source)) input.Source = "manual";
    var result = store.AddJob(input, by: "you");
    return result.Result == "duplicate"
        ? Results.Conflict(new { error = "This job is already in the list.", id = result.Id })
        : Results.Created($"/api/jobs/{result.Id}", store.GetJob(result.Id));
});

api.MapPut("/jobs/{id:guid}", (Guid id, JobEdit edit) => store.EditJob(id, edit));

api.MapPost("/jobs/{id:guid}/status", (Guid id, StatusRequest body) =>
    store.SetStatus(id, ParseStatus(body.Status) ?? throw new ArgumentException("status is required"), body.Note));

api.MapPost("/jobs/{id:guid}/comments", (Guid id, TextRequest body) => store.AddComment(id, body.Text ?? ""));

api.MapDelete("/jobs/{id:guid}/comments/{commentId:guid}", (Guid id, Guid commentId) => store.DeleteComment(id, commentId));

api.MapPut("/jobs/{id:guid}/description", (Guid id, TextRequest body) => store.SaveJobDescription(id, body.Text ?? ""));

api.MapDelete("/jobs/{id:guid}", (Guid id) => store.DeleteJob(id) ? Results.NoContent() : Results.NotFound());

// The tailored resume, shown in the browser. Only files inside the data folder are served.
api.MapGet("/jobs/{id:guid}/resume", (Guid id) =>
{
    var job = store.GetJob(id);
    if (job?.ResumePath is not { Length: > 0 } path) return Results.NotFound(new { error = "No resume for this job yet." });
    var full = Path.GetFullPath(path);
    var root = Path.GetFullPath(store.DataDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        return Results.BadRequest(new { error = "The resume is outside the data folder, so it isn't served." });
    if (!File.Exists(full)) return Results.NotFound(new { error = "The resume file is missing on disk." });
    var type = Path.GetExtension(full).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        _ => "application/octet-stream",
    };
    return Results.File(full, type, enableRangeProcessing: true);
});

app.Lifetime.ApplicationStarted.Register(() =>
    Console.WriteLine($"Job pipeline dashboard: {string.Join(", ", app.Urls)}  (data: {store.DataDir})"));

app.Run();

static JobStatus? ParseStatus(string? value) =>
    string.IsNullOrWhiteSpace(value) ? null
    : Enum.TryParse<JobStatus>(value, ignoreCase: true, out var s) && Enum.IsDefined(s) ? s
    : throw new ArgumentException($"'{value}' is not one of: {string.Join(", ", Enum.GetNames<JobStatus>())}");

record JobDetail(Job Job, string? JdText);
record StatusRequest(string? Status, string? Note);
record TextRequest(string? Text);

public partial class Program;
