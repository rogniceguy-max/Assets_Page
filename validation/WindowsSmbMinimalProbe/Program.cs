using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using MuYingDrive.NativeWpf.Services;

// Single identity/process, no credential persistence or connection cancellation.
// This is a service diagnostic, not WPF UI acceptance or a complete ACL test.
var clock = Stopwatch.StartNew();
var events = new List<object>();
var matched = true;
string? mode = null;
string? runId = null;
string? identityLabel = null;
void Emit(string stage, string phase, string? relativePath = null, Exception? error = null,
    bool? expectedDenied = null)
{
    events.Add(new { utc = DateTimeOffset.UtcNow.ToString("O"), monotonicMs = clock.Elapsed.TotalMilliseconds,
        stage, phase, relativePath, expectedDenied, identityLabel,
        exception = error?.GetType().Name, hresult = error is null ? (int?)null : error.HResult,
        win32 = error is null ? (int?)null : error is Win32Exception w ? w.NativeErrorCode : error.HResult & 0xffff });
}
bool Step(string stage, string? relativePath, Action action, bool expectDenied = false)
{
    Emit(stage, "begin", relativePath);
    try
    {
        action();
        Emit(stage, "success", relativePath, expectedDenied: expectDenied);
        if (expectDenied) matched = false;
        return true;
    }
    catch (Exception error)
    {
        Emit(stage, "error", relativePath, error, expectDenied);
        if (!(expectDenied && (error is UnauthorizedAccessException ||
            (error is Win32Exception w ? w.NativeErrorCode : error.HResult & 0xffff) == 5))) matched = false;
        return false;
    }
}
try
{
    var input = JsonSerializer.Deserialize<Input>(Console.In.ReadToEnd(),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new ArgumentException();
    var root = input.ShareRoot?.TrimEnd('\\') ?? "";
    var parts = root.Split('\\', StringSplitOptions.RemoveEmptyEntries);
    if (!root.StartsWith("\\\\", StringComparison.Ordinal) || parts.Length != 2 ||
        root != "\\\\" + parts[0] + "\\MuYing_ACL_Test" ||
        parts[0] is "." or ".." || parts[0].Any(c => char.IsControl(c) || c is '/' or ':' or ' ' or '?') ||
        !Guid.TryParseExact(input.RunId, "N", out _) || input.Mode is not ("readonly" or "create" or "overwrite" or "create-rename" or "create-delete" or "attributes" or "switch-write" or "switch-denied-read" or "burst-write" or "write-denied-read" or "write-own-denied")) throw new ArgumentException();
    if (input.Mode is not ("switch-write" or "switch-denied-read" or "burst-write" or "write-denied-read" or "write-own-denied") && (string.IsNullOrWhiteSpace(input.Username) || string.IsNullOrEmpty(input.Password)))
        throw new ArgumentException();
    mode = input.Mode;
    runId = input.RunId;
    Emit("validated", "success");
    if (mode is "switch-write" or "switch-denied-read" or "burst-write" or "write-denied-read" or "write-own-denied")
    {
        var identities = input.Identities;
        var identityCount = mode == "write-own-denied" ? 1 : mode == "burst-write" ? 2 : 3;
        var writes = mode is "switch-write" or "burst-write" or "write-own-denied";
        if (identities is null || identities.Length != identityCount ||
            identities[0].Label != "film" || identities[0].RelativeDirectory != "film" || identities[0].Write != (writes || mode == "write-denied-read") ||
            (identityCount >= 2 && (identities[1].Label != "writing" || identities[1].RelativeDirectory != "writing" || identities[1].Write != writes)) ||
            (identityCount == 3 && (identities[2].Label != "visitor" || identities[2].RelativeDirectory != "public" || identities[2].Write)) ||
            identities.Any(i => string.IsNullOrWhiteSpace(i.Username) || string.IsNullOrEmpty(i.Password)) ||
            identities.Select(i => i.Username).Distinct(StringComparer.Ordinal).Count() != identityCount) throw new ArgumentException();
        var sessions = new List<(SwitchIdentity Identity, SafeAccessTokenHandle Token)>();
        try
        {
            foreach (var who in identities)
            {
                identityLabel = who.Label;
                Emit("identity-switch", "begin");
                SafeAccessTokenHandle? isolatedToken = null;
                if (!Step("LogonUser", null, () =>
                {
                    if (!Native.LogonUser(who.Username, null, who.Password, 9, 3, out isolatedToken))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                })) { isolatedToken?.Dispose(); continue; }
                // Retain each logon token to verify the same session after the observation period.
                sessions.Add((who, isolatedToken!));
                WindowsIdentity.RunImpersonated(isolatedToken!, () =>
                {
                    var target = "MuYingDrive.MinimalProbe." + Guid.NewGuid().ToString("N");
                    var vault = new WindowsCredentialVault(new(target + ".session", target + ".nas"));
                    if (!Step("WNet-connect", null, () =>
                    {
                        var result = new WindowsSmbConnectionService(new(root), vault).ConnectWithCredentials(who.Username, who.Password);
                        if (!result.Succeeded) throw new Win32Exception(checked((int)result.NativeErrorCode));
                    })) return;
                    if (mode == "burst-write")
                    {
                        for (var index = 0; index < 6; index++)
                        {
                            var burstRelative = who.RelativeDirectory + "/probe-" + runId + "-" + who.Label + "-" + index + ".txt";
                            if (!Step("create-write-close", burstRelative, () =>
                            {
                                using var file = new FileStream(Path.Combine(root, burstRelative.Replace('/', '\\')), FileMode.CreateNew, FileAccess.Write);
                                file.WriteByte(65);
                                Emit("write-byte", "success", burstRelative);
                            })) break;
                        }
                        return;
                    }
                    var relative = who.RelativeDirectory + (who.Write ? "/probe-" + runId + "-" + who.Label + ".txt" : "/existing.txt");
                    if (mode != "write-denied-read" || who.Label == "film")
                        Step(who.Write ? "create-write-close" : "read-allowed", relative, () =>
                    {
                        var full = Path.Combine(root, relative.Replace('/', '\\'));
                        if (who.Write)
                        {
                            using var file = new FileStream(full, FileMode.CreateNew, FileAccess.Write);
                            file.WriteByte(65);
                            Emit("write-byte", "success", relative);
                        }
                        else
                        {
                            using var file = File.OpenRead(full);
                            _ = file.ReadByte();
                        }
                    });
                    if (mode is "switch-denied-read" or "write-own-denied" || (mode == "write-denied-read" && who.Label != "film"))
                    {
                        var deniedPaths = who.Label switch
                        {
                            "film" => new[] { "personal_writing/existing.txt" },
                            "writing" => new[] { "personal_film/existing.txt" },
                            _ => new[] { "personal_film/existing.txt", "personal_writing/existing.txt" }
                        };
                        foreach (var deniedPath in deniedPaths)
                            Step("direct-read-denied", deniedPath, () =>
                            {
                                using var file = File.OpenRead(Path.Combine(root, deniedPath.Replace('/', '\\')));
                                _ = file.ReadByte();
                            }, true);
                    }
                });
                Emit("identity-switch", "end");
            }
            identityLabel = null;
            Step("observe-12s", null, () => Thread.Sleep(TimeSpan.FromSeconds(12)));
            foreach (var session in sessions)
            {
                identityLabel = session.Identity.Label;
                Emit("identity-resume", "begin");
                WindowsIdentity.RunImpersonated(session.Token, () =>
                {
                    var relative = session.Identity.RelativeDirectory + "/existing.txt";
                    Step("connection-alive-read", relative, () =>
                    {
                        using var file = File.OpenRead(Path.Combine(root, relative.Replace('/', '\\')));
                        _ = file.ReadByte();
                    });
                });
                Emit("identity-resume", "end");
            }
        }
        finally
        {
            foreach (var session in sessions) session.Token.Dispose();
            identityLabel = null;
        }
    }
    else
    {
    Emit("LogonUser", "begin");
    if (!Native.LogonUser(input.Username!, null, input.Password!, 9, 3, out var token))
        throw new Win32Exception(Marshal.GetLastWin32Error());
    Emit("LogonUser", "success");
    using (token)
    WindowsIdentity.RunImpersonated(token, () =>
    {
        var target = "MuYingDrive.MinimalProbe." + Guid.NewGuid().ToString("N");
        var vault = new WindowsCredentialVault(new(target + ".session", target + ".nas"));
        if (!Step("WNet-connect", null, () =>
        {
            var result = new WindowsSmbConnectionService(new(root), vault)
                .ConnectWithCredentials(input.Username!, input.Password!);
            if (!result.Succeeded) throw new Win32Exception(checked((int)result.NativeErrorCode));
        })) return;
        string Full(string relative) => Path.Combine(root, relative.Replace('/', '\\'));
        void ReadSmall(string relative)
        {
            // Open directly so permission failures belong to the operation, not metadata prechecks.
            using var f = File.OpenRead(Full(relative));
            if (f.Length > 4096) throw new InvalidDataException();
            var buffer = new byte[4097];
            if (f.ReadAtLeast(buffer, 4097, throwOnEndOfStream: false) > 4096) throw new InvalidDataException();
        }
        if (mode == "attributes")
        {
            // Independent requests: retain all outcomes even if an earlier metadata request fails.
            Step("directory-attributes", "personal_writing", () =>
            {
                _ = File.GetAttributes(Full("personal_writing"));
            }, true);
            Step("file-attributes", "personal_writing/existing.txt", () =>
            {
                _ = File.GetAttributes(Full("personal_writing/existing.txt"));
            }, true);
            Step("direct-open-read", "personal_writing/existing.txt", () =>
            {
                using var f = File.OpenRead(Full("personal_writing/existing.txt"));
                _ = f.ReadByte();
            }, true);
        }
        else if (mode == "readonly")
        {
            Step("list", "film", () =>
            {
                using var enumerator = Directory.EnumerateFileSystemEntries(Full("film")).GetEnumerator();
                while (enumerator.MoveNext()) { } // Do not output file names or content.
            });
            Step("read-allowed", "film/existing.txt", () => ReadSmall("film/existing.txt"));
            Step("read-denied", "personal_writing/existing.txt", () => ReadSmall("personal_writing/existing.txt"), true);
        }
        else if (mode is "create-rename" or "create-delete")
        {
            var relative = "film/probe-" + runId + ".txt";
            var renamed = "film/probe-" + runId + "-renamed.txt";
            if (Step("create-write-close", relative, () =>
            {
                using var f = new FileStream(Full(relative), FileMode.CreateNew, FileAccess.Write);
                f.WriteByte(65);
                Emit("write-byte", "success", relative);
            }))
            {
                if (mode == "create-delete")
                    Step("delete", relative, () => File.Delete(Full(relative)));
                else
                    Step("rename", renamed, () => File.Move(Full(relative), Full(renamed), false));
            }
        }
        else
        {
            var relative = "film/probe-" + runId + ".txt";
            // No rename, delete, fixture preparation, timestamp writes, or explicit flush.
            Step(mode!, relative, () =>
            {
                using var f = new FileStream(Full(relative), mode == "create" ? FileMode.CreateNew : FileMode.Open, FileAccess.Write);
                if (f.Length > 4096) throw new InvalidDataException();
                f.WriteByte(65);
                Emit("write-byte", "success", relative);
            });
        }
        // Keep this logon context alive through the delayed-server-work observation window.
        Step("observe-12s", null, () => Thread.Sleep(TimeSpan.FromSeconds(12)));
        Step("connection-alive-read", "film/existing.txt", () => ReadSmall("film/existing.txt"));
    });
    }
}
catch (Exception error)
{
    Emit("validation-or-runtime", "error", error: error);
    matched = false;
}
Emit("process-complete", "end");
Console.WriteLine(JsonSerializer.Serialize(new { kind = "Windows SMB diagnostic; no vault persistence; not UI acceptance",
    mode, runId, allMatched = matched, events }, new JsonSerializerOptions { WriteIndented = true }));
return matched ? 0 : 1;

sealed record Input(string ShareRoot, string? Username, string? Password, string RunId, string Mode, SwitchIdentity[]? Identities = null);
sealed record SwitchIdentity(string Label, string Username, string Password, string RelativeDirectory, bool Write);
static class Native
{
    [DllImport("advapi32.dll", EntryPoint = "LogonUserW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool LogonUser(string username, string? domain, string password, int logonType,
        int provider, out SafeAccessTokenHandle token);
}
