# Minigame: Word Game

## Overview

The Word Game is a server-wide, time-limited multiplayer event where all players in a zone compete to
find as many valid words as possible from a fixed set of letters. It appears to be a recurring
scheduled event (not player-initiated). Players can opt out of the current round via
`_isSittingThisGameOut`. The game is accessed via the `/wordgame` command, which opens
`UIWindowTitlebar` managed by `UIWordGame`.

## Mechanics

**Turn flow:**
1. Server broadcasts `ProcessShowWordGame()` — opens the word game window for all eligible players.
2. `WordGameState` is pushed via `ProcessWordGameStatus(...)`, containing:
   - `curWord` — the scrambled/available letters for this round.
   - `highestPossibleWordCount` — how many valid words exist in the letter set.
   - `msRemaining` — countdown timer in milliseconds.
   - `isGameActive` — whether the round is live.
   - `curScores` — `List<WordGameScoreInfo>` with all players' running scores.
3. Players type guesses into `CurrentGuess` (a `UIChatInput` field) and press submit
   (`UIWordGame.OnSubmitWord()`).
4. Client validates the guess locally using `BreakDownLetters()` + `IsSubset()` before sending,
   ensuring the guess only uses letters available in `curWord`.
5. Client sends `ClientCommandEncoder.prepMetaWordGameGuess(string wordGuess)` to the server.
6. Server responds with `ProcessWordGameStatus(WordGameAttemptResult guessResult, string guessedWord, WordGameState gameState)`.

**Guess results (`WordGameAttemptResult` enum):**
| Value | Name | Meaning |
|-------|------|---------|
| 0 | `Error` | Generic server error |
| 1 | `Success` | Word accepted, score awarded |
| 2 | `InvalidWord` | Not a valid dictionary word |
| 3 | `AlreadyGuessedWord` | Player already submitted this word |
| 4 | `GameOver` | Round has ended |

**Letter validation (client-side):**
- `BreakDownLetters(string word, Int32[] buckets)` — converts a word to a per-letter frequency array.
- `IsSubset(Int32[] targetWord, Int32[] guess)` — returns true if all letters in the guess are present
  in the current word's letter set.

**Scoring:**
- Each player accumulates `score` and `wordCount` tracked in `WordGameScoreInfo`.
- `bestWord` (string) is tracked per player in `WordGameScoreInfo`.
- Full scoreboard is included in every `WordGameState` push.
- Scoring formula (points per word, e.g., by length) — **Unknown — not found in dump at time of writing**.

**Win/lose states:**
- Round ends when `msRemaining` reaches 0 or `isGameActive` goes false.
- No explicit "loss" state — players simply accumulate fewer points than competitors.
- `GameOver` attempt result (4) signals the round has closed.
- After the round, the `GameOverScreen` is displayed, showing `LastGameLetters` and the final
  `LastScoresArea` leaderboard until `NextGameTimer` counts down to the next round.

**Polling:**
- `CoroSendUpdates` coroutine fires `ClientCommandEncoder.prepMetaWordGamePoll()` periodically to
  refresh game state when the window is open.
- `_lastStateUpdate` (float) tracks the timestamp of the last received state.

## Protocol / Memory Hooks

### Key Classes

| Class | Assembly | Notes |
|-------|----------|-------|
| `UIWordGame` | Assembly-CSharp | Singleton MonoBehaviour; owns all UI and static game state |
| `WordGameState` | GorgonCore | Server-pushed state snapshot |
| `WordGameScoreInfo` | GorgonCore | Per-player score entry |
| `WordGameAttemptResult` | GorgonCore | Enum: guess outcome |

### `UIWordGame` Field Offsets (instance, base = MonoBehaviour)

| Field | Type | Offset |
|-------|------|--------|
| `_guessedWordsIsDirty` (static) | `bool` | `0x0` |
| `_guessedWords` (static) | `List<string>` | `0x8` |
| `_isSittingThisGameOut` (static) | `bool` | `0x10` |
| `_lastStateUpdate` (static) | `float` | `0x14` |
| `_gameState` (static) | `WordGameState` | `0x18` |
| `myWindow` | `UIWindowTitlebar` | `0x20` |
| `WaitScreen` | `GameObject` | `0x28` |
| `WaitTime` | `Text` | `0x30` |
| `GameScreen` | `GameObject` | `0x38` |
| `CurrentLetters` | `Text` | `0x40` |
| `CurrentGuess` | `UIChatInput` | `0x48` |
| `FoundWordCount` | `Text` | `0x50` |
| `PossibleWordCount` | `Text` | `0x58` |
| `PreviousGuessesArea` | `Transform` | `0x60` |
| `PreviousGuessPrefab` | `GameObject` | `0x68` |
| `GameTimeRemaining` | `Text` | `0x70` |
| `GameOverScreen` | `GameObject` | `0x78` |
| `NextGameTimer` | `Text` | `0x80` |
| `LastGameLetters` | `Text` | `0x88` |
| `LastScoresArea` | `Transform` | `0x90` |
| `LastScorePrefab` | `UIUniversalPrefab` | `0x98` |
| `lastGameState` | `WordGameState` | `0xA0` |
| `curWordLetterBreakdown` | `int[]` | `0xA8` |
| `curGuessLetterBreakdown` | `int[]` | `0xB0` |
| `lastUpdatedGameOver` | `float` | `0xB8` |
| `initInfo` | `WindowTemplateInfo` | `0xC0` |

### `WordGameState` Field Offsets

| Field | Type | Offset |
|-------|------|--------|
| `instanceId` | `int` | `0x10` |
| `isGameActive` | `bool` | `0x14` |
| `msRemaining` | `long` | `0x18` |
| `curWord` | `string` | `0x20` |
| `highestPossibleWordCount` | `int` | `0x28` |
| `curScores` | `List<WordGameScoreInfo>` | `0x30` |

### `WordGameScoreInfo` Field Offsets

| Field | Type | Offset |
|-------|------|--------|
| `isPlayer` | `bool` | `0x10` |
| `playerName` | `string` | `0x18` |
| `score` | `int` | `0x20` |
| `wordCount` | `int` | `0x24` |
| `bestWord` | `string` | `0x28` |

### Message Handlers (in `AreaCmdProcessor_NewGui` / `IAreaCmdProcessor`)

| Direction | Method | Signature |
|-----------|--------|-----------|
| Server → Client | `ProcessShowWordGame()` | No params; opens the window |
| Server → Client | `ProcessWordGameStatus(...)` | `(WordGameAttemptResult guessResult, string guessedWord, WordGameState gameState)` |
| Client → Server | `prepMetaWordGameGuess(string wordGuess)` | In `ClientCommandEncoder`; returns `Cmd` |
| Client → Server | `prepMetaWordGamePoll()` | In `ClientCommandEncoder`; returns `Cmd` |

### Static Entry Points for a Solver Hook

- `UIWordGame.OnGameStateUpdate(WordGameState gameState)` — called whenever the server pushes a new state.
- `UIWordGame.OnWordAttemptResult(string guessedWord, WordGameAttemptResult result)` — called on each guess response.

## System Integrations

**Skills:** No skill named "WordGame" found in skills.json. No XP table or advancement hints found. Word Game advancement / XP — **Unknown — not found in dump at time of writing**.

**Effects:** `effect_13721` has `Keywords: ["Word"]`, `Duration: 7200`, `Desc: "Your mind is racing at insane speeds!\n+1 power/sec"`, `IconId: 4004`. Name field was not captured in grep context. This appears to be a post-game buff reward.

**Quests:** No word-game quests found in quests.json.

**Directed Goals:** No word-game directed goals found in directedgoals.json.

**Consumed items:** The Word Game scroll (item_1186) has `UseVerb: "Play Game"` and `Keywords: ["VendorTrash"]` — its exact role in accessing the game (required to enter, or cosmetic ticket) is **Unknown — not confirmed in dump at time of writing**.

## Monetization

**Item: Word Game scroll**
- `item_1186`, `InternalName: "WordGame"`, `Name: "Word Game"`
- `Description: "A magical scroll that lets you play a word game."`
- `Value: 50` (sell price to vendors)
- `IconId: 4005`
- `UseVerb: "Play Game"`, `UseAnimation: "UseItem"`
- `Keywords: ["VendorTrash"]` (i.e., no player-shop resale)
- `MaxStackSize: 1`
- Sold by **Marna** (NPC_Marna), Serbule — confirmed via sources_items.json `item_1186` entry.

**Payout / reward items:** Not found in itemuses.json or sources_items.json beyond the scroll entry. Post-round rewards — **Unknown — not found in dump at time of writing**.

## Where to Play

| Field | Value |
|-------|-------|
| NPC | Marna (`NPC_Marna`) |
| Area | `AreaSerbule` (Serbule) |
| NPC Position | x:1408.790039  y:45.847092  z:1514.869995 |
| NPC Role | Vendor — sells Word Game scroll (item_1186) |

Whether the word game event is zone-specific or server-global, and whether other NPCs host it, is
**Unknown — not found in dump at time of writing**.

## Open Questions

1. **XP / skill progression** — Is there a WordGame skill, or does it advance a parent skill? Not found in skills.json.
2. **Scoring formula** — How many points per word? Is length or rarity weighted?
3. **Scroll as entry requirement** — Does the player need item_1186 in inventory to participate, or is it purely a cosmetic/unlock item?
4. **Effect_13721 name** — The name of the post-game "+1 power/sec" Word buff was not captured in the grep. Needs a targeted read of effects.json around line 9251.
5. **Server schedule** — How frequently does a round start? Timer between rounds visible in `NextGameTimer` but server-side schedule is unknown.
6. **Dictionary source** — What word list does the server validate against? Not in CDN JSONs.
7. **Multi-area support** — Does the word game run in areas other than Serbule?
