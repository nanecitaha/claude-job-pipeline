using System.Text;
using JobPipeline.Core;

/// <summary>
/// <c>JobPipeline.Dashboard.exe user add|passwd|remove|list [name]</c>: manage who can sign in. Passwords are typed at
/// a hidden prompt, or read from standard input when it's redirected (for scripts:  <c>echo pw | ... user add bob</c>).
/// </summary>
static class UserCommands
{
    const string Usage = """
        Manage who can sign in to the dashboard:
          JobPipeline.Dashboard.exe user add <name>       create a user (you'll be asked for the password)
          JobPipeline.Dashboard.exe user passwd <name>    change a password (signs that user out everywhere)
          JobPipeline.Dashboard.exe user remove <name>    delete a user
          JobPipeline.Dashboard.exe user list             show users and their last sign-in
        Add --Dashboard:DataDir=<folder> if your data isn't in the configured folder.
        """;

    public static int Run(JobStore store, string[] words)
    {
        try
        {
            switch (words)
            {
                case ["user", "add", var name]:
                    var created = store.CreateUser(name, ReadNewPassword());
                    Console.WriteLine($"Created user '{created.Username}' in {store.DatabasePath}.");
                    return 0;
                case ["user", "passwd", var name]:
                    if (store.GetUser(name) is null) throw new PipelineException($"No user named '{name}'.");
                    store.SetPassword(name, ReadNewPassword());
                    Console.WriteLine($"Password changed for '{name}'. Their open sessions are signed out.");
                    return 0;
                case ["user", "remove", var name]:
                    if (!store.DeleteUser(name)) throw new PipelineException($"No user named '{name}'.");
                    Console.WriteLine($"Removed user '{name}'. Their open sessions are signed out.");
                    return 0;
                case ["user", "list"]:
                    var users = store.ListUsers();
                    if (users.Count == 0) Console.WriteLine("No users yet. Create one: JobPipeline.Dashboard.exe user add <name>");
                    foreach (var u in users)
                        Console.WriteLine($"{u.Username,-24} last sign-in: {(u.LastLoginAt is { } t ? t.ToLocalTime().ToString("g") : "never")}");
                    return 0;
                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (PipelineException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    static string ReadNewPassword()
    {
        if (Console.IsInputRedirected)
            return Console.In.ReadLine() ?? "";
        Console.WriteLine($"At least {JobStore.MinPasswordLength} characters. A few random words work well.");
        var first = ReadHidden("Password: ");
        var second = ReadHidden("Type it again: ");
        if (first != second) throw new PipelineException("The two passwords don't match. Nothing was changed.");
        return first;
    }

    static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
