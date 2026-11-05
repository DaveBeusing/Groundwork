# Windows platform host

WP01-L1 provides one Win32 top-level window. The composition root constructs a `WindowsHostModule` with `WindowsHostOptions`, the runtime's `UiWake` and its `RuntimeOwnership`. It initializes the module with the accepted `EngineModuleContext` and awaits `StartAsync` before enabling simulation. The module implements `IInputSource` and `IHostWindowStateSource`, so consumers use Core types only.

The width and height options set initial outer-window dimensions in pixels. `HostWindowSnapshot` reports the current client area. Its revision advances when size, focus or phase changes. Minimized dimensions are zero. Close requests latch terminal runtime stop and prevent reopening. The host accepts one initialization and startup, then stops and disposes idempotently.

The module owns one STA thread, native window, and registered class. The borrowed UI wake belongs exclusively to the window pump and signals terminal intent. The thread waits in `MsgWaitForMultipleObjectsEx` when idle and handles at most 64 messages before testing the wake again. It destroys the window and unregisters the class on its owner thread. Ownership stays registered until native cleanup and borrowed wake access end. If native release fails, the runtime retains custody and `Failure` exposes the primary and cleanup errors separately.

The input producer runs only on the window thread. The simulation consumer captures an immutable snapshot of the retained transitions and current held controls. Keyboard repeat does not add another press. Mouse button capture follows held buttons and capture loss releases them. Focus loss and close clear held controls and pointer position. A full transition buffer drops further transitions until capture and reports the gap while still updating authoritative held state. Event sequences are strictly increasing within a host, including dropped transitions.

The stop wait uses the common deadline and the caller's wait token. For an injected clock, its last trusted input sample and a real-time cap bound teardown without invoking clock code during the wait. An expired wait reports incomplete cleanup while the owner continues its existing teardown. This keeps the runtime from releasing the window or its wake early.

## Verification

Run the lane's locked project restore and normal tests on Windows. Run the smoke category separately on an unlocked interactive desktop. The smoke owns a uniquely named window and checks resize, focus loss and reacquisition, minimize/restore, native close, class unregistration, and retained-wake shutdown while idle. The handle-count delta is recorded as an observation because the runner can allocate unrelated handles.

```powershell
$project = 'tests/Groundwork.Platform.Windows.Tests/Groundwork.Platform.Windows.Tests.csproj'
dotnet restore $project --configfile NuGet.Config --locked-mode
dotnet test $project -c Release --no-restore --filter 'TestCategory!=WindowsDesktopSmoke'
dotnet test $project -c Release --no-build --no-restore --filter 'TestCategory=WindowsDesktopSmoke'
```
