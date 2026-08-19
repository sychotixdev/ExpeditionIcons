using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileCore2.Shared.Helpers;
using ExpeditionIcons.PathPlannerData;

namespace ExpeditionIcons;

public class PathPlanner
{
    public record PerPointLootScore(Vector2 Point, double ScoreDiff, int NewRelics, int Loot);

    public record DetailedLootScore(List<PerPointLootScore> PerPointScore, double TotalScore, ExpeditionEnvironment Environment, PathCandidate Candidate);

    private readonly Dictionary<object, double> _lootValueTable = new(ReferenceEqualityComparer.Instance);
    private readonly PlannerSettings _settings;
    private readonly int _validatedPoints;
    private RuneEncounter[] _runestones = [];
    private double[] _runeMultipliers = [];

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
        var score = 0.0;
        ulong accumulated = 0;
        ulong covered = 0;
        var runeMult = 1.0;

        foreach (var explosionPoint in candidate.Points)
        {
            foreach (var (_, relic) in environment.Relics.Where(x => x.Item1.Distance(explosionPoint) <= environment.ExplosionRadius))
            {
                relics.Add(relic);
            }

            ulong pending = 0;
            var localScore = 0.0;
            foreach (var (_, loot) in environment.Loot
                         .Where(x => x.Item1.DistanceLessThanOrEqual(explosionPoint, environment.ExplosionRadius))
                         .Where(x => lootList.Add(x.Item2)))
            {
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

                var (multiplier, sum) = relics.Select(x => x.GetScoreMultiplier(loot)).Aggregate((mult: 1.0, sum: 0.0), (a, b) => (a.mult * b.Item1, a.sum + b.Item2));
                var value = _lootValueTable[loot] * multiplier * (1 + sum);

                if (loot is RunestoneMonster spawned)
                {
                    var spawnIndex = spawned.Runestone.RunestoneIndex;
                    if ((uint)spawnIndex < (uint)choices.Length)
                    {
                        var picked = spawned.Runestone.GetCandidate(choices[spawnIndex]);
                        value *= runeMult * MaskProduct(picked.RecipeRuneMask & ~accumulated);
                    }
                }
                else if (loot is IRunicMonster)
                {
                    value *= runeMult;
                }

                localScore += value;
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
        }

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
        var score = 0.0;
        ulong accumulated = 0;
        ulong covered = 0;
        var runeMult = 1.0;

        foreach (var explosionPoint in candidate.Points)
        {
            var newRelics = 0;
            var newLoot = 0;
            foreach (var (_, relic) in environment.Relics.Where(x => x.Item1.Distance(explosionPoint) <= environment.ExplosionRadius))
            {
                if (relics.Add(relic))
                {
                    newRelics++;
                }
            }

            ulong pending = 0;
            var localScore = 0.0;
            foreach (var (_, loot) in environment.Loot
                         .Where(x => x.Item1.DistanceLessThanOrEqual(explosionPoint, environment.ExplosionRadius))
                         .Where(x => lootList.Add(x.Item2)))
            {
                newLoot++;
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

                var (multiplier, sum) = relics.Select(x => x.GetScoreMultiplier(loot)).Aggregate((mult: 1.0, sum: 0.0), (a, b) => (a.mult * b.Item1, a.sum + b.Item2));
                var value = _lootValueTable[loot] * multiplier * (1 + sum);

                if (loot is RunestoneMonster spawned)
                {
                    var spawnIndex = spawned.Runestone.RunestoneIndex;
                    if ((uint)spawnIndex < (uint)choices.Length)
                    {
                        var picked = spawned.Runestone.GetCandidate(choices[spawnIndex]);
                        value *= runeMult * MaskProduct(picked.RecipeRuneMask & ~accumulated);
                    }
                }
                else if (loot is IRunicMonster)
                {
                    value *= runeMult;
                }

                localScore += value;
            }

            scorePerPoint.Add(new PerPointLootScore(explosionPoint, localScore, newRelics, newLoot));
            score += localScore;

            if (pending != 0)
            {
                var newBits = pending & ~accumulated;
                if (newBits != 0)
                {
                    accumulated |= newBits;
                    runeMult *= MaskProduct(newBits);
                }
            }
        }

        candidate.CoveredMask = covered;
        return new DetailedLootScore(scorePerPoint, score, environment, candidate);
    }

    /// <summary>
    /// Product of the multipliers of every rune in the mask. Only ever called with the
    /// bits that are actually new, so the loop runs a handful of times at most.
    /// </summary>
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
