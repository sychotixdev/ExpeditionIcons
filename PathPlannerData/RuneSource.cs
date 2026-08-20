namespace ExpeditionIcons.PathPlannerData;

/// <summary>
/// An object that hands runes to later explosions without being a runestone: no recipe choice, no
/// drop of its own, no monsters. Worth nothing directly - its entire value is the multiplier its
/// runes apply to runic monsters caught afterwards.
/// <para>
/// The mask is resolved once when the environment is built, since the runes are fixed. Like a
/// runestone's passed-on runes, it deliberately does not scale the explosion that consumed it.
/// </para>
/// </summary>
public class RuneSource : IExpeditionLoot
{
    public RuneSource(ulong passedOnMask)
    {
        PassedOnMask = passedOnMask;
    }

    public ulong PassedOnMask { get; }
}
