# hammer-texture-scale-uv-locks fixtures

`locks.json` records the lock cases the `texturelock` test group checks:
the +Z quad (4 wide by 2 tall), the scale-lock resize (4x2 -> 8x2, stored
scale halves in U, UV range across the face unchanged), and the UvLock shear
(top edge +1 in U; FaceUv fit residual under 0.75 with the face-centre UV
exact, world-space MapUvMapping fit exact).

Lock semantics (ticket, not Valve): a lock means the UV is glued to the FACE.

DEFERRED: the end-to-end clip check ("a clip of a locked brush preserves
the lock on the kept faces" against `MapClip.Clip`) is not run here — the
clip wiring lands as a follow-up once worker 06 reconciles `MapClip.cs` to
the landed `MapBrush` API. The helper contract that wiring will call
(`TextureLock.CheckLockPersistence` with the kept-face index map) is tested
in `tests/TextureLockTests.cs` ("clip wiring contract: kept faces keep locks
(helper-level)").
