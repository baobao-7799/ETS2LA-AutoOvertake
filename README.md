# baobao-7799 自动超车 / ETS2LA Auto Overtake

Automatic overtaking plugin for [ETS2LA](https://ets2la.com/) (Euro Truck Simulator 2 / American Truck Simulator).

**Author: baobao-7799** · License: GPL-3.0-or-later

Detects a slow vehicle ahead → verifies the passing lane is clear → signals → changes lane → overtakes → merges back automatically. **No keyboard injection** — every control goes through the official `ETS2LA.Game.Output` API.

## Features

- **Automatic driving-side calibration** — learns from oncoming traffic whether to overtake on the left or right (works in UK-style maps without any config)
- **Automatic steering-direction calibration** — self-corrects if the steering sign is inverted
- **User takeover at any moment** — touch the steering wheel, throttle or brake and the plugin instantly releases all outputs
- **Real-time danger checks** — aborts or brakes if the passing lane is blocked ahead, behind or by oncoming traffic
- **In-game settings window** — hold **Right Alt** to open the overlay window and tune every parameter live (auto-saved)
- **Conservative trigger logic** — no overtaking until driving side is calibrated, traffic-vehicle confirmation across frames, TMP vehicles excluded

## How it works

1. **Idle** — speed above `MinSpeed` and a confirmed vehicle ahead that is at least `SpeedDiffThreshold` slower
2. **Signaling** — passing lane clear ahead/behind/no oncoming traffic → indicator on for `ConfirmSeconds`
3. **MovingOut** — PID steering towards the passing lane center while accelerating
4. **Passing** — keeps accelerating until `ReturnGap` meters past the target
5. **Returning** — signals back and steers to the original lane, then releases all outputs and cools down

## Installation

1. Copy `AutoOvertake.dll` into the ETS2LA `Plugins` folder:
   `%LOCALAPPDATA%\ETS2LA\current\Plugins\AutoOvertake.dll`
2. Restart ETS2LA and enable **Auto Overtake 自动超车** under *Settings → Plugins*
3. In game, hold **Right Alt** to open the plugin window

### Game-side prerequisites

| Function | Required game plugin |
| --- | --- |
| Traffic & truck data, steering output | ETS2LA SDK + in-game **ETS2LA Game Plugin** (`ETS2LAPlugin`) |
| Throttle / brake / indicators | **SCSControls** virtual controller ([ETS2LA/scs-sdk-controller](https://github.com/ETS2LA/scs-sdk-controller)) |

> Without SCSControls the plugin can still change lanes (steering), but cannot accelerate or signal. Install both for the full experience.

## Settings

| Parameter | Default | Meaning |
| --- | --- | --- |
| MinSpeed | 12 m/s | Do not overtake below this speed |
| MaxOvertakeSpeed | 33 m/s | Target speed cap while overtaking (~119 km/h) |
| SpeedDiffThreshold | 1.2 m/s | How much slower the vehicle ahead must be to trigger |
| LookAhead | 90 m | How far ahead to search for slow vehicles |
| DangerAhead / DangerBehind | 45 / 30 m | Passing-lane blocking ranges |
| OncomingRange | 160 m | Oncoming-traffic detection range |
| ReturnGap | 35 m | Merge back after passing by this distance |
| LaneOffset / LaneBandHalf | 3.5 / 1.1 m | Target lateral offset and band half-width |
| SteeringGain / SteeringDamp / MaxSteering | 0.035 / 0.35 / 0.45 | Lateral PID tuning |
| ThrottleGain / BrakeGain | 0.18 / 0.3 | Speed controller gains |
| ConfirmSeconds | 0.6 s | Indicator-on duration before moving out |
| RespectSpeedLimit / SpeedLimitExcess | true / 1.4 m/s | Cap by road speed limit |
| Cooldown / AbortCooldown / UserCooldown | 2.5 / 3 / 4 s | Idle delays after finish / abort / user takeover |
| PassingSide | 0 | 0 = auto-calibrate, +1 = force left, −1 = force right |

## Building

No .NET SDK required — the build bootstraps the Roslyn compiler that ships inside ETS2LA.

```powershell
# Requires: ETS2LA installed, .NET 10 runtime, Windows PowerShell 5.1
.\build.ps1
# Output: dist\AutoOvertake.dll
```

## Safety

This is a driving-assistance plugin and automated lane changes carry real risk. Always stay in control and be ready to take over; drive manually in heavy traffic, bad weather or narrow roads. You are responsible for everything the plugin does.

## License

[GPL-3.0-or-later](LICENSE)
