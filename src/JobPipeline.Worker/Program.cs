using JobPipeline.Core;
using JobPipeline.Worker;
using Microsoft.Extensions.Options;

// Read appsettings.json from the exe's folder, not the current directory: the worker is often started from
// elsewhere (a console in the user's home folder, Task Scheduler in System32).
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.Configure<PipelineOptions>(builder.Configuration.GetSection("Pipeline"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<PipelineOptions>>().Value);
builder.Services.AddSingleton(sp =>
{
    var dir = sp.GetRequiredService<PipelineOptions>().DataDir;
    return new JobStore(string.IsNullOrWhiteSpace(dir) ? JobStore.DefaultDataDir() : dir);
});

var pipeline = builder.Configuration.GetSection("Pipeline").Get<PipelineOptions>() ?? new PipelineOptions();

// Tailor: Pipeline:Tailor = "TechStack" (v1) or "CopyMaster". Add your AI tailor here later.
switch (pipeline.Tailor.Trim().ToLowerInvariant())
{
    case "techstack":
        builder.Services.AddSingleton<ITailor, TechStackTailor>();
        AddPdfConverter(builder.Services, pipeline);
        break;
    case "copymaster":
        builder.Services.AddSingleton<ITailor, CopyMasterResumeTailor>();
        break;
    default:
        throw new InvalidOperationException($"Unknown Pipeline:Tailor '{pipeline.Tailor}'. Use \"TechStack\" or \"CopyMaster\".");
}
builder.Services.AddHostedService<TailoringWorker>();

// To run as a Windows Service: add the Microsoft.Extensions.Hosting.WindowsServices package and uncomment.
// builder.Services.AddWindowsService(o => o.ServiceName = "JobPipeline Worker");

builder.Build().Run();

// PDF converter: Pipeline:PdfConverter = "Word" or "LibreOffice". Only registered for tailors that make PDFs.
static void AddPdfConverter(IServiceCollection services, PipelineOptions pipeline)
{
    switch (pipeline.PdfConverter.Trim().ToLowerInvariant())
    {
        case "word" when OperatingSystem.IsWindows():
            services.AddSingleton<IPdfConverter, WordPdfConverter>();
            break;
        case "word":
            throw new PlatformNotSupportedException("Pipeline:PdfConverter \"Word\" needs Windows; use \"LibreOffice\".");
        case "libreoffice":
            services.AddSingleton<IPdfConverter, LibreOfficePdfConverter>();
            break;
        default:
            throw new InvalidOperationException($"Unknown Pipeline:PdfConverter '{pipeline.PdfConverter}'. Use \"Word\" or \"LibreOffice\".");
    }
}
