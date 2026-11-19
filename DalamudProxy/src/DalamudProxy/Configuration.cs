using System.Collections.Generic;
using Dalamud.Configuration;

namespace DalamudProxy;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; }

    public string ProxyUrl { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string ProtectedPassword { get; set; } = string.Empty;

    public string TestUrl { get; set; } = "https://github.com/";

    public bool AutoRefresh { get; set; } = true;

    public List<string> SelectedRepositoryUrls { get; set; } = new();

    public List<string> SelectedPluginInternalNames { get; set; } = new();

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
