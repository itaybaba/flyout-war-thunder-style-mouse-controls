# Validation - v1.0.0

Release date: 2026-10-12. Built from the current refined mouse-flight controller and yaw-assist implementation, with the public name/version and automatic startup help added.

## Build and offline checks

- Release build: zero warnings, zero errors.
- All 99 synthetic assertions passed, including small/large turns, different response rates and control authority, mouse displacement, AoA learning/reset, and yaw-only corrections in level/banked frames.
- Offline yaw tests cover three timesteps, coordinated-turn preservation, release, invalid telemetry, stall fade, and fine-aim intent.
- Synthetic tests do not prove identical behavior on every real aircraft.
- Startup help is a six-second realtime timer triggered after craft/camera initialization on an unpaused flight. F12 cancels its automatic timeout and toggles manual help. This feature has been build-checked; live timer verification is pending.

## Historical live baseline

The underlying pre-yaw controller was checked on Flyout 0.2409, Unity 2022.3.62f3, MelonLoader 0.7.3. Native launches and normal controls were used.

| Aircraft | Largest small-step endpoint aim error | 20-degree step endpoint error |
|---|---:|---:|
| F-42 | 0.38 deg | 0.99 deg |
| M4 | 0.32 deg | 0.07 deg |
| Gryphon | 0.44 deg | 0.80 deg |
| Radial-MK2 | 0.35 deg | 0.37 deg |

These runs preceded the artificial yaw feature; they are baseline evidence, not v1 yaw tests on all four aircraft.

## Paired yaw-assistance test on t7

Same native air-start conditions for both runs: 2,500 m, 180 m/s, throttle 0.6. Initial aim was level with the horizon. Steps were hold, +3, -3, and +20 degrees relative to the preceding target. Each step used 12 samples spaced 0.5 seconds plus bridge overhead.

| Measurement | Yaw assist off | Yaw assist on, 65% |
|---|---:|---:|
| +3-degree endpoint aim error | 0.572 deg | 0.364 deg |
| -3-degree endpoint aim error | 0.745 deg | 0.414 deg |
| +20-degree endpoint aim error | 5.395 deg | 1.280 deg |
| Post-rudder body yaw-rate RMS | 0.792 deg/s | 0.101 deg/s |
| Post-rudder sideslip RMS | 0.254 deg | 0.051 deg |

Recovery used 24 samples after a 0.3 rudder input lasting 0.6 seconds. It includes aiming control and coupled aircraft motion, not isolated free oscillation. Both runs had zero mod errors, stayed above 1,950 m sampled radar height, and confirmed that assistance yielded during the direct rudder lease. Endpoint errors are not settling-time or overshoot measurements.

One paired run demonstrates improvement on t7; it does not establish universal aircraft behavior. Early descending development runs with terrain contact were excluded. The yaw controller is unchanged from this accepted test; only display/version/startup help changed afterward.
