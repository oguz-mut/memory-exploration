using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MnMSolver;

internal static class DecisionEngine
{
    // -------------------------------------------------------------------------
    // 1. Monster Database
    // -------------------------------------------------------------------------

    internal record MonsterInfo(
        string Name,
        int DiceCount,
        int DiceSides,
        int Bonus,
        double AvgDamage,
        int MinDamage,
        int MaxDamage);

    private static MonsterInfo MakeMonster(string name, int count, int sides, int bonus)
    {
        double avg = AverageRoll(count, sides, bonus);
        int min = count + bonus;
        int max = count * sides + bonus;
        return new MonsterInfo(name, count, sides, bonus, avg, min, max);
    }

    internal static readonly Dictionary<string, MonsterInfo> Monsters =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Tier 1 (1D6 class)
            ["Ornery Sheep"]             = MakeMonster("Ornery Sheep",             1, 6, 0),
            ["Lost Pig"]                 = MakeMonster("Lost Pig",                 1, 6, 0),
            ["Mimic"]                    = MakeMonster("Mimic",                    1, 6, 1),
            ["Giant Maggot"]             = MakeMonster("Giant Maggot",             1, 6, 1),
            ["Ranalon Scout"]            = MakeMonster("Ranalon Scout",            1, 6, 1),
            ["Zombie"]                   = MakeMonster("Zombie",                   1, 6, 1),
            // Tier 2 (1D6+2-4)
            ["Human Rogue"]              = MakeMonster("Human Rogue",              1, 6, 2),
            ["Giant Cave Asp"]           = MakeMonster("Giant Cave Asp",           1, 6, 2),
            ["Feral Wolf"]               = MakeMonster("Feral Wolf",               1, 6, 2),
            ["Weak Bloodfang Spider"]    = MakeMonster("Weak Bloodfang Spider",    1, 6, 2),
            ["Cave Bear"]                = MakeMonster("Cave Bear",                1, 6, 3),
            ["Skeleton Swordsman"]       = MakeMonster("Skeleton Swordsman",       1, 6, 4),
            // Tier 3 (2D6 / 1D6+5-6)
            ["Fiendish Lobster"]         = MakeMonster("Fiendish Lobster",         2, 6, -1),
            ["Explorer Golem"]           = MakeMonster("Explorer Golem",           2, 6, -1),
            ["Questing Knight"]          = MakeMonster("Questing Knight",          1, 6, 5),
            ["Regular Bloodfang Spider"] = MakeMonster("Regular Bloodfang Spider", 1, 6, 5),
            ["Mummy"]                    = MakeMonster("Mummy",                    1, 6, 6),
            ["Orc"]                      = MakeMonster("Orc",                      2, 6, 0),
            // Tier 4 (2D6+ / 1D6+7+)
            ["Fairy Scion"]              = MakeMonster("Fairy Scion",              2, 6, 1),
            ["Human Sorceress"]          = MakeMonster("Human Sorceress",          2, 6, 2),
            ["Skeletal Dragon"]          = MakeMonster("Skeletal Dragon",          1, 6, 7),
            ["Injured Orc Berserker"]    = MakeMonster("Injured Orc Berserker",    2, 6, 3),
            ["Spider with Babies"]       = MakeMonster("Spider with Babies",       2, 6, 3),
            ["Enraged Golem"]            = MakeMonster("Enraged Golem",            2, 6, 3),
            ["Dwarf Warlord"]            = MakeMonster("Dwarf Warlord",            2, 6, 3),
            ["Ultrasnail"]               = MakeMonster("Ultrasnail",               2, 6, 3),
            // Tier 5 (boss)
            ["Council Assassin"]         = MakeMonster("Council Assassin",         1, 6, 9),
            ["Scion of Gulagra"]         = MakeMonster("Scion of Gulagra",         2, 6, 5),
            ["Enterprising Demon"]       = MakeMonster("Enterprising Demon",       3, 6, 2),
            ["Remnant of ZEK"]           = MakeMonster("Remnant of ZEK",           4, 6, 0),
        };

    // -------------------------------------------------------------------------
    // 2. Dice Math
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parse dice notation like "2D6+3", "1D6", "3D6+2", "2D-1", "1D6+1".
    /// Sides defaults to 6 if not specified (e.g. "2D+3" => 2d6+3).
    /// Returns (count, sides, bonus).
    /// </summary>
    internal static (int count, int sides, int bonus) ParseDice(string notation)
    {
        if (string.IsNullOrWhiteSpace(notation))
            return (1, 6, 0);

        notation = notation.Trim();
        // Pattern: optionally digits, D, optionally digits, optionally +/- digits
        var match = Regex.Match(notation,
            @"^(?<count>\d+)?[Dd](?<sides>\d+)?(?<sign>[+\-])(?<bonus>\d+)$|^(?<count2>\d+)?[Dd](?<sides2>\d+)?$",
            RegexOptions.IgnoreCase);

        if (!match.Success)
            return (1, 6, 0);

        int count, sides, bonus;

        if (match.Groups["count"].Success || match.Groups["sides"].Success ||
            match.Groups["sign"].Success || match.Groups["bonus"].Success)
        {
            count = match.Groups["count"].Success && match.Groups["count"].Value.Length > 0
                ? int.Parse(match.Groups["count"].Value) : 1;
            sides = match.Groups["sides"].Success && match.Groups["sides"].Value.Length > 0
                ? int.Parse(match.Groups["sides"].Value) : 6;
            int bonusVal = match.Groups["bonus"].Success && match.Groups["bonus"].Value.Length > 0
                ? int.Parse(match.Groups["bonus"].Value) : 0;
            string sign = match.Groups["sign"].Value;
            bonus = sign == "-" ? -bonusVal : bonusVal;
        }
        else
        {
            count = match.Groups["count2"].Success && match.Groups["count2"].Value.Length > 0
                ? int.Parse(match.Groups["count2"].Value) : 1;
            sides = match.Groups["sides2"].Success && match.Groups["sides2"].Value.Length > 0
                ? int.Parse(match.Groups["sides2"].Value) : 6;
            bonus = 0;
        }

        if (count < 1) count = 1;
        if (sides < 2) sides = 6;
        return (count, sides, bonus);
    }

    /// <summary>
    /// Returns a probability distribution over all possible outcomes of NdS+B.
    /// Result is indexed by value; result[v] = P(roll == v).
    /// Index 0 is always 0 (unused). Array length = maxValue + 1.
    /// </summary>
    internal static double[] DiceDistribution(int count, int sides, int bonus)
    {
        if (count < 1) count = 1;
        if (sides < 2) sides = 2;

        // DP: build distribution for 'count' dice of 'sides' sides
        // Start with 1 die
        int maxNoBon = count * sides;
        int minNoBon = count;
        double[] dist = new double[maxNoBon + 1];
        double faceSelf = 1.0 / sides;

        // Single die
        double[] single = new double[sides + 1];
        for (int f = 1; f <= sides; f++)
            single[f] = faceSelf;

        // Convolve
        double[] current = single;
        for (int d = 1; d < count; d++)
        {
            double[] next = new double[current.Length + sides];
            for (int a = 1; a < current.Length; a++)
            {
                if (current[a] == 0) continue;
                for (int f = 1; f <= sides; f++)
                    next[a + f] += current[a] * faceSelf;
            }
            current = next;
        }

        // Apply bonus: shift all values
        int newMin = minNoBon + bonus;
        int newMax = maxNoBon + bonus;
        // Build result indexed by actual value (may go negative)
        // We'll return with offset; caller uses ProbRollLessThan for math
        // For simplicity, return array where index = value (offset if needed)
        // Index starts at 0, value = index, so store at index (value + offset)
        // Use absolute indexing from 0: result[v] where v = actual roll value
        // Negative values: clamp array at 0
        int arrayMin = Math.Max(0, newMin);
        int arrayMax = Math.Max(arrayMin, newMax);
        double[] result = new double[arrayMax + 1];

        for (int i = minNoBon; i <= maxNoBon; i++)
        {
            double prob = (i < current.Length) ? current[i] : 0.0;
            int val = i + bonus;
            if (val >= 0 && val < result.Length)
                result[val] = prob;
            else if (val < 0)
                result[0] += prob; // clamp negatives to 0
        }

        return result;
    }

    /// <summary>P(NdS+B &lt; threshold)</summary>
    internal static double ProbRollLessThan(int count, int sides, int bonus, int threshold)
    {
        if (count < 1) count = 1;
        if (sides < 2) sides = 2;

        double[] dist = DiceDistribution(count, sides, bonus);
        double prob = 0.0;
        int limit = Math.Min(threshold, dist.Length);
        for (int v = 0; v < limit; v++)
            prob += dist[v];
        return Math.Clamp(prob, 0.0, 1.0);
    }

    /// <summary>Expected value of NdS+B.</summary>
    internal static double AverageRoll(int count, int sides, int bonus)
    {
        if (count < 1) count = 1;
        if (sides < 2) sides = 2;
        return count * (sides + 1) / 2.0 + bonus;
    }

    // -------------------------------------------------------------------------
    // 3. Survival Probability
    // -------------------------------------------------------------------------

    /// <summary>
    /// Estimates enemy dice based on encounter depth when no name/dice known.
    /// </summary>
    internal static (int count, int sides, int bonus) EstimateEnemyDice(int encounterNumber)
    {
        if (encounterNumber <= 3)  return (1, 6, 1);  // early
        if (encounterNumber <= 7)  return (1, 6, 3);  // mid
        if (encounterNumber <= 12) return (2, 6, 1);  // late
        return (2, 6, 4);                              // boss
    }

    /// <summary>
    /// P(player survives one hit from enemy) = P(enemy damage &lt; currentHP).
    /// Uses monster DB lookup, then dice parse, then encounter-depth fallback.
    /// </summary>
    internal static double ProbSurvive(int currentHP, string? enemyName, string? enemyDice, int encounterNumber)
    {
        if (currentHP <= 0) return 0.0;

        int count, sides, bonus;

        if (!string.IsNullOrWhiteSpace(enemyName) && Monsters.TryGetValue(enemyName.Trim(), out var info))
        {
            count  = info.DiceCount;
            sides  = info.DiceSides;
            bonus  = info.Bonus;
        }
        else if (!string.IsNullOrWhiteSpace(enemyDice))
        {
            (count, sides, bonus) = ParseDice(enemyDice);
        }
        else
        {
            (count, sides, bonus) = EstimateEnemyDice(encounterNumber);
        }

        // P(survive) = P(enemy roll < currentHP) i.e. damage does not kill
        return ProbRollLessThan(count, sides, bonus, currentHP);
    }

    // -------------------------------------------------------------------------
    // 4. Post-Victory Decision (THE CORE)
    // -------------------------------------------------------------------------

    private const double TOKEN_VALUE    = 50.0;
    private const double RISK_AVERSION  = 1.3;
    private const double EXPECTED_GOLD_PER_FIGHT    = 15.0;
    private const double EXPECTED_ARTIFACT_CHANCE   = 0.15;

    internal static (string action, string rationale, double evCashOut, double evDelve, double evRest, double pSurvive)
        DecidePostVictory(
            int currentHP,
            int maxHP,
            int gold,
            int culturalArtifacts,
            int encounterNumber,
            string? nextEnemyName,
            string? nextEnemyDice,
            bool canRest,
            int estimatedHealDice)
    {
        double evCashOut = gold + culturalArtifacts * TOKEN_VALUE;
        double expectedGain = EXPECTED_GOLD_PER_FIGHT + EXPECTED_ARTIFACT_CHANCE * TOKEN_VALUE;

        double pSurvive = ProbSurvive(currentHP, nextEnemyName, nextEnemyDice, encounterNumber + 1);
        double evDelve  = pSurvive * (evCashOut + expectedGain);

        // Compute rest EV
        double evRest = 0.0;
        if (canRest && estimatedHealDice > 0 && maxHP > 0)
        {
            double healedHP = Math.Min(maxHP, currentHP + AverageRoll(estimatedHealDice, 6, 0));
            double pSurviveHealed = ProbSurvive((int)healedHP, nextEnemyName, nextEnemyDice, encounterNumber + 1);
            evRest = pSurviveHealed * (evCashOut + expectedGain);
        }

        // --- Decision rules ---

        // Rule: Never cash out at 0 gold (unless would otherwise die)
        bool noGold = gold == 0 && culturalArtifacts == 0;

        // Rule 6: Cash out if P(survive) < 0.4
        if (pSurvive < 0.4 && !noGold)
            return ("CashOut", $"Survival probability {pSurvive:P0} < 40% — too risky to continue.", evCashOut, evDelve, evRest, pSurvive);

        // Rule 7: Cash out if P(survive) < 0.6 AND gold > 20
        if (pSurvive < 0.6 && gold > 20 && !noGold)
            return ("CashOut", $"Survival probability {pSurvive:P0} < 60% with {gold}g banked — protect gains.", evCashOut, evDelve, evRest, pSurvive);

        // Rule 8: Rest if HP < 40% max AND healing improves survival by >15%
        if (canRest && maxHP > 0 && currentHP < 0.4 * maxHP)
        {
            double healedHP = Math.Min(maxHP, currentHP + AverageRoll(estimatedHealDice > 0 ? estimatedHealDice : 1, 6, 0));
            double pSurviveHealed = ProbSurvive((int)healedHP, nextEnemyName, nextEnemyDice, encounterNumber + 1);
            if (pSurviveHealed - pSurvive > 0.15)
                return ("Rest", $"HP critically low ({currentHP}/{maxHP}); rest improves survival by {(pSurviveHealed - pSurvive):P0}.", evCashOut, evDelve, evRest, pSurvive);
        }

        // Risk aversion: continue only if EV(continue) > EV(CashOut) * RISK_AVERSION
        double bestContinueEV = canRest ? Math.Max(evDelve, evRest) : evDelve;
        if (!noGold && bestContinueEV <= evCashOut * RISK_AVERSION)
        {
            string bestAction = (canRest && evRest > evDelve) ? "Rest" : "CashOut";
            if (bestAction == "CashOut")
                return ("CashOut", $"EV(continue)={bestContinueEV:F1} does not exceed EV(cashout)={evCashOut:F1} × {RISK_AVERSION} risk factor.", evCashOut, evDelve, evRest, pSurvive);
        }

        // Choose best action among continue options
        if (canRest && evRest > evDelve)
            return ("Rest", $"Rest EV={evRest:F1} > Delve EV={evDelve:F1}; recover before pressing on.", evCashOut, evDelve, evRest, pSurvive);

        return ("Delve", $"EV(delve)={evDelve:F1} > EV(cashout)={evCashOut:F1}; odds favour pressing on (P={pSurvive:P0}).", evCashOut, evDelve, evRest, pSurvive);
    }

    // -------------------------------------------------------------------------
    // 5. Combat Ability Selection
    // -------------------------------------------------------------------------

    internal record CombatAbility(string Name, double AverageDamage, bool IsFirstStrike);

    /// <summary>
    /// Returns the recommended ability name.
    /// Heavily favours first-strike when HP &lt;= estimated enemy average damage.
    /// </summary>
    internal static string SelectCombatAbility(
        IReadOnlyList<CombatAbility> abilities,
        int currentHP,
        string? enemyName,
        string? enemyDice,
        int encounterNumber)
    {
        if (abilities == null || abilities.Count == 0)
            return string.Empty;

        // Determine if HP is dangerously low
        double enemyAvg;
        if (!string.IsNullOrWhiteSpace(enemyName) && Monsters.TryGetValue(enemyName.Trim(), out var info))
            enemyAvg = info.AvgDamage;
        else if (!string.IsNullOrWhiteSpace(enemyDice))
        {
            var (c, s, b) = ParseDice(enemyDice);
            enemyAvg = AverageRoll(c, s, b);
        }
        else
        {
            var (c, s, b) = EstimateEnemyDice(encounterNumber);
            enemyAvg = AverageRoll(c, s, b);
        }

        bool dangerouslyLow = currentHP <= enemyAvg;

        CombatAbility? best = null;
        double bestScore = double.MinValue;

        foreach (var ability in abilities)
        {
            double score = ability.AverageDamage;
            if (dangerouslyLow && ability.IsFirstStrike)
                score += 1000.0; // heavy bias for first strike when in danger
            if (score > bestScore)
            {
                bestScore = score;
                best = ability;
            }
        }

        return best?.Name ?? abilities[0].Name;
    }

    // -------------------------------------------------------------------------
    // 6. Perk Selection
    // -------------------------------------------------------------------------

    private static readonly (string keyword, int priority)[] PerkPriorities =
    [
        ("lucky",      100),
        ("tough",       90),
        ("resilient",   90),
        ("robust",      90),
        ("diplomat",    80),
        ("charm",       80),
        ("heal",        75),
        ("regenerat",   75),
        ("strong",      70),
        ("fierce",      70),
        ("damage",      70),
    ];

    /// <summary>Returns the best perk from a list of perk name strings.</summary>
    internal static string SelectPerk(IReadOnlyList<string> perks)
    {
        if (perks == null || perks.Count == 0) return string.Empty;

        string? best = null;
        int bestPriority = -1;

        foreach (var perk in perks)
        {
            if (string.IsNullOrWhiteSpace(perk)) continue;
            int priority = 50; // unknown default
            foreach (var (keyword, p) in PerkPriorities)
            {
                if (perk.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    priority = p;
                    break;
                }
            }
            if (priority > bestPriority)
            {
                bestPriority = priority;
                best = perk;
            }
        }

        return best ?? string.Empty;
    }

    // -------------------------------------------------------------------------
    // 7. Hat Selection
    // -------------------------------------------------------------------------

    private static readonly (string keyword, int score)[] HatScores =
    [
        ("saving throw", 100),
        ("diplomacy",     90),
        ("health",        80),
        ("hp",            80),
        ("damage",        70),
        ("attack",        70),
        ("gold",          60),
        ("loot",          60),
        ("basic hat",      0),
    ];

    /// <summary>
    /// Returns true if the new hat (described by newHatDescription) is better
    /// than the current hat (described by currentHatDescription).
    /// </summary>
    internal static bool ShouldTakeHat(string? currentHatDescription, string? newHatDescription)
    {
        int ScoreHat(string? desc)
        {
            if (string.IsNullOrWhiteSpace(desc)) return 0;
            foreach (var (keyword, score) in HatScores)
                if (desc.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return score;
            return 50; // unknown hat — modest default
        }

        return ScoreHat(newHatDescription) > ScoreHat(currentHatDescription);
    }

    // -------------------------------------------------------------------------
    // 8. Diplomacy Decision
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns true if attempting diplomacy is advisable.
    /// Requires P(success) > 0.5 AND HP > 30% of max.
    /// </summary>
    internal static bool ShouldAttemptDiplomacy(int diplomacyBonus, int targetNumber, int currentHP, int maxHP)
    {
        double pSuccess = Prob3d6GeqTarget(targetNumber, diplomacyBonus);
        if (pSuccess <= 0.5) return false;
        if (maxHP > 0 && currentHP <= 0.3 * maxHP) return false;
        return true;
    }

    // -------------------------------------------------------------------------
    // 9. 3d6 Probability Helpers
    // -------------------------------------------------------------------------

    /// <summary>P(3d6 + bonus >= target)</summary>
    internal static double Prob3d6GeqTarget(int target, int bonus)
    {
        // P(3d6 + bonus >= target) = P(3d6 >= target - bonus)
        int adjustedTarget = target - bonus;
        if (adjustedTarget <= 3)  return 1.0;
        if (adjustedTarget > 18)  return 0.0;

        // Enumerate all 216 outcomes
        int countGeq = 0;
        for (int a = 1; a <= 6; a++)
        for (int b = 1; b <= 6; b++)
        for (int c = 1; c <= 6; c++)
            if (a + b + c >= adjustedTarget)
                countGeq++;

        return countGeq / 216.0;
    }

    /// <summary>
    /// P(critical failure on 3d6) = P(at least two dice show 1).
    /// = 1 - P(zero 1s) - P(exactly one 1)
    /// = 1 - (5/6)^3 - 3*(1/6)*(5/6)^2
    /// ≈ 0.0741
    /// </summary>
    internal static double ProbCriticalFailure3d6()
    {
        double pNoOnes = Math.Pow(5.0 / 6.0, 3);
        double pExactlyOneOne = 3.0 * (1.0 / 6.0) * Math.Pow(5.0 / 6.0, 2);
        return 1.0 - pNoOnes - pExactlyOneOne;
    }
}
