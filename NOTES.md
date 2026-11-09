# NOTES — the failure log

Read before composing; append when something dies. Newest last.

## 2026-11-09 — the repository starts

- First help patch opened with the point invisible: the default Stride camera looks at longitude 0,
  and Tokyo (139.7°E) is on the far side. Not a defect, a help-patch design fault: a help patch must
  show its result on open. Fixed with an `OrbitCamera` aimed at the point (yaw −lon/360, pitch
  −lat/360, distance negative — the sign every shipped use has).
