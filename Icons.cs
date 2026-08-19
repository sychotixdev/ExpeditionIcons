using System.Collections.Generic;
using System.Linq;
using ExileCore2.Shared.Enums;
using ExpeditionIcons.PathPlannerData;

namespace ExpeditionIcons;

public static class Icons
{
    public static IExpeditionRelic GetRelicType(string relicMod, PlannerSettings plannerSettings)
    {
        var relicDescription = ExpeditionRelicIcons.FirstOrDefault(x => x.BaseEntityMetadataSubstrings.Contains(relicMod));
        return (relicMod, relicDescription) switch
        {
            ("ExpeditionRelicUpsideElitesDuplicated", _) => new DoubledMonstersRelic(),
            (_, { IsWeightCustomizable: true, IconPickerIndex: var index, AffectsMonsters: var affectsMonsters }) when
                (plannerSettings.RelicSettingsMap.GetValueOrDefault(index) ?? RelicSettings.Default) is var setting =>
                new ConfigurableRelic(setting.Multiplier, setting.Increase,
                    affectsMonsters ?? (relicMod.Contains("Monster") || relicMod.Contains("Elite") || relicMod.Contains("PackSize"))),
            _ when relicMod.Contains("Monster") => new ConfigurableRelic(plannerSettings.DefaultRelicSettings.Multiplier, plannerSettings.DefaultRelicSettings.Increase, true),
            _ when relicMod.Contains("Chest") => new ConfigurableRelic(plannerSettings.DefaultRelicSettings.Multiplier, plannerSettings.DefaultRelicSettings.Increase, false),
            _ => null,
        };
    }

    public static readonly List<ExpeditionMarkerIconDescription> ExpeditionRelicIcons = new()
    {
        new()
        {
            IconPickerIndex = IconPickerIndex.CorruptedItems,
            DefaultIcon = MapIconsIndex.QuestObject,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsideCorruptedDropChanceVaal",
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.Experience,
            DefaultIcon = MapIconsIndex.QuestObject,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsideExperience",
                "ExpeditionRelicUpsideExperienceKarui"
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.Rarity,
            DefaultIcon = MapIconsIndex.RewardUniques,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsideItemRarityMonster",
                "ExpeditionRelicUpsideItemRarityMonsterEzomyte"
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.Logbooks,
            DefaultIcon = MapIconsIndex.QuestItem,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsideExpeditionLogbookQuantityMonster",
            },
        },

        new()
        {
            IconPickerIndex = IconPickerIndex.PackSize,
            DefaultIcon = MapIconsIndex.LootFilterLargeGreenTriangle,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsidePackSize",
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.MonsterMods,
            DefaultIcon = MapIconsIndex.LootFilterLargeGreenTriangle,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsideRareMonsterChance",
                "ExpeditionRelicUpsideMagicMonsterChance",
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.Goblin,
            DefaultIcon = MapIconsIndex.LootFilterLargeGreenTriangle,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsideMagicRareMonsterChanceGoblin",
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.RunicMonsterDuplication,
            DefaultIcon = MapIconsIndex.IncursionArchitectReplace,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsideElitesDuplicated",
            },
            IsWeightCustomizable = false,
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.Quantity,
            DefaultIcon = MapIconsIndex.RewardGenericItems,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsideItemQuantityMonster",
            },
        },

        new()
        {
            IconPickerIndex = IconPickerIndex.RarityExcavatedChest,
            DefaultIcon = MapIconsIndex.RewardUniques,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsideItemRarityChest",
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.QuantityExcavatedChest,
            DefaultIcon = MapIconsIndex.RewardGenericItems,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsideItemQuantityChest",
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.ArtifactsExcavatedChest,
            DefaultIcon = MapIconsIndex.LootFilterLargePurpleSquare,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsideItemQuantityChest",
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.KaruiTotem,
            DefaultIcon = MapIconsIndex.QuestObject,
            //Not a relic entity - a MiscExplodables doodad that nonetheless carries an
            //ExpeditionRelicUpside mod, so it rides the existing relic pipeline.
            //The mod's stat range is 1-to-1, so the rarity value is not readable and the
            //weight is a hand-set approximation of the larger (+40%) totem.
            //Monsters only. The mod name carries no scope hint, so without this it would fall
            //through the Monster/Elite/PackSize heuristic and be scored as a chest relic.
            AffectsMonsters = true,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsideSpecialKaruiTotem",
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.SulphitePillar,
            DefaultIcon = MapIconsIndex.QuestItem,
            //The smaller (+20%) counterpart of the Karui Totem. Monsters only.
            AffectsMonsters = true,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsideSpecialSulphite",
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.Artifacts,
            DefaultIcon = MapIconsIndex.LootFilterLargePurpleSquare,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsideIncreasedArtifactsMonster",
            },
        },
    };

    /// <summary>
    /// Resolves a chest marker to its icon description.
    /// The MinimapIcon name is matched by EXACT equality and wins when present - it is the only
    /// thing separating RewardChestCurrency from RewardChestCurrencyRare, which share an
    /// identical .ao file. Falls back to animated .ao metadata for markers with no minimap icon.
    /// Monster and elite markers carry no MinimapIcon at all, which is why they stay on .ao
    /// matching in the callers.
    /// </summary>
    public static ExpeditionMarkerIconDescription GetChestIcon(string minimapIconName, string animatedMetadata)
    {
        if (!string.IsNullOrEmpty(minimapIconName) &&
            LogbookChestIcons.FirstOrDefault(icon => icon.MinimapIconNames.Contains(minimapIconName)) is { } byIcon)
        {
            return byIcon;
        }

        return animatedMetadata == null
            ? null
            : LogbookChestIcons.FirstOrDefault(icon => icon.BaseEntityMetadataSubstrings.Any(animatedMetadata.Contains));
    }

    public static readonly List<ExpeditionMarkerIconDescription> LogbookChestIcons = new()
    {
        new()
        {
            IconPickerIndex = IconPickerIndex.CurrencyChest,
            DefaultIcon = ExpeditionIconsSettings.DefaultChestIcon,
            MinimapIconNames = { "RewardChestCurrency" },
            BaseEntityMetadataSubstrings =
            {
                "Metadata/Terrain/Doodads/Leagues/Expedition/ChestMarkers/ChestCurrency.ao"
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.CurrencyChestRare,
            DefaultIcon = ExpeditionIconsSettings.DefaultChestIcon,
            //Shares a byte-identical .ao with RewardChestCurrency, so the minimap icon name is the
            //ONLY way to tell the two apart. Deliberately has no metadata substrings.
            MinimapIconNames = { "RewardChestCurrencyRare" },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.RunesChest,
            DefaultIcon = ExpeditionIconsSettings.DefaultChestIcon,
            //A distinct reward chest type. NOT related to the runestones on remnants.
            MinimapIconNames = { "RewardChestRunes" },
            BaseEntityMetadataSubstrings =
            {
                "chestmarker3"
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.MapsChest,
            DefaultIcon = ExpeditionIconsSettings.DefaultChestIcon,
            MinimapIconNames = { "RewardChestMaps" },
            BaseEntityMetadataSubstrings =
            {
                "Metadata/Terrain/Doodads/Leagues/Expedition/ChestMarkers/ChestMaps.ao"
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.JewelleryChest,
            DefaultIcon = ExpeditionIconsSettings.DefaultChestIcon,
            MinimapIconNames = { "RewardChestTrinkets" },
            BaseEntityMetadataSubstrings =
            {
                "Metadata/Terrain/Doodads/Leagues/Expedition/ChestMarkers/ChestTrinkets.ao"
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.UniquesChest,
            DefaultIcon = ExpeditionIconsSettings.DefaultChestIcon,
            MinimapIconNames = { "RewardChestUnique" },
            BaseEntityMetadataSubstrings =
            {
                "Metadata/Terrain/Doodads/Leagues/Expedition/ChestMarkers/ChestUniques.ao"
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.LeagueChest,
            DefaultIcon = ExpeditionIconsSettings.DefaultChestIcon,
            //No MinimapIcon name observed for this type yet - .ao matching only.
            BaseEntityMetadataSubstrings =
            {
                "Metadata/Terrain/Doodads/Leagues/Expedition/ChestMarkers/ChestLeague.ao"
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.WeaponChest,
            DefaultIcon = ExpeditionIconsSettings.DefaultChestIcon,
            //No MinimapIcon name observed for this type yet - .ao matching only.
            BaseEntityMetadataSubstrings =
            {
                "Metadata/Terrain/Doodads/Leagues/Expedition/ChestMarkers/ChestWeapon.ao"
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.GemsChest,
            DefaultIcon = ExpeditionIconsSettings.DefaultChestIcon,
            //No MinimapIcon name observed for this type yet - .ao matching only.
            BaseEntityMetadataSubstrings =
            {
                "Metadata/Terrain/Doodads/Leagues/Expedition/ChestMarkers/ChestGems.ao"
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.EssenceChest,
            DefaultIcon = ExpeditionIconsSettings.DefaultChestIcon,
            //No MinimapIcon name observed for this type yet - .ao matching only.
            BaseEntityMetadataSubstrings =
            {
                "Metadata/Terrain/Doodads/Leagues/Expedition/ChestMarkers/ChestEssence.ao"
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.ArmourChest,
            DefaultIcon = ExpeditionIconsSettings.DefaultChestIcon,
            //No MinimapIcon name observed for this type yet - .ao matching only.
            BaseEntityMetadataSubstrings =
            {
                "Metadata/Terrain/Doodads/Leagues/Expedition/ChestMarkers/ChestArmour.ao"
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.DeliriumChest,
            DefaultIcon = ExpeditionIconsSettings.DefaultChestIcon,
            //No MinimapIcon name observed for this type yet - .ao matching only.
            BaseEntityMetadataSubstrings =
            {
                "Metadata/Terrain/Doodads/Leagues/Expedition/ChestMarkers/ChestDelirium.ao"
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.OtherChests,
            DefaultIcon = MapIconsIndex.MissionAlly,
            //Must stay LAST: the ChestMarkers path substring matches every chest above.
            //"chestmarker3" was removed from this list - it is now RunesChest.
            MinimapIconNames = { "RewardChestGeneric" },
            BaseEntityMetadataSubstrings =
            {
                "chestmarker1",
                "chestmarker2",
                "chestmarker_signpost",
                "Metadata/Terrain/Doodads/Leagues/Expedition/ChestMarkers"
            },
        },
    };
}
