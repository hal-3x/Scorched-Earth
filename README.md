# Scorched Earth — Charring

A Cities: Skylines II mod that makes fire leave a mark.

- Objects that survive a fire are darkened with soot, and clean up slowly afterwards.
- Trees killed by fire switch to the game's dead-tree model, darkened so they read as charred,
  and regrow later.

That's the whole scope. Fire *spread* and fire/smoke *rendering* were originally part of this
and have been split out — see [Split-out work](#split-out-work).

---

## Installing

Copy `bin/ScorchedEarth.dll` into

```
%LOCALAPPDATA%Low\Colossal Order\Cities Skylines II\.cache\Mods\local\ScorchedEarth```

then enable it in the game's mod list. `tools/build.py --deploy` does this for you.

If the mod stops appearing in the mod list or in Skyve, the local entry has probably been
dropped from your playset by a backend sync. `python tools/enable_local_mod.py --status` shows
you; running it without `--status` (game closed) puts the entry back.

## Building

```bash
dotnet build ScorchedEarth.csproj -c Release          # with the .NET SDK
pip install pythonnet && python tools/build.py       # without any SDK
```

`tools/build.py` fetches the Roslyn compiler and hosts it on the .NET runtime the game's
launcher already installs, compiling straight against `Cities2_Data/Managed`.

---

## How it works

### How much char

Driven by the fire simulation's own damage figure (`Damaged.m_Damage.y`), not by elapsed time.
That number is already calibrated against each object's structural integrity — it reaches 1
exactly when the object is destroyed — so a brief fire leaves a scorch and a long one leaves a
ruin, with no guessing about how long fires last. A soot floor derived from fire intensity
covers the early part of a fire, before structural damage has built up.

### Trees

A tree that catches fire doesn't survive it: past a token amount of burning it is switched to
the vanilla `TreeState.Dead` state, so the game's own bare dead-tree model is reused rather
than a new asset being shipped. Only `BatchesUpdated` is added afterwards — exactly what the
game's own `TreeGrowthSystem` does when it changes a tree's state.

**Living foliage is never tinted.** Charring is applied only once the tree is already showing
the dead model. Tinting a tree that still has its leaves just turns them black, which is not
what a burned tree looks like — and because the death switch goes through a command buffer,
there is a short window where the tree is still the living model. Skipping it means that
window is never visible.

Regrowth returns them as saplings and lets the vanilla growth simulation take it from there.

### Charring

Charred colours are written **in place** into the object's `MeshColor` buffer, by a system
that runs immediately after `MeshColorSystem` in the `PreCulling` phase and before
`BatchDataSystem` uploads to the GPU in `Rendering`.

The clean colours are cached in an `OriginalMeshColor` buffer, so darkening is always
computed from the original and can never compound. The cache refreshes whenever the game
recomputes an object's colours — detected by the buffer no longer matching what the mod last
wrote, which is also what makes a tree changing growth state work correctly.

> **Why not `CustomMeshColor`.** That is the game's per-instance colour override, and it is
> what the colour picker and recolouring mods use — but it cannot be used here.
> `MeshColorSystem.ApplyCustomMeshColors` hard-resizes the target's `MeshColor` buffer to
> **exactly one entry**, while the batch renderer indexes that buffer at
> `meshBatch.m_MeshIndex` with no bounds check. On anything with more than one sub-mesh
> colour — every tree — that reads past the end of the buffer and uploads garbage, which
> shows as the object flashing a nonsense colour whenever the renderer next touches its
> colour properties. Writing `MeshColor` in place keeps the buffer exactly as the game built
> it.

Soot is applied by desaturating before darkening. A purely multiplicative darkening leaves a
red building looking maroon; desaturating first makes the midpoint of the fade read as ash.

Charring reaches whatever parts of a material the asset drives through its colour channels.
Assets that use little or no colour variation will darken correspondingly little — CS2 has no
per-instance soot channel that is not already owned by the simulation (see below).

How charred something gets is driven by the fire simulation's own damage figure
(`Damaged.m_Damage.y`), not by elapsed time. That number is already calibrated against each
object's structural integrity — it reaches 1 exactly when the object is destroyed — so a brief
fire leaves a scorch and a long one leaves a ruin, without the mod having to guess how long
fires last. A soot floor derived from fire intensity covers the early part of a fire, before
structural damage has built up.

---

## Scope of what the mod touches

- **Components written:** `MeshColor` (contents only, never length), `Tree` (state and growth,
  fire-killed trees only), `BatchesUpdated`.
- **Components added:** `Charred`, `OriginalMeshColor`, `FireKilledTree` — all mod-owned.
- **Not touched:** the fire simulation itself. Ignition, spread, damage, structural integrity,
  fire-service dispatch and destruction are entirely vanilla. No Harmony, no patched game code,
  no entities created, no prefabs edited.
- **Not written, deliberately:** `Damaged.m_Damage` and `Surface.m_Dirtyness`, the two inputs to
  the game's own burn/soot shader channel. `m_Damage.x` counts toward `GetTotalDamage` (writing
  it would destroy buildings) and feeds fire-start probability; `m_Dirtyness` is recomputed from
  building condition every tick by `DirtynessSystem`. Neither is safe for a cosmetic mod to
  drive.

Charring reaches whatever parts of a material the asset drives through its colour channels.
Assets using little colour variation will darken correspondingly little.

The systems tick on a power-of-two interval — the game's scheduler requires it and reads the
value only once, at registration — and throttle down to the user's interval at runtime
(`src/Systems/UpdateThrottle.cs`). Rates are computed from frames actually elapsed, so changing
the interval changes cost, not how fast things char or recover.

## Save compatibility

`Charred`, `OriginalMeshColor` and `FireKilledTree` are serialized, so charring and dead trees
survive a save and reload. Removing the mod leaves those components unreadable and they are
skipped; objects come back with their vanilla colours, and any tree still in its burned state
stays a dead tree until the vanilla tree simulation cycles it.

## Layout

```
src/
  Mod.cs                        entry point and system registration
  Settings.cs, LocaleEN.cs      options screen
  Components.cs                 Charred, OriginalMeshColor, FireKilledTree
  Systems/
    CharringSystem.cs           soot accumulation, tree death
    CharColorSystem.cs          writes the darkened colours
    RecoverySystem.cs           soot fade, tree regrowth
    UpdateThrottle.cs           runtime rate limiting
tools/build.py                  SDK-free build
tools/enable_local_mod.py       restore the local playset entry
parked-visuals/                 the split-out half; not compiled
```

Turn on **Verbose logging** in the About tab to see tree kills and charring activity in
`Logs/ScorchedEarth.log`.

---

## Split-out work

`parked-visuals/` holds the fire-front and smouldering-area code. It is **not compiled into
this mod** and is kept for a separate one.

It does not work yet. Creating its effect-sprite entities crashes the game — an access
violation reading `0x1C` (a null dereference) inside `lib_burst_generated.dll`, confirmed from
the crash dump. The entities are hand-assembled, and the game never produces that component
shape: its own editor effect container is a full object entity built from a real object prefab.
Adding the missing components is not a fix either — `CullingInfo` plus an update tag is exactly
`PreCullingSystem`'s entry condition, which hands the entity to the batch renderer, whose
`PrefabRef` points at an effect prefab with no mesh data behind it.

The likely way out is to stop creating entities entirely: **append** entries to a burning
prefab's own `Effect` buffer. Appending does not disturb the indices live instances already
hold — removal did, which caused an earlier crash — and it lets the game's own machinery handle
instancing, culling and transforms. Details are documented on `EffectSpritePool` and
`FireEffectCatalogSystem` in that folder.
