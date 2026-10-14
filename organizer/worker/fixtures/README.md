# Sanitized replay corpus

These are authored, fictional fixtures, not downloaded site content. They contain no people, explicit content, cookies, account data, images, or credentials. The shape of the HTML identifiers and JSON fields follows the legacy parser rules cited in `../mdcx_worker/adapters.py`. Every URL uses a reserved `.invalid` host. `Response.fixture` and candidate provenance explicitly identify replayed data.

- `xcity-demo-001.html`: fictional DEMO-001, The Lantern Map (2024), nested title markup and a Japanese release label.
- `theporndb-movies.json`: fictional DEMO-001 and DEMO-002, Harbor of Stars (2023); demonstrates lazy fallback and ordered preference.

The fixture transport only maps fixed manifest entries; it never treats a query as a filesystem path. Unknown queries produce no match. Malformed/schema-shift/challenge/status bodies are generated in tests. To add a fixture, author or sanitize it first, review for private data, add an explicit manifest route and a parser test. Do not record live authenticated responses automatically.

Additional quality cases:
- `xcity-conflicting-id.html`: DEMO-005 request resolves to mismatched DEMO-099 HTML; none of those fields may merge.
- `xcity-demo-006.html`: The Quiet Observatory has no release date; same-ID JSON supplies 2021 with independent year provenance.
- JSON DEMO-003/DEMO-004 share the fictional title The Last Beacon; title search must expose both for review.
- JSON DEMO-005: The Mountain Letters, used after rejecting the mismatched HTML record.
