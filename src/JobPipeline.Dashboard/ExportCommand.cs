using System.Globalization;
using System.Text;
using JobPipeline.Core;

/// <summary>
/// <c>JobPipeline.Dashboard.exe export-training &lt;file.jsonl&gt; [min-match]</c>: writes the jobs Claude has extracted as
/// training data for the tech stack model (one JSON object per line) and prints how usable the labels are.
/// </summary>
static class ExportCommand
{
    public static int Run(JobStore store, string[] words)
    {
        if (words.Length is < 2 or > 3)
        {
            Console.Error.WriteLine("""
                Export training data for the tech stack model:
                  JobPipeline.Dashboard.exe export-training <file.jsonl> [min-match]
                min-match (default 0.8): leave out jobs where fewer than this share of the tech stack terms
                appear word for word in the job description.
                """);
            return 2;
        }
        var minMatch = 0.8;
        if (words.Length == 3 && (!double.TryParse(words[2], NumberStyles.Float, CultureInfo.InvariantCulture, out minMatch)
                                  || minMatch is < 0 or > 1))
        {
            Console.Error.WriteLine("min-match is a number from 0 to 1, e.g. 0.8");
            return 2;
        }

        var path = Path.GetFullPath(words[1]);
        ExportReport r;
        using (var file = new StreamWriter(path, append: false, new UTF8Encoding(false)) { NewLine = "\n" })
            r = TrainingExport.Write(store, file, minMatch);

        Console.WriteLine($"""
            Wrote {r.Exported} jobs to {path}
              {r.Jobs} jobs in the database
              {r.NoDescription} left out: no job description
              {r.NoTechStack} left out: no tech stack
              {r.LowMatch} left out: under {minMatch:P0} of their terms appear word for word
              {r.MatchRate:P1} of all tech stack terms appear word for word in their job description
            """);
        if (r.MostMissed.Count > 0)
            Console.WriteLine("Terms most often not found word for word (reworded, or not in the saved description):\n  " +
                              string.Join(", ", r.MostMissed.Select(m => $"{m.Term} ({m.Count})")));
        if (r.Exported < 200)
            Console.WriteLine($"Note: {r.Exported} jobs is on the low side; 300+ gives a much better model.");
        return 0;
    }
}
