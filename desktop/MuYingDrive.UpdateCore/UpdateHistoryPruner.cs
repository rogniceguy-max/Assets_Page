using System.Text.Json;
using System.Text.Json.Serialization;

namespace MuYingDrive.UpdateCore;

/// <summary>What a prune pass removed.</summary>
public sealed record UpdateHistoryPruneResult(int InstallBackupsDeleted, int RunnerDirectoriesDeleted, long BytesReclaimed);

/// <summary>
/// Removes the update leftovers that nothing else ever deletes.
/// </summary>
/// <remarks>
/// Two directories gained one copy per successful update and were never cleaned up by the
/// code that creates them — measured at 4.6 GB after eight updates on one machine:
///
/// <list type="bullet">
/// <item><c>Programs\MuYingDrive.previous-&lt;id&gt;</c> — the install directory that
/// update replaced, about 317 MB each.</item>
/// <item><c>Updates\runner\&lt;id&gt;</c> — the updater staged for that update, about
/// 154 MB each and dead weight the moment the update has run.</item>
/// </list>
///
/// The updater cannot do this itself: on Windows the directory holding a running
/// executable cannot be deleted, so a pass trying to remove its own runner directory
/// would always fail. It runs from the client instead, which is never inside either
/// directory and starts often enough to keep up.
///
/// Nothing is removed until its transaction journal says the update settled. A directory
/// whose journal is missing, unreadable or still in flight is left alone — those are
/// exactly the cases the recovery scan reasons about, and guessing at them would turn a
/// disk-space leak into data loss.
/// </remarks>
public static class UpdateHistoryPruner
{
    private const string PreviousPrefix = "MuYingDrive.previous-";
    private const string FailedPrefix = "MuYingDrive.failed-";
    private const string SettledHealthy = "healthy";
    private const string SettledRolledBack = "rolled-back";

    private static readonly JsonSerializerOptions JournalOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Removes settled runner directories and all but the newest settled install backup.
    /// </summary>
    /// <param name="programsDirectory">The directory holding the install and its backups.</param>
    /// <param name="updateRoot">The update cache root, holding <c>runner</c> and <c>transactions</c>.</param>
    /// <param name="dryRun">
    /// Reports what would be removed without removing it, so the decision can be checked
    /// against a real machine before anything is deleted.
    /// </param>
    /// <remarks>
    /// Keeping one install backup is deliberate rather than incidental: it is the only
    /// way back to the previous version if the new one turns out to be broken, and it is
    /// the difference between a rollback and a re-download. Everything older than the most
    /// recent settled one has no such value.
    /// </remarks>
    public static UpdateHistoryPruneResult Prune(string programsDirectory, string updateRoot, bool dryRun = false)
    {
        if (string.IsNullOrWhiteSpace(programsDirectory) || string.IsNullOrWhiteSpace(updateRoot))
            return new UpdateHistoryPruneResult(0, 0, 0);

        var journals = ReadSettledJournals(Path.Combine(updateRoot, "transactions"));
        if (journals.Count == 0) return new UpdateHistoryPruneResult(0, 0, 0);

        var backups = 0;
        var runners = 0;
        long reclaimed = 0;

        // runner\<id> — the updater for that update has finished, so nothing will read it
        // again. A directory still held open by a live process simply fails to delete and
        // is picked up on the next pass.
        var runnerRoot = Path.Combine(updateRoot, "runner");
        foreach (var id in journals.Keys)
        {
            if (DeleteIfSafeChild(runnerRoot, id, dryRun) is not long bytes) continue;
            runners++;
            reclaimed += bytes;
        }

        // Install backups — newest settled one per prefix survives; the rest are from
        // updates that are several versions old and nothing can roll back to them.
        foreach (var (prefix, settledState) in new[]
                 {
                     (PreviousPrefix, SettledHealthy),
                     (FailedPrefix, SettledRolledBack)
                 })
        {
            var candidates = journals
                .Where(entry => entry.Value.State == settledState)
                .OrderByDescending(entry => entry.Value.UpdatedUtc)
                .Skip(1) // the newest stays
                .Select(entry => entry.Key);

            foreach (var id in candidates)
            {
                if (DeleteIfSafeChild(programsDirectory, prefix + id, dryRun) is not long bytes) continue;
                backups++;
                reclaimed += bytes;
            }
        }

        return new UpdateHistoryPruneResult(backups, runners, reclaimed);
    }

    /// <summary>
    /// Reads the journals of transactions that have settled, keyed by update id.
    /// </summary>
    /// <remarks>
    /// A journal that cannot be read is not settled as far as this is concerned, so its
    /// directories survive. That is the safe direction: a leak costs disk, a wrong guess
    /// costs the rollback copy.
    /// </remarks>
    private static Dictionary<string, (string State, DateTimeOffset UpdatedUtc)> ReadSettledJournals(
        string transactionsDirectory)
    {
        var settled = new Dictionary<string, (string, DateTimeOffset)>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(transactionsDirectory)) return settled;

        IEnumerable<string> paths;
        try
        {
            paths = Directory.EnumerateFiles(transactionsDirectory, "*.json", SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return settled;
        }

        foreach (var path in paths)
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (!IsValidId(id)) continue;

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096,
                    FileOptions.SequentialScan);
                var journal = JsonSerializer.Deserialize<TransactionJournal>(stream, JournalOptions);
                if (journal is null || !IsValidId(journal.UpdateId) ||
                    !string.Equals(journal.UpdateId, id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (journal.State is not (SettledHealthy or SettledRolledBack)) continue;
                settled[id] = (journal.State, journal.UpdatedUtc);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }

        return settled;
    }

    /// <summary>
    /// Deletes <paramref name="parent"/>/<paramref name="name"/> when it is a plain
    /// directory directly under the parent.
    /// </summary>
    /// <returns>
    /// The bytes reclaimed, or null when nothing was deleted — because the directory was
    /// not there, was not safe to remove, or was in use. Absent and refused have to be
    /// distinguishable from "deleted an empty one", or a caller counting deletions
    /// reports removing directories that never existed.
    /// </returns>
    private static long? DeleteIfSafeChild(string parent, string name, bool dryRun)
    {
        DirectoryInfo directory;
        try
        {
            var parentFull = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
            var path = Path.GetFullPath(Path.Combine(parentFull, name));
            // The name is built from a validated 32-hex id, but this still refuses anything
            // that resolves outside the parent, and refuses to follow a link out of it.
            if (!string.Equals(Path.GetDirectoryName(path), parentFull, StringComparison.OrdinalIgnoreCase))
                return null;

            directory = new DirectoryInfo(path);
            if (!directory.Exists) return null;
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        var size = Measure(directory);
        if (dryRun) return size;

        try
        {
            directory.Delete(recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // In use, or something else owns it. Left for the next pass.
            return null;
        }

        return size;
    }

    private static long Measure(DirectoryInfo directory)
    {
        long total = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            try
            {
                foreach (var file in current.EnumerateFiles()) total += file.Length;
                foreach (var child in current.EnumerateDirectories()) pending.Push(child);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return total;
    }

    private static bool IsValidId(string id) => id.Length == 32 && id.All(Uri.IsHexDigit);

    private sealed record TransactionJournal(
        [property: JsonPropertyName("update_id")] string UpdateId,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("updated_utc")] DateTimeOffset UpdatedUtc = default);
}
