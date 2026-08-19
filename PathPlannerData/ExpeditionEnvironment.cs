using System;
using System.Collections.Generic;
using System.Numerics;

namespace ExpeditionIcons.PathPlannerData;

public record ExpeditionEnvironment(
    List<(Vector2, IExpeditionRelic)> Relics,
    List<(Vector2, IExpeditionLoot)> Loot,
    float ExplosionRange,
    float ExplosionRadius,
    int MaxExplosions,
    Vector2 StartingPoint,
    Func<Vector2, bool> IsValidPlacement,
    (Vector2 Min, Vector2 Max) ExclusionArea,
    bool IsLogbook,
    //Indexed by rune bit. Built once per environment so scoring never touches strings.
    double[] RuneMultipliers,
    int RunestoneCount);
