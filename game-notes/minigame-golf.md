# Minigame: Golf

## Overview

Golf is a named non-combat skill (Skill ID 161, `Skills.json` key `"Golf"`) covering "any game
involving the propulsion of an object toward a designated goal on the landscape." It is a child of
the `Gaming` skill group. The game is accessed via the `/golfgame` command, which opens a window
managed by `UIGolfGame`. Players adjust **Power** and **English** (spin) sliders, then execute shots
toward each hole on a course. Courses are loaded at runtime from a pre-parsed asset bundle via
`GolfCourseFactory`.

A special-event effect (`effect_9481`, "Pennoc's Pennant") mentions **Pennoc in Serbule** as a golf
tournament organizer; two craftable necklaces (PennocsPendant1/2) provide the `GolfSprint` buff
on ball expiry.

## Mechanics

**Turn / shot flow:**
1. Server sends `ProcessGolfCommand(GolfCommand.ShowWindow)` → client calls `UIGolfGame.Show()`,
   opens the game window.
2. Server sends `ProcessGolfCommand(GolfCommand.Refresh)` → client calls `UIGolfGame.ServerSaysRefresh()`,
   re-fetches hole and score state.
3. Player adjusts **Power** slider (`curPower`, offset `0x58`) and **English** slider (`curEnglish`,
   offset `0x5C`). Range min/max are set via `SetupRow(...)` at startup.
4. `CoroSendUpdates()` coroutine detects when `lastSentPower` or `lastSentEnglish` differ from
   current values and sends an update packet to the server.
5. Server processes the shot, returns a tool command response via
   `OnToolCommandResponse(int originalSendersCommandIID, bool wasError, string responseText, Dictionary<string,string> parameters)`.
6. Scores are updated with `UpdateScores(Int32[] holeScores, int totalScore)`.

**GolfCommand enum (`GorgonCore/GolfCommand.cs`, type `byte`):**
| Value | Name | Effect |
|-------|------|--------|
| 0 | `ShowWindow` | Open the golf UI |
| 1 | `Refresh` | Server requests UI state refresh |

**Course structure:**

`GolfCourse` (loaded by `GolfCourseFactory`):
- A course has a `CourseID` (int), `InternalName` (string), `Name` (string), `Version` (int), and
  `Holes` (`GolfCourseHole[]`).
- Each `GolfCourseHole` (struct) has `Par` (int, offset `0x0`) and `Desc` (string, offset `0x8`).
- `GolfCourseFactory.Instance` caches courses in `courses: Dictionary<int, GolfCourse>` (offset
  `0x28`) and `internalNames: Dictionary<string, int>` (offset `0x20`).

**Scoring:**
- `curHoleScores: Int32[]` (offset `0xA8`) — per-hole stroke counts.
- `curTotalScore: int` (offset `0xB0`) — running total across all holes.
- Each hole's score box is a `UIUniversalPrefab` from `ScoreBoxes` list. Clicking a score box calls
  `ShowScoreClickMenu(int holeID)` — likely shows hole context (navigate / retry options).
- Par-relative scoring (strokes over/under par) — exact formula **Unknown — not found in dump at time of writing**.

**Navigation helpers:**
- `ShowHoleLocation(int holeID, bool showStart)` — pings the hole's start or pin location on the map.
- `ViewGoalsRow` — a row in the UI for viewing goals associated with the current course.
- `curViewGoals` (offset `0x68`) — tracks current goal display state.

**Skill level caps (from skills.json `Golf`):**
| Flag | Required Golf Level |
|------|---------------------|
| `LevelCap_Golf_60` | 50 |
| `LevelCap_Golf_70` | 60 |
| `LevelCap_Golf_80` | 70 |
| `LevelCap_Golf_90` | 80 |
| `LevelCap_Golf_100` | 90 |

(These flags gate access to higher-difficulty courses or higher-tier holes.)

**Guest level cap:** 15 (trial/guest accounts capped at Golf 15).

**Max bonus levels:** 25.

**XP table:** `TypicalNoncombatSkill`.

## Protocol / Memory Hooks

### Key Classes

| Class | Assembly | Notes |
|-------|----------|-------|
| `UIGolfGame` | Assembly-CSharp | Singleton MonoBehaviour; owns all UI state |
| `GolfCommand` | GorgonCore | Enum (byte): ShowWindow=0, Refresh=1 |
| `GolfCourse` | GorgonCore | Course data: ID, name, holes array |
| `GolfCourseHole` | GorgonCore | Struct: Par + Desc per hole |
| `GolfCourseFactory` | GorgonCore | Singleton factory; keyed by ID and InternalName |
| `GolfCoursePreParsed` | GorgonCore | Asset bundle wrapper: `preParsed: GolfCourse[]` (0x18) |
| `GolfBallAppearanceConfig` | Assembly-CSharp | Entity appearance config; `Particles: ParticleSystem` (0x140) |

### `UIGolfGame` Field Offsets (instance)

| Field | Type | Offset |
|-------|------|--------|
| `myWindow` | `UIWindowTitlebar` | `0x20` |
| `Label` | `TextMeshProUGUI` | `0x28` |
| `PowerRow` | `UIUniversalPrefab` | `0x30` |
| `EnglishRow` | `UIUniversalPrefab` | `0x38` |
| `ViewGoalsRow` | `UIUniversalPrefab` | `0x40` |
| `ScoreBoxTemplate` | `UIUniversalPrefab` | `0x48` |
| `TotalScoreRow` | `UIUniversalPrefab` | `0x50` |
| `curPower` | `int` | `0x58` |
| `curEnglish` | `int` | `0x5C` |
| `curCourseId` | `int` | `0x60` |
| `curHoleId` | `int` | `0x64` |
| `curViewGoals` | `int` | `0x68` |
| `isSlidersUpdateNeeded` | `bool` | `0x6C` |
| `isGoalsUpdateNeeded` | `bool` | `0x6D` |
| `isServerRequestedUpdateNeeded` | `bool` | `0x6E` |
| `initInfo` | `WindowTemplateInfo` | `0x70` |
| `ScoreBoxes` | `List<UIUniversalPrefab>` | `0xA0` |
| `curHoleScores` | `int[]` | `0xA8` |
| `curTotalScore` | `int` | `0xB0` |
| `coro` | `Coroutine` | `0xB8` |
| `isChangingValue` | `bool` | `0xC0` |
| `curToolCmdId` | `int` | `0xC4` |
| `curThrowawayToolCmdId` | `int` | `0xC8` |

### `GolfCourse` Field Offsets

| Field | Type | Offset |
|-------|------|--------|
| `InternalName` | `string` | `0x10` |
| `CourseID` | `int` | `0x18` |
| `Name` | `string` | `0x20` |
| `Version` | `int` | `0x28` |
| `Holes` | `GolfCourseHole[]` | `0x30` |

### `GolfCourseHole` Field Offsets (struct)

| Field | Type | Offset |
|-------|------|--------|
| `Par` | `int` | `0x0` |
| `Desc` | `string` | `0x8` |

### `GolfCourseFactory` Field Offsets (singleton)

| Field | Type | Offset |
|-------|------|--------|
| `Instance` (static) | `GolfCourseFactory` | `0x0` |
| `internalNames` | `Dictionary<string, int>` | `0x20` |
| `courses` | `Dictionary<int, GolfCourse>` | `0x28` |

### Message Handlers

| Direction | Method | Signature |
|-----------|--------|-----------|
| Server → Client | `ProcessGolfCommand(GolfCommand command)` | Dispatches `Show()` or `ServerSaysRefresh()` |
| Client → Server | Tool command system | Uses `curToolCmdId`; response via `OnToolCommandResponse(...)` |

`ProcessGolfCommand` is implemented in both `IAreaCmdProcessor` (interface) and
`AreaCmdProcessor_NewGui` (concrete implementation, Assembly-CSharp).

No dedicated `prepGolf*` encoder methods found in `ClientCommandEncoder` — golf appears to use the
generic tool-command channel (`curToolCmdId` / `OnToolCommandResponse`).

### `CoroSendUpdates` Coroutine State Machine

The coroutine (`<CoroSendUpdates>d__35`) tracks:
- `lastSentPower` (offset `0x28`, int) — previously transmitted power value.
- `lastSentEnglish` (offset `0x2C`, int) — previously transmitted English value.
- `isInitialized` (offset `0x30`, bool) — whether initial values have been sent.

It sends an update only when slider values change, avoiding redundant server calls.

## System Integrations

**Skill:** `Golf` (ID 161), parent `Gaming`, non-combat, `XpTable: "TypicalNoncombatSkill"`.
Level gates exist at Golf 50/60/70/80/90 (see level cap flags above).

**Effects:**
- `effect_13715` — **"Golfer's Sprint"**: `Duration: 120s`, `Desc: "You're moving quickly."`,
  `StackingType: "GolfSprint"`, `Keywords: ["Buff"]`, `IconId: 3823`. Triggered when a golf ball
  is used up (see PennocsPendant items below).
- `effect_9481` — **"Pennoc's Pennant"**: `Duration: -2` (permanent until cleared),
  `Desc: "Pennoc in Serbule is sponsoring a golf tournament! Speak with him for more information."`,
  `Keywords: ["SpecialEvent", "Innate"]`. A special-event notification buff.

**Quests:** No golf quests found in quests.json.

**Directed Goals:** No golf directed goals found in directedgoals.json.

**NPC Pennoc:** Referenced in effect_9481 and in all Pennoc's Pendant items as "a smiling elf" in
Serbule. Not present as a named key in npcs.json — likely a special-event NPC or his NPC ID was
not indexed. Location: **Serbule** (from effect_9481 text).

## Monetization

Golf uses a **golf ball** as the consumable play token. No item named `GolfBall` was found by
InternalName in items.json, but multiple items reference "When your golf ball is used up" in their
EffectDescs, confirming a ball is consumed per round/shot. The ball item itself —
**Unknown InternalName/ID — not found in items.json at time of writing**.

**Reward / gear items found:**

| Item ID | InternalName | Name | Notes |
|---------|-------------|------|-------|
| item_48181 | `PennocsPendant1` | Pennoc's Pendant | Necklace, CraftTarget Lvl 30; on ball expiry: Non-combat Sprint Boost +2 (120s), `{BOOST_ABILITY_KICK}{15}`; Value 90 |
| item_48182 | `PennocsPendant2` | Pennoc's Pendulous Pendant | Necklace, CraftTarget Lvl 60; on ball expiry: Non-combat Sprint Boost +2 (120s), `{MOD_ABILITY_KICK}{0.1}`; requires Endurance 50; Value 210 |

Both pendants have Keywords `["GolfSprint", "PennocsPendant", "Jewelry", "Amulet", "Necklace", "Loot", "Silver"]`.

**Other Pennoc items** (not golf-specific but sold/rewarded by Pennoc):
- `PennocsPension` — sack of money, Value 2000, Keywords `["Coin", "Consumable", "Money"]`.
- `PennocsPenknife` (item_4435) — skinning/butchering tool +3 effective skill, Value 300.

## Where to Play

| Field | Value |
|-------|-------|
| Tournament organizer NPC | Pennoc (elf) |
| NPC area | Serbule (`AreaSerbule`) |
| NPC exact position | **Unknown — not in npcs.json at time of writing** |
| NPC ID | **Unknown — not in npcs.json at time of writing** |

Course locations (from `GolfCourseHole.Desc` / `ShowHoleLocation`) — course data is loaded from a
pre-parsed asset bundle at runtime, not from CDN JSON. No course names or hole descriptions appear
in the CDN JSONs scanned.

## Open Questions

1. **Golf ball item** — What is the InternalName, item ID, and source (vendor / crafted / looted)
   of the consumable golf ball? Not found by InternalName scan.
2. **Course JSON data** — Courses are in a pre-parsed Unity asset bundle, not a CDN JSON. What
   courses exist and how many holes do they have?
3. **Pennoc NPC ID** — Not present in npcs.json under any obvious key. May be a seasonal/event NPC.
4. **Shot mechanics** — What do Power and English integer ranges represent? What does the server
   compute per shot (trajectory, bounce, outcome)?
5. **Scoring formula** — Points per stroke vs par? Handicap system?
6. **Client → Server packet** — How are Power/English values sent? No `prepGolf*` method found in
   `ClientCommandEncoder`; the tool-command channel (`curToolCmdId`) is used but the payload
   format is unknown.
7. **Golf XP sources** — What actions award Golf XP? Not in sources_abilities.json or directedgoals.json.
8. **Tournament schedule** — Is Pennoc's tournament a recurring seasonal event or always available?
