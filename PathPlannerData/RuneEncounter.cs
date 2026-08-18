namespace ExpeditionIcons.PathPlannerData;

/// <summary>
/// An Expedition2 rune encounter with a resolved price.
/// Encounters whose price never resolved are not added to the loot list at all,
/// so <see cref="Value"/> is always a real number here.
/// <para>
/// Doubles as a relic: covering one makes runic monsters caught at or after that explosion
/// more valuable, using the same mechanic as <see cref="ConfigurableRelic"/> and
/// <see cref="DoubledMonstersRelic"/>. The instance is added to both the loot list and the
/// relic list of the environment.
/// </para>
/// <para>
/// Deliberately a class rather than a record, unlike the other relics: records compare by
/// value, so two encounters that happened to share a price would collapse into one entry in
/// the planner's relic HashSet and grant their bonus only once.
/// </para>
/// </summary>
public class RuneEncounter : IRuneEncounter, IExpeditionRelic
{
    private readonly double _runicMonsterMultiplier;
    private readonly double _runicMonsterIncrease;

    public RuneEncounter(double value, double runicMonsterMultiplier, double runicMonsterIncrease)
    {
        Value = value;
        _runicMonsterMultiplier = runicMonsterMultiplier;
        _runicMonsterIncrease = runicMonsterIncrease;
    }

    public double Value { get; }

    public (double, double) GetScoreMultiplier(IExpeditionLoot loot)
    {
        if (loot is RunicMonster)
        {
            return (_runicMonsterMultiplier, _runicMonsterIncrease);
        }

        return (1, 0);
    }
}
