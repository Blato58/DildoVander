# Account archive collection

`GET /app/v1/archive/collection` returns a private, bounded page of keepsakes
owned by the signed-in account or explicitly linked to it. The route uses the
existing live account-session authorization and returns `Cache-Control: no-store`.
It remains available when keepsake generation is disabled; reading a collection
does not queue work or change a keepsake.

The query accepts one each of `limit` (1–50, default 25), `cursor`, `q` (trimmed,
maximum 80 characters), and `access` (`all`, `owned`, or `linked`). Repeated,
malformed, or out-of-range parameters return 400. The protected continuation
cursor binds the account, page size, search, access filter, source high-water
marks, and last scanned ordering key. Rows linked or created after page one are
excluded from that traversal. Revoked, unlinked, deleted, or ownership-changed
rows disappear as pages are read; the endpoint does not restore historical
access or include them from the captured watermark.

The response shape is `{ "schemaVersion": 1, "items": [], "nextCursor": null }`.
Each item contains exactly:

```json
{
  "locator": "00000000-0000-4000-8000-000000000001",
  "linkedAt": 1792700000,
  "officialName": null,
  "storyTitle": null,
  "hasPortrait": false,
  "accessKind": "originalOwner",
  "canManage": true,
  "canReadPrivate": true,
  "canUnlink": false,
  "readiness": {
    "state": "pending",
    "reason": "awaiting_result",
    "canRetry": false,
    "shouldPoll": true,
    "hasStory": false,
    "hasArtwork": false
  },
  "characterName": null,
  "eveningAt": null
}
```

An immutable original owner has `accessKind: originalOwner` and owner
capabilities. An explicit association has `accessKind: linkedViewer`; it may
read the safe preview and readiness, and may unlink, but receives no owner
capabilities or captured character/session details. An exact original-owner
binding takes precedence over a duplicate explicit link. The optional character
name and Unix-second `eveningAt` come only from that owner's captured customer
session; linked viewers always receive null for both. A deleted captured profile
suppresses its character name.

The account list UI uses the Czech locale by default and supports English. It
polls the first page every five seconds for up to twelve polls while online,
pauses expanded pages for manual refresh, and caps the visible list at 200 rows. Search and access filters reset
the current rows and cursor; sign-out, account-context change, or going offline
clears the private list. Existing detail routes remain the detail view, and only
explicit linked-viewer rows can be unlinked from the account list. No collection
action starts generation.

The collection stores monotonic singleton source revisions for immutable owner
bindings and explicit account associations. Existing rows are preserved and
backfilled by the startup schema path or the generated `AccountDatabase`
migration. The revisions contain no account identifier and are not reset when a
link is removed. Owner queries use a bounded ordering index. This is a compact
archive preview, not an account-data export or bulk story/history endpoint.

Apply the identity migration through the existing [account migration procedure](account-setup.md).
It preserves existing associations and creates the source counter in one SQLite
transaction. The existing consistent-backup and newer-schema checks still apply;
returning to an older binary requires the compatible schema or recovery procedure
documented there. The keepsake database adds its ordering metadata through its
existing startup schema path. Local migration verification does not apply this
change to a production database.
