# ADR-0026: Tape export Drive uses the drive.file scope

**Status:** Accepted · **Date:** 2026-10-24 · **Deciders:** Adam (operator) · PR #765
**Relates to:** PRD `R-10.6`, `R-10.7` · gh#755

## Context

The scheduled tape export copies a finished UTC day to a volume and, when configured, to a Google Drive
folder. The copy is additive. Local rows stay, older generations stay, and a later retention decision is
a different ADR.

The tempting credential is the full Drive scope. It can list every file the service account can see and
replace one by name. That is more access than an export needs, and a name is not an identity: two
processes can create two files with the same display name.

## Decision

`GoogleTapeDriveClient.OAuthScope` is `https://www.googleapis.com/auth/drive.file`
(`DriveService.Scope.DriveFile`). The client requests no other Drive scope.

One export object is one Drive file:

- The identity is `TapeDriveIdentity` (day, kind, format, venue, instrument, contract, resolution), stored
  as `appProperties` (`tapeDay`, `tapeKind`, `tapeFmt`, `tapeVenue`, `tapeSymbol`, `tapeContract`,
  `tapeRes`). Lookup uses those properties.
- The receipt stores the Drive file id. A later pass updates that id in place.
- A missing id is looked up by the properties. The first id in lexicographic order is updated. Extras are
  not trashed.
- The client does not call files.delete and does not set `trashed` to true.

A failed upload records the exception type and the HTTP status on the receipt, in the log, and nowhere
else. The provider message is not copied. Permanent failures (the credential cannot be read, HTTP 400,
401, 403, 404) are not retried. Transient failures use exponential backoff with jitter, or the
response's `Retry-After` when it is present, capped at 30 seconds. The whole upload, retries included,
stops at a two-minute deadline, including a wait already in progress. That wait records `Timeout`
instead of throwing. The Google client's built-in 503 backoff is disabled so it does not stack. Two
processes serialise one identity with a session `pg_advisory_lock` on one direct connection for the
pass. The connection is not pooled, sets `lock_timeout` and TCP keepalive, and is reopened if it
drops. The lock is released in a `finally`. The pending receipt commits before the upload. The file
id commits after. A day folder stores `appProperties` and is re-listed after create, so two creators
converge on the first id.

Prefer `TapeExport__Drive__ServiceAccountJsonPath`, a file mounted into the container. Inline
`TapeExport__Drive__ServiceAccountJson` is visible to anyone who can inspect the container. Neither value
is committed, logged, or copied into an exception.

## Alternatives considered

**Full `drive` scope.** It would see a folder a person created and then shared. It would also see and
change every other file that account can open. Rejected. The export creates and updates its own files.

**Replace by display name, or trash and create.** A crash between trash and create drops the last good
file, and two concurrent creates leave two files. Rejected. Update in place, and keep the stored id when
the new attempt fails.

## Consequences

`drive.file` sees files this application created. A folder id from a person's My Drive that was only
shared with the service account can be invisible under this scope. Create the folder with the service
account (or use a folder id this app already created) and set `TapeExport__Drive__FolderId` to that id.
Widening the scope is a new ADR, not a config change.

The scope is not an environment variable. The seven `TapeExport__*` names and their defaults are unchanged.

## Follow-ups

None. Retention of local tape stays out of scope (`R-10.7`).
