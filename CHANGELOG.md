# Changelog

## v0.3.2.4 — field-tested baseline

- Windows 7 SP1 x64 target retained with .NET Framework 4.0 Full, x86.
- Native OPC DA LocalServer built with MSVC v143/ATL for Windows 7 (`6.01` subsystem).
- Removed permanent manual OPC server startup from normal GUI operation.
- Remote COM/DCOM activation uses `AstueMpsOpcDaServer.exe -Embedding`.
- OPC demand-start can launch `AstueMpsReplacement.exe --runtime --opc-demand` when the main application is closed.
- Fixed remote activation failure caused by the previous global single-instance mutex conflict (`0x80080005`).
- Added SET4 model to the add-device wizard with default serial settings `9600 / 8 / Odd / 1`.
- Mercury polling and OPC DA Value/Quality/Timestamp behavior verified on a real Windows 7 installation.
- Repository baseline excludes production projects, site inventories, real plant addresses, logs and dumps.

## Known follow-up work

- Correct explicit `--config` startup semantics.
- Reduce retained TX/RX log volume and batch GUI log updates.
- Continue long-duration stability testing.
- Verify OPC client-count reporting.
- Extend SET4 coverage and testing.
