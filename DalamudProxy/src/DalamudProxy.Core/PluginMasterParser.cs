using System.Globalization;
using System.Text.Json;

namespace DalamudProxy.Core;

public static class PluginMasterParser
{
    public static IReadOnlyList<PluginManifestInfo> Parse(string json, string repositoryMasterUrl)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        var items = EnumerateManifestItems(document.RootElement);
        var result = new List<PluginManifestInfo>();
        Uri? repositoryUri = Uri.TryCreate(repositoryMasterUrl, UriKind.Absolute, out var parsedRepository)
            ? parsedRepository
            : null;

        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var internalName = GetString(item, "InternalName");
            if (string.IsNullOrWhiteSpace(internalName))
                continue;

            var name = GetString(item, "Name");
            var images = new List<string>();
            if (item.TryGetProperty("ImageUrls", out var imageArray) && imageArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var image in imageArray.EnumerateArray())
                {
                    if (image.ValueKind == JsonValueKind.String
                        && UriUtilities.TryNormalize(image.GetString(), repositoryUri, out var normalizedImage))
                        images.Add(normalizedImage);
                }
            }

            Version? assemblyVersion = null;
            if (Version.TryParse(GetString(item, "AssemblyVersion"), out var parsedVersion))
                assemblyVersion = parsedVersion;

            result.Add(new PluginManifestInfo(
                RepositoryMasterUrl: repositoryMasterUrl,
                InternalName: internalName,
                Name: string.IsNullOrWhiteSpace(name) ? internalName : name,
                Author: GetString(item, "Author"),
                Punchline: GetString(item, "Punchline"),
                AssemblyVersion: assemblyVersion,
                DownloadLinkInstall: NormalizeLink(item, "DownloadLinkInstall", repositoryUri),
                DownloadLinkUpdate: NormalizeLink(item, "DownloadLinkUpdate", repositoryUri),
                DownloadLinkTesting: NormalizeLink(item, "DownloadLinkTesting", repositoryUri),
                IconUrl: NormalizeLink(item, "IconUrl", repositoryUri),
                ImageUrls: images));
        }

        return result;
    }

    private static IEnumerable<JsonElement> EnumerateManifestItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray().ToArray();

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("$values", out var values)
            && values.ValueKind == JsonValueKind.Array)
            return values.EnumerateArray().ToArray();

        return Array.Empty<JsonElement>();
    }

    private static string? NormalizeLink(JsonElement element, string propertyName, Uri? baseUri)
    {
        var raw = GetString(element, propertyName);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (UriUtilities.TryNormalize(raw, baseUri, out var normalized))
            return normalized;

        return raw;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null,
        };
    }
}
