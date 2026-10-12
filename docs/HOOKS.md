# Recorder hooks and upstream projects

The recorder's telemetry bundle (see `docs/DEVELOPMENT.md`) comes from observation-only Harmony hooks on the
installed game's methods. This file lists them and what each one can and cannot tell. The network demo
(`DEMO.md`) records the game's own network messages; its event annotations (launches, damage scopes, damage
credit) reuse these hooks, and `src/Demo/DemoRecorder.cs` adds a few demo-only patches of its own (transform
batches, crash-objective lives, kill messages, player aircraft and faction, gun damage scope), which this file
does not cover.

The hooks were written independently against the installed Nuclear Option Mono assemblies (first 0.34.1).
Decompiled game source and cloned research projects stay in the local, gitignored `research/` directory and are
never part of a build or release. The longer hook evidence from the visual-capture period (recorder 0.1 to 0.3)
was moved out of the repository into a separate research archive.

## Existing projects

[NOBlackBox](https://github.com/KopterBuzz/NOBlackBox), inspected at commit
`744b453bacc311ae7c29211b77fd0c3e55d6261d`, records Tacview ACMI by sampling transforms, types and locked
targets. It does not provide the immutable launch context, monotonic recording alignment or projectile-to-damage
attribution this recorder needs.

[NuclearMods](https://github.com/nikkorap/NuclearMods), inspected at commit
`72aaff726ee87b60c7087496273be943a336cbdd`, has examples of BepInEx/Harmony game modifications for Nuclear Option
0.31 and BepInEx 5 Mono.

Neither tree had an explicit license file when inspected. No code from either project was copied.

## Bindings

`src/Hooks.cs` installs 19 bindings. Every callback returns `void` and never changes game arguments or results;
installing a binding only means Harmony accepted it, not that its callback was validated in play.
`tests/VerifyHooks.cs` (the `tests/Contracts.csproj` project) checks each binding against the installed
assembly's metadata: the method exists with an IL body, and the callback parameters match. It does not run
Harmony, Unity or game code.

- `Spawner.SpawnMissile`, both the `MissileDefinition` and the `GameObject` overload: actual missile creation.
  The prefix reads `owner`, `target` and the launch position; the postfix reads the returned Missile. This is the
  launch record (host or single player only). `MountedMissile.Fire` can delay a launch, so the fire button is not
  used.
- `WeaponManager.TargetListChanged`, `SetActiveStation`: target and weapon selection changes. A small per-frame
  check covers target-list paths that skip the notification. Selection does not establish what a missile was
  launched at.
- `Missile.TargetIDChanged`: later seeker target changes, kept separate from the launch assignment. Can link to a
  missile first seen without a launch record.
- `Missile.UserCode_RpcDetonate_897349600`: detonation (`armed`, `hitArmor`, `hitTerrain`, position and normal).
  Its `relativeUnit` is a position reference, not proof of which unit was hit.
- `Missile.PenetrateObject`: direct contact with a damageable. A thread-local scope links only synchronous nested
  damage to that missile and is restored in a finalizer.
- `DamageEffects.BlastFrag`: explicit `missileID` and `dealerID`, with the same scope for nested damage. No
  proximity or nearest-missile guessing.
- `Unit.RecordDamage`: the dealer's damage credit. It has no projectile ID; attribution needs the outer scope.
- `Unit.UserCode_RpcDamage_1709046923`: part health before and after the damage RPC. The RPC has no dealer or
  projectile ID.
- `Unit.ReportKilled`: the game's destruction report. Unknown kill attribution stays unknown.
- `Unit.OnDestroy`: removal only (scene unloads call it too); never a kill. Also retires projectile-ID
  correlation; a raw ID reused later in the same mission stays ambiguous.
- `Unit.UnitDisabled`: the disabled flag, not kill attribution.
- `MissionManager.SetMission`, `StartMission`: new mission identities and a correlation reset.
- `UnitRegistry.RegisterUnit`: keeps the observer's unit registry, with one `UnitRegistry.allUnits` bootstrap per
  mission.
- `Missile.OnStartNetwork`: network initialization of a missile on any machine, including clients. Recorded as
  an observation, not a launch.
- `Aircraft.CmdLaunchMissile`: the local launch request. No projectile exists yet and the request can be
  rejected or delayed.
- `Aircraft.UserCode_RpcLaunchMissile_1465828762`: the replicated launch animation. The owning client does not
  receive it, and it has no projectile ID or launch time.

## Limits

- Exact launch time, launch range and projectile-linked damage need a host or single-player recording. A client
  sees initialization and later local state only.
- Gun rounds, countermeasure deployment, takeoff and landing, and nuclear shockwave attribution are not recorded
  as events.
- Delayed or asynchronous damage without an explicit projectile ID stays unlinked. Disappearance is not a hit,
  and a destruction report is not an exclusive player kill.
- Recorded identities, ranges and seeker data can be hidden from the player; the bundle never claims the player
  saw or recognized something.
