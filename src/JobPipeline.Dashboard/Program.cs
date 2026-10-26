using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using JobPipeline.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.HttpResults;

// Dashboard for the job pipeline: browse, edit, comment on, add and delete jobs in the same jobs.db the worker and
// Claude use. All writes go through JobStore, so it's safe to run alongside them. Everything needs a sign-in;
// accounts live in the users table of jobs.db and are managed from the command line:
//
//   JobPipeline.Dashboard.exe user add <name>       create a user (asks for the password)
//   JobPipeline.Dashboard.exe user passwd <name>    change a password (signs that user out everywhere)
//   JobPipeline.Dashboard.exe user remove <name>    delete a user
//   JobPipeline.Dashboard.exe user list
//
// Settings: appsettings.json next to the exe, overridden by appsettings.Local.json (not in the repo) or the
// command line, e.g.  JobPipeline.Dashboard.exe --Dashboard:DataDir=D:\jobs --urls=http://localhost:5090

// "user ..." commands take the leading words; everything from the first --option on is configuration.
var firstOption = Array.FindIndex(args, a => a.StartsWith('-'));
var words = firstOption < 0 ? args : args[..firstOption];
var options = firstOption < 0 ? Array.Empty<string>() : args[firstOption..];

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = options,
    ContentRootPath = AppContext.BaseDirectory,   // find appsettings.json and wwwroot wherever it's started from
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
builder.Configuration.AddCommandLine(options);

var dataDir = builder.Configuration["Dashboard:DataDir"];
var store = new JobStore(string.IsNullOrWhiteSpace(dataDir) ? JobStore.DefaultDataDir() : dataDir);

if (words.Length > 0)
    return UserCommands.Run(store, words);

builder.Services.AddSingleton(store);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

// Sign-in cookies are encrypted with keys kept in the data folder, so restarts don't sign everyone out.
var keys = builder.Services.AddDataProtection()
    .SetApplicationName("JobPipeline.Dashboard")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(store.DataDir, "dashboard-keys")));
if (OperatingSystem.IsWindows()) keys.ProtectKeysWithDpapi(protectToLocalMachine: true);

var sessionHours = builder.Configuration.GetValue("Dashboard:SessionHours", 12);
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "jobpipeline_session";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;   // Secure whenever the page is served over HTTPS
    o.ExpireTimeSpan = TimeSpan.FromHours(sessionHours);
    o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = ctx => Unauthorized(ctx.HttpContext);
    o.Events.OnRedirectToAccessDenied = ctx => Unauthorized(ctx.HttpContext);
    // Every request re-checks the account: deleting a user or changing their password ends their sessions.
    o.Events.OnValidatePrincipal = async ctx =>
    {
        var name = ctx.Principal?.Identity?.Name;
        var user = name is null ? null : store.GetUser(name);
        if (user is null || ctx.Principal!.FindFirstValue("pwd") != Stamp(user))
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    };
});
builder.Services.AddAuthorization();

// Slow down password guessing: 5 sign-in attempts per minute per address (on top of the slow password hash).
var attemptsPerMinute = builder.Configuration.GetValue("Dashboard:LoginAttemptsPerMinute", 5);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = (ctx, ct) => new ValueTask(ctx.HttpContext.Response.WriteAsJsonAsync(
        new { error = "Too many sign-in attempts. Wait a minute and try again." }, ct));
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = attemptsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();

// Behind a reverse proxy on the same machine (Caddy, IIS, Cloudflare Tunnel), use the visitor's real address and
// scheme. Only proxies on this machine are trusted, so the headers can't be spoofed from outside.
var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
app.UseForwardedHeaders(forwarded);

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
        "font-src 'self' https://fonts.gstatic.com; img-src 'self' data:; frame-ancestors 'none'; form-action 'self'; base-uri 'none'";
    if (ctx.Request.IsHttps) h["Strict-Transport-Security"] = "max-age=31536000";
    await next();
});

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

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// The gate: nothing but the sign-in page is served until you're signed in.
var publicPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/login.html", "/login.js", "/app.css", "/api/login" };
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "/";
    var isApi = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);

    // Changes must come from the page's own scripts: other sites can't add this header to a request.
    if (isApi && !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)
        && ctx.Request.Headers["X-Requested-With"] != "JobPipeline")
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new { error = "Missing X-Requested-With header." });
        return;
    }

    if (ctx.User.Identity?.IsAuthenticated == true || publicPaths.Contains(path)) { await next(); return; }
    if (isApi) { await Unauthorized(ctx); return; }
    var back = path == "/" || path == "/index.html" ? "" : "?next=" + Uri.EscapeDataString(path + ctx.Request.QueryString);
    ctx.Response.Redirect("/login.html" + back);
});

app.UseDefaultFiles();
app.UseStaticFiles();

// ── Sign-in ──

app.MapPost("/api/login", async (HttpContext ctx, LoginRequest body) =>
{
    if (store.UserCount() == 0)
        return Results.Json(new { error = "No users yet. On the server, run: JobPipeline.Dashboard.exe user add <name>" },
            statusCode: StatusCodes.Status401Unauthorized);
    var user = store.VerifyLogin(body.Username ?? "", body.Password ?? "");
    if (user is null)
        return Results.Json(new { error = "Wrong username or password." }, statusCode: StatusCodes.Status401Unauthorized);

    var identity = new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, user.Username), new Claim("pwd", Stamp(user))],
        CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
        new AuthenticationProperties { IsPersistent = body.Remember == true });
    return Results.Ok(new { username = user.Username });
}).RequireRateLimiting("login");

app.MapPost("/api/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
});

// ── Jobs (signed-in users only) ──

var api = app.MapGroup("/api").RequireAuthorization();

api.MapGet("/me", (ClaimsPrincipal user) => new { username = user.Identity!.Name });

api.MapGet("/info", () => new { dataDir = store.DataDir, database = store.DatabasePath, statuses = Enum.GetNames<JobStatus>() });

api.MapGet("/stats", () => store.Stats());

api.MapGet("/jobs", (string? status, string? q, int? limit, int? offset) =>
    store.QueryJobs(ParseStatus(status), q, limit ?? 500, offset ?? 0));

api.MapGet("/jobs/{id:guid}", Results<Ok<JobDetail>, NotFound> (Guid id) =>
    store.GetJob(id) is { } job ? TypedResults.Ok(new JobDetail(job, store.ReadJd(job))) : TypedResults.NotFound());

api.MapPost("/jobs", (NewJob input, ClaimsPrincipal user) =>
{
    if (string.IsNullOrWhiteSpace(input.Source)) input.Source = "manual";
    var result = store.AddJob(input, by: Who(user));
    return result.Result == "duplicate"
        ? Results.Conflict(new { error = "This job is already in the list.", id = result.Id })
        : Results.Created($"/api/jobs/{result.Id}", store.GetJob(result.Id));
});

api.MapPut("/jobs/{id:guid}", (Guid id, JobEdit edit, ClaimsPrincipal user) => store.EditJob(id, edit, Who(user)));

api.MapPost("/jobs/{id:guid}/status", (Guid id, StatusRequest body, ClaimsPrincipal user) =>
    store.SetStatus(id, ParseStatus(body.Status) ?? throw new ArgumentException("status is required"), body.Note, Who(user)));

api.MapPost("/jobs/{id:guid}/comments", (Guid id, TextRequest body, ClaimsPrincipal user) =>
    store.AddComment(id, body.Text ?? "", Who(user)));

api.MapDelete("/jobs/{id:guid}/comments/{commentId:guid}", (Guid id, Guid commentId) => store.DeleteComment(id, commentId));

api.MapPut("/jobs/{id:guid}/description", (Guid id, TextRequest body, ClaimsPrincipal user) =>
    store.SaveJobDescription(id, body.Text ?? "", Who(user)));

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
{
    Console.WriteLine($"Job pipeline dashboard: {string.Join(", ", app.Urls)}  (data: {store.DataDir})");
    var users = store.UserCount();
    Console.WriteLine(users == 0
        ? "No users yet, so nobody can sign in. Create one:  JobPipeline.Dashboard.exe user add <name>"
        : $"{users} user(s) can sign in.");
    foreach (var url in app.Urls)
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !IsLocal(url))
            Console.WriteLine($"WARNING: {url} is plain HTTP on a public address, so passwords travel unencrypted. " +
                              "Put HTTPS in front of it (see docs/GETTING-STARTED.md, \"Host the dashboard\").");
});

app.Run();
return 0;

static Task Unauthorized(HttpContext ctx)
{
    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
    return ctx.Response.WriteAsJsonAsync(new { error = "Please sign in." });
}

// Changes when the password changes, so old sessions stop working.
static string Stamp(UserInfo user) => user.PasswordChangedAt.ToUniversalTime().ToString("O");

static string Who(ClaimsPrincipal user) => user.Identity?.Name ?? "you";

static bool IsLocal(string url)
{
    var host = new Uri(url.Replace("://*", "://0.0.0.0").Replace("://+", "://0.0.0.0")).Host;
    return host is "localhost" || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
}

static JobStatus? ParseStatus(string? value) =>
    string.IsNullOrWhiteSpace(value) ? null
    : Enum.TryParse<JobStatus>(value, ignoreCase: true, out var s) && Enum.IsDefined(s) ? s
    : throw new ArgumentException($"'{value}' is not one of: {string.Join(", ", Enum.GetNames<JobStatus>())}");

record JobDetail(Job Job, string? JdText);
record StatusRequest(string? Status, string? Note);
record TextRequest(string? Text);
record LoginRequest(string? Username, string? Password, bool? Remember);

public partial class Program;
