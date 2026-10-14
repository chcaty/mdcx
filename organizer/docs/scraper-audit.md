# Legacy scraper audit and bounded migration scope

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

## Scope of the new worker slice

This section defines the migration boundary, not a claim that all implementation tests have passed. Consult the worker's tests and its run results for implementation verification.

The slice is a standalone Python 3.12, stdlib-only, JSONL v1 worker using local fictional fixtures. Its intended contract includes bounded active work and global/per-host admission, explicit retry and `Retry-After` handling, cancellation, source health, validation, and normalized field-level provenance represented as flattened strings for the C# process boundary.

Primary-candidate selection remains **ordered lazy fallback**. A narrowly scoped enrichment step may consult lower-priority sources only to fill a missing year. Enrichment must pass an exact canonical-identifier gate against the requested identifier and selected primary candidate. A conflicting identifier cannot contribute a year. A valid higher-priority title or year is preserved; enrichment does not override it or launch every source eagerly. Retain the primary title provenance and separately record the year source, source URL, selector/JSON path, and transformation/validation evidence.

Ambiguous title evidence is surfaced through review flags rather than concealed by a confident-looking automatic match. Any match score is a documented heuristic score, **not a calibrated probability**. Fixture cases and tests for missing-date/year fallback, conflicting identifiers, and ambiguous titles are part of the expanded acceptance scope; this audit does not assert that they have passed.

This limited missing-year enrichment is not a complete port of the legacy per-field/language/type reducer. General per-field source priority, multilingual merging, and media-type-specific source selection remain unported.

Architecture inspiration: [javinizer-go's aggregation result](https://github.com/javinizer/javinizer-go/blob/main/internal/aggregator/aggregator.go) separately exposes `FieldSources` and `ResolvedPriorities`, making the chosen field origin distinct from configured priority. The public source was inspected through its [raw source endpoint](https://raw.githubusercontent.com/javinizer/javinizer-go/main/internal/aggregator/aggregator.go) on the audit date. This is a design reference only; no implementation code from that project was copied into this slice.

The concrete parser lineage is narrow and reviewable:

- The HTML adapter draws from the `program_detail_title` and `hinban` selectors in `mdcx/crawlers/xcity.py:12–20`. The original module also contains many other fields and a search-then-detail network flow (`mdcx/crawlers/xcity.py:23–127`, `mdcx/crawlers/xcity.py:149–205`). Those are outside the initial subset.
- The JSON adapter draws from the detail-record extraction in `mdcx/crawlers/theporndb_movies.py:20–102`, including the legacy title/date/duration/artwork structure. It does not migrate the separate filename/search-result matching algorithm beginning at `mdcx/crawlers/theporndb_movies.py:105`.
- Neutral fixtures identify the fictional films `DEMO-001 — The Lantern Map` and `DEMO-002 — Harbor of Stars`. Schema-inspired fixture extraction does not establish permission to access a source or prove its current live schema.
- There is no live socket transport in this slice. The transport boundary is explicit and access is fail-closed. No login, CAPTCHA solving, scraping around access restrictions, or automatic browser fallback is included.
- The legacy application remains untouched. The new worker does not automatically replace `mdcx/web_async.py`, `FileScraper`, the compatibility layer, or existing source registry behavior.

## Remaining work and acceptance gates

1. **Complete deterministic worker verification.** Test ordered fallback and short-circuiting; exact-canonical-ID-only missing-year enrichment; preservation of valid higher-priority title/year; conflicting IDs; ambiguous title review flags and heuristic-score labeling; flattened per-field provenance, including transformation/validation evidence; malformed HTML/JSON; missing required fields; input/output framing; unknown protocol versions; per-source failures; retries; seconds/date/invalid/long `Retry-After`; timeout; cancellation while queued, in-flight, and sleeping; cache provenance; bounded queue/active-task and global/per-host high-water marks; and zero fixture-mode sockets. Exercise more than one host and more than one source.
2. **Integrate the process boundary.** Verify the desktop starts the selected interpreter, drains stderr without deadlock, handles malformed/oversized JSONL, correlates requests, cancels and reaps children, and survives worker failure. Keep stdout reserved for the versioned protocol.
3. **Preserve the full legacy reducer before calling migration complete.** Cover per-field priority lists, media-type source filters, multilingual cache keys, explicit source mode, short-number normalization, field provenance, and thumbnail/actor aggregation with fixture-based differential tests. Do not silently import old configuration and treat candidate-level fallback plus a missing-year enrichment rule as equivalent.
4. **Design real transport separately.** Establish source permissions and request policies, TLS verification, redirect/destination validation, bounded payload sizes, authentication handling, timeouts, pacing, retry budgets, and sensitive-log redaction. Test each network attempt through admission control, including retries. No live access is implied by this audit.
5. **Measure before advertising optimization.** Compare baseline and new behavior on the same synthetic library and source outcomes. Record interpreter/platform, fixture count, active-task and in-flight-request maxima, peak memory, completion latency distribution, retry counts, cancellation latency, and result/provenance equivalence. Keep network latency separate from parser/scheduler time. There are no before/after performance numbers in this audit.
6. **Resolve release prerequisites.** Reconcile documentation with actual runtime targets, retain attribution and license notices, and clarify the licensing ambiguity before any distribution or commercial-use claim.
