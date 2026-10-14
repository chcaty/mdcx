# Approved native workbench integration

Preserved source baseline: `b9e4309215eed092388611a48963d743783b9955`.
The 1440×900 refined concept was approved on 2026-10-14. This revision implements that direction with native Avalonia controls, reusable color/type styles, vector icons and locally drawn neutral fixture artwork. It is not a bitmap/web view of the concept.

## Implemented behavior

- Sidebar source/status navigation; virtualized media list; independent focused row and checked batch; compact metadata inspector; naming draft; collapsed task logs.
- Hidden checks survive filtering. The header toggles only visible rows; clear removes the whole batch. An unchanged rescan preserves session drafts/checks/caches; changed media-group snapshots clear them.
- Identification processes up to 50 checked items sequentially. It caches candidates without accepting them. A larger batch is rejected before partial work starts.
- Sequential review fixes the intended checked-row queue. Confirmation affects only the current queued item; failures, unresolved ambiguity and out-of-queue focus do not silently advance or accept another item.
- Catalog/title search and provider choice exercise the two offline adapters correctly. Provider IDs and namespaced catalog evidence remain separate after persistence; manual sorting IDs do not become source catalog evidence.
- Unreviewed naming drafts and reviewed-only stored plans are distinct. Both use the same collision/staleness/path safety planner; neither creates directories or alters media.
- Editing withdraws persisted acceptance. Undo revalidates media and sidecars. Rematching preserves locked existing review fields. Generation guards and tracked pending writes prevent candidate switches or close-after-commit races from reviving unapplied reviews.
- Full source/target/media paths remain accessible. Subtitle groups are shown together. Actual headless pointer/key tests exercise checks, search, tab navigation, preview navigation, edit/query expansion and logs.

## Responsive and visual verification

The normal 1440×900 view shows all four synthetic media rows and the complete naming draft. Smaller windows and larger text retain bounded list/inspector scrolling; when the center cannot fit at a readable height it gains an outer vertical scroll instead of overlapping rows. Screenshots cover 1440×900, 1200×800, 125% text, the combined small/large-text case, empty state and reviewed-only preview. A separate 1,000-row synthetic long-path render checks clipping and virtualization behavior, not a performance benchmark.

The static concept's exact fictional titles/statuses are not forced into the runtime. Runtime screenshots display the actual offline parser results, label fixture artwork, and do not invent ambiguity or live metadata to imitate the design.

## Compatibility and remaining limits

SQLite schema remains version 1. New reviews carry a media-group change-detection stamp and unique confirmation ID. Older preview-version reviews lacking that stamp are retained in storage and visibly require rechecking. This is a conservative compatibility choice, not silent reuse of uncertain acceptance.

The installed Avalonia.BuildServices package documents its own build-time telemetry opt-out, separate from the .NET SDK's. Check scripts now set both `AVALONIA_TELEMETRY_OPTOUT=1` and `DOTNET_CLI_TELEMETRY_OPTOUT=1`; a preflight refuses the Avalonia statistics target unless the actual environment opt-out is present. Earlier builds had only the .NET setting, so absence of earlier anonymous build telemetry cannot be established retrospectively. No runtime media/provider telemetry is added by this UI.

This remains a Linux-tested source candidate with offline synthetic metadata and no file executor. Real Windows/macOS chrome, native folder dialogs, clipboard integration, installer/signing, current live-provider accuracy and crash-safe execution are not verified or delivered. Bounded close cleanup does not substitute for a crash-recovery journal. Full-library streaming/index throughput remains future work.
