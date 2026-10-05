using System.Security.Cryptography;
using System.Text.Json;

namespace MraReporting.Auth;

/// <summary>An account for the app's own sign-in. The password is stored only as a salted hash.</summary>
public sealed record LocalUser(string UserName, string DisplayName, string Role, string PasswordHash, DateTime CreatedAt, bool Disabled = false);

/// <summary>
/// The app's own accounts, kept in App_Data/users.json next to the app. Used while the app is being
/// tried out; when MRA directory sign-in is switched on (Auth:Mode = "Directory" or "Windows"), this
/// file is no longer used. Passwords are hashed with PBKDF2-SHA256 (210,000 rounds, random salt)
/// and are never written or logged in plain text.
/// </summary>
public sealed class LocalUserStore
{
    public const int MinPasswordLength = 8;
    private const int Iterations = 210_000;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // Used when the user name does not exist, so a wrong name takes as long to reject as a wrong password.
    private static readonly string DummyHash = Hash("not-a-real-password-" + Guid.NewGuid());

    private readonly string _path;
    private readonly object _lock = new();

    public LocalUserStore(string contentRoot)
    {
        _path = Path.Combine(contentRoot, "App_Data", "users.json");
    }

    public string FilePath => _path;

    public IReadOnlyList<LocalUser> All()
    {
        lock (_lock)
        {
            if (!File.Exists(_path)) return [];
            var text = File.ReadAllText(_path);
            return string.IsNullOrWhiteSpace(text) ? [] : JsonSerializer.Deserialize<List<LocalUser>>(text, Json) ?? [];
        }
    }

    public int Count => All().Count;

    public LocalUser? Find(string userName) =>
        All().FirstOrDefault(u => u.UserName.Equals(userName.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The account when the name and password match and the account is enabled; otherwise null.</summary>
    public LocalUser? Verify(string userName, string password)
    {
        var user = Find(userName ?? "");
        var ok = Check(password ?? "", user?.PasswordHash ?? DummyHash);
        return ok && user is { Disabled: false } ? user : null;
    }

    public void Save(LocalUser user)
    {
        lock (_lock)
        {
            var users = All().Where(u => !u.UserName.Equals(user.UserName, StringComparison.OrdinalIgnoreCase)).ToList();
            users.Add(user);
            Write(users);
        }
    }

    public bool Remove(string userName)
    {
        lock (_lock)
        {
            var users = All().ToList();
            var removed = users.RemoveAll(u => u.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) Write(users);
            return removed;
        }
    }

    private void Write(List<LocalUser> users)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(users.OrderBy(u => u.UserName).ToList(), Json));
        File.Move(temp, _path, overwrite: true);
    }

    // ---------------------------------------------------------------- password hashing

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"PBKDF2-SHA256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool Check(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "PBKDF2-SHA256" || !int.TryParse(parts[1], out var iterations)) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>
/// Slows down password guessing: after 5 wrong attempts for one user name within 15 minutes,
/// that name is locked for 10 minutes. Kept in memory, so a restart clears it.
/// </summary>
public sealed class SignInGuard
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LockFor = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, List<DateTime>> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lockedUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>Minutes left on a lock for this name, or 0 when it may try.</summary>
    public int MinutesLocked(string userName)
    {
        lock (_lock)
        {
            if (_lockedUntil.TryGetValue(userName, out var until) && until > DateTime.UtcNow)
                return (int)Math.Ceiling((until - DateTime.UtcNow).TotalMinutes);
            return 0;
        }
    }

    public void Failed(string userName)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (!_failures.TryGetValue(userName, out var list)) _failures[userName] = list = [];
            list.RemoveAll(t => now - t > Window);
            list.Add(now);
            if (list.Count >= MaxFailures)
            {
                _lockedUntil[userName] = now + LockFor;
                list.Clear();
            }
        }
    }

    public void Succeeded(string userName)
    {
        lock (_lock)
        {
            _failures.Remove(userName);
            _lockedUntil.Remove(userName);
        }
    }
}
