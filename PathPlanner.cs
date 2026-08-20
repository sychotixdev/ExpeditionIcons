using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileCore2.Shared.Helpers;
using ExpeditionIcons.PathPlannerData;

namespace ExpeditionIcons;

public class PathPlanner
{
    public record PerPointLootScore(Vector2 Point, double ScoreDiff, int NewRelics, int Loot, float Radius, List<(Vector2 Pos, float Radius)> Blasts);

    public record DetailedLootScore(List<PerPointLootScore> PerPointScore, double TotalScore, ExpeditionEnvironment Environment, PathCandidate Candidate);

    //Increased area of effect granted by each detonated oil well, added rather than compounded.
    //See RadiusAfterWells for how this was measured.
    private const float OilWellAreaIncreasePerWell = 0.6f;

    //Lighthouses pay out only once this many are destroyed on a single path, and the reward does
    //not grow past that. All-or-nothing, so the score is flat at one and two and jumps at three.
    private const int LighthousesRequired = 3;

    private readonly Dictionary<object, double> _lootValueTable = new(ReferenceEqualityComparer.Instance);
    private readonly PlannerSettings _settings;
    private readonly int _validatedPoints;
    private RuneEncounter[] _runestones = [];
    private double[] _runeMultipliers = [];

    //Reused across calls so the flood fill allocates nothing. Safe because each search thread
    //constructs its own PathPlanner.
    private readonly List<(Vector2 Pos, float Radius)> _blasts = [];

    //Generation stamps instead of a HashSet: dedupe becomes an integer compare and the hot
    //loop gains no third per-call allocation.
    private int[] _chainTriggered = [];
    private int _generation;

    //Per-path state for Sentinel-style rune sources: remaining charges and the precomputed rune
    //product for each. Sized once in Init and cleared per candidate, so scoring allocates nothing.
    private int[] _runeSourceCharges = [];
    private double[] _runeSourceProducts = [];

    //Sources caught during the current explosion point. They activate only once the point is done,
    //so a source never buffs the blast that consumed it.
    private readonly List<int> _pendingSources = [];

    public PathPlanner(PlannerSettings settings)
    {
        _settings = settings;
        _validatedPoints = _settings.ValidatedIntermediatePoints + 1;
    }

    public double GetScore(PathCandidate candidate, ExpeditionEnvironment environment)
    {
        var relics = new HashSet<IExpeditionRelic>();
        var lootList = new HashSet<IExpeditionLoot>();
        var choices = candidate.Choices;
        var chain = environment.ChainExplosives;
        var score = 0.0;
        ulong accumulated = 0;
        ulong covered = 0;
        var runeMult = 1.0;
        var wellsTriggered = 0;
        var lighthouses = 0;
        var sourceMult = 1.0;
        var activeSources = 0;
        var currentRadius = environment.ExplosionRadius;
        Array.Clear(_runeSourceCharges);
        _pendingSources.Clear();
        _generation++;

        foreach (var explosionPoint in candidate.Points)
        {
            var blastCount = CollectBlasts(explosionPoint, currentRadius, chain, out var wellsThisPoint);

            //Relics for the whole point before any loot. Interleaved, a relic reached by the
            //third blast would not apply to loot already scored by the first, which would make
            //the result depend on flood-fill order.
            for (var b = 0; b < blastCount; b++)
            {
                var (blastPos, blastRadius) = _blasts[b];
                foreach (var (relicPos, relic) in environment.Relics)
                {
                    if (relicPos.DistanceLessThanOrEqual(blastPos, blastRadius))
                    {
                        relics.Add(relic);
                    }
                }
            }

            ulong pending = 0;
            var localScore = 0.0;
            for (var b = 0; b < blastCount; b++)
            {
                var (blastPos, blastRadius) = _blasts[b];
                foreach (var (lootPos, loot) in environment.Loot)
                {
                    if (!lootPos.DistanceLessThanOrEqual(blastPos, blastRadius) || !lootList.Add(loot))
                    {
                        continue;
                    }

                    //The static drop is relic-immune and rune-immune, so it skips the aggregate entirely.
                    if (loot is RuneEncounter runestone)
                    {
                        if ((uint)runestone.RunestoneIndex >= (uint)choices.Length)
                        {
                            continue;
                        }

                        var picked = runestone.GetCandidate(choices[runestone.RunestoneIndex]);
                        covered |= 1UL << runestone.RunestoneIndex;
                        pending |= picked.PassedOnMask;
                        localScore += GetRuneWeight(picked.Price);
                        continue;
                    }

                    if (loot is RuneSource runeSource)
                    {
                        _pendingSources.Add(runeSource.Index);
                        continue;
                    }

                    if (loot is Lighthouse)
                    {
                        lighthouses++;
                        continue;
                    }

                    var (multiplier, sum) = relics.Select(x => x.GetScoreMultiplier(loot)).Aggregate((mult: 1.0, sum: 0.0), (a, r) => (a.mult * r.Item1, a.sum + r.Item2));
                    var value = _lootValueTable[loot] * multiplier * (1 + sum);

                    if (loot is RunestoneMonster spawned)
                    {
                        var spawnIndex = spawned.Runestone.RunestoneIndex;
                        if ((uint)spawnIndex < (uint)choices.Length)
                        {
                            var pickedSpawn = spawned.Runestone.GetCandidate(choices[spawnIndex]);
                            value *= runeMult * MaskProduct(pickedSpawn.RecipeRuneMask & ~accumulated);
                        }
                    }
                    else if (loot is IRunicMonster)
                    {
                        value *= runeMult;
                    }

                    //Both kinds of runic monster spend a charge from every active source. Which
                    //ones fall inside a source's window depends on the order Loot happens to be
                    //in when a single blast catches more monsters than there are charges left.
                    if (activeSources > 0 && loot is IRunicMonster)
                    {
                        value *= sourceMult;
                        SpendSourceCharges(ref sourceMult, ref activeSources);
                    }

                    localScore += value;
                }
            }

            score += localScore;

            //Deferred: propagation reaches later explosions only, never this one.
            if (pending != 0)
            {
                var newBits = pending & ~accumulated;
                if (newBits != 0)
                {
                    accumulated |= newBits;
                    runeMult *= MaskProduct(newBits);
                }
            }

            //Same deferral again: a source never buffs the explosion that consumed it.
            if (_pendingSources.Count > 0)
            {
                ActivateSources(ref sourceMult, ref activeSources);
            }

            //Same deferral for the radius: a well never enlarges the blast that set it off.
            if (wellsThisPoint > 0)
            {
                wellsTriggered += wellsThisPoint;
                currentRadius = RadiusAfterWells(environment.ExplosionRadius, wellsTriggered);
            }
        }

        score += LighthouseReward(lighthouses, environment);
        candidate.CoveredMask = covered;
        return score;
    }

    //Sync with method above
    public DetailedLootScore GetDetailedScore(PathCandidate candidate, ExpeditionEnvironment environment)
    {
        var relics = new HashSet<IExpeditionRelic>();
        var lootList = new HashSet<IExpeditionLoot>();
        var scorePerPoint = new List<PerPointLootScore>();
        var choices = candidate.Choices;
        var chain = environment.ChainExplosives;
        var score = 0.0;
        ulong accumulated = 0;
        ulong covered = 0;
        var runeMult = 1.0;
        var wellsTriggered = 0;
        var lighthouses = 0;
        var sourceMult = 1.0;
        var activeSources = 0;
        var currentRadius = environment.ExplosionRadius;
        Array.Clear(_runeSourceCharges);
        _pendingSources.Clear();
        _generation++;

        foreach (var explosionPoint in candidate.Points)
        {
            var pointRadius = currentRadius;
            var blastCount = CollectBlasts(explosionPoint, currentRadius, chain, out var wellsThisPoint);
            var blastsForPoint = new List<(Vector2 Pos, float Radius)>(_blasts);

            var newRelics = 0;
            for (var b = 0; b < blastCount; b++)
            {
                var (blastPos, blastRadius) = _blasts[b];
                foreach (var (relicPos, relic) in environment.Relics)
                {
                    if (relicPos.DistanceLessThanOrEqual(blastPos, blastRadius) && relics.Add(relic))
                    {
                        newRelics++;
                    }
                }
            }

            ulong pending = 0;
            var newLoot = 0;
            var localScore = 0.0;
            for (var b = 0; b < blastCount; b++)
            {
                var (blastPos, blastRadius) = _blasts[b];
                foreach (var (lootPos, loot) in environment.Loot)
                {
                    if (!lootPos.DistanceLessThanOrEqual(blastPos, blastRadius) || !lootList.Add(loot))
                    {
                        continue;
                    }

                    newLoot++;
                    //The static drop is relic-immune and rune-immune, so it skips the aggregate entirely.
                    if (loot is RuneEncounter runestone)
                    {
                        if ((uint)runestone.RunestoneIndex >= (uint)choices.Length)
                        {
                            continue;
                        }

                        var picked = runestone.GetCandidate(choices[runestone.RunestoneIndex]);
                        covered |= 1UL << runestone.RunestoneIndex;
                        pending |= picked.PassedOnMask;
                        localScore += GetRuneWeight(picked.Price);
                        continue;
                    }

                    if (loot is RuneSource runeSource)
                    {
                        _pendingSources.Add(runeSource.Index);
                        continue;
                    }

                    if (loot is Lighthouse)
                    {
                        lighthouses++;
                        continue;
                    }

                    var (multiplier, sum) = relics.Select(x => x.GetScoreMultiplier(loot)).Aggregate((mult: 1.0, sum: 0.0), (a, r) => (a.mult * r.Item1, a.sum + r.Item2));
                    var value = _lootValueTable[loot] * multiplier * (1 + sum);

                    if (loot is RunestoneMonster spawned)
                    {
                        var spawnIndex = spawned.Runestone.RunestoneIndex;
                        if ((uint)spawnIndex < (uint)choices.Length)
                        {
                            var pickedSpawn = spawned.Runestone.GetCandidate(choices[spawnIndex]);
                            value *= runeMult * MaskProduct(pickedSpawn.RecipeRuneMask & ~accumulated);
                        }
                    }
                    else if (loot is IRunicMonster)
                    {
                        value *= runeMult;
                    }

                    //Both kinds of runic monster spend a charge from every active source. Which
                    //ones fall inside a source's window depends on the order Loot happens to be
                    //in when a single blast catches more monsters than there are charges left.
                    if (activeSources > 0 && loot is IRunicMonster)
                    {
                        value *= sourceMult;
                        SpendSourceCharges(ref sourceMult, ref activeSources);
                    }

                    localScore += value;
                }
            }

            scorePerPoint.Add(new PerPointLootScore(explosionPoint, localScore, newRelics, newLoot, pointRadius, blastsForPoint));
            score += localScore;

            //Deferred: propagation reaches later explosions only, never this one.
            if (pending != 0)
            {
                var newBits = pending & ~accumulated;
                if (newBits != 0)
                {
                    accumulated |= newBits;
                    runeMult *= MaskProduct(newBits);
                }
            }

            //Same deferral again: a source never buffs the explosion that consumed it.
            if (_pendingSources.Count > 0)
            {
                ActivateSources(ref sourceMult, ref activeSources);
            }

            //Same deferral for the radius: a well never enlarges the blast that set it off.
            if (wellsThisPoint > 0)
            {
                wellsTriggered += wellsThisPoint;
                currentRadius = RadiusAfterWells(environment.ExplosionRadius, wellsTriggered);
            }
        }

        score += LighthouseReward(lighthouses, environment);
        candidate.CoveredMask = covered;
        return new DetailedLootScore(scorePerPoint, score, environment, candidate);
    }

    /// <summary>
    /// The blasts produced by one placement: the explosion itself, plus every explodable object
    /// it reaches. Results land in <see cref="_blasts"/>, with the placement always at index 0.
    /// <para>
    /// Exactly one level deep. These objects do NOT set each other off - an oil well detonated
    /// right beside a Faridun explosive leaves it intact - so a triggered blast is a leaf and is
    /// never scanned for further objects. Its blast still catches loot, relics and runestones.
    /// </para>
    /// Each object detonates at most once per path, tracked by generation stamp.
    /// </summary>
    private int CollectBlasts(Vector2 origin, float radius, List<ChainExplosive> chain, out int wellsTriggered)
    {
        wellsTriggered = 0;
        _blasts.Clear();
        _blasts.Add((origin, radius));
        if (chain == null || chain.Count == 0)
        {
            return _blasts.Count;
        }

        //Only the placement is tested, never the blasts it spawns.
        for (var c = 0; c < chain.Count && c < _chainTriggered.Length; c++)
        {
            if (_chainTriggered[c] == _generation)
            {
                continue;
            }

            var explosive = chain[c];
            if (!explosive.Position.DistanceLessThanOrEqual(origin, radius))
            {
                continue;
            }

            _chainTriggered[c] = _generation;
            _blasts.Add((explosive.Position, explosive.Radius));
            if (explosive.GrantsRadiusBonus)
            {
                wellsTriggered++;
            }
        }

        return _blasts.Count;
    }

    /// <summary>
    /// Radius after <paramref name="wells"/> oil wells. Each well grants a flat increase to area
    /// of effect and radius scales as the square root of area, so the growth curve flattens.
    /// <para>
    /// Measured: radii at 0/1/2/3 wells came out at 33.94 / 42.43 / 49.40 / 56.83 grid, read by
    /// placing explosives so their circles just touched and halving the entity distance. Sweeping
    /// every integer base against every 5% step of increase, base 34 with +60% is the best fit in
    /// the space - every reading within 1.5% except one that disagreed with its own duplicate by
    /// 3.4%. A flat radius increase and a compounding multiplier both fit the first well by
    /// construction and then diverge badly, predicting 59.4 and 66.3 at three wells.
    /// </para>
    /// </summary>
    private float RadiusAfterWells(float baseRadius, int wells)
    {
        return wells <= 0
            ? baseRadius
            : baseRadius * MathF.Sqrt(1 + wells * OilWellAreaIncreasePerWell);
    }

    /// <summary>
    /// Product of the multipliers of every rune in the mask. Only ever called with the
    /// bits that are actually new, so the loop runs a handful of times at most.
    /// </summary>
    /// <summary>
    /// Value of the logbook the lighthouses yield, awarded once for the whole path rather than at
    /// any single explosion. The configured weight scales the logbook's market value, so 1.0 means
    /// "worth exactly what a logbook sells for" and 0 disables it.
    /// </summary>
    private double LighthouseReward(int lighthouses, ExpeditionEnvironment environment)
    {
        if (lighthouses < LighthousesRequired || environment.LogbookValue <= 0)
        {
            return 0;
        }

        return environment.LogbookValue *
               _settings.ChestSettingsMap.GetValueOrDefault(IconPickerIndex.Lighthouse, new ChestSettings()).Weight;
    }

    /// <summary>
    /// Turns every source caught during the point just finished into an active buff.
    /// </summary>
    private void ActivateSources(ref double mult, ref int active)
    {
        var charges = _settings.RuneScoring.SentinelRuneCharges;
        foreach (var index in _pendingSources)
        {
            //A source is caught at most once per path, so an already-active one means a duplicate
            //index rather than a second pickup. Skip rather than refresh.
            if (charges <= 0 || (uint)index >= (uint)_runeSourceCharges.Length || _runeSourceCharges[index] > 0)
            {
                continue;
            }

            _runeSourceCharges[index] = charges;
            mult *= _runeSourceProducts[index];
            active++;
        }

        _pendingSources.Clear();
    }

    /// <summary>
    /// Spends one charge from every active source. Sources stack: each keeps its own countdown and
    /// every runic monster costs all of them a charge. The product is rebuilt from scratch when one
    /// expires rather than divided out, which would drift over a long path.
    /// </summary>
    private void SpendSourceCharges(ref double mult, ref int active)
    {
        var expired = false;
        for (var i = 0; i < _runeSourceCharges.Length; i++)
        {
            if (_runeSourceCharges[i] > 0 && --_runeSourceCharges[i] == 0)
            {
                expired = true;
                active--;
            }
        }

        if (!expired)
        {
            return;
        }

        mult = 1.0;
        for (var i = 0; i < _runeSourceCharges.Length; i++)
        {
            if (_runeSourceCharges[i] > 0)
            {
                mult *= _runeSourceProducts[i];
            }
        }
    }

    private double MaskProduct(ulong mask)
    {
        var result = 1.0;
        while (mask != 0)
        {
            var bit = System.Numerics.BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;
            if (bit < _runeMultipliers.Length)
            {
                result *= _runeMultipliers[bit];
            }
        }

        return result;
    }

    /// <summary>
    /// The static drop's weight. Path-independent given a price: above the threshold it
    /// scales with price, below it collapses to a flat penalty.
    /// </summary>
    private double GetRuneWeight(double price)
    {
        var runeSettings = _settings.RuneScoring;
        return price >= runeSettings.ValueThreshold
            ? runeSettings.AboveThresholdWeight + (price - runeSettings.ValueThreshold) * runeSettings.AboveThresholdScale
            : runeSettings.BelowThresholdWeight;
    }

    private Vector2 GetNextPosition(Vector2 position, Vector2 previousPosition, float radius, ExpeditionEnvironment environment)
    {
        var positionEnumerable = Enumerable.Range(1, 1000).Select(i => GetNextMaybeInvalidPosition(position, radius * MathF.Pow(0.99f, i)));
        return positionEnumerable.FirstOrDefault(x => IsValidPlacement(previousPosition, environment, x), position);
    }

    private bool IsValidPlacement(Vector2 previousPosition, ExpeditionEnvironment environment, Vector2 position)
    {
        return previousPosition.DistanceLessThanOrEqual(position, environment.ExplosionRange) &&
               Vector2.Clamp(position, environment.ExclusionArea.Min, environment.ExclusionArea.Max) != position &&
               Enumerable.Range(1, _validatedPoints)
                   .Select(i => i / (float)_validatedPoints)
                   .Select(l => Vector2.Lerp(previousPosition, position, l))
                   .All(environment.IsValidPlacement);
    }

    private static Vector2 GetNextMaybeInvalidPosition(Vector2 position, float radius)
    {
        radius = Math.Max(1, radius);
        var length = Math.Max(1, Random.Shared.Next(2) == 0 ? radius : GetWeightedLength(radius));
        var angle = Random.Shared.NextSingle() * MathF.PI * 2;
        var (sin, cos) = MathF.SinCos(angle);
        var rawPoint = position + new Vector2(cos * length, sin * length);
        var roundedPoint = RoundPoint(rawPoint);
        while (!roundedPoint.DistanceLessThanOrEqual(position, radius))
        {
            var diff = roundedPoint - position;
            var maxDiffComponent = Math.Abs(diff.X) > Math.Abs(diff.Y)
                ? new Vector2(Math.Sign(diff.X), 0)
                : new Vector2(0, Math.Sign(diff.Y));
            roundedPoint -= maxDiffComponent;
        }

        return roundedPoint;
    }

    private static Vector2 RoundPoint(Vector2 rawPoint)
    {
        return new Vector2(MathF.Round(rawPoint.X), MathF.Round(rawPoint.Y));
    }

    private static float GetWeightedLength(float radius)
    {
        return Math.Max(Random.Shared.NextSingle(), Random.Shared.NextSingle()) * radius;
    }

    private PathCandidate MutatePath(Vector2 startingPoint, float radius, PathCandidate original, ExpeditionEnvironment environment)
    {
        var mutateTimes = Random.Shared.Next(1, 4);
        var newCandidate = original.Clone();
        var newPath = newCandidate.Points;
        for (var mutation = 0; mutation < mutateTimes; mutation++)
        {
            //Substitutive, matching how skip/swap/move already compete within one event.
            if (Random.Shared.NextDouble() < _settings.RecipeMutateChance && TryApplyRecipeMutation(newCandidate))
            {
                continue;
            }

            if (Random.Shared.Next(2) == 0 && TryApplySkipMutation(newPath, environment))
            {
                continue;
            }

            if (Random.Shared.Next(2) == 0 && TryApplySwapMutation(newPath, environment))
            {
                continue;
            }

            var changeIndex = Random.Shared.Next(newPath.Count);
            Vector2 changedPoint;
            var previousPoint = changeIndex == 0 ? startingPoint : newPath[changeIndex - 1];
            var changingPoint = newPath[changeIndex];
            var tries = 0;
            bool isValidChange;
            do
            {
                if (Random.Shared.Next(2) == 0)
                {
                    changedPoint = GetNextPosition(previousPoint, previousPoint, radius, environment);
                }
                else
                {
                    var allowedMoveRadius = Math.Max(radius - previousPoint.Distance(changingPoint), radius / 5);
                    changedPoint = GetNextPosition(changingPoint, previousPoint, allowedMoveRadius, environment);
                }

                isValidChange = previousPoint.DistanceLessThanOrEqual(changedPoint, radius) &&
                                (changeIndex == newPath.Count - 1 ||
                                 IsValidPlacement(changedPoint, environment, newPath[changeIndex + 1]));
            } while (!isValidChange && tries++ < 10);

            if (!isValidChange)
            {
                continue;
            }

            newPath[changeIndex] = changedPoint;
        }

        return newCandidate;
    }

    /// <summary>
    /// Switches one runestone to a different recipe. Draws from the runestones the path
    /// actually detonates: a choice on an uncovered runestone cannot change the score, so
    /// mutating it burns an evaluation and lets that slot drift to junk under zero selection
    /// pressure. A path that has never been scored has no coverage yet, so fall back to any.
    /// </summary>
    private bool TryApplyRecipeMutation(PathCandidate candidate)
    {
        if (_runestones.Length == 0)
        {
            return false;
        }

        var mask = candidate.CoveredMask;
        int index;
        if (mask != 0)
        {
            var setCount = System.Numerics.BitOperations.PopCount(mask);
            var pick = Random.Shared.Next(setCount);
            for (var i = 0; i < pick; i++)
            {
                mask &= mask - 1;
            }

            index = System.Numerics.BitOperations.TrailingZeroCount(mask);
        }
        else
        {
            index = Random.Shared.Next(_runestones.Length);
        }

        if ((uint)index >= (uint)_runestones.Length || index >= candidate.Choices.Length)
        {
            return false;
        }

        if (_runestones[index] is not { } runestone)
        {
            return false;
        }

        var options = runestone.Candidates.Length;
        if (options <= 1)
        {
            return false;
        }

        var current = Math.Clamp(candidate.Choices[index], 0, options - 1);
        var next = Random.Shared.Next(options - 1);
        if (next >= current)
        {
            next++;
        }

        candidate.Choices[index] = next;
        return true;
    }

    private bool TryApplySkipMutation(List<Vector2> path, ExpeditionEnvironment environment)
    {
        var pathCount = path.Count - 2;
        if (pathCount <= 0)
        {
            return false;
        }

        var searchStartOffset = Random.Shared.Next(0, pathCount + 1);
        if (searchStartOffset == pathCount)
        {
            var injectionIndex = Random.Shared.Next(0, pathCount);
            var midpoint = RoundPoint((path[injectionIndex] + path[injectionIndex + 1]) / 2);
            if (IsValidPlacement(path[injectionIndex], environment, midpoint) &&
                IsValidPlacement(midpoint, environment, path[injectionIndex + 1]))
            {
                path.RemoveAt(path.Count - 1);
                path.Insert(injectionIndex + 1, midpoint);
                return true;
            }

            searchStartOffset = 0;
        }

        for (int i = 0; i < pathCount; i++)
        {
            var checkIndex = 1 + (i + searchStartOffset) % pathCount;
            if (IsValidPlacement(path[checkIndex - 1], environment, path[checkIndex + 1]))
            {
                path.RemoveAt(checkIndex);
                path.Add(GetNextPosition(path.Last(), path.Last(), environment.ExplosionRange, environment));
                return true;
            }
        }

        return false;
    }

    private bool TryApplySwapMutation(List<Vector2> path, ExpeditionEnvironment environment)
    {
        var pathCount = path.Count - 3;
        if (pathCount <= 0)
        {
            return false;
        }

        var searchStartOffset = Random.Shared.Next(0, pathCount);
        for (int i = 0; i < pathCount; i++)
        {
            var checkIndex = 1 + (i + searchStartOffset) % pathCount;
            if (IsValidPlacement(path[checkIndex - 1], environment, path[checkIndex + 1]) &&
                IsValidPlacement(path[checkIndex], environment, path[checkIndex + 2]))
            {
                (path[checkIndex + 1], path[checkIndex]) = (path[checkIndex], path[checkIndex + 1]);
                return true;
            }
        }

        return false;
    }

    public void Init(ExpeditionEnvironment environment)
    {
        _runeMultipliers = environment.RuneMultipliers ?? [];
        _runestones = new RuneEncounter[environment.RunestoneCount];
        _runeSourceCharges = new int[environment.RuneSourceCount];
        _runeSourceProducts = new double[environment.RuneSourceCount];
        _chainTriggered = new int[environment.ChainExplosives?.Count ?? 0];
        _lootValueTable.Clear();
        foreach (var (_, loot) in environment.Loot)
        {
            //The static drop's value depends on the recipe the genome picked, so it is
            //computed inline during scoring and deliberately kept out of the table.
            if (loot is RuneEncounter runestone)
            {
                if (runestone.RunestoneIndex >= 0 && runestone.RunestoneIndex < _runestones.Length)
                {
                    _runestones[runestone.RunestoneIndex] = runestone;
                }

                continue;
            }

            //Neither is valued per item: a rune source only buffs later explosions, and a
            //lighthouse only pays out once enough of them are on the same path.
            if (loot is RuneSource source)
            {
                if ((uint)source.Index < (uint)_runeSourceProducts.Length)
                {
                    _runeSourceProducts[source.Index] = MaskProduct(source.RuneMask);
                }

                continue;
            }

            if (loot is Lighthouse)
            {
                continue;
            }

            _lootValueTable[loot] = loot switch
            {
                RunicMonster => environment.IsLogbook ? _settings.RunicMonsterLogbookWeight : _settings.RunicMonsterWeight,
                RunestoneMonster => _settings.RunestoneMonsterWeight,
                Chest { Type: var type } => _settings.ChestSettingsMap.GetValueOrDefault(type, new ChestSettings()).Weight,
                NormalMonster => _settings.NormalMonsterWeight,
            };
        }

        _lootValueTable.TrimExcess();
    }

    public IEnumerable<PathState> GetBestPathSeries(ExpeditionEnvironment environment)
    {
        if (environment.MaxExplosions <= 0)
        {
            yield return new PathState(NewCandidate(new List<Vector2>(), environment), 0);
            yield break;
        }

        var bestPath = NewCandidate(Enumerable.Repeat(Vector2.Zero, environment.MaxExplosions).ToList(), environment);
        var batch = Enumerable.Range(0, _settings.PathGenerationSize * 2).Select(_ => BuildPath(environment)).ToList();
        while (true)
        {
            var batchWithValues = batch
                .Select(x => (GetScore(x, environment), x))
                .OrderByDescending(x => x.Item1)
                .Take(_settings.PathGenerationSize)
                .ToList();
            var mixedAndMutated = batchWithValues
                .Concat(batchWithValues)
                .Select(i => i.x)
                .Select(x => Random.Shared.NextDouble() > _settings.PathMutateChance ? x : MutatePath(environment.StartingPoint, environment.ExplosionRange, x, environment));
            var newPaths = Enumerable.Range(0, (int)(_settings.PathGenerationSize * _settings.NewRandomPathInjectionRate)).Select(_ => BuildPath(environment));
            var newBatch = mixedAndMutated.Append(bestPath).Concat(newPaths).ToList();
            if (batchWithValues[0].Item1 > GetScore(bestPath, environment))
            {
                bestPath = batchWithValues[0].x;
            }

            yield return new PathState(bestPath, GetScore(bestPath, environment));
            batch = newBatch;
        }
    }

    private PathCandidate NewCandidate(List<Vector2> points, ExpeditionEnvironment environment)
    {
        //Choices default to 0, which pruning guarantees is the highest-priced candidate.
        return new PathCandidate(points, new int[Math.Max(environment.RunestoneCount, 0)]);
    }

    private PathCandidate BuildPath(ExpeditionEnvironment environment)
    {
        var path = new List<Vector2>(environment.MaxExplosions);
        if (Random.Shared.Next(2) != 0 && environment.Relics.Any())
        {
            var environmentExplosionRange = environment.ExplosionRange * 0.9f;
            var relic = environment.Relics[Random.Shared.Next(environment.Relics.Count)];
            var current = environment.StartingPoint;
            do
            {
                var diff = relic.Item1 - current;
                if (diff.Length() < environmentExplosionRange)
                {
                    path.Add(RoundPoint(relic.Item1));
                }
                else
                {
                    current += diff * (environmentExplosionRange / diff.Length());
                    path.Add(RoundPoint(current));
                }

                if (!IsValidPlacement(path.SkipLast(1).LastOrDefault(environment.StartingPoint), environment, path.Last()))
                {
                    path.RemoveAt(path.Count - 1);
                    break;
                }
            } while (!current.DistanceLessThanOrEqual(relic.Item1, environment.ExplosionRadius) &&
                     path.Count < environment.MaxExplosions);
        }

        var point = path.LastOrDefault(environment.StartingPoint);
        while (path.Count < environment.MaxExplosions)
        {
            path.Add(point = GetNextPosition(point, point, environment.ExplosionRange, environment));
        }

        return NewCandidate(path, environment);
    }
}
