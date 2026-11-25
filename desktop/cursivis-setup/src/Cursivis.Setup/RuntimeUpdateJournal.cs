using System.IO;
using System.Text.Json;

namespace Cursivis.Setup;

internal sealed record RuntimeUpdateJournal(string[] PreviousDirectories, byte[]? ProtectedProfile)
{
    internal static readonly string[] PayloadDirectories = ["app", "backend", "desktop", "node", "shared"];
    internal const string FileName = "runtime-update.json";
    private const string ActivatedFileName = "runtime-activated";

    internal static RuntimeUpdateJournal Create(string installRoot, string backupRoot, byte[]? protectedProfile)
    {
        RejectReparsePoint(installRoot);
        Directory.CreateDirectory(backupRoot);
        RejectReparsePoint(backupRoot);
        var present = PayloadDirectories.Where(name => Directory.Exists(Path.Combine(installRoot, name))).ToArray();
        var plan = new RuntimeUpdateJournal(present, protectedProfile);
        WriteAtomically(Path.Combine(backupRoot, FileName), JsonSerializer.SerializeToUtf8Bytes(plan));
        return plan;
    }

    internal static RuntimeUpdateJournal? Read(string backupRoot)
    {
        RejectReparsePoint(backupRoot);
        var path = Path.Combine(backupRoot, FileName);
        if (!File.Exists(path))
        {
            return null;
        }
        RejectReparsePoint(path);
        var plan = JsonSerializer.Deserialize<RuntimeUpdateJournal>(File.ReadAllBytes(path));
        if (plan?.PreviousDirectories is null ||
            plan.PreviousDirectories.Any(name => !PayloadDirectories.Contains(name, StringComparer.Ordinal)))
        {
            throw new InvalidDataException("Cursivis found an unreadable update record. The previous runtime backup was kept.");
        }
        return plan;
    }

    internal static void MoveIntoPlace(string stagingRoot, string installRoot, string backupRoot)
    {
        RejectReparsePoint(stagingRoot);
        RejectReparsePoint(installRoot);
        RejectReparsePoint(backupRoot);
        Directory.CreateDirectory(installRoot);
        foreach (var name in PayloadDirectories)
        {
            var staged = Path.Combine(stagingRoot, name);
            var installed = Path.Combine(installRoot, name);
            var backup = Path.Combine(backupRoot, name);
            RejectReparsePoint(staged);
            RejectReparsePoint(installed);
            if (!Directory.Exists(staged))
            {
                throw new InvalidDataException($"The staged runtime is missing '{name}'.");
            }
            if (Directory.Exists(installed))
            {
                Directory.Move(installed, backup);
            }
            Directory.Move(staged, installed);
        }
    }

    internal void RestoreDirectories(string installRoot, string backupRoot, Action<string> deleteDirectory)
    {
        RejectReparsePoint(installRoot);
        RejectReparsePoint(backupRoot);
        Directory.CreateDirectory(installRoot);
        foreach (var name in PayloadDirectories)
        {
            var installed = Path.Combine(installRoot, name);
            var backup = Path.Combine(backupRoot, name);
            RejectReparsePoint(installed);
            RejectReparsePoint(backup);
            if (Directory.Exists(backup))
            {
                deleteDirectory(installed);
                Directory.Move(backup, installed);
            }
            else if (!PreviousDirectories.Contains(name, StringComparer.Ordinal))
            {
                deleteDirectory(installed);
            }
            // No backup for an original directory means it never moved or was already restored.
        }
    }

    internal static bool WasActivated(string backupRoot) => File.Exists(Path.Combine(backupRoot, ActivatedFileName));

    internal static void MarkActivated(string backupRoot) =>
        WriteAtomically(Path.Combine(backupRoot, ActivatedFileName), []);

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal static void RejectReparsePoint(string path)
    {
        if ((Path.Exists(path) || Directory.Exists(path)) &&
            File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("Cursivis Setup cannot replace a redirected runtime directory.");
        }
    }
}
