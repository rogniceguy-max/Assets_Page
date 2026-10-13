using System.Text;
using MuYingDrive.UpdateCore;

namespace MuYingDrive.NativeWpf.Services;

public static class UpdateHealthReporter
{
    public static void ReportIfRequested(IEnumerable<string> arguments, string version)
    {
        var args = arguments.ToArray();
        var index = Array.FindIndex(args, value => value == "--update-health-check");
        if (index < 0 || index + 1 >= args.Length) return;
        var id = args[index + 1];
        if (id.Length != 32 || !id.All(Uri.IsHexDigit)) return;
        var marker = Path.Combine(UpdatePathPolicy.UpdateRoot, "health", $"{id}.ok");
        UpdatePathPolicy.EnsureNoReparsePointComponents(marker);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        UpdatePathPolicy.EnsureNoReparsePointComponents(marker);
        var temporary = marker + ".tmp";
        UpdatePathPolicy.EnsureNoReparsePointComponents(temporary);
        File.WriteAllText(temporary, $"{id}\n{version}\n", new UTF8Encoding(false));
        File.Move(temporary, marker, overwrite: true);
    }
}
