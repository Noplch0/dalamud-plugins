using DalamudProxy.Core;

namespace DalamudProxy.Tests;

public sealed class PluginMasterParserTests
{
    [Fact]
    public void Parse_ReadsUrlsAndRelativeLinks()
    {
        const string json = """
        [
          {
            "InternalName": "SamplePlugin",
            "Name": "Sample Plugin",
            "Author": "Tester",
            "Punchline": "Hello",
            "AssemblyVersion": "1.2.3.4",
            "DownloadLinkInstall": "../downloads/install.zip",
            "DownloadLinkUpdate": "https://cdn.example/update.zip",
            "DownloadLinkTesting": "https://cdn.example/testing.zip",
            "IconUrl": "../images/icon.png",
            "ImageUrls": [ "../images/one.png", "https://cdn.example/two.png" ]
          }
        ]
        """;

        var parsed = PluginMasterParser.Parse(json, "https://repo.example/path/pluginmaster.json");

        var manifest = Assert.Single(parsed);
        Assert.Equal("SamplePlugin", manifest.InternalName);
        Assert.Equal("Sample Plugin", manifest.Name);
        Assert.Equal(new Version(1, 2, 3, 4), manifest.AssemblyVersion);
        Assert.Equal("https://repo.example/downloads/install.zip", manifest.DownloadLinkInstall);
        Assert.Equal("https://repo.example/images/icon.png", manifest.IconUrl);
        Assert.Equal(
            new[] { "https://repo.example/images/one.png", "https://cdn.example/two.png" },
            manifest.ImageUrls);
    }

    [Fact]
    public void Parse_SupportsSerializedCollectionWrapperAndSkipsInvalidEntries()
    {
        const string json = """
        {
          "$values": [
            { "InternalName": "", "Name": "ignored" },
            { "InternalName": "Valid", "Name": "Valid Plugin", "AssemblyVersion": "1.0.0.0" }
          ]
        }
        """;

        var parsed = PluginMasterParser.Parse(json, "https://repo.example/pluginmaster.json");

        Assert.Single(parsed);
        Assert.Equal("Valid", parsed[0].InternalName);
        Assert.Equal("Valid Plugin", parsed[0].Name);
    }
}

