# WP11 R09 terrain timing

Run the combined-pressure fixture in an interactive Windows x64 session. The route uses seed 42, radius 128, a synthetic terrain source, a 360 × 280 window, and the native D3D12 path. It walks and turns before changing coverage, applying the first sealed edit and recovering coverage. For each transition, the fixture fills the speculative CPU allocation, retains the previous generation, holds the native upload at its entry, and runs a second private protected build before releasing the upload.

```powershell
pwsh -NoProfile -File benchmarks/Measure-Wp11R09Reference.ps1 -Adapter Both -Samples 1 `
  -ExpectedCommit (git rev-parse HEAD) -OutputDirectory (Join-Path $env:TEMP 'wp11-r09-workstation')
```

Use a clean checkout and a fresh output directory outside it. A workstation smoke uses one measured repetition after the discarded warm-up per adapter and mode. For a reference run, select the accepted hardware and collect the default ten repetitions per mode. The script alternates diagnostics off/on order and verifies the passed test result, phase count, and native settlement before reporting nearest-rank p50/p95/p99 and maximum. It preserves the raw QPC marks, frequency, machine and driver identity, script hash, source SHA, logs and TRX. Schema 2 adds the following intervals.

| Interval | Start | End |
| --- | --- | --- |
| Publishing worker queue wait | Scheduler accepted the publishing job, before the fixture dispatches it | Entry to the handler's publishing `VolumeStreamingWork.Execute` call |
| Publishing worker execution | Entry to that call | Return from the successful call |
| Worker completion observation | Successful return | Fixture observes scheduler settlement |
| Upload release to native ready | Fixture releases the held native upload, after checking it was unready | Fixture observes readiness for that exact generation |
| Authorized receipt to consuming BeginTick | Coordinator accepts the registered frame after successful native presentation and queue-signal retirement processing | Coordinator consumes that exact receipt at a later finalized Core BeginTick |
| Authorized transition to consuming BeginTick | Fixture issues the coverage, edit or recovery request before changing its view or starting terrain work | Same consuming BeginTick |
| View change to consuming BeginTick | Fixture applies the new view | Same consuming BeginTick |

The first three rows distinguish scheduler/dispatch waiting, wall time within work (including publication of work readiness), and host observation. They exclude host admission retries before acceptance and distinguish publishing work from the private overlap probe. Release-to-ready includes host polling. Receipt and BeginTick values use the opt-in coordinator journal. They carry the generation, original registered frame, view and renderer epoch. The BeginTick stamp must belong to the same world and have a strictly later tick. No additional journal records or runtime I/O are required, and timing defaults to off.

## Installed WYU reference follow-up

The Groundwork pressure route exercises its own authorized recovery request and view change. The installed flight has a distinct host authorization, overlap during preload, and a return to the ground renderer. Keep the original host authorization before preload and the return-to-ground boundary as separate QPC markers. Join them to the exact generation and receipt that allow the later authoritative BeginTick to reopen movement. Report the wall interval including any overlapped flight and label the ground handoff separately. Compare recovery with the proposed #88 ceiling only for the equivalent authorized request path under the specified workload. Repeat for reload if qualifying that path.

In the installed host, mark the successful publisher submission or enqueue before the worker can run, then entry and return of the corresponding publishing Execute call. Report host admission/retry time separately. Enable coordinator event timing before the first event and match accepted and consumed events by world, generation, frame, view and epoch. Exercise a held native upload in the qualification build and measure from its actual release. Report ordinary upload start and observed Present as distinct intervals. Keep the exact package/source revisions, payload manifest, raw logs and measurement script with the installed evidence.

The canceled/superseded paths produce no successful end-to-end sample. Preserve their outcome separately when measuring attempts and resource settlement. The earlier installed R09 run used a separate WYU reference payload. Reference adjudication remains in [#88](https://github.com/DaveBeusing/Groundwork/issues/88).
