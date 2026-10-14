# Synthetic JavBus catalog-contract fixtures

These are neutral fictional, authored, uncaptured HTML documents. They copy only
structural selectors used by `mdcx/crawlers/javbus.py`: the first h3/title,
catalog-label span and sibling/parent text, release-label span.header, and search
anchors with the movie-box class. No live source was requested or verified. None
of the titles, dates, or identifiers asserts an online record exists.

The minimal standalone provider first requests a canonical catalog detail route.
Only a genuine transport HTTP 404 can trigger the historical censored search
route. Search locators never substitute for the catalog field in detail HTML.
Lowercase/underscore/no-separator detail routes are preserved as source-scoped
route IDs; catalog identity is separately derived from the detail label.
Multiple distinct matching search routes are rejected as ambiguous.

The corpus contains ordinary nested-label fields, alternate title/date shapes,
wrong/missing/ambiguous catalog IDs, one exact search target, ambiguous routes,
and an explicit structurally empty search page. Tests add malicious, malformed,
non-UTF-8, and challenge variants in memory. There is no image, performer, media,
credential, browser, anti-bot, or external-request fixture. The BF-658 test checks
only an expected request URL with a synthetic error response, never a metadata
record or real-world availability claim.
