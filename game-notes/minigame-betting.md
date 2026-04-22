# Arena Betting — Research Notes

Sources: `Assembly-CSharp/UIBettingRow.cs`, `Assembly-CSharp/IAreaCmdProcessor.cs`,
`Assembly-CSharp/AreaCmdProcessor_NewGui.cs`, CDN: `npcs.json`, `items.json`, `quests.json`,
`areas.json`, `landmarks.json`, `itemuses.json`

---

## Overview

Arena Betting is a passive spectator minigame at the **Red Wing Casino** (`AreaCasino`).
Players place gold wagers on monster fights staged in the casino arena.  The UI is built around
`UIBettingRow` (one row per betable outcome), and the interaction flows through the standard
`ProcessTalkScreen` pathway rather than having its own dedicated `IAreaCmdProcessor` handler.

Otis the Ogre (`NPC_Otis`, Red Wing Casino) is confirmed to be an arena fighter via in-game quest
text: "CLUB MAKE CRACK SOUND IN ARENA" and "IS STAFF POTION FOR ARENA!" (quests 15301 / 15304).
Item `item_55515` ("Otis's Gift Club") description: *"Otis the Ogre once used this in an epic
arena battle."*

**NPC 13138 (Kuzavek)** — referenced in task brief as the betting NPC; **not present** in the
CDN `npcs.json` at time of writing.  The CDN file uses internal script names as keys; Kuzavek's
script name is Unknown.  No entry with that name or numeric ID was found.

---

## Mechanics

### UIBettingRow

`Assembly-CSharp/UIBettingRow.cs` — one instance per wager option displayed.

#### Fields

| Field         | Offset | Type         | Description                                 |
|---------------|--------|--------------|---------------------------------------------|
| `Label`       | 0x20   | `Text`       | Bet option label (e.g. monster name)        |
| `Input`       | 0x28   | `InputField` | Player's entered wager amount               |
| `IncButton`   | 0x30   | `Button`     | "+Increment" quick-fill button              |
| `PayoffLabel` | 0x38   | `Text`       | Displays the odds string (e.g. "2:1")       |
| `MinBet`      | 0x40   | `int`        | Minimum allowed wager                       |
| `MaxBet`      | 0x44   | `int`        | Maximum allowed wager                       |
| `Increment`   | 0x48   | `int`        | Step size for the IncButton                 |

#### Methods

| Method                      | Behaviour                                                   |
|-----------------------------|-------------------------------------------------------------|
| `AllowInput(bool)`          | Enable / disable the input field and inc button             |
| `Clear()`                   | Zero the input field                                        |
| `GetInput() → int`          | Read validated wager amount                                 |
| `OnPressIncButton()`        | Adds `Increment` to input, clamped to `[MinBet, MaxBet]`   |
| `SetInput(int)`             | Private setter with clamp logic                             |
| `SetPayoff(string odds)`    | Writes the odds string to `PayoffLabel`                     |

Multiple `UIBettingRow` instances are likely parented to a single betting panel (class name
Unknown — no `UIBettingPanel` or `UIArenaWindow` found in the dump).

### Bet flow

No dedicated `ProcessBetting*` or `ProcessArena*` handler exists in `IAreaCmdProcessor`.
The betting session is driven entirely through existing generic handlers:

1. Player interacts with the betting NPC (Kuzavek or similar).
2. Server sends `ProcessTalkScreen(entityIdTalker, title, text, notification, choiceIDs[],
   choices[], curState, TalkScreenType)` — this populates the betting UI panel with fight
   options and odds.
3. Player sets wager amounts via `UIBettingRow.Input` / `IncButton`.
4. Player confirms via a TalkScreen choice ID.
5. Server resolves the fight off-screen and returns another `ProcessTalkScreen` with the
   result (win/lose message) and updated gold balance.

This is the same pathway used by rune puzzles, golf, and other embedded NPC interactions.

### Monster matchups

Which monsters fight in each match: Unknown — not in CDN JSON at time of writing.
Quests confirm Otis the Ogre (`NPC_Otis`, pos x:-0.65 y:4.5 z:180.74) is an arena participant.
Another referenced fighter is "Ushug" (mentioned in quest_15304: "USHUG HAS SECRET...
TEACH USHUG NO CHEATING IN ARENA!") — Ushug's NPC script name is Unknown; not in npcs.json.

### Time limits

Time between fights / window to place a bet: Unknown — not in CDN JSON at time of writing.

---

## Odds and payout system

### Odds string

`UIBettingRow.SetPayoff(string odds)` receives a pre-formatted string from the server (e.g.
`"2:1"`, `"3:2"`).  The client displays it verbatim; **all odds computation is server-side**.

### Payout formula

Unknown — not exposed in CDN JSON or client dump.  The client only reads `MinBet`, `MaxBet`,
and the odds string; it does not compute expected payouts locally.

### Wager limits

`MinBet` (0x40) and `MaxBet` (0x44) on each `UIBettingRow` are populated per-fight by the
server.  Exact values for live fights: Unknown.

---

## Protocol / memory hooks

**Log hook:**
Watch `Player.log` for `ProcessTalkScreen` calls where `entityIdTalker` matches the betting
NPC's entity ID (Kuzavek, entity ID numeric 13138 in-game, though exact runtime entity ID may
differ from the NPC script ID).  The `title`/`text` payload carries the fight description and
odds strings.  Exact log-line format: Unknown — not confirmed by live log capture.

**Direct memory hook:**
There is no singleton for the betting UI.  To observe bet state, find the active `UIBettingRow`
instance(s) at runtime and read:

- `MinBet`   @ instance + 0x40  (int32)
- `MaxBet`   @ instance + 0x44  (int32)
- `Increment`@ instance + 0x48  (int32)

The `PayoffLabel.text` string (two pointer hops from 0x38) gives the current odds.

There is no equivalent to the Match-3 or Dice Game log-driven protocol for betting —
it piggybacks on `TalkScreen` with no dedicated command type.

---

## System integrations

### Currency

Bets are placed in standard gold (`CurrencyType.Gold`).  Winnings are also returned as gold
via the `ProcessSetCurrency` pathway (inferred from standard TalkScreen reward flow).

### Quest tie-ins

| Quest ID   | Name          | NPC          | Relation to arena                                          |
|------------|---------------|--------------|------------------------------------------------------------|
| quest_15301| NEW CLUB      | NPC_Otis     | Otis wants Perfect Cedar Wood to craft a new arena club    |
| quest_15304| NEED POTION   | NPC_Otis     | Otis wants a 20% Staff Boost Potion for arena use; rewards 1000g + 5× Red Wing Tokens (`item_14053`) |

No quests specifically about *placing a bet* were found in quests.json.

### Red Wing Token (`item_14053`)

The arena-adjacent reward currency.  Description: *"This reddish token is made of very thin
metal."*  Obtained from Otis quest_15304 (5 tokens).  Redemption shop: Unknown — likely
`NPC_Qatik` or another Casino NPC barter.

### Favor interactions

Otis (`NPC_Otis`) preferences: loves Meat-Heavy Prepared Meals (Pref 2), Clubs (Pref 2),
Hammers (Pref 1.5).  Raising Otis favor is required to unlock quest_15304 (Friends level).

Kuzavek favor: Unknown — NPC not in npcs.json.

---

## Monetization

### Expected value

Cannot be computed — payout formula and fight-win probabilities are not in the CDN data.
The house odds string is server-authored; there is no client-side sanity check.

### Tickets and special items

No betting-specific ticket or payout-item was found in `items.json` or `itemuses.json`.
The `item_14043`–`item_14045` free-play tickets are for **Monsters and Mantids** and
**Match-3** tables, not for arena betting.

### Red Wing Token economy

`item_14053` (Red Wing Token) is obtainable from Otis quests.  Barter catalogue (what tokens
buy): Unknown — not in CDN npcs.json services at time of writing.

### Arena-related item drops

- `item_43035` — Orcish Gladiator Gloves (used by orcish arena fighters; not a drop, but
  confirms arena fighters exist as entities).
- `item_55515` — Otis's Gift Club (reward from Otis after quest_15301; value/stats not read).

---

## Where to bet

**Red Wing Casino** (`AreaCasino`, friendly name "Red Wing Casino").

| NPC              | Script name    | Position (x / y / z)     | Role                              |
|------------------|----------------|---------------------------|-----------------------------------|
| Kuzavek          | Unknown        | Unknown                   | Betting NPC (NPC ID 13138)        |
| Otis the Ogre    | NPC_Otis       | -0.65 / 4.5 / 180.74      | Arena fighter; quest giver        |
| Ushug            | Unknown        | Unknown                   | Arena fighter (mentioned in quest)|
| Lady Alethina    | NPC_LadyAlethina| 16.27 / 4.5 / 70.66      | "Gambling and watching people"    |

No landmarks.json entries specifically label an arena pit or betting window within `AreaCasino`.

Travel to Red Wing Casino: Unknown — no portal or teleport circle labelled "Red Wing Casino"
appears in landmarks.json for any area (the casino is a self-contained zone).

---

## Open questions

1. **Kuzavek NPC script name** — what is the internal script key for NPC 13138?  Not in
   npcs.json; would need to be read from in-game or from `ai.json`.
2. **Ushug NPC identity** — is Ushug a second arena fighter NPC or a mob?
3. **Fight schedule** — how often do fights run?  Fixed timer, player-triggered, or
   server-scheduled event?
4. **Payout formula** — is it simple (bet × numerator / denominator) or does the house take
   a cut?
5. **Wager caps** — what are the actual `MinBet`/`MaxBet` values served for each fight?
6. **UIBettingPanel parent class** — the class that owns and populates `UIBettingRow[]` instances
   was not found in the Cpp2IL dump; it may be dynamically instantiated or stripped.
7. **Red Wing Token barter catalogue** — what can `item_14053` be exchanged for?
8. **Access route to Red Wing Casino** — portal location not found in landmarks.json; likely
   requires in-game discovery or a quest trigger.
