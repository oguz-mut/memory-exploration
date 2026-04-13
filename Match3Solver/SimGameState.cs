// SimGameState — Full game state with scoring

class SimGameState
{
    public SimBoard Board { get; private set; } = null!;
    public int Score { get; private set; }
    public int Chain { get; private set; }
    public int TurnsRemaining { get; private set; }
    public int TurnsMade { get; private set; }
    public int Tier { get; private set; }
    public bool IsFreeTurn { get; private set; }
    public bool IsExtraTurnEarned { get; private set; }
    public bool IsGameOver { get; private set; }
    public int[] TotalPiecesMatched { get; private set; } = [];
    public int[] TierPiecesMatched { get; private set; } = [];
    public bool[] PiecesTiered { get; private set; } = [];
    private Match3Config _config = null!;

    /// <summary>
    /// Cascade-aware scoring: EffectiveScore + bonus for deep cascades.
    /// Moves that trigger long cascades (chain depth 3+) get a bonus because:
    /// 1. Chain multipliers already amplify score, but cascades also reshuffle the board
    /// 2. More disruption (cells changed) = more PRNG-generated pieces = fresh match potential
    /// 3. The bonus nudges the solver to prefer cascade-heavy moves when scores are close
    /// </summary>
    public int CascadeScore
    {
        get
        {
            int cascadeBonus = 0;
            if (Chain > 1)
            {
                // Each cascade level beyond the initial match gets a bonus
                // Scaled by tier — cascades are worth more at higher tiers
                int tierScale = (_config.ScoreDeltasPerTier.Length > 0 && Tier < _config.ScoreDeltasPerTier.Length)
                    ? _config.ScoreDeltasPerTier[Tier] + _config.ScoreFor3s
                    : _config.ScoreFor3s;
                cascadeBonus = (Chain - 1) * tierScale;
            }
            return EffectiveScore + cascadeBonus;
        }
    }

    /// <summary>
    /// Tier-rush scoring: prioritize reaching higher tiers where valuable items live.
    ///
    /// Strategy:
    /// 1. Massive bonus per tier reached — unlocking T2/T3 pieces is the primary goal
    /// 2. Bonus for tier progress — reward even partial progress toward next tier
    /// 3. Only value items at the highest tiers (T2+) — don't distort play for cheap T0 items
    /// 4. Game score is still a tiebreaker when tier state is equal
    /// </summary>
    public int EffectiveScore
    {
        get
        {
            if (_config.PieceValues.Length == 0) return Score;

            int bonus = 0;

            // Big bonus per tier reached: each tier unlocks better items
            // Scale by the max item value at each tier so we know what we're unlocking
            int tierBonus = 0;
            for (int t = 1; t <= Tier && t <= _config.PieceReqsPerTier.Length; t++)
            {
                int maxValAtTier = 0;
                for (int i = 0; i < _config.Pieces.Length; i++)
                    if (_config.Pieces[i].Tier == t && i < _config.PieceValues.Length)
                        maxValAtTier = Math.Max(maxValAtTier, _config.PieceValues[i]);
                // Reaching a tier is worth ~10 matches of its best item
                tierBonus += Math.Max(maxValAtTier * 10, 500);
            }
            bonus += tierBonus;

            // Tier progress bonus: how close to the NEXT tier-up?
            // This nudges the solver to evenly match all piece types rather than
            // pile up matches on one type while ignoring others.
            if (_config.PieceReqsPerTier.Length > 0 && Tier < _config.PieceReqsPerTier.Length)
            {
                int req = _config.PieceReqsPerTier[Tier];
                if (req > 0)
                {
                    int activePieces = Board.ActivePieceTypes;
                    // Find the piece type with the LEAST progress — that's the bottleneck
                    int minProgress = int.MaxValue;
                    int totalProgress = 0;
                    for (int i = 0; i < activePieces && i < TierPiecesMatched.Length; i++)
                    {
                        int prog = Math.Min(TierPiecesMatched[i], req);
                        totalProgress += prog;
                        minProgress = Math.Min(minProgress, prog);
                    }
                    if (minProgress == int.MaxValue) minProgress = 0;

                    // Value of reaching next tier
                    int nextTier = Tier + 1;
                    int nextTierMaxVal = 0;
                    for (int i = 0; i < _config.Pieces.Length; i++)
                        if (_config.Pieces[i].Tier == nextTier && i < _config.PieceValues.Length)
                            nextTierMaxVal = Math.Max(nextTierMaxVal, _config.PieceValues[i]);
                    int nextTierReward = Math.Max(nextTierMaxVal * 10, 500);

                    // Bottleneck progress: heavily weight the minimum (all types must reach req)
                    // This prevents the solver from ignoring one type
                    double bottleneckFraction = (double)minProgress / req;
                    bonus += (int)(bottleneckFraction * nextTierReward * 0.8);

                    // Smaller bonus for total progress (spreading matches helps)
                    double totalFraction = (double)totalProgress / (req * activePieces);
                    bonus += (int)(totalFraction * nextTierReward * 0.2);
                }
            }

            // Only value items at T2+ tiers — don't distort play for common T0/T1 items
            for (int i = 0; i < TotalPiecesMatched.Length && i < _config.PieceValues.Length; i++)
            {
                if (_config.Pieces[i].Tier >= 2)
                    bonus += TotalPiecesMatched[i] * _config.PieceValues[i];
            }

            return Score + bonus;
        }
    }

    /// <summary>Initialize from a memory-read board.</summary>
    public void StartFromMemory(Match3Config config, int[] pieces, MonoRandom rng)
    {
        StartFromMemoryWithTurns(config, pieces, rng, config.NumTurns);
    }

    public void StartFromMemoryWithTurns(Match3Config config, int[] pieces, MonoRandom rng, int turnsLeft)
        => StartFromMemoryWithTurns(config, pieces, rng, turnsLeft, 0, 0, 0, new int[config.Pieces.Length], new int[config.Pieces.Length]);

    /// <summary>Initialize from a memory-read board with full live game state (score, tier, match counters).</summary>
    public void StartFromMemoryWithTurns(Match3Config config, int[] pieces, MonoRandom rng, int turnsLeft,
        int score, int tier, int turnsMade, int[] totalPiecesMatched, int[] tierPiecesMatched)
    {
        _config = config;
        var pieceTiers = config.Pieces.Select(p => p.Tier).ToArray();
        // Detect active piece types from what's actually on the board
        int maxType = pieces.Max();
        int activePieces = Math.Max(maxType + 1, pieceTiers.Count(t => t == 0));
        Board = new SimBoard(config.Width, config.Height, config.Pieces.Length, pieces, pieceTiers, rng);
        Board.SetActivePieceTypes(activePieces);
        Score = score;
        Chain = 0;
        TurnsRemaining = turnsLeft;
        TurnsMade = turnsMade;
        Tier = tier;
        IsFreeTurn = false;
        IsExtraTurnEarned = false;
        IsGameOver = false;
        TotalPiecesMatched = totalPiecesMatched.Length == config.Pieces.Length
            ? (int[])totalPiecesMatched.Clone() : new int[config.Pieces.Length];
        TierPiecesMatched = tierPiecesMatched.Length == config.Pieces.Length
            ? (int[])tierPiecesMatched.Clone() : new int[config.Pieces.Length];
        PiecesTiered = new bool[config.Pieces.Length];
        // Reconstruct PiecesTiered from live tierPiecesMatched so FindTierUp() knows
        // which pieces already met the threshold in the current tier
        if (tier < config.PieceReqsPerTier.Length)
        {
            int req = config.PieceReqsPerTier[tier];
            for (int i = 0; i < activePieces && i < TierPiecesMatched.Length; i++)
                PiecesTiered[i] = TierPiecesMatched[i] >= req;
        }
    }

    public SimGameState() { }

    public SimGameState Clone()
    {
        return new SimGameState
        {
            Board = Board.Clone(),
            Score = Score, Chain = Chain,
            TurnsRemaining = TurnsRemaining, TurnsMade = TurnsMade,
            Tier = Tier, IsFreeTurn = IsFreeTurn,
            IsExtraTurnEarned = IsExtraTurnEarned, IsGameOver = IsGameOver,
            TotalPiecesMatched = (int[])TotalPiecesMatched.Clone(),
            TierPiecesMatched = (int[])TierPiecesMatched.Clone(),
            PiecesTiered = (bool[])PiecesTiered.Clone(),
            _config = _config
        };
    }

    public MoveResult MakeMove(int x, int y, MoveDir dir)
    {
        if (IsGameOver) return MoveResult.OtherError;
        var result = Board.MakeBasicMove(x, y, dir);
        if (result != MoveResult.Success) return result;

        IsExtraTurnEarned = false;
        // Chain starts at 1: the game's chain field is 1 for the initial swap matches,
        // 2 for first cascade, etc. ScoresPerChainLevel is 1-indexed in the game.
        // Live data showed ~10pt gap per move consistent with off-by-one on chain index.
        Chain = 1;
        var stepResults = new StepResults();
        while (Board.Step(stepResults))
        {
            AddToScore(stepResults);
            Chain++;
        }

        TurnsMade++;
        if (!IsExtraTurnEarned) TurnsRemaining--;
        FindTierUp();
        if (TurnsRemaining <= 0) IsGameOver = true;
        else if (Board.GetAllValidMoves().Count == 0) IsGameOver = true;
        return MoveResult.Success;
    }

    private void AddToScore(StepResults info)
    {
        int tierDelta = (_config.ScoreDeltasPerTier.Length > 0 && Tier < _config.ScoreDeltasPerTier.Length)
            ? _config.ScoreDeltasPerTier[Tier] : 0;
        int chainMultiplier = (_config.ScoresPerChainLevel.Length > 0)
            ? _config.ScoresPerChainLevel[Math.Min(Chain, _config.ScoresPerChainLevel.Length - 1)] : 1;

        void ScoreMatches(Dictionary<int, int> matches, int baseScore)
        {
            foreach (var (type, count) in matches)
                Score += (baseScore + tierDelta) * chainMultiplier * count;
        }

        ScoreMatches(info.Match3s, _config.ScoreFor3s);
        ScoreMatches(info.Match4s, _config.ScoreFor4s);
        ScoreMatches(info.Match5s, _config.ScoreFor5s);
        if (info.HadMatch4OrMore) IsExtraTurnEarned = true;

        // Use unique killed-piece counts (deduplicates cross-match shared pieces)
        if (info.PiecesKilled != null)
        {
            for (int type = 0; type < info.PiecesKilled.Length && type < TotalPiecesMatched.Length; type++)
            {
                TotalPiecesMatched[type] += info.PiecesKilled[type];
                TierPiecesMatched[type] += info.PiecesKilled[type];
            }
        }
    }

    private void FindTierUp()
    {
        if (_config.PieceReqsPerTier.Length == 0 || Tier >= _config.PieceReqsPerTier.Length) return;
        int req = _config.PieceReqsPerTier[Tier];
        bool allMet = true;
        for (int i = 0; i < Board.ActivePieceTypes; i++)
        {
            if (TierPiecesMatched[i] >= req) PiecesTiered[i] = true;
            else allMet = false;
        }
        if (allMet)
        {
            Tier++;
            TierPiecesMatched = new int[_config.Pieces.Length];
            PiecesTiered = new bool[_config.Pieces.Length];
            int active = 0;
            for (int i = 0; i < _config.Pieces.Length; i++)
                if (_config.Pieces[i].Tier <= Tier) active++;
            Board.SetActivePieceTypes(active);
        }
    }
}
