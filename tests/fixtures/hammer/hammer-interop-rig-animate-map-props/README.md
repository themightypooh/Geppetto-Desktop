# hammer-interop-rig-animate-map-props — fixtures

Rig / animate on map props: a `prop_dynamic` can reference a rigged model + an
animation clip (named clips, loop-seam check, root motion), doors / movers
animate through the I/O system driving clip playback, animated props export
with their clips.

## Files

- `props.json` — one animated prop (`guard_01`, `prop_dynamic`,
  `DefaultAnim: guard_idle`, looping) and one mover (`door_01`, `func_door`,
  2 tied brushes) with `Open` / `Close` clip bindings. Clip NAMES only: the
  clips themselves are built in code by `tests/RigAnimateMapTests.cs`
  (`IdleLoopClip` / `DoorOpenClip` / `DoorCloseClip`) — the JSON drives
  placement, binding and playback names, and states the expected outcome
  (0 validation issues, 3 manifest rows, door ends open firing
  `OnFullyOpen`).

## What the tests cover (group `riganimatemap`)

- `prop_dynamic` with a looping default clip validates clean; an unknown
  default clip is an `UnknownClip` error; a looping clip with a popping seam
  is a `LoopSeamPops` error while the same clip one-shot is not.
- `prop_static` / `prop_physics` cannot bind a clip (`NotAnimatableProp`).
- Door `Open` / `Close` bind and validate; unknown input / unknown clip
  rejected. Firing the `Open` input (an `EntityIoInput` row) plays
  `door_open` headless — frame steps, sampled pose, finish at the last frame
  with `DoorOpen` true and `OnFullyOpen` fired. `Close` and `Toggle` covered.
- `SetAnimation` plays the clip its parameter names; `SetDefaultAnimation`
  re-points the `DefaultAnim` keyvalue and rebinds; `TurnOff` stops.
- Missing target (`UnknownTarget`), shared targetname (`AmbiguousTarget`),
  `#id` addressing.
- Root motion: `extract` moves hips travel onto the root bone, `strip` plays
  in place; the export manifest lists entity / model / clip / frames / root
  bone / seam for every bound clip.

## Run

Temp-register `RigAnimateMapTests.Register(registry, settings)` in
`tests/TestRegistry.cs`, run the group only, then revert the registration
(coordinator-owned file — see the ticket's done bar):

```sh
dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter riganimatemap
```
