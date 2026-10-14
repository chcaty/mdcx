using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Organizer.Core;

namespace Organizer.Infrastructure;

/// <summary>Shared, bounded process boundary. Provider diagnostics are never retained or displayed.</summary>
internal static partial class MetadataWorkerProtocol
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void ValidateRequest(string query, IReadOnlyList<string>? sources, bool configured)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (query.Length > 256 || query.Any(char.IsControl))
            throw new ArgumentException("Query must contain at most 256 printable characters.", nameof(query));
        // Valid UTF-16 surrogate pairs represent supplementary Unicode characters. Only
        // unpaired code units are invalid; ordinary emoji/title text must remain usable.
        try { _ = StrictUtf8.GetByteCount(query); }
        catch (EncoderFallbackException)
        { throw new ArgumentException("Query must contain valid Unicode text.", nameof(query)); }
        if (sources is not null && (sources.Count is < 1 or > 2 || sources.Distinct(StringComparer.Ordinal).Count() != sources.Count ||
            sources.Any(source => configured ? source != "javbus" : source is not "xcity" and not "theporndb_movies")))
            throw new ArgumentException("Sources must be unique adapters supported by this client mode.", nameof(sources));
    }

    public static Process CreateProcess(string pythonExecutable, string workerScript, string? metadataConfigPath = null)
    {
        var script = Path.GetFullPath(workerScript);
        if (!File.Exists(script)) throw new FileNotFoundException("Python scraper worker was not found.", script);
        if (metadataConfigPath is not null && !File.Exists(metadataConfigPath))
            throw new ScraperException("invalid_config", "The explicitly selected metadata configuration is unavailable.");
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(pythonExecutable)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false, true),
                StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false)
            }
        };
        // Embedded Python ignores PYTHONDONTWRITEBYTECODE from the environment.
        process.StartInfo.ArgumentList.Add("-B");
        process.StartInfo.ArgumentList.Add("-u");
        process.StartInfo.ArgumentList.Add(script);
        if (metadataConfigPath is not null)
        {
            process.StartInfo.ArgumentList.Add("--metadata-config");
            process.StartInfo.ArgumentList.Add(metadataConfigPath);
        }
        process.StartInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        process.StartInfo.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        return process;
    }

    public static string MatchCommand(string requestId, string query, IReadOnlyList<string>? sources)
    {
        var command = new Dictionary<string, object> { ["version"] = 1, ["requestId"] = requestId, ["command"] = "match", ["query"] = query };
        if (sources is not null) command["sources"] = sources;
        return JsonSerializer.Serialize(command);
    }

    public static MetadataCandidate ReadCandidate(JsonElement payload, string query, IReadOnlyList<string>? sources, bool configured)
    {
        var candidate = payload.Deserialize<MetadataCandidate>(JsonOptions) ?? throw new ScraperException("invalid_candidate", "Missing candidate.");
        if (string.IsNullOrWhiteSpace(candidate.Id) || candidate.Id.Length > 256 ||
            string.IsNullOrWhiteSpace(candidate.Title) || candidate.Title.Length > 1000 ||
            string.IsNullOrWhiteSpace(candidate.Source) || candidate.Source.Length > 128 ||
            !Uri.TryCreate(candidate.SourceUrl, UriKind.Absolute, out var sourceUrl) || sourceUrl.Scheme != "https" || !string.IsNullOrEmpty(sourceUrl.UserInfo) ||
            candidate.Provenance is null || candidate.Provenance.Count == 0 || candidate.Provenance.Count > 128 ||
            candidate.Provenance.Any(p => p.Key.Length > 256 || p.Value is null || p.Value.Length > 4096) ||
            candidate.ReviewReasons is null || candidate.ReviewReasons.Count > 32 || candidate.ReviewReasons.Any(r => r is null || r.Length > 1000) ||
            candidate.Year is < 1888 or > 2100 || !double.IsFinite(candidate.Confidence) || candidate.Confidence is < 0 or > 1)
            throw new ScraperException("invalid_candidate", "Worker candidate failed validation.");
        if ((sources is not null && !sources.Contains(candidate.Source, StringComparer.Ordinal)) ||
            (configured ? candidate.Source != "javbus" : candidate.Source == "javbus") || !HasValidIdentity(candidate, query))
            throw new ScraperException("invalid_identity", "Worker candidate requires identity schema 2 with consistent provider, catalog and display identities.");
        return candidate;
    }

    private static bool HasValidIdentity(MetadataCandidate candidate, string query)
    {
        if (!candidate.Provenance.TryGetValue("identitySchema", out var identitySchema) || identitySchema != "2") return false;
        if (string.IsNullOrWhiteSpace(candidate.SourceId) || candidate.SourceId.Length > 128 || candidate.SourceId.Any(char.IsControl)) return false;
        if (candidate.CatalogIdentifier is { } catalog &&
            (string.IsNullOrWhiteSpace(catalog.Namespace) || catalog.Namespace.Length > 64 || catalog.Namespace.Any(char.IsControl) ||
             string.IsNullOrWhiteSpace(catalog.Value) || catalog.Value.Length > 128 || catalog.Value.Any(char.IsControl))) return false;
        if (candidate.Source == "xcity")
            return candidate.SourceId.Length <= 12 && candidate.SourceId[0] is >= '1' and <= '9' && candidate.SourceId.All(char.IsAsciiDigit) &&
                candidate.CatalogIdentifier is { Namespace: "xcity:hinban" } identity && candidate.Id == identity.Value &&
                candidate.SourceUrl == $"https://xcity.jp/avod/detail/?id={candidate.SourceId}";
        if (candidate.Source == "theporndb_movies")
            return candidate.CatalogIdentifier is null && char.IsAsciiLetterOrDigit(candidate.SourceId[0]) &&
                !candidate.SourceId.Contains("..", StringComparison.Ordinal) &&
                candidate.SourceId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') &&
                candidate.Id == $"theporndb_movies:{candidate.SourceId}" &&
                candidate.SourceUrl == $"https://api.theporndb.net/movies/{Uri.EscapeDataString(candidate.SourceId)}" &&
                candidate.RequiresReview && candidate.ReviewReasons.Contains("missing_catalog_identifier", StringComparer.Ordinal);
        if (candidate.Source == "javbus")
            return JavBusRoute().IsMatch(candidate.SourceId) &&
                candidate.CatalogIdentifier is { Namespace: "javbus:catalog" } identity && JavBusCatalog().IsMatch(identity.Value) &&
                candidate.Id == identity.Value && CanonicalCatalog(candidate.SourceId) == identity.Value && CanonicalCatalog(query) == identity.Value &&
                candidate.SourceUrl == $"https://www.javbus.com/{candidate.SourceId}" && candidate.RequiresReview &&
                candidate.ReviewReasons.Contains("catalog_metadata_requires_review", StringComparer.Ordinal);
        return false;
    }

    private static string? CanonicalCatalog(string value)
    {
        var match = CatalogInput().Match(value.Normalize(NormalizationForm.FormKC).Trim());
        return match.Success ? $"{match.Groups[1].Value.ToUpperInvariant()}-{match.Groups[2].Value}{match.Groups[3].Value.ToUpperInvariant()}" : null;
    }

    [GeneratedRegex(@"\A[A-Za-z]{2,12}[-_]?[0-9]{1,12}[A-Za-z]?\z", RegexOptions.CultureInvariant)]
    private static partial Regex JavBusRoute();
    [GeneratedRegex(@"\A[A-Z]{2,12}-[0-9]{1,12}[A-Z]?\z", RegexOptions.CultureInvariant)]
    private static partial Regex JavBusCatalog();
    [GeneratedRegex(@"\A([A-Za-z]{2,12})[-_ ]?([0-9]{1,12})([A-Za-z]?)\z", RegexOptions.CultureInvariant)]
    private static partial Regex CatalogInput();

    public static string SafeErrorCode(JsonElement e)
    {
        var payload = e.TryGetProperty("error", out var nested) ? nested : e;
        return payload.TryGetProperty("code", out var code) ? KnownCode(code.GetString()) : "worker_error";
    }

    private static string KnownCode(string? code) => code switch
    {
        "live_unavailable" or "unsupported_query" or "network" or "tls" or "timeout" or "deadline" or "rate_limited" or
        "auth_required" or "challenge" or "policy_blocked" or "parse" or "schema_drift" or "validation" or "identity_mismatch" or
        "ambiguous_identity" or "response_too_large" or "http_error" or "invalid_config" or "dependency_unavailable" or
        "not_found" or "replay_mismatch" or "search_truncated" or "invalid_request" or "invalid_sources" or "unsupported_version" or
        "request_too_large" or "duplicate_request" or "not_active" or "session_full" or "busy" or "internal" => code,
        _ => "worker_error"
    };

    public static ScraperException SourceFailure(IReadOnlySet<string> codes, bool configured)
    {
        // Preserve the existing fixture unavailable/unsupported behavior, including mixed failures.
        if (codes.Count > 0 && codes.All(code => code is "live_unavailable" or "unsupported_query"))
            return SafeError(codes.Contains("live_unavailable") ? "live_unavailable" : "unsupported_query");
        if (configured && codes.Count == 1) return SafeError(codes.Single());
        return SafeError("sources_failed");
    }

    public static ScraperException SafeError(string code) => new(code, code switch
    {
        "live_unavailable" => "The selected live metadata source is unavailable or disabled.",
        "unsupported_query" => "The selected metadata source does not support this query type.",
        "network" or "tls" => "The metadata connection failed. Check the source and explicit proxy settings.",
        "timeout" or "deadline" => "The metadata request exceeded its time limit.",
        "rate_limited" => "The source is rate limited. Its cooldown remains active in this session.",
        "auth_required" => "The source requires authorized access; no sign-in was attempted.",
        "challenge" => "The source requires human verification; no challenge was attempted.",
        "policy_blocked" => "The metadata request was blocked by the source access policy.",
        "parse" or "schema_drift" or "validation" or "identity_mismatch" or "ambiguous_identity" => "The source metadata could not be validated reliably.",
        "invalid_config" => "The metadata configuration is invalid or unavailable.",
        "dependency_unavailable" => "The configured metadata worker dependency is unavailable.",
        "sources_failed" => "Metadata sources failed; no reliable no-match conclusion is available.",
        "session_full" => "The worker session request limit was reached. Explicitly reload the source settings to start a new session.",
        _ => "The metadata worker reported a failure. No reliable result is available."
    });

    public static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }

    public static async Task DrainDiagnosticsAsync(StreamReader reader, CancellationToken token, long maxCharacters = long.MaxValue)
    {
        var buffer = new char[2048];
        long count = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            if ((count += read) > maxCharacters) throw new ScraperException("protocol_limit", "Worker diagnostic output exceeded the session limit.");
    }

    public static async Task<string?> ReadBoundedLineAsync(StreamReader reader, CancellationToken token)
    {
        var line = new StringBuilder();
        var character = new char[1];
        while (await reader.ReadAsync(character.AsMemory(), token).ConfigureAwait(false) != 0)
        {
            if (character[0] == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length >= 65536) throw new ScraperException("protocol_limit", "Worker line exceeded 64 KiB.");
            line.Append(character[0]);
        }
        return line.Length == 0 ? null : line.ToString();
    }
}
