# WP02 D3D12 renderer contract

**Status** Proposed for review. The package graph, public surface and initial
limits below require owner acceptance before implementation.

**Baseline** The [WP01 handover](../wp01-handover.md) records the clean Windows
matrix, the accepted runtime contracts and WYU's immutable engine pin
`0.1.0-dev.42137008200.1`. Preserve the verified no-renderer startup and shutdown
path while introducing an opt-in renderer.

## Milestone and dependency boundary

Open one native Windows window, clear and depth-render a small 3D scene,
present it, and keep input and fixed-step simulation responsive through
resize, minimize, restore and close. The scene is a built-in unit cube with a
camera and solid tint. Windows 11 x64 is the initial qualification matrix.
Broader OS and GPU support requires evidence. Asset loading, terrain, physics,
gameplay, multi-window, HDR, fullscreen, general render graphs, and live device
recovery remain outside this milestone.

| Proposed package | Direct engine dependencies | Responsibility |
| --- | --- | --- |
| `Groundwork.Rendering` | `Groundwork.Core` | Portable frame values, renderer status and the lifecycle interface |
| `Groundwork.Rendering.D3D12` | `Groundwork.Rendering`, `Groundwork.Platform.Windows`, `Groundwork.Core` | Windows adapter/device, DXGI swap chain, draw and GPU resource ownership |

This proposes a narrow addition to the WP01 dependency graph. Platform.Windows
owns its presentation target capability and has no reference to either renderer
package. Core gains no renderer or native API dependency. Rendering exposes only
managed value types and Core lifecycle. Neither renderer package references ECS,
Jobs, Simulation or WYU. WYU constructs the host, simulation and selected
renderer and remains the owner of scene composition.

## Public operations and frame handoff

The following names describe the proposed API surface. Public members and
observable outcomes are part of the review. Native bindings and test seams
are provider internals.

| Surface | Proposed contract |
| --- | --- |
| `IRenderer : IEngineModule` | Use the existing initialize, start, stop and dispose semantics. One lifecycle coordinator drives transitions. `TrySubmit(in RenderFrame)` and `Read()` may run concurrently with the render owner and stop. |
| `RenderFrame` | Fixed-size immutable value with a positive, strictly increasing submission sequence, finite SDR clear RGBA in `[0,1]`, and either no scene or one cube instance. The optional scene carries an affine world transform, tint, rigid view transform, vertical FOV in radians and positive near/far distances with `near < far`. FOV lies strictly between zero and π. All numeric inputs must be finite. |
| `TrySubmit` | Returns `Closed` after terminal admission closure, `NotRunning` before successful start, `InvalidFrame` for invalid values, or `StaleSequence` for an otherwise valid sequence at or below the last accepted sequence. A valid higher sequence returns `Accepted` for an empty slot or `Replaced` for an occupied slot. Rejected submissions preserve the slot and sequence. Success copies the value before returning. This call never waits for a GPU slot, consumes an observer lease, or invokes caller code. |
| `Read` | Returns a coherent immutable snapshot with lifecycle, surface availability and applied pixel extent, accepted and last successfully presented sequences, saturating replacement/skip counters, and bounded first-fault and cleanup summaries. Only an `S_OK` normal present updates the presented sequence. This records acceptance by DXGI rather than display completion. |

The initial transform convention is right-handed, with row-vector
`System.Numerics` semantics, a camera looking down negative view Z, and
Direct3D depth in `[0,1]`. Projection uses the last applied positive pixel
extent for aspect ratio. The backend owns matrix packing and shader
transposition. The initial color target is SDR UNORM with explicit channel
values. A readback test checks known projected positions, color and depth
using this convention.

One render owner holds the currently drawn frame and one latest-only CPU
submission may be pending. While suspended, the pending slot can retain the
newest frame. No sequence repeats or wraps. The owner may redraw the most
recent accepted value after a surface change, without treating the redraw as
a new simulation tick. Once stop closes submission, discard pending work and
never reopen that renderer instance.

Lifecycle progresses through created, initialized, running, stopping and
stopped, with terminal fault and incomplete-cleanup outcomes observable in
`Read`. Running includes a suspended surface. Stop is valid after partial
startup. The coordinator starts the Windows host, attaches and starts the
renderer, then enables the render-producing simulation loop. In teardown it
stops frame production and settles the renderer before completing window
teardown. A native close can initiate the same stop concurrently.

### Simulation and observer boundary

WYU submits from its coordinator after `AdvanceAsync` returns. A stopped
iteration supplies no frame, including clear-only frames. If the iteration's
non-null `NextWakeTimestamp` is already due on the same monotonic clock, the
coordinator can skip rendering and service the retained simulation debt.
Rendering has no authority to advance the simulation, change the 60/20 Hz
cadence or hold up a world barrier.

For observer-derived data, the coordinator acquires at most one published
snapshot lease per extraction, checks the expected world and current tick
against `SimulationIteration.CompletedTick`, and copies bounded values into
the frame. It releases the lease and drops snapshot references before
submission, rendering or any wait. The last confirmed writer barrier can be
ahead of the last published observer and is unsuitable as the frame source.
Use a clear-only frame before a scene is available, skip when a lease cannot
be acquired because of capacity, and stop admission on terminal closure.
Failed or blocked publication supplies no new renderer state. The milestone
scene can be generated from the completed tick using bounded data. General ECS
extraction needs a separate contract.

Interpolation is visual only. When used, it interpolates adjacent published
samples for the same world and scene identity using the matching iteration's
factor in `[0,1)`. On pause, missing previous data or discontinuity, use the
current sample. Never extrapolate or interpolate across a world change.

## Windows surface and close protocol

The Windows host provides an optional, exclusive presentation-target lease
after `StartAsync` confirms a live window. The lease binds a stable window
generation to a coherent snapshot of phase, client pixel extent and revision.
It grants the Windows backend scoped access to the native target while the
host retains HWND ownership. Target identity does not survive destruction.
The portable interface, simulation and jobs never receive the native handle.
Renderer attachment failure leaves partial resources subject to normal stop.

The platform STA owns the window, pump, input, target publication and
destruction. A single dedicated render thread owns all DXGI and D3D12 calls.
The owner observes target changes through a retained render wake or equivalent
bounded mailbox. Close acknowledgment has a separate retained notification
to the platform. The renderer does not consume the existing UI or simulation
wake handles. Never hold a platform or Core lock across a driver call. DXGI
can depend on message dispatch while a presentation call is in flight.
[Microsoft DXGI threading guidance](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/dxgi-best-practices)

Before creating provider resources or starting native work, acquire a
runtime ownership lease that strongly retains the render owner. Hold this
lease through proved CPU and GPU quiescence, COM and handle cleanup, and
any process custody. It outlives both individual admission permits and
target release when device-only cleanup remains. Budget one lifetime credit
and one in-flight operation credit for the renderer in addition to the host
and world reservations. Denial at startup fails the attachment. Denial during
running work skips admission and reports capacity or terminal closure.

1. Close or runtime stop latches terminal intent and revokes the target's
   admission of swap-chain creation, resize, `ExecuteCommandLists`, and
   present, including visibility probes. Each new operation needs a
   short-lived Core ownership permit followed by a local target admission
   check, without nested locks. If either check fails, release any permit and
   skip the call. An operation with both grants may settle after revocation.
   Signaling a retirement fence for already-submitted work and releasing
   resources are settlement operations and may run after the admission fence.
   For example, a stopped frame that submitted commands but lost permission
   to present still needs a retirement fence.
2. The STA continues pumping during renderer drain. It never synchronously
   joins the renderer or waits for a GPU fence. The render owner fences new
   submissions, drains or retains outstanding work, and acknowledges release
   only after no target operation can still run and swap-chain references
   have been released.
3. The STA destroys its window on acknowledgment. Unexpected native
   destruction revokes the generation immediately and faults the attached
   provider. Stale work never reacquires a reused handle. Without an attached
   renderer, use the existing WP01 close path.

Use the earliest Core shutdown deadline throughout the stop path. The host
coordinator can return incomplete cleanup at the deadline while process
custody retains the renderer, target, HWND and pumping STA for an outstanding
native call. Neither timeout nor caller cancellation proves quiescence.
Late acknowledgment permits cleanup without reopening the renderer. Repeated
stop and dispose share settlement and cannot extend the deadline. The STA
remains the native destruction owner. [Win32 `DestroyWindow` rules](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-destroywindow)

## D3D12 provider baseline

- **Startup and selection.** Select an eligible hardware adapter and require
  D3D12 feature level 11_0 for the initial pipeline. Explicit WARP mode uses
  `EnumWarpAdapter` for tests and development. A hardware request fails clearly
  if capability is absent. Initialize without submitting work. Start reports
  ready after the device, queue and fixed resources exist and the target is
  attached. A minimized target may start suspended. Apply a finite startup
  wait and retain partial ownership if the native operation has not returned.
  [Device creation and adapter selection](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-d3d12createdevice)
- **Presentation.** Use one direct queue and one windowed flip-discard swap
  chain for the HWND. Start with two `R8G8B8A8_UNORM` back buffers, one `D32_FLOAT`
  depth target, single sampling, vsync interval 1 and maximum DXGI frame latency
  1. Use the frame-latency waitable object to pace the render owner. No app
  waits on it from the UI or simulation thread. No concurrent GDI drawing into
  the attached client area. [DXGI flip model](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-flip-model) · [DXGI latency wait](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_3/nf-dxgi1_3-idxgiswapchain2-getframelatencywaitableobject)
- **GPU ownership.** Use at most two frame contexts with fixed descriptor and
  upload capacity. Each owns its allocator and transient allocations until a
  monotonically increasing queue fence retires that submission. Transition the
  selected back buffer from present to render target and back before `Present`.
  Fence submitted work even if presentation fails or is skipped. If signaling
  fails, retain the affected resources until completion or safe device teardown
  can be established. Never reset or reuse an in-flight allocation. Reserve
  `UINT64_MAX` for device-removal detection and fail closed before fence wrap.
  [Fence-based resource management](https://learn.microsoft.com/en-us/windows/win32/direct3d12/fence-based-resource-management) · [Present resource state](https://learn.microsoft.com/en-us/windows/win32/direct3d12/using-resource-barriers-to-synchronize-resource-states-in-direct3d-12) · [Fence removal sentinel](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12fence-getcompletedvalue)
- **Resize and visibility.** Coalesce to the newest target snapshot. Focus-only
  revisions do not resize. A minimized, zero or unsupported extent suspends
  presentation. Set an initial maximum of 4,096 pixels per dimension and retain
  one back-buffer generation at a time. This bounds nominal color plus depth
  pixels to 192 MiB. Actual device allocations and driver overhead are measured
  separately. Stop submitting before resize, retire affected fence values,
  release references to the old targets, then resize and recreate dependent
  views. Recheck extent and generation after waits and before admitting resize
  or present. Occlusion or busy presentation is a suspended or skipped frame,
  with a paced wake or visibility probe. No tight retry loop.
  [Resize buffer rules](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-resizebuffers) · [Present outcomes](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-present)
- **Faults and deadlines.** Use finite host-configured budgets for startup,
  resize, native present and retirement of outstanding GPU work. Proposed
  defaults are five seconds for startup and two seconds for each running
  operation. A watchdog independent of the render owner races completion
  against each absolute deadline. On expiry it latches terminal failure and
  requests Core stop even if the owner remains inside the driver. Idle and
  occluded pacing alone do not consume a progress budget. Late completion
  settles custody and cannot publish a new ready state. Shutdown uses the
  Core deadline. Report first failure and cleanup separately with numeric
  stage, HRESULT, device-removal reason and last frame sequence where available.
  Bound diagnostic storage and DRED capture. Configure optional process-wide
  DRED settings before device creation. Device reset or removal is terminal
  for the renderer instance. The provider reports the required runtime-scoped
  Host fault and requests `StartupFault` or `RuntimeFault` through the existing
  Core context so a waiting composition coordinator wakes on terminal intent.
  Full queues, minimize and expected close are routine states. The provider
  never authorizes world recovery. A hung driver retains CPU and GPU ownership
  under the deadline and custody rules above.
  [Device-removed diagnostics](https://learn.microsoft.com/en-us/windows/win32/direct3d12/use-dred)

## Review gates and implementation order

1. Approve this contract, including package graph, native capability, frame
   ownership, initial limits and terminal fault policy. Review the concrete
   C# native interop and shader toolchain separately before adding either
   dependency. Record ABI ownership, license, pinned versions, reproducible
   shader inputs and build provenance. C#/.NET 10 remains the default. A native
   C++ helper needs measured justification and explicit approval.
2. Add the portable contract and Windows target seam with controlled fake
   driver tests. Prove latest-only admission, stale frames, zero extent,
   resize storms, close during startup/resize/present, external destruction,
   timeout custody with late acknowledgment, and unchanged renderer-free WP01
   smoke. Prove the pump remains responsive during a blocked fake native call.
   Exercise denied lifetime and admission credits, and stop between submit
   and present. With no user close, inject a stuck resize and present and
   verify that the watchdog initiates stop while retaining the blocked
   operation. Delayed GPU completion or device-only cleanup after HWND
   acknowledgment must keep runtime settlement incomplete.
3. Add the D3D12 provider. Exercise allocator and fence ownership with fake
   completion, delayed GPU, signal failure, device removal and repeat stop.
   Require WARP offscreen clear, transform and depth readback on a designated
   Windows job. Record capability skips explicitly. A skipped case does not
   satisfy the WARP gate. Use the debug layer and live-object reporting where
   installed, and collect actionable diagnostics for unexpected live objects.
4. Verify an interactive hardware Windows run with adapter and driver identity,
   visible cube, resize/minimize/restore, real close, and no premature HWND or
   GPU resource release. Test stop while resize or present is outstanding and
   record normal and fault teardown separately. Check synthetic 60/20 Hz,
   bounded catch-up and observer lease release under renderer backpressure.
   Record frame/submit and teardown timings with machine and configuration.
   These measurements establish a baseline rather than a throughput gate.
5. Extend the engine package and graph allowlists in the implementation PRs,
   publish a uniquely versioned immutable package set, then update WYU's exact
   pins and SDK-generated lock. Verify a separate clean Windows build with a
   fresh cache, missing or wrong artifact rejection, real startup/shutdown and
   the explicitly selected renderer path. Owner acceptance follows independent
   specialist and integration review under [ADR-0007](../adr/ADR-0007-solo-review-governance.md).
