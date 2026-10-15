# Twin v17 deployed microphone verification

User authorized VPS deployment on 2026-10-15. Source is merged PR202.
See provenance.json for exact build and input identities, deploy-proof.json for
activation/health preservation, twin-release.json for every deployed file hash,
and browser-media-result.json plus post-test-proof.json for actual final checks.
Screenshots show the completed fake conversation at desktop/mobile sizes.

The standard installer refused the current Nginx layout before activation.
The scoped fallback retained the existing verified routing/unit, privately backed
up configuration, atomically switched config/current, restarted Twin and verified
exact HTTPS viewer bytes plus both services. Rollback was reviewed, not injected.
No generic installer compatibility change is claimed.

The standalone verifier requires an external private integration credential and
writes a private original-settings backup outside this artifact directory. It
asserts fake provider configuration before starting fake browser media, delays
only the first microphone fetch600ms and records the visible active warning via
MutationObserver. It always stops, inspects to idle, restores settings with the
latest revision, verifies readback, releases control and closes the browser.
Never copy credentials, settings backups, transcripts or PCM into this directory.

Earlier harness attempts are disclosed in the browser receipt. The final focused
run passed. Optional active reload/UI Stop checks were disabled. Completed reload,
cleanup Stop/inspect/idle and subsequent independent original-settings/native-state
readback passed. No real provider call, acoustic, physical or fresh CAD motion
acceptance is claimed. Existing full v16 software evidence remains dated v16.
