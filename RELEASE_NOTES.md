## Summary

- Bumped version to 4.0.4
- Moved distribution into 2 sub: Bundled and Lite
- Bundled is the default with .net 10 Bundled in
- Lite is an installer without .net 10 bundled in, which requires .net 10 installed.
- Fixed issue regarding background service
- 4.0.4 RC3: Fixed background-service error 5 handling, added repair controls, and checks the service before agent installation.
- 4.0.4 RC4: Fixed background-service installation for Intune/Entra ID email accounts using cloud user SIDs.

## Impact

Existing installations default to Bundled. A manual Lite install writes a protected edition marker under ProgramData; future manager updates request only the Lite asset. Bundled installations continue requesting BeszelAgentManagerSetup.exe.

Local installer sizes:

- Bundled: 84.7 MB
- Lite: 36.3 MB
