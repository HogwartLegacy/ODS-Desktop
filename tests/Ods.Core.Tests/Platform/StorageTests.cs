using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Ods.Core.Downloads;
using Ods.Core.Platform;
using Ods.Core.Runtime;
using Ods.Core.Settings;

namespace Ods.Core.Tests.Platform;

public sealed class StorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ods-storage-tests-" + Guid.NewGuid().ToString("N"));

    public StorageTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void The_api_key_file_is_64_hex_characters_readable_only_by_this_user()
    {
        var path = Path.Combine(_root, "secrets", "llama-api-key");

        var key = ApiKeyFile.EnsureKey(path);

        Assert.Matches("^[0-9a-f]{64}$", key);
        Assert.Equal(64, new FileInfo(path).Length);
        Assert.Equal(key, ApiKeyFile.EnsureKey(path));
        var rules = new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        var rule = Assert.Single(rules);
        Assert.Equal(WindowsIdentity.GetCurrent().User, rule.IdentityReference);
        Assert.False(rule.IsInherited);
    }

    [Fact]
    public void A_malformed_api_key_file_is_reported()
    {
        var path = Path.Combine(_root, "bad-key");
        File.WriteAllText(path, "not-a-key\r\n");

        Assert.Throws<InvalidDataException>(() => ApiKeyFile.Read(path));
    }

    [Fact]
    public void A_damaged_settings_file_is_set_aside_and_reported()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, "{ not json");
        var store = new SettingsStore(path);

        var settings = store.Load();

        Assert.False(settings.OnboardingCompleted);
        Assert.NotNull(store.RecoveredFrom);
        Assert.True(File.Exists(store.RecoveredFrom));
        store.Save(settings);
        Assert.False(new SettingsStore(path).Load().OnboardingCompleted);
    }

    [Fact]
    public async Task An_installed_runtime_is_verified_and_tampering_is_detected()
    {
        var (flavor, downloads) = FakeRuntime();
        using var http = new HttpClient();
        var installer = new RuntimeInstaller(Path.Combine(_root, "runtimes"), downloads, new Downloader(http));

        var installed = await installer.InstallAsync(flavor, null, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(installed.Executable));
        Assert.True(File.Exists(Path.Combine(installed.Directory, "pin.json")));
        Assert.True(installer.Check(flavor, forceFullHash: true).Ok);
        Assert.Empty(Directory.GetFiles(downloads));

        File.WriteAllText(Path.Combine(installed.Directory, "evil.dll"), "planted");
        Assert.Contains("unexpected executable", installer.Check(flavor).Problem);
        File.Delete(Path.Combine(installed.Directory, "evil.dll"));

        var exe = installed.Executable;
        var bytes = File.ReadAllBytes(exe);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(exe, bytes);
        Assert.Contains("SHA-256", installer.Check(flavor, forceFullHash: true).Problem);
    }

    [Fact]
    public async Task An_archive_with_an_unsafe_path_is_refused()
    {
        var (flavor, downloads) = FakeRuntime(extraEntry: "../escape.dll");
        using var http = new HttpClient();
        var installer = new RuntimeInstaller(Path.Combine(_root, "runtimes"), downloads, new Downloader(http));

        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(flavor, null, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(_root, "runtimes", "escape.dll")));
    }

    /// <summary>A pinned CPU "runtime" whose archive is already in the downloads folder, so no network is used.</summary>
    private (RuntimeFlavor Flavor, string Downloads) FakeRuntime(string? extraEntry = null)
    {
        var downloads = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(downloads);
        const string asset = "llama-b1234-bin-win-cpu-x64.zip";
        var zipPath = Path.Combine(downloads, asset);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "llama-server.exe", "ggml.dll", extraEntry }.OfType<string>())
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write($"contents of {name} " + new string('x', 1024));
            }
        }
        var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(zipPath)));
        var flavor = new RuntimeFlavor("win-cpu-x64", "cpu", "x64", "b1234", null, null, null,
            [new RuntimeArchive(asset, sha, new FileInfo(zipPath).Length, new Uri($"https://github.com/ggml-org/llama.cpp/releases/download/b1234/{asset}"))],
            ["llama-server.exe", "ggml.dll"]);
        return (flavor, downloads);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
