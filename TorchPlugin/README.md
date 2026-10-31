# Thin Torch loader for testing the current main core

This is a **local test host**, not a replacement Magnetar installation or an old Torch fork.
`Shared`, `MultigridProjectorApi` and the server patches/configuration/plugin are linked unchanged.
Only `ServerPlugin/PluginSession.cs` and `Preloader.cs` are excluded: Torch owns the session lifecycle.

Source actually built: `despc/multigrid-projector`, local `main` at
`4d34ff41f6b89af86c7d3e8c6e31dd2984b8418f` (not the earlier task-context `6efec9e`).
The loader does not check out, modify or substitute the core.

## Build (Git Bash on this stand)

```bash
export DOTNET_ROOT='C:/Program Files/JetBrains/JetBrains Rider 2026.2.1/lib/ReSharperHost/windows-x64/dotnet'
SDK="$DOTNET_ROOT/dotnet.exe"
SDK_BIN=C:/Users/user/AppData/Local/hermes/cache/scratch/mgp-magnetar/PluginSdk/bin/Release/netstandard2.0
REPO=C:/Users/user/sources/multigrid-projector

# Original Magnetar project, no deployment (baseline for the IL comparison):
"$SDK" build "$REPO/ServerPlugin/ServerPlugin.csproj" -c Release -f net48 \
  -p:Dedicated64=C:/SE/DedicatedServer64 -p:MagnetarBin="$SDK_BIN" \
  -p:RunPostBuildEvent=Never --nologo

# Torch artifact; no deployment target exists in this project:
"$SDK" build "$REPO/TorchPlugin/TorchPlugin.csproj" -c Release \
  -p:Dedicated64=C:/SE/DedicatedServer64 -p:TorchBin=C:/SE \
  -p:MagnetarBin="$SDK_BIN" --nologo
```

Output: `TorchPlugin/bin/Release/net48/`. Use the **whole folder**: `MultigridProjector.dll`,
`manifest.xml`, real `PluginSdk.dll`, Harmony 2.4.2 and the copied runtime dependencies.
Do not deploy publicized game assemblies, Torch assemblies, or the smoke-test executable.
`PluginSdk` comes from the actual Magnetar source build, not a stub.
Its XML configuration storage also initializes JSON support; therefore `System.Text.Json` 8.0.5,
`System.Text.Encodings.Web`, `Microsoft.Bcl.AsyncInterfaces`, `System.Memory`, `System.Buffers`,
`System.Numerics.Vectors`, `System.Runtime.CompilerServices.Unsafe`,
`System.Threading.Tasks.Extensions` and `System.ValueTuple` are copied by MSBuild.
Mono.Cecil and its companion assemblies are included by the original plugin dependency.
Do not overwrite host-global dependency versions: resolve any loader/version conflict explicitly.

## Lifecycle and safety boundaries

- Raw `TorchPlugin.Init` only subscribes to `ITorchSessionManager.SessionStateChanged`. It must run
  **before** a world starts loading. Late installation is rejected; hot reload is disabled.
- At `TorchSessionState.Loading`, call the unchanged `ServerPlugin.Plugin.Init(null)` once.
  This invokes the original startup/configuration, `EnsureOriginal` compatibility checks, and both
  Harmony patch categories. No Magnetar early bootstrap is installed. Loading must occur after
  serializer/object-builder registration, before grids are deserialized; this is the stand's required
  Torch lifecycle ordering. Do not move this call to raw plugin initialization.
- If the original private `failed` flag is set, no shared session is started. Reading that flag is a
  narrowly scoped reflection bridge; no guard is bypassed and no method hash is changed.
- At `Loaded`, create one unchanged `MultigridProjectorSession`; its constructor needs
  `MyAPIGateway.Utilities` and multiplayer services. Since existing PBs already fired entity-create
  during loading, the host replays the core's private `OnEntityCreate` once for an existing PB.
- Torch `Update` is the **only** caller of this session's `Update`. In current core that update is
  PB-API preparation, not a second projection simulation loop. No `PluginSession` descriptor exists
  in the Torch DLL, so game discovery cannot register an additional updater.
- Dispose the shared session at `Unloading` (idempotent fallback at `Unloaded` and plugin Dispose),
  before gateway teardown. Original plugin/config disposal happens at plugin Dispose.
- The original core's dedicated-server `Comms.Dispose` does not remove its dedicated
  `PlayerConnected` subscription (it removes only `MultiplayerServer`). This loader deliberately
  does not change core behavior. **Use a full server process restart, not in-process world reload**.
- Harmony and Torch detours on the same target are still a real compatibility risk. The original
  guard is preserved, but an offline build/smoke test is NOT proof that live Torch patches compose.
  The parent must inspect startup patch logs, `patch_conflicts`, and the actual multigrid weld result.
  Do not disable the guard to force a green run.

## Offline verification

```bash
python "$REPO/TorchPlugin/tests/test_loader_contract.py"
"$SDK" build "$REPO/TorchPlugin/tests/Smoke/Smoke.csproj" -c Release --nologo
"$REPO/TorchPlugin/tests/Smoke/bin/Release/net48/Smoke.exe" \
  "$REPO/TorchPlugin/bin/Release/net48" \
  "$REPO/ServerPlugin/bin/Release/net48/MultigridProjector.dll"
```

The smoke harness loads the real Torch/game/PluginSdk dependencies in a separate .NET Framework
process using Torch-style byte-array assembly loading and major/minor dependency matching,
with the stand's existing Harmony 2.1.1 preloaded (the original plugin resolves its bundled 2.4.2).
It tests raw Init through interface proxies, verifies no early patching, checks subscription
cleanup and late-load rejection, round-trips real PluginSdk XML storage in the test output folder,
loads the manifest with Torch's serializer, compares all original top-level core/server method IL,
checks all original Harmony patch classes and runtime `IgnoresAccessChecksTo` attributes, and
confirms the automatic session component/preloader are absent. It **does not** invoke Loading
patch installation or construct a real game session offline; those require the parent live run.

## Live acceptance on the local Torch stand (2026-10-31)

The parent deployed this build only to `C:/SE` (no production deployment). Original startup
compatibility checks passed. Survival `multigrid_projector_weld` passed twice (28.0 s / 18.4 s):
15 full physical blocks on 3 grids from 2 seed blocks; exact per-component payment;
both welded pistons moved and returned with the projector off; fixture cleanup verified.

**NOT PRODUCTION-READY:** `patch_conflicts` failed with 6 Torch/Harmony overlaps:

- `MyProjectorBase.BuildInternal` (including Torch build-trust validation)
- `MyGridPhysicalHierarchy.BreakLink`
- `MyMechanicalConnectionBlockBase.CreateTopPartAndAttach`
- `MyProjectorBase.Build`
- `MyMechanicalConnectionBlockBase.OnBuildSuccess`
- `MyShipToolBase.UpdateAfterSimulation10`

A successful weld does not establish that both patch engines run on shared targets.
No guards were removed and no core patches were suppressed to hide this failure.
Resolve patch composition explicitly before considering production use. Full process restart remains required.
No commit or push was performed.
