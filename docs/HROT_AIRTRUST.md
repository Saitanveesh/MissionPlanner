# HROT AirTrust — Mission Planner native integration

This is an **extension of the actual ArduPilot Mission Planner fork**, not a replacement web dashboard.

- Upstream: https://github.com/ArduPilot/MissionPlanner
- HROT fork: https://github.com/Saitanveesh/MissionPlanner
- Native plugin source: [plugins/HROT_AirTrust.cs](../plugins/HROT_AirTrust.cs)
- Plugin API: [Plugin/Plugin.cs](../Plugin/Plugin.cs)
- Existing upstream example: [Plugins/OpenDroneID2/OpenDroneID_Plugin.cs](../Plugins/OpenDroneID2/OpenDroneID_Plugin.cs)

## Implementation

- **HROT TRUST** button on Mission Planner's own main menu.
- **Flight Data → HROT AirTrust** native WinForms page with Mission Planner theme.
- **Observed aircraft** table, mission permits, credential registry, incident desk,
  sensors and evidence ledger.
- **Real Flight Data map overlay** shows only non-demo observations delivered by
  an external adapter. These are *not* necessarily independently authenticated sensors.
- Local Core API operations: public-key enrollment, fresh challenge and Ed25519
  signature verification, permission approval, incident acknowledgement, SHA-256
  local evidence-chain check.
- Shared flight-control screens remain unchanged. Plugin **never arms, disarms,
  sends MAVLink commands, or assumes access to contractor drone telemetry**.

## Important architecture distinction

Mission Planner is the native host and UX foundation.
The HROT Core backend still needs to run separately as a local observer/trust service
at \`http://127.0.0.1:8765\`. Source for the existing research-grade Core:
https://github.com/Saitanveesh/GroundStation

No standalone browser interface is required for day-to-day HROT UI. You can leave the
browser dashboard closed. The backend service is kept so that sensor/credential
verification does not require MAVLink access.

**The extension requires real external sensor input to show non-demo tracks.**
With an empty or unavailable Core, the native plugin intentionally displays zero
observations, not fake dots or a fabricated detection PASS.

## Install / first Windows trial

Requires an existing working copy of **Mission Planner** on Windows and its
plugin loader. No flight controller is required for this initial UI/identity test.

1. Close Mission Planner.
2. Open PowerShell.
3. Run the commands below **after editing** \`$mp\` to the folder that contains
   your actual \`MissionPlanner.exe\`.
4. Start Mission Planner; allow the native C# source plugin loader to compile.
5. Click **HROT TRUST** in the top bar or open **Flight Data → HROT AirTrust**.
6. In a second terminal, run the existing HROT Core service locally if you want
   live registry, evidence and adapter observations.

\`\`\`powershell
$mp = "C:\Program Files (x86)\Mission Planner"   # CHANGE to your actual install
New-Item -ItemType Directory -Force "$mp\plugins" | Out-Null
Invoke-WebRequest "https://raw.githubusercontent.com/Saitanveesh/MissionPlanner/feat/hrot-airtrust-native/plugins/HROT_AirTrust.cs" -OutFile "$mp\plugins\HROT_AirTrust.cs"
\`\`\`

If the HROT branch is merged into \`master\`, substitute \`master\` for
\`feat/hrot-airtrust-native\` in the raw URL.

If Mission Planner reports a plugin compile error, open its plugin error
viewer (Ctrl+P on supported builds), capture the exact error, and check the
running program's version. Do **not** modify the aircraft firmware to resolve
GCS plugin errors.

To test key verification with a software signer, follow the Core's:
https://github.com/Saitanveesh/GroundStation#3-real-ed25519-test--without-any-aircraft

If using the Core in access-controlled mode, set \`HROT_ADMIN_TOKEN\` in the
environment of the process launching Mission Planner. The Core itself must be
started with the same token. Use only localhost, not an exposed public port.

## Security and research scope

| Feature | Status |
|---|---|
| Original Mission Planner native UI / map reuse | Implemented in source |
| Externally observed tracks on original map | Implemented in source, awaiting physical input |
| Locally registered Ed25519 key verification | Via existing local Core |
| PUF reconstruction on STM32 | Separate laboratory work; not claimed by this plugin |
| Hardware origin / secure-element attestation | Not implemented |
| Actual DRIP / RF receiver driver | Not implemented |
| Authentic physical airframe binding | Not implemented |
| Automatically correlating external tracks | Not implemented |
| Production-grade trust federation and revocation | Not implemented |
| Signed, externally anchored forensic log | Not implemented |
| Field-tested Windows build | Pending Windows validation |

**Do not interpret a successful signature as proof that a particular track contains
the signer.** Identity validity, physical association and mission authorization
remain distinct states.

## Coding and testing

This fork's HROT changes should stay on a branch and merge only after
review and a real Windows plugin loader smoke test. The source file is
top-level under \`plugins/\` because \`PluginLoader.LoadAll()\` enumerates
\`plugins/*.cs\` and compiles those files.

The fork is very large. To verify the upstream code independently, clone with
\`git clone --recurse-submodules https://github.com/Saitanveesh/MissionPlanner.git\`,
then use the project's own build instructions. No Mission Planner full-binary
build success is claimed by the plugin's source-only changes.
