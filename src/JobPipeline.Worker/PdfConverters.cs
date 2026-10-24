using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace JobPipeline.Worker;

/// <summary>Turns a .docx into a .pdf. Chosen with Pipeline:PdfConverter ("Word" or "LibreOffice").</summary>
public interface IPdfConverter
{
    Task ConvertAsync(string docxPath, string pdfPath, CancellationToken ct);
}

/// <summary>
/// Converts with Microsoft Word. Word starts hidden (no window) for each conversion and quits right after.
/// Office automation only works reliably in a logged-in session: run the worker from a console or a scheduled
/// task set to "Run only when user is logged on", not as a Windows service. Open Word by hand once first so
/// activation or first-run prompts can't block it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WordPdfConverter(PipelineOptions options) : IPdfConverter
{
    const int WdExportFormatPdf = 17;
    const int WdDoNotSaveChanges = 0;

    public Task ConvertAsync(string docxPath, string pdfPath, CancellationToken ct)
    {
        var docx = Path.GetFullPath(docxPath);
        var pdf = Path.GetFullPath(pdfPath);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Word's COM objects expect a single-threaded apartment.
        var thread = new Thread(() =>
        {
            try { Convert(docx, pdf); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true, Name = "word-pdf" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return done.Task.WaitAsync(TimeSpan.FromSeconds(options.ConversionTimeoutSeconds), ct)
            .ContinueWith(t =>
            {
                if (t.Exception?.InnerException is TimeoutException)
                    throw new TimeoutException(
                        $"Word did not finish converting within {options.ConversionTimeoutSeconds}s. A dialog may be " +
                        "waiting in a hidden Word window: end WINWORD.EXE in Task Manager, open Word once by hand to clear it.");
                t.GetAwaiter().GetResult();
            }, TaskScheduler.Default);
    }

    static void Convert(string docx, string pdf)
    {
        var type = Type.GetTypeFromProgID("Word.Application")
                   ?? throw new InvalidOperationException(
                       "Microsoft Word is not installed. Install it or set Pipeline:PdfConverter to \"LibreOffice\".");
        dynamic? word = null, doc = null;
        try
        {
            word = Activator.CreateInstance(type)!;
            word.Visible = false;
            word.DisplayAlerts = 0;
            doc = word.Documents.Open(docx, false, true, false);          // FileName, ConfirmConversions, ReadOnly, AddToRecentFiles
            doc.ExportAsFixedFormat(pdf, WdExportFormatPdf);              // OutputFileName, ExportFormat
        }
        finally
        {
            if (doc is not null) { try { doc.Close(WdDoNotSaveChanges); } catch { } Marshal.FinalReleaseComObject(doc); }
            if (word is not null) { try { word.Quit(WdDoNotSaveChanges); } catch { } Marshal.FinalReleaseComObject(word); }
        }
    }
}

/// <summary>
/// Converts with LibreOffice in headless mode (no window, works without a logged-in user, free).
/// Pipeline:LibreOfficePath defaults to C:\Program Files\LibreOffice\program\soffice.exe on Windows, soffice elsewhere.
/// </summary>
public sealed class LibreOfficePdfConverter(PipelineOptions options) : IPdfConverter
{
    public async Task ConvertAsync(string docxPath, string pdfPath, CancellationToken ct)
    {
        var exe = string.IsNullOrWhiteSpace(options.LibreOfficePath) ? DefaultPath() : options.LibreOfficePath;
        if (Path.IsPathRooted(exe) && !File.Exists(exe))
            throw new FileNotFoundException(
                $"LibreOffice not found at '{exe}'. Install it or set Pipeline:LibreOfficePath, or use \"Word\".");

        // Own output folder and own profile: no clash with files of the same name or a LibreOffice the user has open.
        var work = Directory.CreateTempSubdirectory("jobpipeline-lo-").FullName;
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add($"-env:UserInstallation={new Uri(Path.Combine(work, "profile")).AbsoluteUri}");
            foreach (var a in new[] { "--headless", "--norestore", "--nologo", "--convert-to", "pdf", "--outdir", work })
                psi.ArgumentList.Add(a);
            psi.ArgumentList.Add(Path.GetFullPath(docxPath));

            Process process;
            try { process = Process.Start(psi) ?? throw new InvalidOperationException("LibreOffice did not start."); }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new FileNotFoundException($"Could not start LibreOffice ('{exe}'): {ex.Message}");
            }

            using (process)
            {
                var stderr = process.StandardError.ReadToEndAsync(ct);
                _ = process.StandardOutput.ReadToEndAsync(ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.ConversionTimeoutSeconds));
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    if (ct.IsCancellationRequested) throw;
                    throw new TimeoutException($"LibreOffice did not finish within {options.ConversionTimeoutSeconds}s.");
                }

                var produced = Path.Combine(work, Path.GetFileNameWithoutExtension(docxPath) + ".pdf");
                if (process.ExitCode != 0 || !File.Exists(produced))
                    throw new InvalidOperationException(
                        $"LibreOffice could not convert the resume (exit {process.ExitCode}): {(await stderr).Trim()}");
                File.Move(produced, pdfPath, overwrite: true);
            }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    static string DefaultPath() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe")
        : "soffice";
}
