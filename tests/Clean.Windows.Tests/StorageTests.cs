using Clean.Core;
using Clean.Windows;

namespace Clean.Windows.Tests;

public sealed class StorageTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(1048576)]
    public void LegacyMegabytesKeepTheirByteThresholdAcrossMigrationAndBackup(long mib)
    {
        using var dir = new TestDirectory(); var file = Path.Combine(dir.Root, "settings.json");
        File.WriteAllText(file, $"{{\"LargeFileMiB\":{mib},\"MinimumAgeDays\":9}}");
        var store = new JsonSettingsStore(dir.Root); var loaded = store.Load();
        Assert.Null(loaded.Warning); Assert.Equal(mib * 1048576, loaded.Settings.GetLargeFileMinimumBytes());
        store.Save(loaded.Settings);
        var json = File.ReadAllText(file);
        Assert.Contains("LargeFileMB", json); Assert.DoesNotContain("LargeFileMiB", json);
        File.WriteAllText(file, "{broken");
        var backup = store.Load(); Assert.NotNull(backup.Warning);
        Assert.Equal(mib * 1048576, backup.Settings.GetLargeFileMinimumBytes());
    }
    [Fact] public void NewUnitTakesPrecedenceAndInvalidLegacyValueFallsBack()
    {
        using var dir = new TestDirectory(); var file = Path.Combine(dir.Root, "settings.json");
        File.WriteAllText(file, "{\"LargeFileMB\":100,\"LargeFileMiB\":200}");
        var store = new JsonSettingsStore(dir.Root);
        Assert.Equal(100000000, store.Load().Settings.GetLargeFileMinimumBytes());
        File.WriteAllText(file, "{\"LargeFileMiB\":-1}");
        Assert.NotNull(store.Load().Warning);
    }
    [Fact] public void CorruptSettingsRecoverPreviousValidBackupAndRemainRecoverable()
    {
        using var dir = new TestDirectory(); var store = new JsonSettingsStore(dir.Root);
        store.Save(new() { MinimumAgeDays = 9 }); store.Save(new() { MinimumAgeDays = 12 });
        File.WriteAllText(Path.Combine(dir.Root, "settings.json"), "{broken");
        var recovered = store.Load(); Assert.Equal(9, recovered.Settings.MinimumAgeDays); Assert.NotNull(recovered.Warning);
        store.Save(recovered.Settings); File.WriteAllText(Path.Combine(dir.Root, "settings.json"), "again broken");
        Assert.Equal(9, store.Load().Settings.MinimumAgeDays);
    }
    [Fact] public void InvalidAndNullSettingsFallBackWithoutCrashing()
    {
        using var dir = new TestDirectory(); var file = Path.Combine(dir.Root, "settings.json");
        File.WriteAllText(file, "{\"ExcludedDirectories\":null}"); var result = new JsonSettingsStore(dir.Root).Load();
        Assert.Equal(7, result.Settings.MinimumAgeDays); Assert.NotNull(result.Warning);
    }
    [Fact] public void SettingsRoundTripAndNoTemporaryFilesRemain()
    {
        using var dir = new TestDirectory(); var store = new JsonSettingsStore(dir.Root);
        store.Save(new() { MinimumAgeDays = 20, LargeFileMB = 200, ExcludedDirectories = [@"C:\Data"], SelectedPage = 4 });
        var settings = store.Load().Settings; Assert.Equal(20, settings.MinimumAgeDays); Assert.Equal(200, settings.LargeFileMB);
        Assert.Equal(@"C:\Data", Assert.Single(settings.ExcludedDirectories)); Assert.Equal(4, settings.SelectedPage);
        Assert.Empty(Directory.EnumerateFiles(dir.Root, "*.tmp"));
    }
    [Fact] public void InterruptedHistoryIsNeverReportedAsSuccessAndIsBounded()
    {
        using var dir = new TestDirectory(); var store = new JsonHistoryStore(dir.Root);
        for (int i = 0; i < 55; i++) store.Save(new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(i), "进行中", 10, 1, 0, 0, 0, 9, 100));
        Assert.Equal(50, store.Load().Length); Assert.All(store.Load(), x => Assert.StartsWith("中断", x.State));
        store.Clear(); Assert.Empty(store.Load()); Assert.False(File.Exists(Path.Combine(dir.Root, "history.json.bak")));
    }
    [Fact] public void WriteFailurePreservesExistingSettings()
    {
        using var dir = new TestDirectory(); var store = new JsonSettingsStore(dir.Root); store.Save(new() { MinimumAgeDays = 8 });
        using (var locked = new FileStream(Path.Combine(dir.Root, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => store.Save(new() { MinimumAgeDays = 20 }));
        Assert.Equal(8, store.Load().Settings.MinimumAgeDays);
    }
}

