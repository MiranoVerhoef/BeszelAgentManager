## Summary

- Bumped manager, helper, and installers to 4.0.9.
- Fixed View fingerprint reading the signed-in user's fingerprint instead of the LocalSystem agent service's fingerprint (#61).
- Routed fingerprint viewing through the secured background-service broker and serialized it with Reset.
- Made View and Reset use the service's applied NSSM environment and working directory, including custom `DATA_DIR`, rather than saved but unapplied settings.
- Included PR #60's NSSM UTF-16 output fix, preserving non-ASCII environment values and paths during settings verification (thanks @mews-se).
- Added regression tests for SYSTEM-profile environment selection, custom data directories, and Unicode output decoding.

## Impact

Existing agent data and fingerprints remain in their current locations; no `DATA_DIR` migration is performed. Fingerprint viewing no longer runs the agent in the signed-in user's profile. The official agent command may initialize a missing fingerprint in the service's data directory, as it does during agent startup. Services manually changed to a non-LocalSystem account are rejected by fingerprint actions to avoid showing the wrong identity.

Settings such as `SYSTEM_NAME=Bärbar` and filesystem paths containing non-ASCII characters now survive NSSM readback verification instead of incorrectly triggering rollback. Other Windows tools retain their existing output decoding.

Bundled and Lite installers retain edition-aware updates and SHA-256-aware incremental file replacement.
