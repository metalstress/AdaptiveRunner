# Build

Requires Windows, .NET SDK 8, Visual Studio C++ x64 build tools and the Windows SDK.

```powershell
./integration/Build-SpotterIntegration.ps1
```

The package is written to `updates/spotter`. To install your build:

```powershell
./integration/Build-SpotterIntegration.ps1 -Install -GameBin 'D:\SteamLibrary\steamapps\common\SnowRunner\Sources\Bin'
```

`--check-logging` checks file logging without opening the game or controller. Gameplay and tactile testing are manual; no synthetic driving tests are used.

The extra wheel-physics reader supports the Steam executable timestamp `6ADB8627`. Other builds keep the effects based on NextGen's published state.

Protocol references: [NextGen shared state](https://github.com/andizla/snowrunner-dashboard/blob/main/docs/SnowRunnerGauges_State.h), [OpenFlydigi](https://github.com/mkaliaha/openflydigi/blob/main/PROTOCOL.md). The shift preset follows Flydigi's Forza module.
