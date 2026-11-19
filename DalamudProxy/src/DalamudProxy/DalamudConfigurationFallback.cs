using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace DalamudProxy;

internal static class DalamudConfigurationFallback
{
    public static IReadOnlyList<string> ReadRepositoryUrls(IPluginLog log)
    {
        foreach (var path in GetCandidatePaths())
        {
            if (!File.Exists(path))
                continue;

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var urls = new HashSet<string>(StringComparer.Ordinal);
                var root = document.RootElement;

                if (root.TryGetProperty("MainRepoUrl", out var mainRepo)
                    && mainRepo.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(mainRepo.GetString()))
                    urls.Add(mainRepo.GetString()!);

                if (root.TryGetProperty("ThirdRepoList", out var thirdParty))
                    ReadThirdPartyUrls(thirdParty, urls);

                if (urls.Count > 0)
                    return urls.ToArray();
            }
            catch (Exception ex)
            {
                log.Warning(ex, "DalamudProxy: 无法解析 Dalamud 配置文件 {Path}", path);
            }
        }

        return Array.Empty<string>();
    }

    private static IEnumerable<string> GetCandidatePaths()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(appData, "XIVLauncherCN", "dalamudConfig.json");
        yield return Path.Combine(appData, "XIVLauncher", "dalamudConfig.json");
    }

    private static void ReadThirdPartyUrls(JsonElement element, ISet<string> target)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                ReadRepositoryEntry(item, target);
            return;
        }

        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("$values", out var values)
            && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in values.EnumerateArray())
                ReadRepositoryEntry(item, target);
        }
    }

    private static void ReadRepositoryEntry(JsonElement item, ISet<string> target)
    {
        if (item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty("Url", out var url)
            && url.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(url.GetString()))
            target.Add(url.GetString()!);
    }
}

