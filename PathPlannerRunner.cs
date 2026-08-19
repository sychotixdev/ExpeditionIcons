using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ExileCore2;
using ExpeditionIcons.PathPlannerData;

namespace ExpeditionIcons;

public class PathPlannerRunner
{
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    public bool IsRunning => _task is { IsCompleted: false };
    private PathPlanner _pathPlanner;
    private ExpeditionEnvironment _environment;
    private BestValue[] BestValues;
    //The UI asks for the best path's detailed score every frame, so it is computed once per
    //candidate and cached here. Keys compare by reference, which means a candidate must not be
    //mutated after it has been scored - MutatePath clones rather than editing in place.
    private readonly ConditionalWeakTable<PathCandidate, PathPlanner.DetailedLootScore> _lootCache = [];

    public PathPlanner.DetailedLootScore CurrentBestPath
    {
        get
        {
            if (BestValues?.Where(x => x != null).MaxBy(x => x.Score)?.Path is not { } bestPath)
            {
                return null;
            }

            if (_lootCache.TryGetValue(bestPath, out var existingScore))
            {
                return existingScore;
            }

            return _pathPlanner is { } pathPlanner &&
                   _environment is { } environment
                ? _lootCache.GetValue(bestPath, p => pathPlanner.GetDetailedScore(p, environment))
                : null;
        }
    }

    public double CurrentBestScore => BestValues?.Max(x => x?.Score ?? 0) ?? 0;

    private Task _task;

    public void Start(PlannerSettings settings, ExpeditionEnvironment environment, SoundController soundController)
    {
        _task = Run(settings, environment, soundController);
    }

    private async Task Run(PlannerSettings settings, ExpeditionEnvironment environment, SoundController soundController)
    {
        try
        {
            _environment = environment;
            _pathPlanner = new PathPlanner(settings);
            _pathPlanner.Init(environment);
            var threadCount = Math.Max(settings.SearchThreads.Value, 1);
            BestValues = new BestValue[threadCount];
            var tasks = new List<Task>();
            for (int i = 0; i < threadCount; i++)
            {
                var ii = i;
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        var p = new PathPlanner(settings);
                        var sw = Stopwatch.StartNew();
                        var iterationSw = Stopwatch.StartNew();
                        p.Init(environment);
                        foreach (var bestPath in p.GetBestPathSeries(environment))
                        {
                            BestValues[ii] = new BestValue(bestPath.Candidate, bestPath.Score, (BestValues[ii]?.Iteration ?? 0) + 1, iterationSw.Elapsed.TotalMilliseconds);
                            iterationSw.Restart();
                            if (sw.Elapsed.TotalSeconds >= settings.MaximumGenerationTimeSeconds.Value ||
                                _cts.IsCancellationRequested)
                            {
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugWindow.LogError($"Expedition search thread failed: {ex}");
                    }
                }));
            }

            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"ExpeditionIcons PathPlanner failed before completion: {ex}");
        }
        finally
        {
            DebugWindow.LogMsg("ExpeditionIcons PathPlanner finished.");
            if (settings.PlaySoundOnFinish)
            {
                soundController.PlaySound("expedition_attention");
            }

            _ = CurrentBestPath;
            _environment = null;
            _pathPlanner = null;
        }
    }

    public void Stop() => _cts.Cancel();
}

public record BestValue(PathCandidate Path, double Score, int Iteration, double LastGenerationTime);