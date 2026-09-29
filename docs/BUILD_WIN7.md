# Windows 7 build notes

The managed application targets .NET Framework 4.0 Full, x86.
The native OPC DA LocalServer targets Windows 7 SP1 and is built with MSVC v143 + ATL.

Build on a Windows 10/11 x64 host:

```cmd
check_win7_native_toolset.cmd
build_and_package_x86.cmd
```

Expected audits include:

```text
PASS: AstueMpsReplacement targets .NET Framework 4.0 Full.
PASS: GetSystemTimePreciseAsFileTime is NOT statically imported.
6.01 subsystem version
```

Do not commit production `.mpp`/`.astue` projects, live inventories, logs, dumps, credentials or site addressing.
