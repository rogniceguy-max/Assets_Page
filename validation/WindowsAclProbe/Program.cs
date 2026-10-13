using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using MuYingDrive.NativeWpf.Services;

// This is a service integration probe, not a WPF visual acceptance test.
var results = new List<object>();
var allMatched = true;
try
{
    var input = JsonSerializer.Deserialize<Input>(Console.In.ReadToEnd(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new ArgumentException();
    var root = input.ShareRoot.TrimEnd('\\');
    var parts = root.Split('\\', StringSplitOptions.RemoveEmptyEntries);
    if (!root.StartsWith("\\\\") || parts.Length != 2 || parts[1] != "MuYing_ACL_Test" ||
        !Guid.TryParseExact(input.RunId, "N", out _) || input.Identities.Length == 0)
        throw new ArgumentException();
    // Validate every identity/path before any network operation.
    foreach (var who in input.Identities)
    {
        if (!Regex.IsMatch(who.Label, "^[a-zA-Z0-9-]{1,40}$") || who.Spaces.Length != 5 ||
            string.IsNullOrWhiteSpace(who.Username) || string.IsNullOrEmpty(who.Password)) throw new ArgumentException();
        foreach (var space in who.Spaces)
        {
            if (string.IsNullOrWhiteSpace(space.RelativePath) || Path.IsPathRooted(space.RelativePath) ||
                space.RelativePath.Contains(':') || space.RelativePath.Any(char.IsControl) ||
                space.RelativePath.Split(['/', '\\']).Any(p => p is "" or "." or "..")) throw new ArgumentException();
        }
    }
    foreach (var who in input.Identities)
    {
        if (!Native.LogonUser(who.Username, who.Domain, who.Password, 9, 3, out var token))
        {
            results.Add(new { identity = who.Label, stage = "LogonUser", win32 = Marshal.GetLastWin32Error() });
            allMatched = false;
            continue;
        }
        using (token)
        WindowsIdentity.RunImpersonated(token, () =>
        {
            var target = "MuYingDrive.AclProbe." + Guid.NewGuid().ToString("N");
            var vault = new WindowsCredentialVault(new(target + ".session", target + ".nas"));
            try
            {
                // Constructor-only vault dependency: no credential store operations.
                var connection = new WindowsSmbConnectionService(new(root), vault)
                    .ConnectWithCredentials(who.Username, who.Password);
                results.Add(new { identity = who.Label, stage = "WPF-service-WNet-connect", succeeded = connection.Succeeded,
                    win32 = connection.NativeErrorCode, failure = connection.Failure.ToString() });
                if (!connection.Succeeded) { allMatched = false; return; }
                foreach (var space in who.Spaces)
                {
                    var directory = Path.GetFullPath(Path.Combine(root, space.RelativePath));
                    if (!directory.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException();
                    var prefix = "muying-acl-probe-" + input.RunId + "-" + who.Label;
                    string Fixture(string op) => Path.Combine(directory, prefix + "-" + op + ".txt");
                    void Check(string operation, bool expected, Action action)
                    {
                        try
                        {
                            // Refuse server-reported reparse points at every traversed directory.
                            var cursor = root;
                            foreach (var segment in space.RelativePath.Split(['/', '\\']))
                            {
                                cursor = Path.Combine(cursor, segment);
                                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                                    throw new InvalidOperationException("Reparse point refused");
                            }
                            action();
                            results.Add(new { identity = who.Label, space = space.Label, operation, expected,
                                succeeded = true, matched = expected, win32 = 0 });
                            if (!expected) allMatched = false;
                        }
                        catch (Exception error)
                        {
                            var code = error is Win32Exception w ? w.NativeErrorCode : error.HResult & 0xffff;
                            var denied = error is UnauthorizedAccessException || code == 5;
                            var matched = !expected && denied;
                            results.Add(new { identity = who.Label, space = space.Label, operation, expected,
                                succeeded = false, matched, win32 = code, exception = error.GetType().Name });
                            if (!matched) allMatched = false;
                        }
                    }
                    void Existing(string path)
                    {
                        // Exists suppresses access-denied errors; preserve the actual OS result.
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                            throw new InvalidOperationException();
                        var info = new FileInfo(path);
                        if (info.Length > 4096)
                            throw new InvalidOperationException();
                    }
                    Check("list", space.ExpectRead, () => { using var entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator(); _ = entries.MoveNext(); });
                    Check("read", space.ExpectRead, () => { var p = Fixture("read"); Existing(p); using var stream = File.OpenRead(p); _ = stream.ReadByte(); });
                    var created = false;
                    Check("create", space.ExpectWrite, () => { using var f = new FileStream(Fixture("create"), FileMode.CreateNew, FileAccess.Write); created = true; f.WriteByte(65); });
                    Check("overwrite", space.ExpectWrite, () => { var p = Fixture("overwrite"); Existing(p); using var f = new FileStream(p, FileMode.Open, FileAccess.Write); f.WriteByte(66); });
                    Check("rename", space.ExpectWrite, () => { var p = Fixture("rename"); Existing(p); File.Move(p, Fixture("renamed"), false); });
                    Check("delete", space.ExpectWrite, () => { var p = Fixture("delete"); Existing(p); File.Delete(p); });
                    // Leave all probe files for evidence and explicit later cleanup; no recursion.
                    results.Add(new { identity = who.Label, space = space.Label, stage = "probe-artifacts", created });
                }
            }
            catch (Exception error) { results.Add(Error(who.Label, "probe", error)); allMatched = false; }
            // No WNetCancelConnection; dispose only this isolated logon token.
        });
    }
}
catch (Exception error) { results.Add(Error("input", "validation-or-runtime", error)); allMatched = false; }
Console.WriteLine(JsonSerializer.Serialize(new { kind = "Windows linked WPF explicit-credential SMB service integration; credential vault and complete login persistence NOT verified; not UI acceptance", allMatched, results }, new JsonSerializerOptions { WriteIndented = true }));
return allMatched ? 0 : 1;

static object Error(string identity, string stage, Exception error) => new { identity, stage,
    exception = error.GetType().Name, win32 = error is Win32Exception w ? w.NativeErrorCode : error.HResult & 0xffff };
sealed record Input(string ShareRoot, string RunId, Identity[] Identities);
sealed record Identity(string Label, string Username, string Password, string? Domain, Space[] Spaces);
sealed record Space(string Label, string RelativePath, bool ExpectRead, bool ExpectWrite);
static class Native
{
    [DllImport("advapi32.dll", EntryPoint = "LogonUserW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool LogonUser(string username, string? domain, string password, int logonType, int provider, out SafeAccessTokenHandle token);
}
