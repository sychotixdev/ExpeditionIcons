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

    public record DetailedLootScore(List<PerPointLootScore> PerPointScore, double TotalScore, ExpeditionEnvironment Environment);

    private readonly Dictionary<object, double> _lootValueTable = new(ReferenceEqualityComparer.Instance);
    private readonly PlannerSettings _settings;
    private readonly int _validatedPoints;

    public PathPlanner(PlannerSettings settings)
    {
        _settings = settings;
        _validatedPoints = _settings.ValidatedIntermediatePoints + 1;
    }

    public double GetScore(List<Vector2> state, ExpeditionEnvironment environment)
    {
        var relics = new HashSet<IExpeditionRelic>();
        var lootList = new HashSet<IExpeditionLoot>();
        var score = 0.0;
        foreach (var explosionPoint in state)
        {
            foreach (var (_, relic) in environment.Relics.Where(x => x.Item1.Distance(explosionPoint) <= environment.ExplosionRadius))
            {
                relics.Add(relic);
            }

            var localScore = 0.0;
            foreach (var (_, loot) in environment.Loot
                         .Where(x => x.Item1.DistanceLessThanOrEqual(explosionPoint, environment.ExplosionRadius))
                         .Where(x => lootList.Add(x.Item2)))
            {
                var (multiplier, sum) = relics.Select(x => x.GetScoreMultiplier(loot)).Aggregate((mult: 1.0, sum: 0.0), (a, b) => (a.mult * b.Item1, a.sum + b.Item2));
                localScore += _lootValueTable[loot] * multiplier * (1 + sum);
            }

            score += localScore;
        }

        return score;
    }

    //Sync with method above
    public DetailedLootScore GetDetailedScore(List<Vector2> state, ExpeditionEnvironment environment)
    {
        var relics = new HashSet<IExpeditionRelic>();
        var lootList = new HashSet<IExpeditionLoot>();
        var scorePerPoint = new List<PerPointLootScore>();
        var score = 0.0;
        foreach (var explosionPoint in state)
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

            var localScore = 0.0;
            foreach (var (_, loot) in environment.Loot
                         .Where(x => x.Item1.DistanceLessThanOrEqual(explosionPoint, environment.ExplosionRadius))
                         .Where(x => lootList.Add(x.Item2)))
            {
                newLoot++;
                var (multiplier, sum) = relics.Select(x => x.GetScoreMultiplier(loot)).Aggregate((mult: 1.0, sum: 0.0), (a, b) => (a.mult * b.Item1, a.sum + b.Item2));
                localScore += _lootValueTable[loot] * multiplier * (1 + sum);
            }

            scorePerPoint.Add(new PerPointLootScore(explosionPoint, localScore, newRelics, newLoot));
            score += localScore;
        }

        return new DetailedLootScore(scorePerPoint, score, environment);
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

    private List<Vector2> MutatePath(Vector2 startingPoint, float radius, List<Vector2> originalPath, ExpeditionEnvironment environment)
    {
        var mutateTimes = Random.Shared.Next(1, 4);
        var newPath = originalPath.ToList();
        for (var mutation = 0; mutation < mutateTimes; mutation++)
        {
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

        return newPath;
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
        _lootValueTable.Clear();
        foreach (var (_, loot) in environment.Loot)
        {
            _lootValueTable[loot] = loot switch
            {
                RunicMonster => environment.IsLogbook ? _settings.RunicMonsterLogbookWeight : _settings.RunicMonsterWeight,
                Chest { Type: var type } => _settings.ChestSettingsMap.GetValueOrDefault(type, new ChestSettings()).Weight,
                NormalMonster => _settings.NormalMonsterWeight,
                RuneEncounter runeEncounter => GetRuneWeight(runeEncounter),
            };
        }

        _lootValueTable.TrimExcess();
    }

    /// <summary>
    /// Rune encounters that never resolved a price are not added to the loot list at all,
    /// so everything reaching this method has a real value.
    /// </summary>
    private double GetRuneWeight(RuneEncounter runeEncounter)
    {
        var runeSettings = _settings.RuneScoring;
        return runeEncounter.Value >= runeSettings.ValueThreshold
            ? runeSettings.AboveThresholdWeight + (runeEncounter.Value - runeSettings.ValueThreshold) * runeSettings.AboveThresholdScale
            : runeSettings.BelowThresholdWeight;
    }

    public IEnumerable<PathState> GetBestPathSeries(ExpeditionEnvironment environment)
    {
        if (environment.MaxExplosions <= 0)
        {
            yield return new PathState(new List<Vector2>(), 0);
            yield break;
        }

        var bestPath = Enumerable.Repeat(Vector2.Zero, environment.MaxExplosions).ToList();
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
                //.Select(x => Random.Shared.NextDouble() > _settings.PathMixChance ? x : MergePaths(x, RandomBiasedElement(batch)))
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

    private List<Vector2> BuildPath(ExpeditionEnvironment environment)
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

        return path;
    }
}