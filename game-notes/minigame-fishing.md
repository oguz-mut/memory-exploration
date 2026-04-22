# Fishing Timing-Bar — Research Notes

Sources: `Assembly-CSharp/UIFishingBarController.cs`, `Assembly-CSharp/FishingPole.cs`,
`GorgonCore/FishUICommand.cs`, `Assembly-CSharp/IAreaCmdProcessor.cs`,
`Assembly-CSharp/AreaCmdProcessor_NewGui.cs`, `Assembly-CSharp/FishAppearanceConfig.cs`,
CDN: `skills.json`, `items.json`, `quests.json`, `npcs.json`, `sources_items.json`

---

## Overview

Fishing is a non-combat gathering skill (ID `3`, `XpTable: GatheringSkill`) that produces raw fish
items used in Cooking and Alchemy.  The player equips a fishing pole (`FishingPole` component),
stands near water, and activates the skill.  A timing-bar minigame (`UIFishingBarController`) then
drives the catch outcome via a series of server-to-client `FishUICommand` messages.

There are two distinct fishing modes:
- **Active rod fishing** — the real-time timing-bar described in this document.
- **Ice Fishing Gear** (items `item_20401`–`item_20410`) — consumable contraptions placed on frozen
  lakes; these run autonomously for 5 min / 30 min / 2 h / 4 h / 8 h and require no UI interaction.

---

## Timing-bar mechanics

### Server→Client protocol enum

`GorgonCore/FishUICommand.cs`:

```csharp
public enum FishUICommand : byte
{
    Casting      = 0,   // pole cast; bar initialised in "Casting" state
    CaughtFish   = 1,   // fish on the hook; switches to Active state
    UpdateSuccess = 2,  // player clicked at the right moment (check added)
    UpdateFailure = 3,  // player clicked at the wrong moment / missed
    EndSuccess   = 4,   // all required checks reached → item awarded
    EndFailure   = 5,   // timer expired before enough checks
    EndCanceled  = 6,   // player or server cancelled
}
```

### IAreaCmdProcessor dispatch

```csharp
// IAreaCmdProcessor.cs line 80
void ProcessFishingUI(FishUICommand cmd, float timeRemaining, float totalTime,
                      int curSuccesses, int neededSuccesses, int itemCode);
```

Implemented by `AreaCmdProcessor_NewGui.ProcessFishingUI(...)` (line 237), which calls
`UIFishingBarController` singleton methods.

### UIFishingBarController state machine

`Assembly-CSharp/UIFishingBarController.cs` — inherits `MonoBehaviourSingleton<UIFishingBarController>`

#### Internal GameState enum (field 0xD8)

| Value | Name    | Meaning                              |
|-------|---------|--------------------------------------|
| 0     | Casting | Pole has been cast; waiting for bite |
| 1     | Active  | Fish on hook; timing bar running     |
| 2     | Won     | All checks completed successfully    |
| 3     | Lost    | Timer expired / failed               |

#### Key instance fields

| Field                 | Offset | Type       | Description                                  |
|-----------------------|--------|------------|----------------------------------------------|
| `gameState`           | 0xD8   | `int` (enum) | Current `GameState`                         |
| `curTime`             | 0xDC   | `float`    | Seconds remaining (counting down)            |
| `maxTime`             | 0xE0   | `float`    | Total allowed time for this catch            |
| `curChecks`           | 0xE4   | `int`      | Number of successful clicks so far           |
| `maxChecks`           | 0xE8   | `int`      | Number of successful clicks needed to win    |
| `curFishingTimerCoro` | 0xF0   | `Coroutine`| Handle to the running timer coroutine        |
| `curIconAnimationCoro`| 0xF8   | `Coroutine`| Handle to the checkmark animation coroutine  |

#### Key UI fields

| Field               | Offset | Type              | Role                              |
|---------------------|--------|-------------------|-----------------------------------|
| `BarFill`           | 0x38   | `Image`           | Fill image — width = curTime/maxTime |
| `TimeRemainingText` | 0x40   | `TextMeshProUGUI` | Seconds-remaining display         |
| `BarBaseColor`      | 0x48   | `Color`           | Normal bar colour                 |
| `BarFlashColor`     | 0x58   | `Color`           | Flash on `UpdateSuccess`          |
| `BarCountdownColor` | 0x68   | `Color`           | Colour when time is low           |
| `BarFlashColorFailure` | 0x78 | `Color`          | Flash on `UpdateFailure`          |
| `GlowObjects`       | 0x88   | `Image[]`         | Glow ring elements                |
| `CheckboxHolder`    | 0x90   | `GameObject`      | Parent of check/uncheck icons     |
| `TemplateUnchecked` | 0x98   | `GameObject`      | Unchecked checkbox prefab         |
| `TemplateChecked`   | 0xA0   | `GameObject`      | Checked checkbox prefab           |
| `SuccessResults`    | 0xB0   | `GameObject`      | Shown on EndSuccess               |
| `FailureResults`    | 0xB8   | `GameObject`      | Shown on EndFailure               |
| `ItemName`          | 0xC8   | `TextMeshProUGUI` | Name of fish caught               |
| `ItemIcon`          | 0xD0   | `RawImage`        | Icon of fish caught               |

#### Click-to-outcome mapping

The server controls all outcome logic.  The client only sends a click; the server replies with
`UpdateSuccess` or `UpdateFailure`, then eventually `EndSuccess`, `EndFailure`, or `EndCanceled`.

- `UpdateSuccess` → `curChecks++`, `CoroAnimateCheckmark` plays, bar flashes `BarFlashColor`
- `UpdateFailure` → bar flashes `BarFlashColorFailure`; `OnWrongAbilityUsed(float newCurTime)`
  adjusts remaining time (server can penalise misclicks)
- `EndSuccess`    → `CoroAnimateSuccess` plays, `SuccessResults` shown, `ItemName`/`ItemIcon` set
- `EndFailure`    → `FailureResults` shown
- `EndCanceled`   → `OnFishingCanceled()` / `CancelFishing()` called

The checkboxes (`MaintainCheckmarks`) track `curChecks` / `maxChecks` visually.

#### Coroutines

| Coroutine                        | State-machine class          | Purpose                                      |
|----------------------------------|------------------------------|----------------------------------------------|
| `CoroFishingTimer`               | `<CoroFishingTimer>d__39`    | Ticks `curTime` down; triggers countdown glow|
| `CoroAnimateCheckmark(GO)`       | `<CoroAnimateCheckmark>d__41`| Animates a checkbox check mark               |
| `CoroAnimateSuccess`             | `<CoroAnimateSuccess>d__40`  | Scale-bounce on win                          |
| `CoroFlashBarFill(Color)`        | `<CoroFlashBarFill>d__43`    | Short bar-fill colour flash                  |
| `CoroFlashWholeBar(Color,float)` | `<CoroFlashWholeBar>d__42`   | Full-bar flash with configurable duration    |

---

## Protocol / memory hooks

**Log hook (existing approach for other minigames):**
Watch `Player.log` for the server message that triggers `ProcessFishingUI`.  The log line will
carry the `FishUICommand` byte, `timeRemaining`, `totalTime`, `curSuccesses`, `neededSuccesses`,
and `itemCode`.  Exact log-line format: Unknown — not confirmed in dump at time of writing.

**Direct memory hook (MemoryLib approach):**
`UIFishingBarController` is a `MonoBehaviourSingleton` — vtable scan for the singleton instance,
then read:

- `gameState`  @ instance + 0xD8  (int32, values 0–3)
- `curTime`    @ instance + 0xDC  (float)
- `maxTime`    @ instance + 0xE0  (float)
- `curChecks`  @ instance + 0xE4  (int32)
- `maxChecks`  @ instance + 0xE8  (int32)

These five fields are everything needed to replicate the bar state or build an overlay.

**FishingPole visual state** (`Assembly-CSharp/FishingPole.cs`):

| Field               | Offset | Type        |
|---------------------|--------|-------------|
| `Pole`              | 0x20   | `GameObject`|
| `HandPoint`         | 0x28   | `GameObject`|
| `EndPoint`          | 0x30   | `GameObject`|
| `FishingLine`       | 0x40   | `LineRenderer`|
| `selectable`        | 0x48   | `Selectable` |
| `combatant`         | 0x50   | `Combatant`  |
| `fish`              | 0x58   | `GameObject` |
| `idxCurVisualEffect`| 0x68   | `int`        |

`OnNonPlayerSpawned(int entityId)` is called when the fish entity spawns; `InitFish()` initialises
the fish GameObject.

---

## System integrations

### Fishing skill (ID 3)

- **XP table:** `GatheringSkill` (passive advancement: `ModestPassive`)
- **GuestLevelCap:** 15
- **MaxBonusLevels:** 25
- **Level cap unlocks:** favor-gated via `InteractionFlagLevelCaps`:

| Flag                  | Unlock level | NPC / location                              |
|-----------------------|-------------|---------------------------------------------|
| `LevelCap_Fishing_60` | 50          | Rugen, Rahu                                 |
| `LevelCap_Fishing_70` | 60          | Rugen, Rahu                                 |
| `LevelCap_Fishing_80` | 70          | Mysterious entity deep beneath Povus        |
| `LevelCap_Fishing_90` | 80          | Mysterious entity (Povus) OR Justin Smoot, Statehelm |
| *(implied 100)*       | 90          | Justin Smoot, Statehelm                     |

- **Skill rewards** (level → reward):

| Level | Reward                        |
|-------|-------------------------------|
| 5     | Ability: FishGut1             |
| 10    | BonusToSkill: Anatomy_Fish    |
| 15    | Ability: FishGut2             |
| 20    | BonusToSkill: Anatomy_Fish    |
| 25    | Ability: FishGut3             |
| 30    | BonusToSkill: Anatomy_Fish    |
| 35    | BonusToSkill: Knife           |
| 37    | BonusToSkill: SpiritFox       |
| 40    | BonusToSkill: Anatomy_Fish    |
| 42    | BonusToSkill: AnimalHandling  |
| 45    | Ability: FishGut4             |
| 50    | BonusToSkill: Anatomy_Fish    |
| 55    | BonusToSkill: SushiPreparation|
| 60    | Ability: FishGut5             |
| 65    | BonusToSkill: Knife           |
| 70    | BonusToSkill: Angling         |
| 100   | BonusToSkill: Angling         |

### Combat abilities (Fishing skill)

FishGut1–5 are knife attacks that deal double damage to Fish-anatomy creatures.  They require a
skinning knife (5% destruction chance) and share a single reset timer (`SharesResetTimerWith`).

| InternalName | Item  | Level | Damage (PvE) | Power Cost | Range |
|--------------|-------|-------|--------------|------------|-------|
| FishGut1     | ability_1301 | 5 | 28 | 14 | 5 |
| FishGut2     | ability_1302 | 15 | 53 | 19 | 5 |
| FishGut3     | ability_1303 | 25 | — (not read) | — | 5 |
| FishGut4     | ability_1304 | 45 | — | — | 5 |
| FishGut5     | ability_1305 | 60 | — | — | 5 |
| FishGut6     | item_31436 (rare recipe) | — | — | — | — |

All share `ResetTime: 12s`.  `FishGut6` is unlocked via rare `FishingRecipe` scroll (`item_31436`).

### Bait and lures

- **Guardian Lure** (`item_1300`, `InternalName: GuardianLure`) — corpse trophy from ranalon
  guardians; keyword `GuardianLure`; value 250g; stack 100.
- **Scray Lure** (`item_1306`, `InternalName: ScrayLure`) — chunk of dead scray; value 100g;
  stack 100.
- Work orders exist for both (`item_32094`, `item_32095`).
- How lures affect catch outcomes (bonus catch rate, specific species): Unknown — not found in
  dump at time of writing; likely server-side fishing table logic.

### Ice Fishing Gear (passive variant)

| Item          | InternalName          | Duration | Catch range | Catch species                                      |
|---------------|-----------------------|----------|-------------|---------------------------------------------------|
| item_20401    | IceFishingGear1       | 5 min    | 1           | Grapefish, Perch                                   |
| item_20402    | IceFishingGear2       | 30 min   | 2–5         | Grapefish, Perch, Eel                              |
| item_20403    | IceFishingGear3       | 2 h      | 3–12        | Grapefish, Perch, Eel, Shark                       |
| item_20404    | IceFishingGear4       | 4 h      | 5–20        | Grapefish, Perch, Eel, Shark                       |
| item_20405    | IceFishingGear5       | 8 h      | 12–35       | Grapefish, Perch, Eel, Shark                       |
| item_20406    | SpecializedIceFishingGear1 | 5 min | 1       | Walleye, Rock Carp                                 |
| item_20407    | SpecializedIceFishingGear2 | 30 min| 2–5       | Walleye, Rock Carp, Smallmouth Bass                |
| item_20408    | SpecializedIceFishingGear3 | 2 h   | 3–12      | Walleye, Rock Carp, Smallmouth Bass, Yellow Perch  |
| item_20409    | SpecializedIceFishingGear4 | 4 h   | 5–20      | + Catfish                                          |
| item_20410    | SpecializedIceFishingGear5 | 8 h   | 12–35     | + Catfish                                          |

All use `UseVerb: "Fish"`, `Keywords: Consumable, Contraption`, `NumUses: 1`.

### Notable NPCs

| NPC script name    | Area        | Position (x/y/z)               | Role                                              |
|--------------------|-------------|--------------------------------|---------------------------------------------------|
| NPC_JustinSmoot    | Statehelm   | 1042 / 29.7 / 1070             | Training (Fishing, Cooking); cap unlocks at 80–90 |
| NPC_Irkima         | Red Wing Casino | -20.3 / 0 / 38            | Loves raw fish (Pref 2.5) — good favor gift target|
| Rugen              | Rahu        | unknown                        | Cap unlocks at 50–70 (not in npcs.json)           |
| Mysterious entity  | Povus (deep)| unknown                        | Cap unlocks at 70–80                              |

---

## Monetization

### Raw fish vendor values

| Item (item_ID) | Name         | Sell value |
|----------------|--------------|------------|
| item_5101      | Clownfish    | 6g         |
| item_5102      | Perch        | 12g        |
| item_5103      | Eel          | 10g        |
| item_5104      | Shark        | 20g        |
| item_5106      | Grapefish    | 9g         |
| item_5107      | Cavefish     | 30g        |
| item_5109      | Lungfish     | 10g        |
| item_5110      | Flounder     | 35g        |
| item_5111      | Amberjack    | 50g        |
| item_5115      | Catfish      | 45g        |
| item_5130      | Walleye      | 40g        |
| item_5142      | Tigerfish    | 75g        |
| item_5145      | Redfish      | 70g        |
| item_5146      | Lake Sturgeon| 75g        |
| item_5147      | Radiant Muskie | 80g      |
| item_5148      | Vidarian Pickerel | 85g   |
| item_5149      | Frostfin     | 80g        |
| item_5150      | Irmaki       | 85g        |

### High-value fillets (processed)

| Item ID   | Name                   | Sell value |
|-----------|------------------------|------------|
| item_5263 | Marlin Fillet          | 160g       |
| item_5261 | Striped Bass Fillet    | 155g       |
| item_5262 | Eagle Ray Fillet       | 155g       |
| item_5252 | Crater Gar Fillet      | 150g       |
| item_5085 | Shark Fin              | 150g       |

### Work orders

Work orders exist for essentially every species and fillet variant (`item_32201`–`item_32250` and
`item_34921`–`item_34941`).  These drive a significant portion of fishing income — work order
values are multiples of the base vendor price but exact multiplier: Unknown — not exposed in
CDN JSON; visible only at the in-game vendor.

### XP rates

Exact XP-per-catch numbers: Unknown — not in CDN JSON at time of writing.  XP table is
`GatheringSkill`; the `advancementtables.json` or `xptables.json` in the CDN would contain the
actual XP curve but per-catch XP is server-side.

### Consumable: Decoction of Fishing Knowledge

`item_15603` — `Keywords: Alchemy, Consumable, Decoction, Potion`; value 250g.  Likely grants
a temporary Fishing XP boost; exact effect: Unknown.

---

## Where to fish

The CDN landmarks.json does not tag specific fishing spots by name; fishing happens at any valid
body of water the server acknowledges.  Known fishing locations from skill advancement hints and
quests:

| Area        | Evidence                                                     |
|-------------|--------------------------------------------------------------|
| Serbule     | quest_10204 (100 fish for Mushroom Jack); grapefish / perch quests |
| Serbule Hills | quest_15104 (Grapefish Roundup) — lakes and streams nearby |
| Sun Vale    | quest_12302 (eels for Spot in Animal Town); Urglemarg dive area |
| Rahu        | Rugen NPC; quest_16205 (grapefish for Nishika); Rahu Sewers fish farm (quest_16401) |
| Povus       | High-level cap NPC (deep underwater)                         |
| Kur Mountains | Ice fishing implied (frozen lakes); Grapefish / Perch ice gear |
| Statehelm   | Justin Smoot is the high-level trainer                       |

Specific water-body coordinates: Unknown — not labelled in landmarks.json.

---

## Open questions

1. **Timing window size** — what is the server-side window (ms) for a click to register as
   `UpdateSuccess` vs `UpdateFailure`?  Not in CDN data; would require Player.log analysis during
   live fishing.
2. **Misclick time penalty** — `OnWrongAbilityUsed(float newCurTime)` receives a new curTime from
   the server; how large is the penalty (fixed seconds? percentage)?
3. **Species tables per location** — which fish can spawn at each body of water?  Likely a
   server-side area property; not in CDN JSON.
4. **Lure effects** — do Guardian Lure / Scray Lure add specific species or only increase catch
   chance?  Not exposed in items.json.
5. **`FishGut3–5` damage values** — not read from the dump (scroll cut off); full data exists at
   `ability_1303`–`ability_1305` in abilities.json.
6. **Decoction of Fishing Knowledge effect** — exact buff not in items.json.
7. **Rugen and Povus entity NPC script names / positions** — not present in npcs.json.
8. **Per-catch XP** — not exposed in CDN JSON; needs in-game measurement.
