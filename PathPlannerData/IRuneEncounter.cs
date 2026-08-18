namespace ExpeditionIcons.PathPlannerData;

/// <summary>
/// Marker interface for Expedition2 rune encounters.
/// Deliberately implements neither <see cref="IMonster"/> nor <see cref="IChest"/>:
/// <see cref="ConfigurableRelic.GetScoreMultiplier"/> only matches those two, so rune
/// encounters are immune to relic modifiers. A "+40% chest quantity" relic must not
/// inflate a rune price, and must especially not multiply a negative penalty.
/// <para>
/// Note this is about being a relic <i>target</i>. A rune encounter is still a relic
/// <i>source</i> - see <see cref="RuneEncounter"/>.
/// </para>
/// </summary>
public interface IRuneEncounter : IExpeditionLoot
{
}
