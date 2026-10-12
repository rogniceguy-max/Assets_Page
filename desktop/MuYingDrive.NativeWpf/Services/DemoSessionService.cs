using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MuYingDrive.NativeWpf.Models;

namespace MuYingDrive.NativeWpf.Services;

/// <summary>
/// Local-only demo identity store. It never calls NAS, SMB, or a server.
/// Passwords are represented by a salted PBKDF2 hash and are never written in clear text.
/// </summary>
public sealed class DemoSessionService
{
    private const int Pbkdf2Iterations = 210_000;
    private readonly string _storePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MuYingDrive.NativeWpf",
        "demo-accounts.json");
    private readonly DemoStore _store;

    public DemoSessionService()
    {
        _store = LoadStore();
    }

    public DemoAccount? RestoreSession()
    {
        if (string.IsNullOrWhiteSpace(_store.ActiveUserName))
        {
            return null;
        }

        var account = _store.Accounts.FirstOrDefault(item =>
            string.Equals(item.UserName, _store.ActiveUserName, StringComparison.OrdinalIgnoreCase));
        return account is null ? null : new DemoAccount(account.UserName, account.Department);
    }

    public bool TryRegister(string rawUserName, string password, string department, out DemoAccount? account, out string error)
    {
        account = null;
        error = string.Empty;

        if (!TryNormalizeUserName(rawUserName, out var userName, out error))
        {
            return false;
        }

        if (string.IsNullOrEmpty(password) || password.Length < 8)
        {
            error = "密码至少需要 8 位。";
            return false;
        }

        if (_store.Accounts.Any(item => string.Equals(item.UserName, userName, StringComparison.OrdinalIgnoreCase)))
        {
            error = "这个用户名已经注册，请直接登录。";
            return false;
        }

        if (department is not ("影视资源库" or "文学创作库"))
        {
            error = "请选择所属部门。";
            return false;
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        var saved = new SavedAccount
        {
            UserName = userName,
            Department = department,
            Salt = Convert.ToBase64String(salt),
            PasswordHash = Convert.ToBase64String(hash),
            Iterations = Pbkdf2Iterations
        };

        _store.Accounts.Add(saved);
        _store.ActiveUserName = userName;
        SaveStore();
        account = new DemoAccount(userName, department);
        return true;
    }

    public bool TrySignIn(string rawUserName, string password, out DemoAccount? account, out string error)
    {
        account = null;
        error = string.Empty;

        if (!TryNormalizeUserName(rawUserName, out var userName, out error))
        {
            return false;
        }

        var saved = _store.Accounts.FirstOrDefault(item =>
            string.Equals(item.UserName, userName, StringComparison.OrdinalIgnoreCase));
        if (saved is null || !VerifyPassword(password, saved))
        {
            error = "用户名或密码不正确。";
            return false;
        }

        _store.ActiveUserName = saved.UserName;
        SaveStore();
        account = new DemoAccount(saved.UserName, saved.Department);
        return true;
    }

    public void SignOut()
    {
        _store.ActiveUserName = null;
        SaveStore();
    }

    public static bool TryNormalizeUserName(string rawUserName, out string userName, out string error)
    {
        userName = (rawUserName ?? string.Empty).Trim();
        error = string.Empty;

        if (userName.Length == 0)
        {
            error = "请输入用户名。";
            return false;
        }

        if (userName.Any(character => char.IsControl(character) || character is '/' or '\\'))
        {
            error = "用户名不能包含控制字符或路径分隔符。";
            return false;
        }

        return true;
    }

    private static bool VerifyPassword(string password, SavedAccount saved)
    {
        try
        {
            var salt = Convert.FromBase64String(saved.Salt);
            var expected = Convert.FromBase64String(saved.PasswordHash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, saved.Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private DemoStore LoadStore()
    {
        try
        {
            if (File.Exists(_storePath))
            {
                return JsonSerializer.Deserialize<DemoStore>(File.ReadAllText(_storePath)) ?? new DemoStore();
            }
        }
        catch (IOException)
        {
            // The local demo starts fresh if its disposable sample store is unavailable.
        }
        catch (JsonException)
        {
            // A damaged demo store must not block opening the native UI.
        }

        return new DemoStore();
    }

    private void SaveStore()
    {
        var directory = Path.GetDirectoryName(_storePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _storePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_store, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        File.Move(temporaryPath, _storePath, overwrite: true);
    }

    private sealed class DemoStore
    {
        public List<SavedAccount> Accounts { get; set; } = [];
        public string? ActiveUserName { get; set; }
    }

    private sealed class SavedAccount
    {
        public string UserName { get; set; } = string.Empty;
        public string Department { get; set; } = string.Empty;
        public string Salt { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public int Iterations { get; set; }
    }
}
