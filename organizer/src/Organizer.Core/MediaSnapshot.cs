using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Organizer.Core;

/// <summary>Review/change-detection token for a scanned media group, not a content hash or duplicate proof.</summary>
public static class MediaSnapshot
{
    public const string ProvenanceKey = "workbench.sourceSnapshot";

    public static string Fingerprint(MediaAsset asset)
    {
        var fields = new List<string> { asset.Id, asset.Fingerprint };
        foreach (var sidecar in asset.Sidecars.OrderBy(item => item.Path, StringComparer.Ordinal))
        {
            fields.Add(sidecar.Path);
            fields.Add(sidecar.Fingerprint);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields))));
    }
}
