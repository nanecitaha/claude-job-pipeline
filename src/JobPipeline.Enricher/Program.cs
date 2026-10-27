using JobPipeline.Core;
using JobPipeline.Enricher;
using Microsoft.Extensions.Options;

// The enricher turns bare posting URLs (add_urls, status Discovered) into full jobs: it reads each posting over
// plain HTTP, preferring the employer ATS's public JSON API, applies the hunt rules, extracts the tech stack and
// hands kept jobs to the tailoring worker as New. Claude never has to read or re-type a job description.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

builder.Services.Configure<EnrichOptions>(builder.Configuration.GetSection("Enrich"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<EnrichOptions>>().Value);
builder.Services.AddSingleton(sp =>
{
    var dir = sp.GetRequiredService<EnrichOptions>().DataDir;
    return new JobStore(string.IsNullOrWhiteSpace(dir) ? JobStore.DefaultDataDir() : dir);
});
builder.Services.AddSingleton<IFetcher>(sp => new HttpFetcher(sp.GetRequiredService<EnrichOptions>()));
builder.Services.AddSingleton<PostingReader>();
builder.Services.AddHostedService<EnrichWorker>();

builder.Build().Run();
