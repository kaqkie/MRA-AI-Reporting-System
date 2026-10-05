namespace MraReporting.Auth;

/// <summary>
/// Manages the app's own accounts from the command line, run in the project folder:
///   dotnet run -- add-user albert --name "Albert Zakulanda" --role Admin
///   dotnet run -- reset-password albert
///   dotnet run -- disable-user albert      /  enable-user albert
///   dotnet run -- remove-user albert
///   dotnet run -- list-users
/// Passwords are typed at a hidden prompt, so they never appear on screen or in the command history.
/// </summary>
public static class UserAdmin
{
    public static readonly string[] Commands = ["add-user", "reset-password", "disable-user", "enable-user", "remove-user", "list-users"];

    public static bool IsCommand(string[] args) => args.Length > 0 && Commands.Contains(args[0], StringComparer.OrdinalIgnoreCase);

    public static int Run(string[] args, string contentRoot)
    {
        var store = new LocalUserStore(contentRoot);
        var command = args[0].ToLowerInvariant();
        var name = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1].Trim() : "";

        try
        {
            switch (command)
            {
                case "list-users":
                    var users = store.All();
                    if (users.Count == 0) { Console.WriteLine("No accounts yet. Add one with: dotnet run -- add-user yourname"); return 0; }
                    Console.WriteLine($"{"User name",-20} {"Name",-30} {"Role",-8} {"Status",-9} Created");
                    foreach (var u in users)
                        Console.WriteLine($"{u.UserName,-20} {u.DisplayName,-30} {u.Role,-8} {(u.Disabled ? "disabled" : "active"),-9} {u.CreatedAt:yyyy-MM-dd}");
                    return 0;

                case "add-user":
                {
                    if (!ValidName(name)) return Fail("Give a user name of 3 to 30 letters, digits, dots or dashes, e.g. dotnet run -- add-user albert");
                    if (store.Find(name) is not null) return Fail($"'{name}' already exists. Use reset-password to change the password.");
                    var display = Option(args, "--name") ?? name;
                    var role = NormaliseRole(Option(args, "--role") ?? (store.Count == 0 ? "Admin" : "User"));
                    var password = AskNewPassword();
                    if (password is null) return 1;
                    store.Save(new LocalUser(name, display, role, LocalUserStore.Hash(password), DateTime.UtcNow));
                    Console.WriteLine($"Account '{name}' created ({role}). Sign in at http://localhost:5080/login");
                    return 0;
                }

                case "reset-password":
                {
                    var user = store.Find(name);
                    if (user is null) return Fail($"No account called '{name}'. See: dotnet run -- list-users");
                    var password = AskNewPassword();
                    if (password is null) return 1;
                    store.Save(user with { PasswordHash = LocalUserStore.Hash(password) });
                    Console.WriteLine($"Password changed for '{user.UserName}'.");
                    return 0;
                }

                case "disable-user":
                case "enable-user":
                {
                    var user = store.Find(name);
                    if (user is null) return Fail($"No account called '{name}'. See: dotnet run -- list-users");
                    store.Save(user with { Disabled = command == "disable-user" });
                    Console.WriteLine($"Account '{user.UserName}' {(command == "disable-user" ? "disabled" : "enabled")}.");
                    return 0;
                }

                case "remove-user":
                    if (!store.Remove(name)) return Fail($"No account called '{name}'. See: dotnet run -- list-users");
                    Console.WriteLine($"Account '{name}' removed.");
                    return 0;
            }
        }
        catch (IOException ex)
        {
            return Fail($"Could not update {store.FilePath}: {ex.Message}");
        }
        return Fail("Unknown command.");
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static bool ValidName(string name) =>
        name.Length is >= 3 and <= 30 && name.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_');

    private static string NormaliseRole(string role) =>
        role.Equals("admin", StringComparison.OrdinalIgnoreCase) ? "Admin" : "User";

    private static string? Option(string[] args, string option)
    {
        var i = Array.FindIndex(args, a => a.Equals(option, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string? AskNewPassword()
    {
        var first = ReadHidden($"New password (at least {LocalUserStore.MinPasswordLength} characters): ");
        if (first.Length < LocalUserStore.MinPasswordLength)
        {
            Console.Error.WriteLine($"The password must be at least {LocalUserStore.MinPasswordLength} characters.");
            return null;
        }
        var second = ReadHidden("Type it again: ");
        if (first != second)
        {
            Console.Error.WriteLine("The two passwords do not match. Nothing was changed.");
            return null;
        }
        return first;
    }

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0) { chars.RemoveAt(chars.Count - 1); Console.Write("\b \b"); }
                continue;
            }
            if (!char.IsControl(key.KeyChar)) { chars.Add(key.KeyChar); Console.Write('*'); }
        }
        Console.WriteLine();
        return new string(chars.ToArray());
    }
}
