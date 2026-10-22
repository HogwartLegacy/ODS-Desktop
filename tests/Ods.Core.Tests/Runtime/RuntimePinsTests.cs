using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ods.Core.Runtime;

namespace Ods.Core.Tests.Runtime;

/// <summary>The desktop pins stay identical to the contracts ODS's installers already use.</summary>
public sealed class RuntimePinsTests
{
    private static RuntimePins Pins => RuntimePins.Load(Path.Combine(Repo.DesktopRoot, "runtime-pins.json"));

    [Fact]
    public void Vulkan_pin_equals_the_amd_backend_contract()
    {
        var contract = Repo.Json("config", "backends", "amd.json")["runtime"]!["llama_server"]!["windows"]!;
        var vulkan = Pins.Get("win-vulkan-x64");

        Assert.Equal((string?)contract["release_tag"], vulkan.ReleaseTag);
        var archive = Assert.Single(vulkan.Archives);
        Assert.Equal((string?)contract["asset"], archive.Asset);
        Assert.Equal((string?)contract["sha256"], archive.Sha256);
        Assert.Equal((long)contract["size"]!, archive.Size);
    }

    [Fact]
    public void Arm64_cuda_pin_equals_the_nvidia_backend_contract()
    {
        var contract = Repo.Json("config", "backends", "nvidia.json")["runtime"]!["llama_server"]!["windows_arm64"]!;
        var cuda = Pins.Get((string)contract["flavor"]!);

        Assert.Equal((string?)contract["release_tag"], cuda.ReleaseTag);
        Assert.Equal(contract["archives"]!.AsArray().Select(a => ((string)a!["asset"]!, (string)a["sha256"]!, (long)a["size"]!)),
            cuda.Archives.Select(a => (a.Asset, a.Sha256, a.Size)));
        Assert.Equal(contract["required_files"]!.AsArray().Select(f => (string)f!), cuda.RequiredFiles);
        Assert.Equal(Version.Parse((string)contract["min_driver_version"]!), cuda.MinDriverVersion);
    }

    [Fact]
    public void X64_builds_use_the_windows_native_release_tag_of_the_installer()
    {
        var constants = File.ReadAllText(Repo.Path("installers", "windows", "lib", "constants.ps1"));
        var tag = Regex.Match(constants, "\\$script:LLAMA_CPP_RELEASE_TAG = \"(b\\d+)\"").Groups[1].Value;

        Assert.All(Pins.Flavors.Where(f => f.Architecture == "x64"), flavor => Assert.Equal(tag, flavor.ReleaseTag));
    }

    [Fact]
    public void Every_flavor_downloads_only_official_release_assets()
    {
        Assert.All(Pins.Flavors.SelectMany(f => f.Archives), archive =>
        {
            Assert.Equal("github.com", archive.Url.Host);
            Assert.StartsWith("/ggml-org/llama.cpp/releases/download/", archive.Url.AbsolutePath);
        });
    }

    [Fact]
    public void Desktop_version_matches_the_installer_version()
    {
        var constants = File.ReadAllText(Repo.Path("installers", "windows", "lib", "constants.ps1"));
        var version = Regex.Match(constants, "\\$script:ODS_VERSION = \"([0-9.]+)\"").Groups[1].Value;

        Assert.Equal(version, Repo.OdsVersion);
    }

    [Fact]
    public void A_malformed_pin_is_rejected_not_used()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ods-pins-{Guid.NewGuid():N}.json");
        var pins = JsonNode.Parse(File.ReadAllText(Path.Combine(Repo.DesktopRoot, "runtime-pins.json")))!;
        pins["flavors"]![0]!["archives"]![0]!["sha256"] = "not-a-hash";
        File.WriteAllText(path, pins.ToJsonString());
        try
        {
            Assert.Throws<InvalidDataException>(() => RuntimePins.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
