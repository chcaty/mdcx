# Metadata worker: offline compatibility slice

Python **3.12+**, standard library only. Run it independently of the legacy root package (which currently requires Python 3.13.4 and third-party dependencies):

```sh
python3 organizer/worker/main.py
python3 -m unittest discover -s organizer/worker/tests -v
```

The entrypoint works from any current directory. It reads UTF-8 JSON Lines on stdin, emits **only JSONL on stdout**, and puts startup diagnostics on stderr. Close stdin to finish outstanding requests and exit. The host must drain both pipes asynchronously. No package install, GUI import, browser, personal media, credentials, or live network is needed.

## Deliberate migration boundary

This is new scheduling/validation/transport logic plus pure parser adapters based on actual extraction rules in the repository. It does not invoke or wrap the old CLI and does not import the old global manager.

- `xcity`: HTML detail identifiers `span#hinban`, title `span#program_detail_title`, and release label `発売日` from `mdcx/crawlers/xcity.py`.
- `theporndb_movies`: the `slug`, `title`, and `date` detail-record fields from `mdcx/crawlers/theporndb_movies.py`, replayed inside a synthetic search envelope.
- Preserves explicitly ordered, lazy source fallback. A complete candidate stops further requests. A missing year may be filled only from the same exact canonical ID at a lower priority; valid prior title/year are never overwritten.
- It does **not** port all sources, search/detail HTTP flows, old configuration, language routing, media-type selection, artwork, actors, translations, or the full per-field reducer. User-reviewed metadata persistence is owned by the .NET application, not this stateless worker.

Fixtures are synthetic neutral film records with reserved `.invalid` URLs. Their source names identify parser lineage, not current live-provider verification. Every candidate says `provenance.fixture="true"`. All existing repository LICENSE/README notices are preserved; see [the scraper audit](../docs/scraper-audit.md) for exact references and the unresolved GPLv3/additional README restrictions. Nothing here grants new distribution or commercial-use rights.

## Protocol v1

One JSON object per line. A session accepts at most 8192 unique match IDs and 32 active matches, with no unbounded pending request queue. IDs must be 1–128 ASCII letters/digits or `_.:-`; never reuse an ID during one process session. A rejected busy match consumes its ID, so retry with a new one. Restart for a fresh session after the session limit.

```json
{"version":1,"requestId":"match-1","command":"match","query":"DEMO-001"}
{"version":1,"requestId":"match-2","command":"match","query":"DEMO-002","sources":["theporndb_movies","xcity"]}
{"version":1,"requestId":"match-1","command":"cancel"}
```

`query` contains 1–256 printable characters. `sources` is an optional, nonempty, unique ordered subset of `xcity` and `theporndb_movies`; that order is the default. Unknown keys, commands, sources, duplicate JSON keys, nonfinite JSON values, and versions other than integer 1 are rejected. A frame is limited to 16 KiB including its newline. An oversized frame is drained to its end without accumulating the remainder, and the next line can still be processed.

Each event carries `version`, `requestId`, and `type`:

- `candidate`: one `candidate` object with `id`, `title`, optional integer `year`, `source`, `sourceUrl`, `confidence`, boolean `requiresReview`, array `reviewReasons`, and string-valued `provenance`.
- `health`: process-cumulative counters in `sources`, keyed by source name. A snapshot can include other concurrently running requests.
- `completed`: terminal event, `count`, `sourceFailures`, and status `matched`, `no_match`, or `sources_failed`. `sources_failed` means zero candidates and at least one source failure; it must not be presented as a verified absence of metadata.
- `error`: `error: {code,message,retryable}` and `terminal`. A source failure has `source` and `terminal:false`, allowing a lower-priority source to succeed. A terminal error ends the match. Duplicate-ID, inactive-cancel, and malformed-frame errors that reuse an already-seen ID are nonterminal diagnostics and do not end another request.
- `cancelled`: terminal acknowledgment for an accepted cancellation. Even cancellation before the match coroutine starts produces exactly one acknowledgment. No later candidate/health/completed work events are emitted for that request.

Normal match ordering is zero or more nonterminal source errors, zero or more candidates, health, completed. Requests can interleave; use IDs rather than arrival order. The overall match deadline is 30 seconds. Each admitted match has exactly one terminal outcome. A malformed input with no trustworthy ID uses `requestId:""`; do not associate it with an active match. Unknown/stale/repeated cancellation returns `not_active` without reserving an ID. IDs remain tombstoned for the session so delayed results cannot become results for a new request with a reused ID.

Example candidate (provenance abbreviated):

```json
{"version":1,"requestId":"match-1","type":"candidate","candidate":{"id":"DEMO-001","title":"The Lantern Map","year":2024,"source":"xcity","sourceUrl":"https://xcity.example.invalid/detail/DEMO-001","confidence":0.99,"requiresReview":false,"reviewReasons":[],"provenance":{"fixture":"true","adapterVersion":"1","matchMethod":"exact_identifier","confidenceScale":"heuristic_v1_not_probability","field.title.source":"xcity","field.title":"span#program_detail_title"}}}
```

## Quality and provenance

The score is a deterministic **evidence-ranking heuristic, not a calibrated probability**. Never present 0.99 as “99% accurate.” Current rules: exact canonical ID = 0.99; exact title = 0.95; title similarity at least 0.65 = up to 0.84; invalid optional date deducts 0.05. Scores do not authorize filesystem changes or automatic acceptance. The application reviews metadata separately.

- Catalog-ID normalization applies Unicode NFKC and case/separator equivalence for the narrow letters + digits + optional edition-letter form. Leading zeroes and edition suffixes remain significant; general source slug punctuation is not stripped. Identity classification happens **after** normalization, so fullwidth input cannot escape the exact-ID gate.
- Identifier queries never degrade to a title-only match. A mismatched xcity detail ID raises `identity_mismatch`; a valid search list is filtered to exact requested IDs rather than selecting its first hit. Duplicate exact IDs or multiple title candidates remain separate and require review.
- Title-only/fuzzy matches require review. Missing required selectors are `schema_drift`, invalid syntax is `parse`, and a valid empty result is no-match. These are distinct outcomes.
- Titles are normalized with Unicode NFKC and whitespace collapse. Dates are validated as actual calendar dates, then bounded to 1888–2100 before extracting a year. An invalid date is omitted with evidence rather than guessed.
- Missing-year enrichment requires exactly one same-ID fallback. It preserves a good prior title, flags a conflicting alternate title, and never copies fields from a mismatched identity. Failed/empty/invalid fallback data cannot erase the prior candidate.
- Provenance includes raw field values, source/URL/selector or JSON path, adapter version, transformation/validation evidence, response-body SHA-256, source priority, match method, and review reasons. All provenance values are strings to match the .NET dictionary contract. The top-level source remains the primary source; an enriched year's `field.year.source`, `.url`, `.rawValue`, `.bodySha256`, `.identityGate`, and `.fallbackPriority` record its independent origin.
- Fixtures intentionally have no fabricated “retrieved now” timestamp. There is no cache in this slice. Adding one must retain original retrieval/provenance instead of silently making old responses look fresh.

Replay examples:

| Query | Expected behavior |
| --- | --- |
| `DEMO-001` | xcity exact-ID result, no unnecessary fallback |
| `DEMO-002` | xcity not found; JSON adapter succeeds |
| `DEMO-005` | wrong HTML detail ID rejected; JSON adapter succeeds |
| `DEMO-006` | valid HTML title preserved; missing year filled from exact-ID JSON record |
| `The Last Beacon` | two different IDs with same title, both require review |
| `No Such Fictional Film` | completed `no_match` |

Architectural ideas were compared against [Javinizer-Go's field provenance boundary](https://github.com/javinizer/javinizer-go/blob/main/internal/aggregator/aggregator.go), [JavSP's exact-ID/duplicate distinction](https://github.com/Yuukiy/JavSP/blob/master/javsp/web/javdb.py), and [tinyMediaManager's fill-only metadata option](https://www.tinymediamanager.org/docs/movies/settings#metadata-scrape-defaults). These are design references, not copied implementation or blanket endorsements of their identity fallback rules. The new neutral fixtures and implementations are independently authored, with selected MDCx extraction lineage noted above.

## Scheduling, retries, and source health

The shared scheduler defaults to four in-flight transport calls globally, two per hostname, and a 100 ms minimum interval between starts on a hostname. Every attempt, including retries, passes admission. Waits do not hold global transport slots. A host cooldown is rechecked after waiting for a global slot. Cancellation releases permits while queued, in flight, and sleeping.

Read-only GET-shaped requests get at most three attempts, a 10-second per-attempt timeout, and exponential backoff with small jitter when no valid server delay is provided. Only timeout/connection errors, selected transient OS errors, and HTTP 408/429/500/502/503/504 are retried. Generic OS, parsing, validation, schema, access, challenge, 404, and ordinary 4xx errors are not retried.

`Retry-After` supports delta seconds and HTTP-date, case-insensitive headers, and a shared host cooldown. A delay exceeding 60 seconds is surfaced rather than shortened; subsequent work still respects the original cooldown, subject to its own overall deadline. Cancellation can interrupt all async waits. The scheduler bounds bodies to 1 MiB, and the JSON adapter bounds records to 100. A future real transport must enforce the body bound during streaming, before buffering.

Health separates transport attempts (`requests`, `successes`, `failures`, `retries`, `rateLimited`, `inFlight`, `peakInFlight`, `latencyMs`, `lastError`) from parsed matches/no-matches/validation errors (`matches`, `noMatches`, `parserFailures`) and cancellations. HTTP 404 is a transport failure and a no-match, not a broken parser. `successes` means successful HTTP-shaped responses, not accepted metadata; `retries` counts scheduled retries, including a retry later cancelled during backoff.

## Error taxonomy

- Input/lifecycle: `invalid_request`, `unsupported_version`, `invalid_sources`, `request_too_large`, `duplicate_request`, `not_active`, `busy`, `session_full`
- Transport/access: `network`, `timeout`, `http_error`, `rate_limited`, `not_found`, `auth_required`, `challenge`, `policy_blocked`, `response_too_large`
- Metadata: `parse`, `schema_drift`, `validation`, `identity_mismatch`
- Worker: `deadline`, `internal`

Error messages are deliberately safe constants or status codes. Raw response bodies, URLs with query strings, credentials, and exception text never become protocol error messages.

## Transport safety and remaining work

The shipped executable has **no live-network switch or socket implementation**. `FixtureTransport` maps reviewed fixed files and never treats user input as a path. `AccessControlledTransport` is an injectable, fail-closed seam requiring an explicit HTTPS allowlist and a per-request authorization function. Its tests use fake transports; it does not claim to implement robots fetching, TLS, DNS-rebinding defense, or authenticated live access.

Before any real transport is added, review source permission/robots/terms, allowed destinations including resolved IPs, streaming body bounds, TLS verification, redirect policy, authentication boundaries, cancellation, and log redaction. No ambient cookies, browser impersonation, login automation, CAPTCHA bypass, proxy workaround, or anti-bot evasion is included. 401/403, redirects, and recognizable challenge pages are surfaced rather than bypassed.

Tests cover pure parser replay, identity negatives, field fallback, provenance, source priority, explicit ambiguity, safe errors, no sockets, concurrency high-water marks, per-attempt admission, retry policies/cooldowns, cancellation, malformed/oversized JSONL, process streaming, and process EOF. They do not establish live-source accuracy, production performance, all-provider parity, or the full legacy application's compatibility.
