using System.IO;
using Cursivis.Setup;
using Xunit;

namespace Cursivis.Companion.Tests;

public sealed class RuntimeUpdateJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CursivisUpdateTests", Guid.NewGuid().ToString("N"));
    private string Install => Path.Combine(_root, "Cursivis");
    private string Stage => Path.Combine(_root, "stage");
    private string Backup => Path.Combine(_root, "backup");

    [Fact]
    public void InterruptedReplacementPreservesDirectoriesTheUpdateNeverMoved()
    {
        Write(Install, "app", "previous app");
        Write(Install, "backend", "previous backend");
        Write(Install, "desktop", "untouched browser data");
        Write(Stage, "app", "replacement");
        RuntimeUpdateJournal.Create(Install, Backup, protectedProfile: null);
        Assert.Throws<InvalidDataException>(() => RuntimeUpdateJournal.MoveIntoPlace(Stage, Install, Backup));
        Assert.Equal("replacement", File.ReadAllText(Path.Combine(Install, "app", "fixture.txt")));

        var recovered = Assert.IsType<RuntimeUpdateJournal>(RuntimeUpdateJournal.Read(Backup));
        recovered.RestoreDirectories(Install, Backup, DeleteIfPresent);
        Assert.Equal("previous app", File.ReadAllText(Path.Combine(Install, "app", "fixture.txt")));
        Assert.Equal("previous backend", File.ReadAllText(Path.Combine(Install, "backend", "fixture.txt")));
        Assert.Equal("untouched browser data", File.ReadAllText(Path.Combine(Install, "desktop", "fixture.txt")));
    }

    [Fact]
    public void FailedRollbackOnLockedFileKeepsBackupForTheNextSetupRun()
    {
        Write(Install, "app", "working app");
        foreach (var name in RuntimeUpdateJournal.PayloadDirectories) Write(Stage, name, "new runtime");
        RuntimeUpdateJournal.Create(Install, Backup, protectedProfile: null);
        RuntimeUpdateJournal.MoveIntoPlace(Stage, Install, Backup);

        using (var locked = File.Open(Path.Combine(Install, "app", "fixture.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var recovery = Assert.IsType<RuntimeUpdateJournal>(RuntimeUpdateJournal.Read(Backup));
            Assert.ThrowsAny<IOException>(() => recovery.RestoreDirectories(Install, Backup, DeleteIfPresent));
            Assert.False(RuntimeUpdateJournal.WasActivated(Backup));
            Assert.Equal("working app", File.ReadAllText(Path.Combine(Backup, "app", "fixture.txt")));
        }

        var retry = Assert.IsType<RuntimeUpdateJournal>(RuntimeUpdateJournal.Read(Backup));
        retry.RestoreDirectories(Install, Backup, DeleteIfPresent);
        Assert.Equal("working app", File.ReadAllText(Path.Combine(Install, "app", "fixture.txt")));
        Assert.False(Directory.Exists(Path.Combine(Install, "backend")));
        RuntimeUpdateJournal.MarkActivated(Backup);
        Assert.True(RuntimeUpdateJournal.WasActivated(Backup));
    }

    private static void Write(string root, string name, string content)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "fixture.txt"), content);
    }

    private static void DeleteIfPresent(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    public void Dispose() => DeleteIfPresent(_root);
}
