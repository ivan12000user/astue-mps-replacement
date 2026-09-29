# ASTUE OPC DA Server

Win32/x86 **OPC DA 2.05a server and meter polling application** for Windows 7 SP1 and newer Windows systems. It polls supported electricity meters over serial / Moxa RealCOM links and publishes cached values to OPC DA clients. The current field-tested baseline is **v0.3.2.4**.

## Current scope

- Windows 7 SP1 compatible managed application: **.NET Framework 4.0 Full, x86**;
- native x86 OPC DA 2.05a LocalServer;
- COM / Moxa RealCOM transport;
- Mercury 230 family polling (`M230AR`, `M230ART`, `M230ARTP`, `M230ART2P`);
- SET-4TM read-only pilot;
- import of legacy `.mpp` projects (`ZIP/File1`, Windows-1251);
- editable `Server -> Bus -> Device -> SubDevice -> Group -> Tag` tree;
- Value / Quality / Timestamp cache;
- per-line sequential polling, timeout/retry and communication diagnostics;
- deterministic OPC ItemID derived from tree paths;
- remote COM/DCOM activation.

The repository intentionally does **not** contain production `.mpp/.astue` projects, real line/device inventories, plant IP addresses, logs, dumps or credentials.

## v0.3.2.4

The GUI no longer keeps a permanent `--manual` OPC process running. Normal OPC activation is performed by COM/DCOM using:

```text
AstueMpsOpcDaServer.exe -Embedding
```

When an OPC client connects, the LocalServer can demand-start the main application as:

```text
AstueMpsReplacement.exe --runtime --opc-demand
```

This avoids the former single-instance mutex conflict that could result in `0x80080005` during remote activation.

The add-device wizard contains built-in model templates for Mercury and SET4. Selecting SET4 configures `9600 / 8 / Odd / 1` by default.

## OPC DA identity

```text
ProgID: Astue.MpsOpcDa.1
CLSID:  {7B0E174E-31F0-4A2A-9D8E-3C3D952BD901}
```

The OPC server is read-only. OPC clients consume the shared snapshot/cache; they do not initiate meter requests directly. Communication loss is exposed as bad quality rather than stale `Good`.

## Build

On a Windows 10/11 x64 build host:

```cmd
check_win7_native_toolset.cmd
build_and_package_x86.cmd
```

Required for the native Win7 target: **MSVC v143 + ATL v143**. The build scripts audit the native imports and subsystem version.

## Safety

Do not poll the same physical serial line from two masters simultaneously. Before testing a line, stop/disable that line in the existing polling application or otherwise guarantee exclusive COM access.

## Roadmap

1. long-duration stability test under real polling + OPC clients;
2. reduce in-memory TX/RX log retention and batch GUI log updates;
3. verify/fix explicit `--config` startup semantics;
4. extend SET4 coverage;
5. direct TCP transport for terminal servers where useful.

## Independence and trademarks

This is an independent project. Product and model names are used only to identify compatible equipment or protocols. No affiliation with or endorsement by the respective manufacturers is implied.

## License

No open-source license has been declared yet. The source is publicly visible, but reuse, modification and redistribution rights should be clarified by adding a license before encouraging third-party reuse.
