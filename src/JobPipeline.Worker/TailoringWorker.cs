using JobPipeline.Core;
using Microsoft.Extensions.Options;

namespace JobPipeline.Worker;

/// <summary>
/// Claims New jobs one at a time, tailors a resume, and moves each to ReadyToApply.
/// Drains the queue, then sleeps PollSeconds. Safe to run more than one instance: claims are atomic.
/// </summary>
public sealed class TailoringWorker(JobStore store, ITailor tailor, IOptions<PipelineOptions> options, ILogger<TailoringWorker> log)
    : BackgroundService
{
    readonly PipelineOptions _o = options.Value;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("Tailoring worker started. Data folder: {Dir}. Tailor: {Tailor}{Converter}.",
            store.DataDir, tailor.GetType().Name, tailor is TechStackTailor ? $" (PDF via {_o.PdfConverter})" : "");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, _o.PollSeconds)));
        do
        {
            try
            {
                while (!ct.IsCancellationRequested && await ProcessOneAsync(ct)) { }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Tailoring pass failed; will retry next tick");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    /// <returns>true if a job was processed (so the caller should look for another).</returns>
    public async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        var job = store.ClaimForTailoring(TimeSpan.FromMinutes(_o.StaleClaimMinutes));
        if (job is null) return false;

        log.LogInformation("Tailoring {Company} – {Title} ({Id}), attempt {Attempt}", job.Company, job.Title, job.Id, job.TailorAttempts);
        try
        {
            var jd = store.ReadJd(job);
            if (string.IsNullOrWhiteSpace(jd))
                throw new InvalidOperationException("No job description text saved for this job; nothing to tailor from.");

            var outputDir = Path.Combine(store.DataDir, "resumes");
            var result = await tailor.TailorAsync(new TailorRequest(job, jd, outputDir), ct);
            if (!File.Exists(result.ResumePath))
                throw new InvalidOperationException($"Tailor returned a path that does not exist: {result.ResumePath}");

            store.CompleteTailoring(job.Id, Path.GetFullPath(result.ResumePath), result.Note);
            log.LogInformation("Ready to apply: {Company} – {Title} → {Resume}", job.Company, job.Title, result.ResumePath);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // shutdown mid-job: the claim goes stale and is reclaimed next start
        }
        catch (Exception ex)
        {
            var after = store.FailTailoring(job.Id, ex.Message, _o.MaxTailorAttempts);
            log.LogWarning("Tailoring failed for {Id} ({Status} after {Attempts} attempts): {Error}",
                job.Id, after.Status, after.TailorAttempts, ex.Message);
        }
        return true;
    }
}
