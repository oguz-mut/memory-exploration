using FarmBot;

// ══════════════════════════════════════════════════════════════════════════════
// CONFIGURATION — Edit these to match your setup
// ══════════════════════════════════════════════════════════════════════════════

// Screen coordinates for each seed slot on the bottom row of your inventory.
// To find these: hover over each seed slot and note the pixel position.
// Each entry is (x, y) for that seed's inventory slot.
int[][] seedSlots =
[
    [1630, 910],  // Seed slot 1
    [1670, 910],  // Seed slot 2
    [1710, 910],  // Seed slot 3
    [1750, 910],  // Seed slot 4
    [1790, 910],  // Seed slot 5
    [1830, 910],  // Seed slot 6
];

// How many seed slots are actually in use (set to match your seed count)
int activeSeedCount = 3;

// How many plants total (activeSeedCount * 2, since 2 per seed type)
int totalPlants = activeSeedCount * 2;

// How many tend cycles (< + ") per round — should cover all plants + water + fertilizer
// Set this higher than totalPlants to account for water/fertilizer objects nearby
int tendCyclesPerRound = 12;

// Delays (ms)
int delayAfterPlant = 1500;        // Wait after double-clicking a seed
int delayBetweenSelectAndInteract = 500;  // Wait between < and "
int delayAfterInteract = 2000;     // Wait after interacting (water/fert/harvest animation)
int delayBetweenTendCycles = 500;  // Pause between each < + " pair
int delayBetweenRounds = 5000;     // Pause between full tend rounds (plant growth time)
int waterFillWaitMs = 15000;       // How long to wait at water trough
int fertilzerCraftWaitMs = 3000;   // How long to wait for fertilizer craft

// How many rounds before auto-crafting fertilizer (0 = never auto-craft)
int craftFertilizerEveryNRounds = 3;

// How many rounds before auto-refilling water (0 = only when detected empty)
int refillWaterEveryNRounds = 5;

// Parse CLI overrides
foreach (var arg in args)
{
    if (arg.StartsWith("--seeds=") && int.TryParse(arg["--seeds=".Length..], out int s))
        activeSeedCount = s;
    if (arg.StartsWith("--tend=") && int.TryParse(arg["--tend=".Length..], out int t))
        tendCyclesPerRound = t;
}

// ══════════════════════════════════════════════════════════════════════════════
// SETUP
// ══════════════════════════════════════════════════════════════════════════════

string logDir = @"C:\Users\oguzb\AppData\LocalLow\Elder Game\Project Gorgon";
string logPath = Path.Combine(logDir, "Player.log");
if (!File.Exists(logPath) || new FileInfo(logPath).Length == 0)
    logPath = Path.Combine(logDir, "Player-prev.log");
if (!File.Exists(logPath))
{
    Console.WriteLine("No Player.log found. Exiting.");
    return;
}

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

var log = new FarmLogWatcher(logPath);
_ = Task.Run(() => log.Run(ct));
await Task.Delay(500);

// ══════════════════════════════════════════════════════════════════════════════
// DISPLAY
// ══════════════════════════════════════════════════════════════════════════════

Console.WriteLine("╔══════════════════════════════════════════════════╗");
Console.WriteLine("║            FARMBOT — Garden Automation           ║");
Console.WriteLine("╠══════════════════════════════════════════════════╣");
Console.WriteLine($"║  Active seeds:    {activeSeedCount,-31}║");
Console.WriteLine($"║  Plants:          {totalPlants,-31}║");
Console.WriteLine($"║  Tend cycles:     {tendCyclesPerRound,-31}║");
Console.WriteLine("╠══════════════════════════════════════════════════╣");
Console.WriteLine("║  F5  = Start / Pause                            ║");
Console.WriteLine("║  ESC = Quit                                     ║");
Console.WriteLine("╠══════════════════════════════════════════════════╣");
Console.WriteLine("║  Workflow:                                       ║");
Console.WriteLine("║  1. Place seeds on bottom inventory row          ║");
Console.WriteLine("║  2. Stand in your garden                         ║");
Console.WriteLine("║  3. Press F5 to start                            ║");
Console.WriteLine("╚══════════════════════════════════════════════════╝");
Console.WriteLine();

// ══════════════════════════════════════════════════════════════════════════════
// STATE MACHINE
// ══════════════════════════════════════════════════════════════════════════════

var state = FarmState.Idle;
int roundCount = 0;
bool running = false;

Console.WriteLine("[*] Press F5 to start farming, ESC to quit.");

while (!ct.IsCancellationRequested)
{
    // Check for ESC at any point
    if (InputSender.IsEscapePressed())
    {
        Console.WriteLine("[*] ESC pressed — stopping.");
        cts.Cancel();
        break;
    }

    switch (state)
    {
        // ── IDLE: wait for F5 ──
        case FarmState.Idle:
        {
            var key = await InputSender.WaitForHotkey(ct);
            if (key == HotKey.F5)
            {
                running = true;
                state = FarmState.Planting;
                Console.WriteLine();
                Console.WriteLine("[>] Starting farming loop...");
            }
            else if (key == HotKey.Escape)
            {
                cts.Cancel();
            }
            break;
        }

        // ── PLANTING: double-click each seed slot twice ──
        case FarmState.Planting:
        {
            Console.WriteLine($"[>] Planting {activeSeedCount} seed types (2 each)...");

            if (!InputSender.FocusGameWindow())
            {
                Console.WriteLine("[!] Game window not found!");
                state = FarmState.Idle;
                break;
            }
            await Task.Delay(300, ct);

            for (int i = 0; i < activeSeedCount && i < seedSlots.Length; i++)
            {
                int x = seedSlots[i][0];
                int y = seedSlots[i][1];

                // Plant 1 of this seed
                Console.WriteLine($"  [plant] Seed {i + 1}: double-click ({x}, {y}) — plant 1/2");
                InputSender.DoubleClick(x, y);
                await Task.Delay(delayAfterPlant, ct);

                // Plant 2 of this seed
                Console.WriteLine($"  [plant] Seed {i + 1}: double-click ({x}, {y}) — plant 2/2");
                InputSender.DoubleClick(x, y);
                await Task.Delay(delayAfterPlant, ct);
            }

            Console.WriteLine("[*] All seeds planted. Starting tend cycle...");
            await Task.Delay(2000, ct); // Brief wait for plants to appear
            state = FarmState.Tending;
            break;
        }

        // ── TENDING: cycle through plants with < and " ──
        case FarmState.Tending:
        {
            roundCount++;
            Console.WriteLine($"[>] Tend round #{roundCount} — cycling {tendCyclesPerRound} targets...");

            if (!InputSender.FocusGameWindow())
            {
                Console.WriteLine("[!] Game window not found!");
                state = FarmState.Idle;
                break;
            }
            await Task.Delay(200, ct);

            for (int i = 0; i < tendCyclesPerRound; i++)
            {
                // Check for pause/stop
                var hotkey = InputSender.CheckHotkey();
                if (hotkey == HotKey.Escape) { cts.Cancel(); break; }
                if (hotkey == HotKey.F5)
                {
                    Console.WriteLine("[*] Paused. Press F5 to resume.");
                    running = false;
                    state = FarmState.Idle;
                    break;
                }

                // Select next target
                InputSender.PressSelectNext();
                await Task.Delay(delayBetweenSelectAndInteract, ct);

                // Interact
                InputSender.PressInteract();
                await Task.Delay(delayAfterInteract, ct);

                // Small gap before next cycle
                await Task.Delay(delayBetweenTendCycles, ct);
            }

            if (ct.IsCancellationRequested || !running) break;

            // Check if we need water (detected from log)
            if (log.NeedsWater)
            {
                state = FarmState.RefillingWater;
                break;
            }

            // Periodic water refill
            if (refillWaterEveryNRounds > 0 && roundCount % refillWaterEveryNRounds == 0)
            {
                state = FarmState.RefillingWater;
                break;
            }

            // Periodic fertilizer craft
            if (craftFertilizerEveryNRounds > 0 && roundCount % craftFertilizerEveryNRounds == 0)
            {
                state = FarmState.CraftingFertilizer;
                break;
            }

            // Replant cycle — after tending, replant then tend again
            if (roundCount % 2 == 0)
            {
                // Every other round, replant in case plants were harvested
                state = FarmState.Planting;
            }
            else
            {
                // Wait for plants to grow, then tend again
                Console.WriteLine($"[*] Waiting {delayBetweenRounds / 1000}s for plant growth...");
                await Task.Delay(delayBetweenRounds, ct);
                state = FarmState.Tending;
            }
            break;
        }

        // ── CRAFTING FERTILIZER: C then X ──
        case FarmState.CraftingFertilizer:
        {
            Console.WriteLine("[>] Crafting fertilizer (C → X)...");

            if (!InputSender.FocusGameWindow())
            {
                Console.WriteLine("[!] Game window not found!");
                state = FarmState.Tending;
                break;
            }
            await Task.Delay(200, ct);

            // Open crafting
            InputSender.PressC();
            await Task.Delay(1000, ct);

            // Start craft
            InputSender.PressX();
            await Task.Delay(fertilzerCraftWaitMs, ct);

            Console.WriteLine("[*] Fertilizer crafted. Resuming tending...");
            state = FarmState.Tending;
            break;
        }

        // ── REFILLING WATER: /target water trough → interact → wait → return ──
        case FarmState.RefillingWater:
        {
            Console.WriteLine("[>] Refilling water bottles...");
            log.ClearWaterFlag();

            if (!InputSender.FocusGameWindow())
            {
                Console.WriteLine("[!] Game window not found!");
                state = FarmState.Tending;
                break;
            }
            await Task.Delay(200, ct);

            // Target the water trough via chat command
            InputSender.PressEnter();
            await Task.Delay(300, ct);
            InputSender.TypeText("/target water trough");
            await Task.Delay(100, ct);
            InputSender.PressEnter();
            await Task.Delay(500, ct);

            // Interact to auto-walk to it
            Console.WriteLine("[>] Walking to water trough...");
            InputSender.PressInteract();
            await Task.Delay(5000, ct); // Walk time

            // Interact again to start filling
            InputSender.PressInteract();
            await Task.Delay(1000, ct);

            // Wait for filling to complete
            Console.WriteLine($"[>] Filling water bottles... ({waterFillWaitMs / 1000}s)");
            await Task.Delay(waterFillWaitMs, ct);

            // Now target a plant to walk back
            Console.WriteLine("[>] Walking back to garden...");
            InputSender.PressEnter();
            await Task.Delay(300, ct);
            InputSender.TypeText("/target plant");
            await Task.Delay(100, ct);
            InputSender.PressEnter();
            await Task.Delay(500, ct);

            InputSender.PressInteract();
            await Task.Delay(5000, ct); // Walk back time

            Console.WriteLine("[*] Water refilled. Resuming tending...");
            state = FarmState.Tending;
            break;
        }
    }
}

Console.WriteLine($"[*] FarmBot stopped. Rounds completed: {roundCount}");

enum FarmState
{
    Idle,
    Planting,
    Tending,
    CraftingFertilizer,
    RefillingWater
}
