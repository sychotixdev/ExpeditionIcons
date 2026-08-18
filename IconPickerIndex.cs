using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace ExpeditionIcons;

[JsonConverter(typeof(StringEnumConverter))]
public enum IconPickerIndex
{
    Experience,
    Rarity,
    Logbooks,
    PackSize,
    MonsterMods,
    Goblin,
    Artifacts,
    Quantity,
    CorruptedItems,
    RarityExcavatedChest,
    ArtifactsExcavatedChest,
    QuantityExcavatedChest,
    RunicMonsterDuplication,
    EliteMonstersIndicator,
    BadModsIndicator,
    LeagueChest,
    JewelleryChest,
    WeaponChest,
    CurrencyChest,
    CurrencyChestRare,
    RunesChest,
    MapsChest,
    GemsChest,
    EssenceChest,
    ArmourChest,
    DeliriumChest,
    UniquesChest,
    OtherChests,

    //Retired: these chest types are no longer listed in Icons.LogbookChestIcons and can no
    //longer be matched. The enum members are deliberately KEPT. This enum is serialized by
    //name (StringEnumConverter) and is used as the key type of IconMapping, ChestSettingsMap
    //and RelicSettingsMap - deleting a member makes Newtonsoft throw while deserializing any
    //saved config that still mentions it, which would discard every setting the user has.
    BlightChest,
    FragmentChest,
    HeistChest,
    BreachChest,
    RitualChest,
    MetamorphChest,
    FossilsChest,
    DivinationCardsChest,
    LegionChest,
}