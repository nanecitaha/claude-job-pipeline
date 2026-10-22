using JobPipeline.Core;
using JobPipeline.Worker;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<PipelineOptions>(builder.Configuration.GetSection("Pipeline"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<PipelineOptions>>().Value);
builder.Services.AddSingleton(sp =>
{
    var dir = sp.GetRequiredService<PipelineOptions>().DataDir;
    return new JobStore(string.IsNullOrWhiteSpace(dir) ? JobStore.DefaultDataDir() : dir);
});

// Swap this registration for your real tailor.
builder.Services.AddSingleton<ITailor, CopyMasterResumeTailor>();
builder.Services.AddHostedService<TailoringWorker>();

// To run as a Windows Service: add the Microsoft.Extensions.Hosting.WindowsServices package and uncomment.
// builder.Services.AddWindowsService(o => o.ServiceName = "JobPipeline Worker");

builder.Build().Run();
