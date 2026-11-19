using Dalamud.Configuration;

namespace DalamudProxy;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    public bool Enabled { get; set; }

    public bool ProxyRepositoryReads { get; set; } = true;

    public bool ProxyPluginDownloads { get; set; } = true;

    public bool AutoRefresh { get; set; } = true;

    public string ProxyUrl { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string ProtectedPassword { get; set; } = string.Empty;

    public string TestUrl { get; set; } = "https://github.com/";

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
