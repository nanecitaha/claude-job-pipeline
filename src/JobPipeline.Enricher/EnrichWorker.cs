using JobPipeline.Core;

namespace JobPipeline.Enricher;

/// <summary>
/// Claims Discovered jobs one at a time, reads each posting, applies the hunt rules and saves the result.
/// Drains the queue, then sleeps PollSeconds. Safe to run more than one instance: claims are atomic.
/// </summary>
public sealed class EnrichWorker(JobStore store, PostingReader reader, EnrichOptions options, ILogger<EnrichWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var rules = HuntRules.Load(store.DataDir);
        log.LogInformation("Enricher started. Data folder: {Dir}. Filters: {Source}. Tech terms: {Count}.",
            store.DataDir, rules.FromDefaults ? "built-in defaults (no \"## Filters\" section in hunt-config.md)" : "hunt-config.md",
            TechStack.Load(store.DataDir).Count);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.PollSeconds)));
        do
        {
            try
            {
                while (!ct.IsCancellationRequested && await ProcessOneAsync(ct)) { }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Enrich pass failed; will retry next tick");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    /// <returns>true if a job was processed (so the caller should look for another).</returns>
    public async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        var job = store.ClaimForEnrichment(TimeSpan.FromMinutes(Math.Max(1, options.StaleClaimMinutes)),
            TimeSpan.FromMinutes(Math.Max(0, options.RetryDelayMinutes)));
        if (job is null) return false;

        var url = job.ListingUrl ?? job.ApplyUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            store.FailEnrichment(job.Id, "no URL to read", EnrichFailure.Permanent, options.MaxAttempts);
            return true;
        }
        if (job.EnrichAttempts > Math.Max(1, options.MaxAttempts))
        {
            // Reclaimed after a crash more times than allowed: something about this posting kills the enricher.
            store.FailEnrichment(job.Id, $"gave up after {job.EnrichAttempts - 1} attempts", EnrichFailure.Permanent, options.MaxAttempts);
            return true;
        }

        log.LogInformation("Reading {Url} (attempt {Attempt})", url, job.EnrichAttempts);
        try
        {
            var read = await reader.ReadAsync(url, ct);
            var posting = read.Job;
            var jd = posting.JdText ?? "";

            // Rules and terms are re-read per job so edits to hunt-config.md or tech-terms.txt apply right away.
            var (verdict, reason) = HuntRules.Load(store.DataDir).Evaluate(posting.Title, jd);
            posting.TechStack = TechStack.Load(store.DataDir).Extract(posting.Title + "\n" + jd);
            if (verdict == EnrichVerdict.Keep)
            {
                posting.MatchedTitle = reason;
                reason = $"read via {read.How}";
            }

            var after = store.CompleteEnrichment(job.Id, posting, verdict, reason);
            log.LogInformation("{Status}: {Company} - {Title} ({How}){Why}", after.Status, after.Company, after.Title, read.How,
                after.ActionDesc is { } why ? $": {why}" : "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // shutdown mid-job: the claim goes stale and is reclaimed next start
        }
        catch (PipelineException ex)
        {
            // The job changed under us (e.g. you set its status on the dashboard while it was being read).
            log.LogWarning("{Url}: not saved: {Error}", url, ex.Message);
        }
        catch (EnrichException ex)
        {
            Fail(job, url, ex.Message, ex.Kind, ex: null);
        }
        catch (Exception ex)
        {
            // A bug or an unexpected response shape: retried like a network error, then EnrichFailed.
            Fail(job, url, $"unexpected error: {ex.Message}", EnrichFailure.Transient, ex);
        }
        return true;
    }

    void Fail(Job job, string url, string error, EnrichFailure kind, Exception? ex)
    {
        try
        {
            var after = store.FailEnrichment(job.Id, error, kind, options.MaxAttempts);
            if (ex is null) log.LogWarning("{Url}: {Error} → {Status}", url, error, after.Status);
            else log.LogError(ex, "{Url}: {Error} → {Status}", url, error, after.Status);
        }
        catch (PipelineException pe)
        {
            log.LogWarning("{Url}: {Error}; status not updated: {Why}", url, error, pe.Message);
        }
    }
}
