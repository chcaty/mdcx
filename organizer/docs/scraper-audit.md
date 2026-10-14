# Legacy scraper audit and bounded migration scope

Historical implementation/audit record. Later configured-source and portable-delivery changes are documented in [architecture](architecture.md), [validation](validation.md) and [Windows delivery](windows-delivery.md); the scope statements below describe their recorded baseline.

Audit date: 2026-10-14. Baseline: `0188d8559b4d1165048a6a5cc921955b1a44d076`, branch `local/media-organizer-slice`.

This is a source audit of the existing Python implementation, plus a small, isolated control-flow probe. Legacy source files were not changed. File and line references below are repository-relative and refer to that baseline. No live metadata sources, credentials, personal media, or production services were used. This document does not report a throughput benchmark, a full legacy test-suite pass, or live-source compatibility.

## Repository and runtime facts

- The project describes a succession of upstream forks and a substantial refactor (`README.md:5–14`). Preserve that provenance when extracting or adapting logic.
- Contribution instructions use `uv sync --all-extras --dev`, an editable install, and `uv run pytest` (`CONTRIBUTING.md:10–18`, `CONTRIBUTING.md:41–47`). Qt and web startup are separate paths (`CONTRIBUTING.md:20–39`). The worker need not import either UI to parse a fixture.
- The authoritative package declaration requires Python `>=3.13.4` for `os.path.ALLOW_MISSING` (`pyproject.toml:9`). The README still advertises Python 3.9 and says legacy Windows builds can use 3.8 (`README.md:3`, `README.md:24`). Those statements are inconsistent; the new standalone Python 3.12 worker is a separate compatibility target, not evidence that the full legacy application runs on 3.12.
- The legacy package depends on HTTP, browser, parsing, image, Qt, and other packages (`pyproject.toml:10–29`), with FastAPI optional (`pyproject.toml:38–39`). A small stdlib fixture worker avoids installing the whole application solely to exercise scheduler and parser contracts.
- Existing crawler-test infrastructure explicitly has a network opt-in flag (`tests/crawlers/conftest.py:11–13`, `tests/crawlers/conftest.py:29–32`). Preserve offline tests as the default; do not silently enable that flag.

### Licensing review gate

`LICENSE:1–6` identifies GNU GPL version 3. The README says GPLv3 applies and then adds study/technical-exchange-only, no-public-social-promotion, local-law, responsibility, and noncommercial conditions (`README.md:36–44`). The license text also discusses charging for distribution (`LICENSE:22–27`, `LICENSE:195–205`) and additional terms (`LICENSE:343–385`).

These repository statements present an unresolved licensing ambiguity. This audit inventories the statements; it does not decide which additional terms are enforceable or certify a commercial-use entitlement. Keep the existing LICENSE and attribution intact. Before distributing or commercially using a derivative, clarify the intended grant with the relevant rightsholders and obtain appropriate legal review. Do not silently relicense copied or adapted scraper logic under a permissive license.

## Existing architecture and semantics to preserve

The current path is broadly `Scraper` → `FileScraper` → `CrawlerProvider` → a modern or legacy source adapter → shared asynchronous HTTP client. The provider caches source instances and closes them and its browser provider (`mdcx/crawler.py:20–49`). The top-level run closes its provider in `finally` (`mdcx/core/scraper.py:73–77`). These are useful resource-lifetime behaviors, not deficiencies.

The most important constraint is that source selection is already lazy and priority-sensitive:

1. Each field has an ordered source list and a language preference (`mdcx/config/models.py:93–100`). The existing serialized spelling is `site_prority`; any configuration migration must account for it.
2. `_call_crawlers` filters that ordered list against the allowed source set for the media type (`mdcx/core/file_crawler.py:146–151`). Type selection and special-source routes occur before reduction (`mdcx/core/file_crawler.py:320–382`).
3. It requests sources in order for each field, only as needed. Successful responses are cached by `(source, language)`; non-multilingual sources use an undefined-language key, and failed keys are skipped for the rest of that reduction (`mdcx/core/file_crawler.py:158–194`).
4. It accepts the first nonempty field value, records its source and external ID, and stops evaluating lower-priority sources for that field (`mdcx/core/file_crawler.py:210–230`). Image-specific and original-title side effects also exist there.
5. It retains additional thumbnails and actor data from responses already obtained, then deduplicates and completes derived values (`mdcx/core/file_crawler.py:238–256`).
6. The per-source input copy preserves special short-number rules (`mdcx/models/types.py:138–141`); explicit single-source mode has its own processing (`mdcx/core/file_crawler.py:262–304`, `mdcx/core/file_crawler.py:428–449`).

**Do not replace this reducer with an eager `gather` over every source.** That would increase calls, change failure behavior, and potentially change language and per-field provenance semantics. Safe concurrency is primarily across independent media items, with bounded request scheduling inside each item. If later parallelizing source work, preserve configured priority in the chosen value and prove that speculative requests are appropriate before sending them.

## Verified findings

### 1. Retry attempts bypass the per-host rate limiter

Evidence: the limiter is acquired once at `mdcx/web_async.py:111`, before the attempt loop at `mdcx/web_async.py:114`. The loop can issue multiple requests (`mdcx/web_async.py:118–129`).

Consequence: the first attempt is accounted for, but retry traffic is not. Existing default host pacing is five requests per second (`mdcx/web_async.py:18–27`); it is a rate limit, not a per-host in-flight concurrency limit. A retry should pass through the same global/per-host admission and pacing rules as an initial attempt.

Probe: a synthetic 429 followed by 200 performed two transport calls but only one limiter acquisition. This is a control-flow observation, not a measured production overload.

### 2. Transient transport exceptions do not retry

Evidence: each attempt starts with `retry = False` (`mdcx/web_async.py:114–116`). HTTP statuses 408, 429, and 504 set it true (`mdcx/web_async.py:131–137`), but `Timeout` and `ConnectionError` only assign an error message (`mdcx/web_async.py:141–144`). The subsequent `if not retry` exits the loop (`mdcx/web_async.py:149–150`).

Consequence: even with three configured attempts, an initial timeout or connection error ends the operation after one request. Define explicit retryable transport failures and use bounded attempts. Automatically retry only requests whose semantics permit retry, such as the read-only GET operations in this slice; do not generalize that behavior blindly to the legacy POST helpers.

Probe: each synthetic timeout and connection-error case made one transport call despite a successful second response being available in the fake transport.

### 3. Retry-After and broader server-failure classification are missing

Evidence: the only retryable statuses in this path are 408, 429, and 504 (`mdcx/web_async.py:131–137`); the delay is `attempt * 3 + 2` (`mdcx/web_async.py:151–154`). This request method does not read a `Retry-After` header.

Consequence: an explicit server cooldown can be ignored, and a 503 is not retried. A replacement policy should classify transient statuses, support both seconds and HTTP-date `Retry-After`, use cancellable bounded waits, and cap attempts and total duration. If a valid cooldown exceeds the allowed wait budget, return a deferred/failure outcome rather than silently retrying earlier than the server allows. Ordinary authentication/authorization errors, not-found results, and malformed metadata should not enter a generic retry storm.

Probe: a synthetic 429 with `Retry-After: 120` slept for 2 seconds in the fake clock before succeeding; a synthetic 503 stopped after one attempt.

### 4. TLS verification is disabled in the shared legacy session

Evidence: the client constructs `AsyncSession` with `verify=False` (`mdcx/web_async.py:47–54`).

Consequence: the configured session does not use normal certificate verification, weakening server-authenticity checks. A future real transport must verify TLS and fail closed on certificate errors. The fixture-only worker has no network transport, so it cannot be described as having repaired the live legacy client or validated live TLS behavior.

### 5. File concurrency is bounded, but scheduled task count is not

Evidence: the implementation materializes a separate task-argument list (`mdcx/core/scraper.py:128–133`) and passes one coroutine per file to `asyncio.gather` (`mdcx/core/scraper.py:150–158`). A semaphore correctly limits entry into `process_one_file`, but every file is still represented by a scheduled awaitable.

Consequence: queued task overhead grows with the number of media files. The finding is not that every file issues network requests at once: the semaphore and the HTTP session's `max_clients=50` already impose bounds (`mdcx/web_async.py:47–50`). A bounded producer/consumer queue or a bounded set of active tasks can limit scheduling overhead while preserving results. Keeping the input list or result list still costs memory proportional to the number of items; bounded active tasks alone do not make the entire job constant-memory.

No peak-memory or speedup measurement was made. Validate both active-task high-water marks and real memory usage before advertising a scalability improvement.

### 6. The legacy compatibility wrapper discards additional language results

Evidence: the adapter takes the first source dictionary and then its first language dictionary (`mdcx/crawlers/base/compat.py:85–91`). Its own comment identifies sources that may return distinct language data and notes that multiple languages then require multiple requests.

Consequence: useful response data can be discarded, forcing repeated source work when multiple fields require different languages. Preserve all `(source, language)` values when migrating those adapters. The current candidate-level worker does not yet implement this full multilingual reduction; it must not be presented as a fix for that legacy behavior.

### 7. Exceptions become logs instead of structured errors at the legacy boundary

Evidence: `LegacyCrawler.run` catches `Exception`, logs a traceback, and returns a response without assigning `debug_info.error` (`mdcx/crawlers/base/compat.py:47–53`). That field exists (`mdcx/models/types.py:321–337`). The reducer attempts to raise a supplied error and otherwise substitutes a generic empty-result error (`mdcx/core/file_crawler.py:185–188`).

Consequence: the caller can lose a structured distinction between source-not-found, parser failure, and other errors even when the traceback remains in logs. The worker contract should separate transport, policy, parsing, validation, cancellation, and not-found outcomes, with safe source health counters. Do not emit credentials or full sensitive response bodies into JSONL events or logs.

### 8. Existing provenance is useful but narrower than the new contract

Evidence: source IDs and external IDs already exist on crawler results (`mdcx/models/types.py:299–307`); the reducer stores a source string for each selected field (`mdcx/core/file_crawler.py:215–219`, `mdcx/models/types.py:356–359`).

Migration opportunity: retain these facts and add explicit adapter/version, retrieval time, and the fixture or response origin where appropriate. The worker contract uses flattened string-valued provenance for the C# boundary, including each selected field's source, URL, and selector or JSON path, together with transformation and validation evidence. A year filled from a lower-priority source must name that source independently of the primary title source. Do not claim the old implementation has no provenance. A cached result must retain its original retrieval information rather than acquire a misleading fresh timestamp merely because it was read from cache.

## Reproduction record

The isolated probe ran under Python 3.12.14. It parsed `mdcx/web_async.py` as an AST, extracted `AsyncWebClient.request`, removed signature annotations to avoid third-party dependency imports, and executed the otherwise unchanged method with fake URL parsing, responses, exceptions, limiter acquisition, and asynchronous sleep. It neither imported the legacy package nor opened a socket. Fake responses supplied `Retry-After: 120`, and `self.retry` was 3 in every case.

| Synthetic outcomes | Transport calls | Limiter acquisitions | Recorded sleeps | Result |
| --- | ---: | ---: | --- | --- |
| 429, then 200 | 2 | 1 | `[2]` | Success |
| Timeout, then available 200 | 1 | 1 | `[]` | Failure |
| ConnectionError, then available 200 | 1 | 1 | `[]` | Failure |
| 503, then available 200 | 1 | 1 | `[]` | Failure |

These probes establish the specific control flow above. They do not test curl-cffi integration, DNS, actual TLS, real cancellation latency, or live site responses. No full legacy environment was installed or tested during this audit.

## Corrected historical-contract slice (2026-10-14)

The standalone Python 3.12 stdlib worker uses **authored synthetic, uncaptured** request/response fixtures. Historical provider hostnames are literal replay expectations only. No network sockets, live provider responses, credentials, captured imagery or real media are used. The original legacy application and its 38-provider registry remain untouched.

### Three different identities

The prior initial fixture used catalog-shaped TPDB slugs and read `data[]` search rows as if they were detail records. That conflated provider lookup identity, display number and catalog identity. It is corrected here, not defended as legacy compatibility:

- Internal `ProviderIdentity(source,value)` is opaque. xcity uses a numeric detail route ID; TPDB movies uses the exact slug.
- `CatalogIdentifier(namespace,value)` is independently evidenced and nullable. xcity's `span#hinban` yields namespace `xcity:hinban`. No inspected TPDB detail field supplies a proven catalog number in that namespace. TPDB's legacy `get_number(series,release,title)` derives a display label, not a shared catalog crosswalk.
- Existing candidate `id` is a display/review key: xcity `DEMO-001`; TPDB `theporndb_movies:harbor-of-stars`. Additive top-level `sourceId` and `catalogIdentifier` object/null keep source lookup and catalog meaning separate. Flat provenance has `identitySchema="2"` and both provider identity components so existing review persistence retains the evidence. JSONL framing stays v1; the upgraded client rejects candidates without the new identity contract, so upgrade worker and client together.

Catalog normalization applies Unicode NFKC and narrow case/separator equivalence within an explicit namespace. Leading zeroes and edition suffixes remain significant. Opaque source IDs are not normalized. TPDB source slugs or titles resembling `DEMO-001` cannot satisfy a bare catalog query; this adapter skips unsupported catalog-shaped queries without requests.

Primary selection remains ordered lazy fallback. Generic missing-year enrichment requires an explicit same namespace and canonical value, and cannot overwrite a valid prior title/year. **The two actual migrated adapters provide no comparable cross-provider identity evidence, so neither fills the other's fields.** The DEMO-006 year remains missing. The generic fill-only rule is tested using clearly named fake providers with an explicit common test namespace; that test is not a real crosswalk or provider parity claim.

### Separate search and detail contracts

- xcity's historical search URL is `https://xcity.jp/result_published/?q=` plus the number without hyphens (`mdcx/crawlers/xcity.py:151`). The search parser extracts `table.resultList/tr/td/a` links, as the crawler does at line 166, then requests a separate detail document. The bounded numeric route `/avod/detail/?id=...` is evidenced by the crawler's line-259 example; it is a safe supported subset, not a complete inventory of historical routes. Required detail fields use `span#program_detail_title` and `span#hinban`; a release label uses `li/span.koumoku`. Repeated required span IDs are rejected instead of concatenated.
- xcity's numeric provider identity is response-route-bound, because the inspected legacy detail fields do not independently prove its numeric route ID. Optional canonical-link consistency is extra hardening when present, not required legacy schema or captured evidence. The catalog detail field is checked independently against a catalog query.
- TPDB movies uses `/movies?q=...&per_page=100`, a JSON search `data` array, followed by `/movies/{slug}`, a detail `data` object (`mdcx/crawlers/theporndb_movies.py` filename-search and detail branches). The returned detail slug must equal the selected search slug exactly. Candidate title/date provenance uses `data.title` / `data.date`, never a search row. Hash lookup, filename/date/actor matching, translations and other detail fields remain unported.
- Exact source, GET method, URL, headers and credential reference are validated. TPDB has historical JSON/bearer header shapes with an explicitly inert test marker and a fixture credential reference; its User-Agent is intentionally modernized to `MDCxOrganizer-OfflineContract/2`. No real credentials, ambient config or browser impersonation are involved. JSONL has no credential, URL or fixture-path input fields.
- `FixtureTransport` maps only a fixed local allowlist after route validation. `ReplayTransport` additionally validates the exact ordered request script and asserts that all expected requests were consumed. Unsafe/off-origin routes, userinfo, fragments, ports, extra/duplicate query keys, malformed encoding, encoded path separators and unexpected headers/methods fail closed. Query text is canonically encoded and cannot become a destination or file path.

### Bounded selection and honest evidence

A search may contain unrelated hits. One known wrong catalog detail cannot hide a later exact match. Per-record schema/identity errors preserve other candidates. Incomplete details leave review evidence; authentication, challenge, policy or exhausted rate-limit access stops further targets for that provider instead of repeating denied access.

Repeated same-provider search IDs are fetched once but preserve duplicate count and `duplicate_search_results`. Distinct same-title identities remain separate and reviewable. The matcher inspects at most ten unique details per source. If the budget is reached with more targets, it emits `search_truncated` and flags retained candidates `search_results_truncated`; zero accepted candidates under truncation cannot be reported as a clean verified no-match. Search responses are bounded to 100 records and response bodies to 1 MiB. These are resource bounds, not throughput measurements.

Every fixture candidate retains synthetic/uncaptured origin, adapter/identity revision, detail and search hashes, source IDs/catalog namespace, source/field URLs, selectors or JSON paths, transforms, validation, priority and review reasons as flattened string provenance. No fabricated current retrieval timestamp or cache freshness is added. Scores remain deterministic heuristics, not calibrated probabilities.

The neutral demo includes a genuine separate **synthetic xcity** DEMO-002 record to preserve the existing second-item UI smoke. It no longer depends on treating TPDB's `harbor-of-stars` slug as DEMO-002. Fictional TPDB titles still exercise its explicit provider selection and separate search/detail flow.

The expanded offline suite covers exact methods/headers/routes, separate wrappers, empty and malformed results, Unicode/edition/zero identity negatives, duplicate search evidence, wrong detail catalog IDs/slugs, unsafe/encoded URL negatives, auth/status failures, source selection, bounded details, cancellation during detail, parser/transport counter separation, retained provenance, and zero socket access. See the worker tests and final verification run for current results; this audit is not live validation or an all-provider migration claim.

This slice still does not preserve the complete legacy per-field/language/type reducer, short-number routing, image/actor side effects or all scraper output fields. Architecture comparison references retained from the earlier audit are design inspirations only; no external scraper implementation was copied.

## Remaining work and acceptance gates

1. **Complete deterministic worker verification.** Test ordered fallback and short-circuiting; explicit-namespace-and-value-only missing-year enrichment with test providers; no unproven TPDB/xcity merge; preservation of valid higher-priority title/year; wrong detail identities; separate search/detail envelopes and exact offline request expectations; ambiguous title review flags and heuristic-score labeling; flattened per-field provenance, including transformation/validation evidence; malformed HTML/JSON; missing required fields; input/output framing; unknown protocol versions; per-source failures; retries; seconds/date/invalid/long `Retry-After`; timeout; cancellation while queued, in-flight, and sleeping; cache provenance; bounded queue/active-task and global/per-host high-water marks; and zero fixture-mode sockets. Exercise more than one host and more than one source.
2. **Integrate the process boundary.** Verify the desktop starts the selected interpreter, drains stderr without deadlock, handles malformed/oversized JSONL, correlates requests, cancels and reaps children, and survives worker failure. Keep stdout reserved for the versioned protocol.
3. **Preserve the full legacy reducer before calling migration complete.** Cover per-field priority lists, media-type source filters, multilingual cache keys, explicit source mode, short-number normalization, field provenance, and thumbnail/actor aggregation with fixture-based differential tests. Do not silently import old configuration and treat candidate-level fallback plus a missing-year enrichment rule as equivalent.
4. **Design real transport separately.** Establish source permissions and request policies, TLS verification, redirect/destination validation, bounded payload sizes, authentication handling, timeouts, pacing, retry budgets, and sensitive-log redaction. Test each network attempt through admission control, including retries. No live access is implied by this audit.
5. **Measure before advertising optimization.** Compare baseline and new behavior on the same synthetic library and source outcomes. Record interpreter/platform, fixture count, active-task and in-flight-request maxima, peak memory, completion latency distribution, retry counts, cancellation latency, and result/provenance equivalence. Keep network latency separate from parser/scheduler time. There are no before/after performance numbers in this audit.
6. **Resolve release prerequisites.** Reconcile documentation with actual runtime targets, retain attribution and license notices, and clarify the licensing ambiguity before any distribution or commercial-use claim.
