using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace JobPipeline.Core;

/// <summary>A dashboard user, without the password hash.</summary>
public sealed record UserInfo(
    Guid Id, string Username, DateTimeOffset CreatedAt, DateTimeOffset PasswordChangedAt, DateTimeOffset? LastLoginAt);

// Dashboard sign-in accounts, in the `users` table of jobs.db (schema v3).
public sealed partial class JobStore
{
    public const int MinPasswordLength = 10;
    const int MaxPasswordLength = 256;
    static readonly Regex UsernamePattern = new(@"^[A-Za-z0-9._@-]{3,64}$", RegexOptions.Compiled);

    static void CreateUsersTable(SqliteConnection conn, SqliteTransaction tx)
    {
        Exec(conn, tx, """
            CREATE TABLE IF NOT EXISTS users (
                id                  TEXT PRIMARY KEY,
                username            TEXT NOT NULL UNIQUE COLLATE NOCASE,
                password_hash       TEXT NOT NULL,
                created_at          TEXT NOT NULL,
                password_changed_at TEXT NOT NULL,
                last_login_at       TEXT
            );
            UPDATE meta SET value = '3' WHERE key = 'schema_version' AND CAST(value AS INTEGER) < 3;
            """);
    }

    public UserInfo CreateUser(string username, string password)
    {
        username = CheckUsername(username);
        CheckPassword(password);
        var hash = PasswordHashing.Hash(password);   // slow on purpose: do it before taking the write lock
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        Require(FindUser(conn, tx, username) is null, $"A user named '{username}' already exists.");
        var now = Now.ToUniversalTime().ToString("O");
        var id = Guid.NewGuid();
        Run(conn, tx, "INSERT INTO users (id, username, password_hash, created_at, password_changed_at) " +
                      "VALUES ($id, $name, $hash, $now, $now)",
            ("$id", id.ToString()), ("$name", username), ("$hash", hash), ("$now", now));
        tx.Commit();
        return GetUser(username)!;
    }

    /// <summary>Sets a new password. Signs the user out everywhere (sessions carry the password-changed time).</summary>
    public UserInfo SetPassword(string username, string password)
    {
        CheckPassword(password);
        var hash = PasswordHashing.Hash(password);
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var user = FindUser(conn, tx, username) ?? throw new PipelineException($"No user named '{username}'.");
        Run(conn, tx, "UPDATE users SET password_hash = $hash, password_changed_at = $now WHERE id = $id",
            ("$hash", hash), ("$now", Now.ToUniversalTime().ToString("O")), ("$id", user.Info.Id.ToString()));
        tx.Commit();
        return GetUser(username)!;
    }

    public bool DeleteUser(string username)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        if (FindUser(conn, tx, username) is null) return false;
        Run(conn, tx, "DELETE FROM users WHERE username = $name", ("$name", username.Trim()));
        tx.Commit();
        return true;
    }

    public IReadOnlyList<UserInfo> ListUsers()
    {
        using var conn = Open();
        return QueryUsers(conn, null, "SELECT * FROM users ORDER BY username COLLATE NOCASE").Select(u => u.Info).ToList();
    }

    public UserInfo? GetUser(string username)
    {
        using var conn = Open();
        return FindUser(conn, null, username)?.Info;
    }

    /// <summary>
    /// Checks a sign-in. Returns the user on success, null otherwise. Takes about the same time whether or not the
    /// user exists, so response times don't reveal valid usernames.
    /// </summary>
    public UserInfo? VerifyLogin(string username, string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length > MaxPasswordLength) return null;
        using var conn = Open();
        var user = FindUser(conn, null, username ?? "");
        if (user is null)
        {
            PasswordHashing.Verify(password, PasswordHashing.DummyHash);
            return null;
        }
        var (ok, rehash) = PasswordHashing.Verify(password, user.Hash);
        if (!ok) return null;

        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        Run(conn, tx, "UPDATE users SET last_login_at = $now WHERE id = $id",
            ("$now", Now.ToUniversalTime().ToString("O")), ("$id", user.Info.Id.ToString()));
        if (rehash)   // stronger settings since this hash was made: upgrade it silently, without signing anyone out
            Run(conn, tx, "UPDATE users SET password_hash = $hash WHERE id = $id",
                ("$hash", PasswordHashing.Hash(password)), ("$id", user.Info.Id.ToString()));
        tx.Commit();
        return FindUser(conn, null, user.Info.Username)!.Info;
    }

    public int UserCount()
    {
        using var conn = Open();
        return (int)Scalar(conn, null, "SELECT COUNT(*) FROM users");
    }

    // ── helpers ──

    sealed record UserRow(UserInfo Info, string Hash);

    static UserRow? FindUser(SqliteConnection conn, SqliteTransaction? tx, string username) =>
        QueryUsers(conn, tx, "SELECT * FROM users WHERE username = $name", ("$name", username.Trim())).FirstOrDefault();

    static List<UserRow> QueryUsers(SqliteConnection conn, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql.Replace("SELECT *", "SELECT id, username, password_hash, created_at, password_changed_at, last_login_at");
        foreach (var (name, value) in args) Add(cmd, name, value);
        using var r = cmd.ExecuteReader();
        var rows = new List<UserRow>();
        while (r.Read())
            rows.Add(new UserRow(new UserInfo(
                Guid.Parse(r.GetString(0)), r.GetString(1),
                DateTimeOffset.Parse(r.GetString(3)), DateTimeOffset.Parse(r.GetString(4)),
                r.IsDBNull(5) ? null : DateTimeOffset.Parse(r.GetString(5))), r.GetString(2)));
        return rows;
    }

    static void Run(SqliteConnection conn, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) Add(cmd, name, value);
        cmd.ExecuteNonQuery();
    }

    static string CheckUsername(string username)
    {
        username = (username ?? "").Trim();
        Require(UsernamePattern.IsMatch(username),
            "Usernames are 3 to 64 characters: letters, numbers, and . _ @ - only.");
        return username;
    }

    static void CheckPassword(string password)
    {
        Require(password is { Length: >= MinPasswordLength },
            $"Passwords need at least {MinPasswordLength} characters. A few random words work well.");
        Require(password.Length <= MaxPasswordLength, $"Passwords can be at most {MaxPasswordLength} characters.");
        Require(password.Trim().Length > 0, "A password can't be only spaces.");
    }
}

/// <summary>
/// PBKDF2-HMAC-SHA256 with a random 16-byte salt, stored as <c>pbkdf2-sha256$iterations$salt$hash</c> (base64).
/// The iteration count follows OWASP's current advice; older hashes are upgraded at the next sign-in.
/// </summary>
public static class PasswordHashing
{
    public const int Iterations = 600_000;
    const int SaltBytes = 16, HashBytes = 32;
    const string Scheme = "pbkdf2-sha256";

    internal static readonly string DummyHash = Hash("not a real password, only used to even out timing");

    public static string Hash(string password, int iterations = Iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Scheme}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <returns>Whether it matches, and whether the stored hash should be redone with current settings.</returns>
    public static (bool Ok, bool NeedsRehash) Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out var iterations) || iterations < 1)
            return (false, false);
        byte[] salt, expected;
        try { salt = Convert.FromBase64String(parts[2]); expected = Convert.FromBase64String(parts[3]); }
        catch (FormatException) { return (false, false); }
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        var ok = CryptographicOperations.FixedTimeEquals(actual, expected);
        return (ok, ok && iterations < Iterations);
    }
}
