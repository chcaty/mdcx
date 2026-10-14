# Authored offline request/response corpus

These are fictional, synthetic, uncaptured fixtures. No provider response was downloaded or captured, and none of these films, catalog numbers, slugs, dates or numeric route IDs establishes real provider availability. There are no people, adult imagery, personal media, cookies, accounts or real credentials in the corpus.

Historical provider origins and route strings are retained solely as exact offline request expectations. They are not followed. `FixtureTransport` and `ReplayTransport` have no network fallback. Every production fixture response has `fixture=true`; candidate provenance says `responseOrigin=synthetic_uncaptured`. The worker neither accepts credential fields nor accepts fixture paths over JSONL. Files are eagerly loaded from a fixed code-owned filename allowlist.

## Request shapes and lineage

- xcity: `GET https://xcity.jp/result_published/?q=DEMO001` returns HTML with `table.resultList/tr/td/a` links. A separate `GET https://xcity.jp/avod/detail/?id=1001` returns `span#hinban`, `span#program_detail_title` and an optional release date under `li/span.koumoku`. This follows `mdcx/crawlers/xcity.py:151–205`; the numeric detail URL appears in its line-259 example. Only that bounded numeric route subset is supported. Other routes fail closed.
- TPDB movies: `GET https://api.theporndb.net/movies?q=Harbor%20of%20Stars&per_page=100` returns `data[]` search hints, followed by `GET https://api.theporndb.net/movies/harbor-of-stars` returning a `data` object. This follows `mdcx/crawlers/theporndb_movies.py`'s filename-search and detail branches. Slugs remain opaque provider identifiers. No inspected TPDB field supplies a catalog number comparable to xcity's `hinban`.
- TPDB request objects carry the historical JSON Accept/Content-Type and bearer-auth shape using the conspicuously inert `MDCX_OFFLINE_TEST_MARKER_NOT_A_CREDENTIAL` literal and a fixture credential reference. The User-Agent is intentionally `MDCxOrganizer-OfflineContract/2`, not the legacy browser string. These are injected test metadata, never credentials or authorization to contact a provider.

## Cases

- `xcity-search-001.html` → `xcity-demo-001.html`: numeric provider ID `1001`, catalog `DEMO-001`, The Lantern Map (2024). Nested title text belongs to one required span.
- `xcity-search-002.html` → `xcity-demo-002.html`: numeric provider ID `1002`, catalog `DEMO-002`, Harbor of Stars (2023). This legitimate independent xcity synthetic record preserves the UI demo without turning a TPDB slug into a catalog ID.
- `xcity-search-005.html` → `xcity-conflicting-id.html`: request DEMO-005 resolves to `1005`, but detail catalog is DEMO-099. It is rejected. TPDB cannot provide an ID fallback with unproven catalog evidence.
- `xcity-search-006.html` → `xcity-demo-006.html`: `1006`, DEMO-006, The Quiet Observatory has no release date. Its year stays missing; matching words in a TPDB title are not a cross-provider identity crosswalk.
- `xcity-search-empty.html`: legacy Japanese no-results text.
- `theporndb-search-*.json`: separate `data[]` search documents; `theporndb-detail-*.json`: independent `data{}` detail documents. `theporndb-movies.json` is a complete synthetic search-list parsing example, never a detail response.
- TPDB The Last Beacon has two distinct slugs/years; both remain reviewable candidates. TPDB Harbor of Stars uses display ID `theporndb_movies:harbor-of-stars`, source ID `harbor-of-stars`, and null catalog identity.

A search response's title/date is never accepted as detail metadata. Repeated same-provider search targets are fetched once, but their count and `duplicate_search_results` review reason are retained. Conflicting search hints are not silently promoted to authoritative fields. Unknown safe queries return empty search responses; unknown bounded detail routes return 404. Unsafe or noncanonical URLs, wrong methods/headers/auth references and unexpected replay requests fail closed.

Malformed schemas, wrong details, duplicate IDs, status/auth errors, encoded/unsafe routes, budget truncation and cancellation are authored directly in tests. Add fixture files only after reviewing their provenance and privacy. Do not automatically capture authenticated responses.
