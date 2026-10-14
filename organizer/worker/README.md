# Metadata worker: offline historical-contract slice

Python **3.12+**, standard library only. Run independently of the legacy root package (Python 3.13.4 and third-party dependencies):

```sh
python3 organizer/worker/main.py
python3 -m unittest discover -s organizer/worker/tests -v
```

The entrypoint works from any current directory. It reads UTF-8 JSON Lines, emits **only JSONL on stdout**, and writes startup diagnostics to stderr. EOF drains outstanding work and exits. The host must drain both pipes asynchronously. No package install, GUI import, browser, personal media, credentials or live network is needed.

## Deliberate migration boundary

This is an independently implemented scheduling/validation boundary with narrow parser and historical HTTP-request contracts from the repository. It neither wraps the old CLI nor imports its global manager.

- xcity: search `/result_published/?q=...` (catalog hyphens removed) → links in `table.resultList/tr/td/a` → separate numeric `/avod/detail/?id=...` request → `span#hinban`, `span#program_detail_title`, and the `発売日` release label. Detail route subset is grounded in the existing crawler's line-259 example, not a claim that every historical/live route is supported.
- TPDB movies: `/movies?q=...&per_page=100` search **data array**, then `/movies/{slug}` detail **data object**. Search hints never directly become metadata. The filename/date/actor ranking algorithm and hash branch are not ported.
- Requests are exact offline objects: source, GET method, URL, headers and credential reference. The TPDB JSON/bearer header shape uses one fixed inert test marker. Its explicit offline User-Agent intentionally replaces the legacy browser string. No actual credentials are read or transmitted.
- Source priority is ordered and lazy. Only requested sources run. A complete accepted candidate stops lower-source work; a source's detail targets run sequentially with a ten-unique-target budget. Independent match requests remain bounded and concurrent.
- The slice does not port all providers, legacy configuration, full per-field/language/type reduction, actors, artwork, translations, hash lookup, or real HTTP. User-reviewed metadata persistence belongs to the .NET application.

Fixtures are **authored fictional, synthetic and uncaptured**, not archives of provider responses. Historical route strings use provider origins solely to test shape; they are never followed. Every fixture candidate records `fixture="true"` and `responseOrigin="synthetic_uncaptured"`. See [the corpus](fixtures/README.md) and [audit](../docs/scraper-audit.md). Existing LICENSE/README notices remain unchanged; the GPLv3/additional README restriction ambiguity is unresolved, and nothing grants distribution or commercial-use rights.

## JSONL v1 framing with candidate identity schema 2

Framing remains JSONL version 1, but candidate identity schema 2 is required by the upgraded client. Older worker candidates without typed identity fields are rejected; upgrade the worker and client together. Existing display/review fields remain, with explicit identity fields added.

One JSON object per line. A session accepts at most 8192 unique match IDs and 32 active matches. IDs must be 1–128 ASCII letters/digits or `_.:-`; never reuse an ID within a process session. A rejected busy match consumes its ID. Restart after the session limit.

```json
{"version":1,"requestId":"match-1","command":"match","query":"DEMO-001"}
{"version":1,"requestId":"match-2","command":"match","query":"Harbor of Stars","sources":["theporndb_movies"]}
{"version":1,"requestId":"match-1","command":"cancel"}
```

`query` contains 1–256 printable characters. `sources` is a nonempty, unique ordered subset of `xcity` and `theporndb_movies`; that order is the default. Unknown fields (including credentials, headers, URLs and fixture paths), commands/sources, duplicate JSON keys, nonfinite numbers and versions other than integer 1 are rejected. A frame is limited to 16 KiB including newline. Oversized frames are drained without buffering the remainder before the next line is processed.

Events carry `version`, `requestId`, `type`:

- `candidate`: existing `id`, `title`, optional integer `year`, `source`, `sourceUrl`, `confidence`, boolean `requiresReview`, array `reviewReasons` and string-valued `provenance`. Additive `sourceId` is the opaque provider identifier; `catalogIdentifier` is `{namespace,value}` or explicit null.
- `health`: process-cumulative counters keyed by source; a snapshot may include concurrent work.
- `completed`: terminal `count`, `sourceFailures`, and status `matched`, `no_match`, or `sources_failed`. The latter means zero candidates and at least one failure, not a verified absence of metadata. A truncated search is explicitly a source failure, even when no candidate was found in the inspected subset.
- `error`: safe `{code,message,retryable}` plus `terminal`. Source errors include `source` and `terminal:false`. A per-detail failure preserves other accepted details and allows later targets within the budget. Duplicate-ID, stale-cancel and malformed-frame errors for already-seen IDs are nonterminal diagnostics.
- `cancelled`: terminal acknowledgment, including when cancelled before first coroutine execution. No later candidate/health/completed events for that request.

Normal order: zero or more source errors, candidates, health, completed. Concurrent requests can interleave. A valid admitted match has one terminal result; the overall deadline is 30 seconds. A malformed frame with no trustworthy ID uses `requestId:""`. IDs remain tombstoned until process exit.

Illustrative candidates, with provenance abbreviated:

```json
{"id":"DEMO-001","source":"xcity","sourceId":"1001","catalogIdentifier":{"namespace":"xcity:hinban","value":"DEMO-001"},"sourceUrl":"https://xcity.jp/avod/detail/?id=1001","title":"The Lantern Map","year":2024,"confidence":0.99,"requiresReview":false,"reviewReasons":[],"provenance":{"identitySchema":"2","fixture":"true","responseOrigin":"synthetic_uncaptured"}}
{"id":"theporndb_movies:harbor-of-stars","source":"theporndb_movies","sourceId":"harbor-of-stars","catalogIdentifier":null,"sourceUrl":"https://api.theporndb.net/movies/harbor-of-stars","title":"Harbor of Stars","year":2023,"confidence":0.95,"requiresReview":true,"reviewReasons":["title_only_match","missing_catalog_identifier"],"provenance":{"identitySchema":"2","fixture":"true","responseOrigin":"synthetic_uncaptured"}}
```

## Identity, quality and provenance

`id` remains a backward-compatible **display/review key**, never an inter-provider join key. Internal `ProviderIdentity(source,value)` and `CatalogIdentifier(namespace,value)` are different types. An xcity numeric route ID is distinct from its `hinban` catalog value. TPDB slug is exact, opaque and source-scoped; it is never converted to a catalog number, even if it looks like `DEMO-001`.

- Only declared catalog identifiers receive Unicode NFKC and narrow case/separator equivalence. Leading zeroes and edition suffixes stay significant. Provider IDs/slugs are never canonicalized, and detail slugs must exactly equal the selected search slug.
- Catalog-number queries never degrade into title hits. TPDB has no verified catalog namespace or crosswalk in the inspected record schema, so catalog-shaped queries skip that adapter without making requests. Use a title to exercise TPDB.
- xcity catalog mismatch is an explicit rejected detail, but later search targets are still inspected within the budget. A numeric xcity source ID is **response-route-bound**: the historical detail fields do not independently prove it. Optional canonical-link consistency is additional hardening, not a required historical selector or captured evidence. Wrong canonical identities are rejected when present.
- Required detail spans must occur exactly once; repeated required IDs cannot concatenate into an invented identity. Missing/duplicate fields are `schema_drift`; invalid syntax is `parse`; valid empty search is no-match.
- Duplicate same-provider search targets are fetched once, retaining duplicate count and review reason. Distinct same-title identities remain separate. Search hints never override detail fields.
- At most ten unique detail requests per source per match. Additional results emit `search_truncated` and set `search_results_truncated` on retained candidates, so the inspected prefix is not portrayed as exhaustive. Other detail failures set `incomplete_detail_results`. A broken target cannot erase a good candidate. Authentication/challenge/policy denials or a rate-limit budget stop further detail attempts for that provider; prior candidates remain reviewable.
- Missing-year enrichment requires an explicit same catalog namespace **and** canonical value. These two shipped adapters supply no such cross-provider evidence, so xcity's missing year stays missing. Synthetic fake-provider tests exercise the generic fill-only rule with explicit shared namespace evidence; those tests do not establish a real crosswalk. Valid prior title/year is preserved.
- Score is an evidence-ranking **heuristic, not a calibrated probability**: exact catalog ID 0.99; exact title 0.95; title similarity at least 0.65 up to 0.84; invalid optional date deducts 0.05. Scores never authorize filesystem changes. Title matches and absent catalog identity require review.
- Titles use NFKC and whitespace collapse. Dates must be actual calendar dates within 1888–2100. Invalid optional dates remain missing with evidence.
- Flattened string provenance preserves identity schema, provider identity, catalog namespace, field source/URL/selector/JSON path, transformations, validation, detail/search body hashes, search URL, duplicate count, detail budget, priority, and review reasons. `data.title` / `data.date` are detail paths; `data[].slug` is only a search locator. An enriched field retains its own source and identity gate.
- Fixtures have no fabricated retrieval timestamp. No cache exists; future cache reads must preserve original retrieval/provenance.

Replay examples:

| Query | Expected behavior |
| --- | --- |
| `DEMO-001` | xcity search then detail, exact catalog ID |
| `DEMO-002` | separate xcity search/detail synthetic record |
| `DEMO-005` | wrong xcity detail catalog rejected; no fabricated TPDB ID fallback |
| `DEMO-006` | xcity title preserved; missing year remains missing |
| `Harbor of Stars`, sources TPDB only | source-qualified slug display; catalog null; review |
| `The Last Beacon` | two TPDB slugs with same title, both reviewable |
| `No Such Fictional Film` | completed no_match |

## Scheduling, retries, and source health

The shared scheduler defaults to four in-flight transport calls globally, two per hostname, and a 100 ms minimum interval between starts on a hostname. Every attempt, including retries, passes admission. Waits do not hold global transport slots. A host cooldown is rechecked after waiting for a global slot. Cancellation releases permits while queued, in flight, and sleeping.

Read-only GET-shaped requests get at most three attempts, a 10-second per-attempt timeout, and exponential backoff with small jitter when no valid server delay is provided. Only timeout/connection errors, selected transient OS errors, and HTTP 408/429/500/502/503/504 are retried. Generic OS, parsing, validation, schema, access, challenge, 404, and ordinary 4xx errors are not retried.

`Retry-After` supports delta seconds and HTTP-date, case-insensitive headers, and a shared host cooldown. A delay exceeding 60 seconds is surfaced rather than shortened; subsequent work still respects the original cooldown, subject to its own overall deadline. Cancellation can interrupt all async waits. The scheduler bounds bodies to 1 MiB, the JSON adapter bounds search records to 100, and the matcher caps detail requests at ten unique targets per source. A future real transport must enforce the body bound during streaming, before buffering.

Health separates transport attempts (`requests`, `successes`, `failures`, `retries`, `rateLimited`, `inFlight`, `peakInFlight`, `latencyMs`, `lastError`) from parsed matches/no-matches/validation errors (`matches`, `noMatches`, `parserFailures`) and cancellations. HTTP 404 is a transport failure and a no-match, not a broken parser. `successes` means successful HTTP-shaped responses, not accepted metadata; `retries` counts scheduled retries, including a retry later cancelled during backoff.

## Error taxonomy

- Input/lifecycle: `invalid_request`, `unsupported_version`, `invalid_sources`, `request_too_large`, `duplicate_request`, `not_active`, `busy`, `session_full`
- Transport/access: `network`, `timeout`, `http_error`, `rate_limited`, `not_found`, `auth_required`, `challenge`, `policy_blocked`, `response_too_large`
- Metadata/replay: `parse`, `schema_drift`, `validation`, `identity_mismatch`, `search_truncated`, `replay_mismatch`
- Worker: `deadline`, `internal`

Error messages are deliberately safe constants or status codes. Raw response bodies, URLs with query strings, credentials, and exception text never become protocol error messages.

## Transport safety and remaining work

The shipped executable has **no live-network switch or socket implementation**. `FixtureTransport` validates exact reviewed routes/method/headers and maps fixed files, never user-controlled paths. `ReplayTransport` additionally enforces ordered exact expected requests and can assert that every expectation was consumed. `AccessControlledTransport` is an injectable, fail-closed seam requiring an explicit HTTPS allowlist and a per-request authorization function. Its tests use fake transports; it does not claim to implement robots fetching, TLS, DNS-rebinding defense, or authenticated live access.

Before any real transport is added, review source permission/robots/terms, allowed destinations including resolved IPs, streaming body bounds, TLS verification, redirect policy, authentication boundaries, cancellation, and log redaction. No ambient cookies, browser impersonation, login automation, CAPTCHA bypass, proxy workaround, or anti-bot evasion is included. 401/403, redirects, and recognizable challenge pages are surfaced rather than bypassed.

Tests cover pure parser replay, identity negatives, field fallback, provenance, source priority, explicit ambiguity, safe errors, no sockets, concurrency high-water marks, per-attempt admission, retry policies/cooldowns, cancellation, malformed/oversized JSONL, process streaming, and process EOF. They do not establish live-source accuracy, production performance, all-provider parity, or the full legacy application's compatibility.
