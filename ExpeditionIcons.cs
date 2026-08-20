using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.Elements;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.PoEMemory.Models;
using ExileCore2.Shared.Enums;
using ExileCore2.Shared.Helpers;
using ExileCore2.Shared.Nodes;
using ExpeditionIcons.PathPlannerData;
using GameOffsets2.Native;
using ImGuiNET;
using RectangleF = ExileCore2.Shared.RectangleF;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace ExpeditionIcons;

public class ExpeditionIcons : BaseSettingsPlugin<ExpeditionIconsSettings>
{
    private const string MarkerPath = "Metadata/MiscellaneousObjects/Expedition/ExpeditionMarker";
    private const string ExplosivePath = "Metadata/MiscellaneousObjects/Expedition/ExpeditionExplosive";
    private const string RelicPath = "Metadata/MiscellaneousObjects/Expedition/ExpeditionRelic";
    private const string RuneEncounterPath = RunePricer.EncounterMetadataPrefix;

    //Explodable doodads that carry ExpeditionRelicUpside mods, so they route through the relic
    //pipeline. These paths are biome-specific (Logbook_Wastes): another biome will use a different
    //segment and these will silently stop matching. If the /Objects/Totem and /Objects/Sulphite
    //suffixes turn out to be stable across biomes, switch to an EndsWith match.
    //Basin-specific, same tradeoff as the Wastes totems: inert in other biomes.
    private const string FaridunExplosivePath = "Metadata/Terrain/Gallows/Leagues/Expedition/Logbook_Basin/Objects/FaridunExplosive";
    private const string OilWellPath = "Metadata/Terrain/Gallows/Leagues/Expedition/Logbook_Basin/Objects/OilWell";

    private const string KaruiTotemPath = "Metadata/Terrain/Gallows/Leagues/Expedition/Logbook_Wastes/Objects/Totem";
    private const string SulphitePillarPath = "Metadata/Terrain/Gallows/Leagues/Expedition/Logbook_Wastes/Objects/Sulphite";

    //Unlike the four above, this path carries no biome segment, so it should match everywhere.
    private const string BeastSkinPath = "Metadata/Terrain/Leagues/Expedition/Objects/ExpeditionBeastSkin";

    //Blast radii of the two objects that detonate when caught, in grid units. Both are static:
    //unlike our own explosions they are not scaled by the oil well bonus, by each other, or by
    //MapExpeditionExplosionRadiusPct.
    //Basin's Faridun explosive and the Gallows boom barrel behave identically, same radius.
    private const string BoomBarrelPath = "Metadata/Terrain/Gallows/Leagues/Expedition/Objects/ExplodingFill_BoomBarrel";

    //Not an expedition path, but a Sentinel encounter object can spawn inside one. It hands a
    //rune to later explosions exactly as a runestone's passed-on runes do, and is worth nothing
    //else - no drop of its own, no monsters.
    private const string SentinelPath = "Metadata/MiscellaneousObjects/Sentinel/SentinelRandomEncounterObject";

    //Two Karui gates can block a single hoard, and destroying either one opens it, so a gate
    //within this distance of one already counted is dropped rather than scored a second time.
    private const float KaruiGateClusterRadius = 100;

    private const float FaridunExplosiveRadius = 75;
    private const float OilWellRadius = 140;

    private const string TextureName = "Icons.png";
    private const double CameraAngle = 38.7 * Math.PI / 180;
    private static readonly float CameraAngleCos = (float)Math.Cos(CameraAngle);
    private static readonly float CameraAngleSin = (float)Math.Sin(CameraAngle);

    private const float GridToWorldMultiplier = 250 / 23f;

    //Measured in-game rather than inherited, by placing explosives in a straight line at maximum
    //reach with MapExpeditionMaximumPlacementDistancePct at 0. Logbook expeditions allow a longer
    //reach than the encounters found in maps - 108 against 90.14 - so the two need separate bases.
    //The inherited value of 87 came from the PoE1 plugin and was short for both.
    private const int LogbookExplosiveBaseRange = 108;
    private const int MapExplosiveBaseRange = 90;

    //Measured, with MapExpeditionExplosionRadiusPct at 0, by placing explosives so their circles
    //just touch and halving the distance between the entities. In a logbook that gave 67.88 grid
    //apart, so 33.94; in a map, two touching pairs gave 27.95 and 27.23. Maps are smaller in both
    //radius and placement range - the range ratio is exactly 5/6, which on 34 would predict 28.33.
    //Reading the logbook value off the drawn circle instead had suggested 33, and the inherited
    //value was 30.
    private const int LogbookExplosiveBaseRadius = 34;
    private const int MapExplosiveBaseRadius = 28;

    private readonly ConcurrentDictionary<string, List<ExpeditionMarkerIconDescription>> _relicModIconMapping = new();
    private readonly ConcurrentDictionary<string, ExpeditionMarkerIconDescription> _metadataIconMapping = new();
    private readonly Dictionary<uint, EntityCacheItem> _cachedEntities = new Dictionary<uint, EntityCacheItem>();
    private readonly ConcurrentDictionary<string, ExpeditionEntityType> _entityTypeCache = new();
    private double _mapScale;
    private Vector2 _mapCenter;
    private bool _largeMapOpen;
    private Vector2 _playerGridPos;
    private float _playerZ;
    private List<Vector2> _explosives2DPositions = [];
    private float _explosiveRadius;
    private float _explosiveRange;

    //Static game data, so built once and never invalidated on area change.
    private Dictionary<string, string> _sentinelRuneIdByModKey;
    private BaseItemType _logbookBaseItemType;
    private PathPlannerRunner _plannerRunner;
    private RunePricer _runePricer;
    private (Vector2, float)? _detonatorPos;
    private bool _zoneCleared;
    private int[][] _pathfindingData;
    private Vector2i _areaDimensions;
    private List<float> _scoreHistory = [];
    private PathCandidate _editedPath;
    private int? _editedIndex = null;
    private PathPlanner.DetailedLootScore _editedPathEval;
    private PathPlanner.DetailedLootScore EditedOrNativeScore => _editedPathEval ?? _plannerRunner?.CurrentBestPath;

    private PathPlanner.DetailedLootScore _expectedChoiceSource;
    private Dictionary<uint, RunestoneCandidate> _expectedChoices = new();

    /// <summary>
    /// Which recipe the search settled on at each runestone the displayed path covers.
    /// Runestones the path skips are absent - callers must draw nothing for those rather than
    /// falling back to the top-priced recipe, which would be indistinguishable from a real answer.
    /// Rebuilt only when the underlying score object changes.
    /// </summary>
    private Dictionary<uint, RunestoneCandidate> ExpectedChoices
    {
        get
        {
            var score = EditedOrNativeScore;
            if (!ReferenceEquals(score, _expectedChoiceSource))
            {
                _expectedChoiceSource = score;
                _expectedChoices = BuildExpectedChoices(score);
            }

            return _expectedChoices;
        }
    }

    private static Dictionary<uint, RunestoneCandidate> BuildExpectedChoices(PathPlanner.DetailedLootScore score)
    {
        var result = new Dictionary<uint, RunestoneCandidate>();
        if (score?.Candidate is not { } candidate || score.Environment is not { } environment)
        {
            return result;
        }

        foreach (var (_, entry) in environment.Loot)
        {
            if (entry is not RuneEncounter runestone ||
                runestone.RunestoneIndex < 0 ||
                runestone.RunestoneIndex >= candidate.Choices.Length ||
                (candidate.CoveredMask & (1UL << runestone.RunestoneIndex)) == 0)
            {
                continue;
            }

            result[runestone.EntityId] = runestone.GetCandidate(candidate.Choices[runestone.RunestoneIndex]);
        }

        return result;
    }

    /// <summary>
    /// Which runestone the open Expedition2 window belongs to is not tracked by the game data we
    /// read, so the nearest covered runestone is the pragmatic answer.
    /// </summary>
    private RunestoneCandidate GetExpectedChoiceForOpenWindow()
    {
        var choices = ExpectedChoices;
        if (choices.Count == 0)
        {
            return null;
        }

        RunestoneCandidate best = null;
        var bestDistance = float.MaxValue;
        foreach (var e in _cachedEntities.Values)
        {
            if (GetEntityType(e.Path) != ExpeditionEntityType.RuneEncounter ||
                !choices.TryGetValue(e.Id, out var choice))
            {
                continue;
            }

            var distance = e.GridPos.Distance(_playerGridPos);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = choice;
            }
        }

        return best;
    }

    private Camera Camera => GameController.Game.IngameState.Camera;

    private (Vector2 Pos, float Rotation)? DetonatorPos => _detonatorPos ??= RealDetonatorPos;

    private (Vector2, float)? RealDetonatorPos
    {
        get
        {
            var entity = DetonatorEntity;
            if (entity == null)
            {
                return null;
            }

            var positioned = entity.GetComponent<Positioned>();
            return positioned == null ? null : (entity.GridPos, positioned.Rotation);
        }
    }

    private Entity DetonatorEntity =>
        GameController.EntityListWrapper.ValidEntitiesByType[EntityType.IngameIcon]
            .FirstOrDefault(x => x.Path == "Metadata/MiscellaneousObjects/Expedition/ExpeditionDetonator" ||
                                 x.Path == "Metadata/MiscellaneousObjects/Expedition/ExpeditionDetonatorTreasureIsland");

    private int PlacedExplosiveCount => ExpeditionInfo.PlacedExplosiveCount;
    private Vector2i[] PlacedExplosives => ExpeditionInfo.PlacedExplosiveGridPositions;

    private Vector2i? PlacementIndicatorPos =>
        ExpeditionInfo.IsExplosivePlacementActive
            ? GameController.EntityListWrapper.ValidEntitiesByType[EntityType.MiscellaneousObjects]
                  .FirstOrDefault(x => x.Path == "Metadata/MiscellaneousObjects/Expedition/ExpeditionPlacementIndicator")?.GridPos.RoundToVector2I() ??
              ExpeditionInfo.PlacementIndicatorGridPosition
            : null;

    private ExpeditionDetonatorInfo ExpeditionInfo => GameController.IngameState.IngameUi.ExpeditionDetonatorElement.Info;

    private RectangleF LocalWindowRect => GameController.Window.GetWindowRectangleTimeCache with { Location = Vector2.Zero };

    public override bool Initialise()
    {
        _runePricer = new RunePricer(GameController, Settings.RuneSettings);
        GameController.SoundController.PreloadSound("expedition_attention", Path.Join(DirectoryFullName, "attention.wav"));
        Graphics.InitImage(TextureName);
        IconPickerDrawer.Instance._iconsImageId = Graphics.GetTextureId(TextureName);
        Settings.PlannerSettings.StartSearch.OnPressed += StartSearch;
        Settings.PlannerSettings.StopSearch.OnPressed += StopSearch;
        Settings.PlannerSettings.ClearSearch.OnPressed += ClearSearch;
        RegisterHotkey(Settings.PlannerSettings.StartSearchHotkey);
        RegisterHotkey(Settings.PlannerSettings.StopSearchHotkey);
        RegisterHotkey(Settings.PlannerSettings.ClearSearchHotkey);
        return base.Initialise();
    }

    public override void DrawSettings()
    {
        var knownRecipes = Settings.RuneSettings.KnownRecipes.OrderBy(x => x).ToList();
        foreach (var priceOverride in Settings.RuneSettings.PriceOverrides.Content)
        {
            priceOverride.Type.SetListValues(knownRecipes);
        }

        base.DrawSettings();
    }

    private static void RegisterHotkey(HotkeyNode hotkey)
    {
        Input.RegisterKey(hotkey);
        hotkey.OnValueChanged += () => { Input.RegisterKey(hotkey); };
    }

    private void StopSearch()
    {
        if (_plannerRunner is { } run)
        {
            run.Stop();
            Settings.PlannerSettings.SearchState = SearchState.Stopped;
        }
        else
        {
            Settings.PlannerSettings.SearchState = SearchState.Empty;
        }
    }

    private void StartSearch()
    {
        _scoreHistory = [];
        _plannerRunner?.Stop();
        _plannerRunner = null;

        ExpeditionEnvironment environment;
        try
        {
            environment = PlannerEnvironment;
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"ExpeditionIcons planner cannot start: {ex}");
            Settings.PlannerSettings.SearchState = SearchState.Empty;
            return;
        }

        var plannerRunner = new PathPlannerRunner();
        plannerRunner.Start(Settings.PlannerSettings, environment, GameController.SoundController);
        _plannerRunner = plannerRunner;
        Settings.PlannerSettings.SearchState = SearchState.Searching;
    }

    private void ClearSearch()
    {
        if (_plannerRunner is { } run)
        {
            run.Stop();
            _plannerRunner = null;
            _scoreHistory = [];
            _editedPath = null;
            _editedIndex = null;
            _editedPathEval = null;
        }
    }

    public override void AreaChange(AreaInstance area)
    {
        _plannerRunner?.Stop();
        _plannerRunner = null;
        _scoreHistory = [];
        _editedPath = null;
        _editedIndex = null;
        _editedPathEval = null;
        _detonatorPos = null;
        _cachedEntities.Clear();
        _runePricer?.Reset();
        _zoneCleared = false;
        _pathfindingData = GameController.IngameState.Data.RawPathfindingData;
        _areaDimensions = GameController.IngameState.Data.AreaDimensions;
    }

    /// <summary>
    /// True in a logbook expedition, false in the smaller encounters found in maps. Feeds both the
    /// placement range and <see cref="ExpeditionEnvironment.IsLogbook"/>, which must not be allowed
    /// to disagree. Previously inferred from MapMinimapMainAreaRevealed, which no longer exists -
    /// and since a missing stat reads as 0, that had been silently reporting every zone as a map.
    /// </summary>
    private bool IsLogbookArea =>
        (GameController.IngameState.Data.MapStats?.GetValueOrDefault(GameStat.MapExpeditionIsLogbookArea) ?? 0) != 0;

    /// <summary>
    /// Maps a Sentinel mod to the rune it grants, using the SentinelMod column the game already
    /// carries on every rune. Going through game data rather than stripping the mod name means the
    /// rune id is byte-identical to the one a runestone recipe produces - which matters, because
    /// rune mask bits are interned per id string, and two spellings of the same rune would occupy
    /// two bits and multiply together instead of being recognised as one rune.
    /// </summary>
    private Dictionary<string, string> SentinelRuneIdByModKey => _sentinelRuneIdByModKey ??=
        GameController.Files.Expedition2Runes.EntriesList
            .Where(x => !string.IsNullOrEmpty(x.SentinelMod?.Key) && !string.IsNullOrEmpty(x.Id))
            .GroupBy(x => x.SentinelMod.Key)
            .ToDictionary(x => x.Key, x => x.First().Id, StringComparer.Ordinal);

    /// <summary>
    /// Market value of one Expedition Logbook, via NinjaPricer. Resolved on every call rather than
    /// cached, since NinjaPricer may register its bridge method after we initialise; zero when it
    /// is absent, which makes the lighthouse reward worth nothing instead of a made-up number.
    /// The base item lookup IS cached - Translate logs to the console on a miss.
    /// </summary>
    private double LogbookValue
    {
        get
        {
            var getCurrencyValue = GameController.PluginBridge.GetMethod<Func<BaseItemType, double>>("NinjaPrice.GetBaseItemTypeValue");
            if (getCurrencyValue == null)
            {
                return 0;
            }

            _logbookBaseItemType ??= GameController.Files.BaseItemTypes.Translate(Icons.LogbookMetadata);
            return _logbookBaseItemType == null ? 0 : getCurrencyValue(_logbookBaseItemType);
        }
    }

    private ExpeditionEntityType GetEntityType(string path)
    {
        return _entityTypeCache.GetOrAdd(path, p => p switch
        {
            RelicPath => ExpeditionEntityType.Relic,
            FaridunExplosivePath => ExpeditionEntityType.ChainExplosive,
            OilWellPath => ExpeditionEntityType.ChainExplosive,
            BoomBarrelPath => ExpeditionEntityType.ChainExplosive,
            KaruiTotemPath => ExpeditionEntityType.Relic,
            SulphitePillarPath => ExpeditionEntityType.Relic,
            BeastSkinPath => ExpeditionEntityType.Relic,
            MarkerPath => ExpeditionEntityType.Marker,
            _ when Icons.StrongboxIndexByPath.ContainsKey(p) => ExpeditionEntityType.Strongbox,
            SentinelPath => ExpeditionEntityType.RuneSource,
            _ when p.StartsWith(RuneEncounterPath, StringComparison.Ordinal) => ExpeditionEntityType.RuneEncounter,
            _ when p.StartsWith("Metadata/Terrain/Leagues/Expedition/Tiles/ExpeditionChamber") => ExpeditionEntityType.Cave,
            _ when p.StartsWith("Metadata/Terrain/Gallows/Leagues/Expedition/Objects/ExpeditionOlrothEntrance") => ExpeditionEntityType.Boss,
            _ => ExpeditionEntityType.None,
        });
    }

    private Vector3 ExpandWithTerrainHeight(Vector2 gridPosition)
    {
        return new Vector3(gridPosition.GridToWorld(), GameController.IngameState.Data.GetTerrainHeightAt(gridPosition));
    }

    private void DrawCirclesInWorld(List<Vector3> positions, float radius, Color color)
    {
        const int segments = 90;
        const int segmentSpan = 360 / segments;
        var playerPos = GameController.Player?.GetComponent<Positioned>()?.WorldPos;
        if (playerPos == null)
        {
            return;
        }

        foreach (var position in positions
                     .Where(x => playerPos.Value.Distance(new Vector2(x.X, x.Y)) < 80 * GridToWorldMultiplier + radius))
        {
            foreach (var segmentId in Enumerable.Range(0, segments))
            {
                (Vector2, Vector2) GetVector(int i)
                {
                    var (sin, cos) = MathF.SinCos(MathF.PI / 180 * i);
                    var offset = new Vector2(cos, sin) * radius;
                    var xy = position.Xy() + offset;
                    var screen = Camera.WorldToScreen(ExpandWithTerrainHeight(xy.WorldToGrid()));
                    return (xy, screen);
                }

                var segmentOrigin = segmentId * segmentSpan;
                var (w1, c1) = GetVector(segmentOrigin);
                var (w2, c2) = GetVector(segmentOrigin + segmentSpan);
                if (Settings.ExplosivesSettings.EnableExplosiveRadiusMerging)
                {
                    if (positions
                        .Where(x => x != position)
                        .Select(x => new Vector2(x.X, x.Y))
                        .Any(x => Vector2.Distance(w1, x) < radius &&
                                  Vector2.Distance(w2, x) < radius))
                    {
                        continue;
                    }
                }

                Graphics.DrawLine(c1, c2, 1, color);
            }
        }
    }

    public override void Tick()
    {
        IconPickerDrawer.Instance._iconsImageId = Graphics.GetTextureId(TextureName);
        if (Settings.RuneSettings.EnableRuneDisplay || Settings.PlannerSettings.RuneScoring.EnableRuneScoring)
        {
            _runePricer?.Update();
        }

        Settings.PlannerSettings.SearchState = _plannerRunner switch
        {
            { IsRunning: true } => SearchState.Searching,
            { IsRunning: false, CurrentBestPath.PerPointScore.Count: > 0 } => SearchState.Stopped,
            _ => SearchState.Empty
        };

        var detonatorPos = DetonatorPos;
        var playerGridPos = GameController.Player?.GetComponent<Positioned>()?.WorldPos.WorldToGrid();
        if (playerGridPos == null)
        {
            return;
        }

        _playerGridPos = playerGridPos.Value;
        if (detonatorPos is { Pos: var dp } && _playerGridPos.Distance(dp) < 90)
        {
            _zoneCleared = DetonatorEntity?.IsTargetable != true;
            if (_zoneCleared)
            {
                ClearSearch();
                return;
            }
        }

        var ingameUi = GameController.Game.IngameState.IngameUi;
        var map = ingameUi.Map;
        var largeMap = map.LargeMap.AsObject<SubMap>();
        _largeMapOpen = largeMap.IsVisible;
        _mapScale = GameController.IngameState.Camera.Height / 677f * largeMap.Zoom;
        _mapCenter = largeMap.GetClientRect().TopLeft + largeMap.Shift + largeMap.DefaultShift;
        _playerZ = GameController.Player.GetComponent<Render>().Z;

        _explosiveRadius = Settings.ExplosivesSettings.CalculateRadiusAutomatically
            //ReSharper disable once PossibleLossOfFraction
            //rounding here is extremely important to get right, this is taken from the game's code
            ? (IsLogbookArea ? LogbookExplosiveBaseRadius : MapExplosiveBaseRadius) *
              (100 + (GameController.IngameState.Data.MapStats?.GetValueOrDefault(GameStat.MapExpeditionExplosionRadiusPct) ?? 0)) / 100 * GridToWorldMultiplier
            : Settings.ExplosivesSettings.ExplosiveRadius.Value;
        //ReSharper disable once PossibleLossOfFraction
        //rounding here is extremely important to get right, this is taken from the game's code
        _explosiveRange = (IsLogbookArea ? LogbookExplosiveBaseRange : MapExplosiveBaseRange) *
                          (100 + (GameController.IngameState.Data.MapStats?.GetValueOrDefault(GameStat.MapExpeditionMaximumPlacementDistancePct) ?? 0)) / 100 *
                          GridToWorldMultiplier;

        foreach (var entity in new[] { EntityType.IngameIcon, EntityType.Terrain, EntityType.Chest }
                     .SelectMany(x => GameController.EntityListWrapper.ValidEntitiesByType[x]))
        {
            if (GetEntityType(entity.Path) != ExpeditionEntityType.None)
            {
                var newValue = BuildCacheItem(entity);
                _cachedEntities[entity.Id] = _cachedEntities.TryGetValue(entity.Id, out var oldValue)
                    ? oldValue.Merge(newValue)
                    : newValue;
            }
        }

        return;
    }

    /// <summary>
    /// Chest markers are classified by MinimapIcon name first, falling back to the animated .ao
    /// metadata. Cached on the pair, since the same .ao can map to two different chest types
    /// (RewardChestCurrency vs RewardChestCurrencyRare) depending on the icon.
    /// </summary>
    private ExpeditionMarkerIconDescription ResolveChestIcon(EntityCacheItem e)
    {
        var animatedMetadata = e.BaseAnimatedEntityMetadata;
        if (animatedMetadata == null && string.IsNullOrEmpty(e.MinimapIconName))
        {
            return null;
        }

        return _metadataIconMapping.GetOrAdd($"{e.MinimapIconName}|{animatedMetadata}",
            _ => Icons.GetChestIcon(e.MinimapIconName, animatedMetadata));
    }

    /// <summary>
    /// Interns rune ids to bit positions for the duration of one environment build, and holds
    /// the multiplier for each. Scoring then works on masks and never touches strings.
    /// </summary>
    private sealed class RuneBitTable
    {
        private readonly Dictionary<string, int> _bits = new(StringComparer.Ordinal);
        private readonly List<double> _multipliers = [];
        private readonly PlannerSettings _settings;
        private bool _loggedOverflow;

        public RuneBitTable(PlannerSettings settings)
        {
            _settings = settings;
        }

        public double[] Multipliers => _multipliers.ToArray();

        /// <summary>Bit index for this rune, or -1 once the 64-rune budget is exhausted.</summary>
        public int GetBit(string runeId)
        {
            if (string.IsNullOrEmpty(runeId))
            {
                return -1;
            }

            if (_bits.TryGetValue(runeId, out var bit))
            {
                return bit;
            }

            //Drop rather than wrap: a wrapped bit would silently corrupt every score.
            if (_bits.Count >= 64)
            {
                if (!_loggedOverflow)
                {
                    _loggedOverflow = true;
                    DebugWindow.LogError($"ExpeditionIcons: more than 64 distinct runes in this area. '{runeId}' and any further runes are ignored by the planner.");
                }

                return -1;
            }

            bit = _bits.Count;
            _bits[runeId] = bit;
            _multipliers.Add(_settings.RuneMultipliers.GetValueOrDefault(runeId, _settings.DefaultRuneMultiplier));
            return bit;
        }

        public double Product(ulong mask)
        {
            var result = 1.0;
            while (mask != 0)
            {
                var bit = System.Numerics.BitOperations.TrailingZeroCount(mask);
                mask &= mask - 1;
                result *= _multipliers[bit];
            }

            return result;
        }
    }

    private RuneBitTable BuildRuneBitTable()
    {
        return new RuneBitTable(Settings.PlannerSettings);
    }

    /// <summary>
    /// Turns a runestone's eligible recipes into the candidate list the genome indexes into.
    /// Pipeline: dedupe, dominance prune (lossless), value window, top-N backstop, seed first.
    /// </summary>
    private RunestoneCandidate[] BuildRunestoneCandidates(RuneValueInfo info, RuneBitTable runeBits)
    {
        var planner = Settings.PlannerSettings;
        var scoring = planner.RuneScoring;
        var passedOnPositions = info.PassedOnPositions;
        var raw = new List<RunestoneCandidate>(info.Recipes.Count);
        var seen = new HashSet<(ulong, ulong, double)>();

        foreach (var entry in info.Recipes)
        {
            var runes = entry.Recipe?.Runes;
            if (runes == null)
            {
                continue;
            }

            ulong recipeMask = 0;
            foreach (var rune in runes)
            {
                var bit = runeBits.GetBit(rune?.Id);
                if (bit >= 0)
                {
                    recipeMask |= 1UL << bit;
                }
            }

            //PassedOnRunePositions are 0-based, and so is indexing into Recipe.Runes.
            ulong passedOnMask = 0;
            if (passedOnPositions != null)
            {
                foreach (var position in passedOnPositions)
                {
                    if (position < 0 || position >= runes.Count)
                    {
                        continue;
                    }

                    var bit = runeBits.GetBit(runes[position]?.Id);
                    if (bit >= 0)
                    {
                        passedOnMask |= 1UL << bit;
                    }
                }
            }

            if (!seen.Add((recipeMask, passedOnMask, entry.Value)))
            {
                continue;
            }

            raw.Add(new RunestoneCandidate(entry.Recipe, entry.Value, recipeMask, passedOnMask, runeBits.Product(passedOnMask)));
        }

        if (raw.Count == 0)
        {
            return [];
        }

        //Dominance: worse-or-equal price with a subset of both masks can never win.
        var kept = raw.FindAll(b => !raw.Any(a =>
            !ReferenceEquals(a, b) &&
            a.Price >= b.Price &&
            (a.RecipeRuneMask & b.RecipeRuneMask) == b.RecipeRuneMask &&
            (a.PassedOnMask & b.PassedOnMask) == b.PassedOnMask &&
            (a.Price > b.Price || a.RecipeRuneMask != b.RecipeRuneMask || a.PassedOnMask != b.PassedOnMask)));

        var bestPrice = kept.Max(x => x.Price);

        //Value window, only when something clears the threshold. Below it every candidate scores
        //the same flat weight, so price carries no information and filtering on it would prune
        //along a dimension that does not affect the score.
        if (bestPrice >= scoring.ValueThreshold)
        {
            var window = planner.RecipeSearchWindow.Value;
            var scale = scoring.AboveThresholdScale.Value;
            var windowed = kept.FindAll(x => (bestPrice - x.Price) * scale <= window);
            if (windowed.Count > 0)
            {
                kept = windowed;
            }
        }

        //The seed must end up at index 0: BuildPath fills every genome with 0, so a different
        //candidate first would silently start every path from the wrong recipe.
        var seed = kept.MaxBy(x => x.Price);
        var rest = kept.Where(x => !ReferenceEquals(x, seed))
            .OrderByDescending(x => x.PassedOnProduct)
            .ThenByDescending(x => x.Price)
            .Take(Math.Max(0, planner.MaxRecipeCandidates.Value - 1));

        return new[] { seed }.Concat(rest).ToArray();
    }

    private (Vector2 Min, Vector2 Max)? GetExclusionRect()
    {
        if (DetonatorPos is not { } detonatorPos)
        {
            return null;
        }

        var negVec = new Vector2(-11.5f, -8.5f);
        var posVec = new Vector2(10.5f, 23.5f);
        var rotations = (int)Math.Round(detonatorPos.Rotation / (MathF.PI / 2));
        for (int i = 0; i < rotations; i++)
        {
            (negVec.X, negVec.Y, posVec.X, posVec.Y) = (-posVec.Y, negVec.X, -negVec.Y, posVec.X);
        }

        return (detonatorPos.Pos + negVec, detonatorPos.Pos + posVec);
    }

    private ExpeditionEnvironment PlannerEnvironment => BuildEnvironment();

    private ExpeditionEnvironment BuildEnvironment()
    {
        if (DetonatorPos is not { Pos: var detonatorPos })
        {
            throw new Exception("Unable to plan a path: detonator position is unknown");
        }

        var loot = new List<(Vector2, IExpeditionLoot)>();
        //Collected separately so duplicates can be dropped once every gate is known.
        var karuiGates = new List<(uint Id, Vector2 Pos)>();
        var relics = new List<(Vector2, IExpeditionRelic)>();
        var runeBits = BuildRuneBitTable();
        var runestoneCount = 0;
        var chainExplosives = new List<ChainExplosive>();
        foreach (var e in _cachedEntities.Values)
        {
            switch (GetEntityType(e.Path))
            {
                case ExpeditionEntityType.Marker:
                {
                    var animatedMetaData = e.BaseAnimatedEntityMetadata;
                    if (animatedMetaData != null)
                    {
                        if (animatedMetaData.Contains("elitemarker"))
                        {
                            loot.Add((e.GridPos, new RunicMonster()));
                        }
                        else
                        {
                            var iconDescription = ResolveChestIcon(e);
                            if (iconDescription != null)
                            {
                                loot.Add((e.GridPos, new PathPlannerData.Chest(iconDescription.IconPickerIndex)));
                            }
                        }
                    }

                    continue;
                }
                case ExpeditionEntityType.Relic:
                {
                    var mods = e.Mods;
                    if (mods == null)
                    {
                        continue;
                    }

                    if (e.MinimapIconHide != false) continue;
                    if (!mods.Any(x => x.Contains("ExpeditionRelic"))) continue;

                    if (ContainsWarnMods(mods))
                    {
                        relics.Add((e.GridPos, new WarningRelic()));
                        continue;
                    }

                    var iconDescriptions = mods.SelectMany(mod =>
                        _relicModIconMapping.GetOrAdd(mod, s =>
                            Icons.ExpeditionRelicIcons.Where(icon =>
                                icon.BaseEntityMetadataSubstrings.Any(s.Contains)).ToList())).Distinct();
                    var allSubStrings = iconDescriptions.SelectMany(d => d.BaseEntityMetadataSubstrings).ToList();
                    var fittingMods = mods
                        .SelectMany(mod => allSubStrings.Where(mod.Contains))
                        .Distinct()
                        .Select(x => Icons.GetRelicType(x, Settings.PlannerSettings));
                    relics.AddRange(fittingMods.Select(expeditionRelic => (e.GridPos, expeditionRelic)));

                    break;
                }
                case ExpeditionEntityType.Cave:
                {
                    //Shits given about performance: some? a few?
                    for (int i = 0; i < Settings.PlannerSettings.LogbookCaveRunicMonsterMultiplier; i++)
                    {
                        loot.Add((e.GridPos, new RunicMonster()));
                    }

                    for (int i = 0; i < Settings.PlannerSettings.LogbookCaveArtifactChestMultiplier; i++)
                    {
                        loot.Add((e.GridPos, new PathPlannerData.Chest(IconPickerIndex.LeagueChest)));
                    }

                    break;
                }
                case ExpeditionEntityType.Boss:
                {
                    //Shits given about performance: some? a few?
                    for (int i = 0; i < Settings.PlannerSettings.LogbookBossRunicMonsterMultiplier; i++)
                    {
                        loot.Add((e.GridPos, new RunicMonster()));
                    }

                    break;
                }
                case ExpeditionEntityType.RuneSource:
                {
                    if (Settings.PlannerSettings.RuneScoring.EnableRuneScoring)
                    {
                        ulong mask = 0;
                        foreach (var modKey in e.RewardItemModKeys ?? [])
                        {
                            if (!SentinelRuneIdByModKey.TryGetValue(modKey, out var runeId))
                            {
                                continue;
                            }

                            var bit = runeBits.GetBit(runeId);
                            if (bit >= 0)
                            {
                                mask |= 1UL << bit;
                            }
                        }

                        //No recognised rune means nothing to propagate, so nothing to score.
                        if (mask != 0)
                        {
                            loot.Add((e.GridPos, new RuneSource(mask)));
                        }
                    }

                    break;
                }
                case ExpeditionEntityType.Strongbox:
                {
                    if (e.Path == Icons.LighthousePath)
                    {
                        //Not a Chest: its reward depends on how many others the path also reaches,
                        //so it cannot be valued where it is caught.
                        loot.Add((e.GridPos, new Lighthouse()));
                    }
                    else if (e.Path == Icons.KaruiGatePath)
                    {
                        karuiGates.Add((e.Id, e.GridPos));
                    }
                    else
                    {
                        loot.Add((e.GridPos, new PathPlannerData.Chest(Icons.StrongboxIndexByPath[e.Path])));
                    }

                    break;
                }
                case ExpeditionEntityType.ChainExplosive:
                {
                    var isOilWell = e.Path == OilWellPath;
                    chainExplosives.Add(new ChainExplosive(
                        e.GridPos,
                        isOilWell ? OilWellRadius : FaridunExplosiveRadius,
                        isOilWell));
                    break;
                }
                case ExpeditionEntityType.RuneEncounter:
                {
                    //Encounters with no resolved price, and encounters already activated, are
                    //simply absent from the loot list: the planner neither chases nor avoids them.
                    if (Settings.PlannerSettings.RuneScoring.EnableRuneScoring &&
                        _runePricer != null &&
                        !_runePricer.IsActivated(e.Id) &&
                        _runePricer.TryGetInfo(e.Id, out var runeInfo))
                    {
                        var candidates = BuildRunestoneCandidates(runeInfo, runeBits);
                        //CoveredMask is a ulong, so runestone 64+ cannot be represented. Drop and
                        //log rather than wrap, which would silently corrupt coverage tracking.
                        if (candidates.Length > 0 && runestoneCount >= 64)
                        {
                            DebugWindow.LogError("ExpeditionIcons: more than 64 runestones in this area, the extras are ignored by the planner.");
                        }
                        else if (candidates.Length > 0)
                        {
                            var runestone = new PathPlannerData.RuneEncounter(e.Id, runestoneCount++, candidates);

                            //Two independent contributions: the static drop (relic-immune, kept
                            //out of the relic list entirely) and the monsters it spawns.
                            loot.Add((e.GridPos, runestone));
                            loot.Add((e.GridPos, new RunestoneMonster(runestone)));
                        }
                    }

                    break;
                }
            }
        }

        //One hoard can be walled off by two gates, and blowing either one opens it. Scoring both
        //would make a pair look twice as valuable as a lone gate guarding the same reward, so only
        //the first of each cluster counts. Ordered by entity id purely so the survivor is stable
        //across rebuilds - _cachedEntities does not promise an order.
        var countedGates = new List<Vector2>();
        foreach (var gate in karuiGates.OrderBy(x => x.Id))
        {
            if (countedGates.Exists(x => x.DistanceLessThanOrEqual(gate.Pos, KaruiGateClusterRadius)))
            {
                continue;
            }

            countedGates.Add(gate.Pos);
            loot.Add((gate.Pos, new PathPlannerData.Chest(IconPickerIndex.KaruiGate)));
        }

        return new ExpeditionEnvironment(
            relics.FindAll(x => x.Item2 != null),
            loot.FindAll(x => x.Item2 != null),
            _explosiveRange / GridToWorldMultiplier,
            _explosiveRadius / GridToWorldMultiplier,
            ExpeditionInfo.TotalExplosiveCount,
            detonatorPos,
            IsValidPlacement,
            GetExclusionRect() ?? default,
            IsLogbookArea,
            runeBits.Multipliers,
            runestoneCount,
            chainExplosives,
            LogbookValue);
    }

    private bool IsValidPlacement(Vector2 x)
    {
        if (_pathfindingData == null ||
            x.X < 0 ||
            x.Y < 0 ||
            x.X >= _areaDimensions.X ||
            x.Y >= _areaDimensions.Y)
        {
            return false;
        }

        var rowIndex = (int)x.Y;
        var columnIndex = (int)x.X;
        if (rowIndex >= _pathfindingData.Length)
        {
            return false;
        }

        var row = _pathfindingData[rowIndex];
        return row != null &&
               columnIndex < row.Length &&
               row[columnIndex] > 3;
    }

    public override void Render()
    {
        if (Settings.PlannerSettings.ClearSearchHotkey.PressedOnce())
        {
            ClearSearch();
        }

        if (Settings.PlannerSettings.StopSearchHotkey.PressedOnce())
        {
            StopSearch();
        }

        if (_zoneCleared)
        {
            return;
        }

        if (Settings.PlannerSettings.StartSearchHotkey.PressedOnce())
        {
            StartSearch();
        }

        var explosives3D = GameController.EntityListWrapper.ValidEntitiesByType[EntityType.IngameIcon]
            .Where(x => x.Path == ExplosivePath)
            .Select(x => x.Pos)
            .ToList();
        _explosives2DPositions = explosives3D.Select(x => new Vector2(x.X, x.Y)).ToList();
        if (Settings.ExplosivesSettings.ShowExplosives)
        {
            DrawCirclesInWorld(
                positions: explosives3D,
                radius: _explosiveRadius,
                color: Settings.ExplosivesSettings.ExplosiveColor.Value);
        }

        foreach (var e in _cachedEntities.Values)
        {
            switch (GetEntityType(e.Path))
            {
                case ExpeditionEntityType.Marker:
                {
                    var animatedMetaData = e.BaseAnimatedEntityMetadata;
                    if (animatedMetaData != null)
                    {
                        if (animatedMetaData.Contains("elitemarker"))
                        {
                            var mapSettings = Settings.IconMapping.GetValueOrDefault(IconPickerIndex.EliteMonstersIndicator, new IconDisplaySettings());
                            if (mapSettings.ShowOnMap)
                            {
                                DrawIconOnMap(e, mapSettings.Icon ?? ExpeditionIconsSettings.DefaultEliteMonsterIcon, mapSettings.Tint, Vector2.Zero);
                            }

                            if (mapSettings.ShowInWorld)
                            {
                                DrawIconInWorld(e, mapSettings.Icon ?? ExpeditionIconsSettings.DefaultEliteMonsterIcon, mapSettings.Tint, Vector2.Zero);
                            }
                        }
                        else
                        {
                            var iconDescription = ResolveChestIcon(e);
                            if (iconDescription != null)
                            {
                                var settings = Settings.IconMapping.GetValueOrDefault(iconDescription.IconPickerIndex, new IconDisplaySettings());
                                var icon = settings.Icon ?? iconDescription.DefaultIcon;
                                if (settings.ShowOnMap)
                                {
                                    DrawIconOnMap(e, icon, settings.Tint, Vector2.Zero);
                                }

                                if (settings.ShowInWorld)
                                {
                                    DrawIconInWorld(e, icon, settings.Tint, Vector2.Zero);
                                }
                            }
                        }
                    }

                    continue;
                }
                case ExpeditionEntityType.Relic:
                {
                    var mods = e.Mods;
                    if (e.Mods == null) continue;
                    if (e.MinimapIconHide != false) continue;
                    if (!mods.Any(x => x.Contains("ExpeditionRelic"))) continue;

                    if (ContainsWarnMods(mods))
                    {
                        var mapSettings = Settings.IconMapping.GetValueOrDefault(IconPickerIndex.BadModsIndicator, new IconDisplaySettings());
                        if (mapSettings.ShowOnMap)
                        {
                            DrawIconOnMap(e, mapSettings.Icon ?? ExpeditionIconsSettings.DefaultBadModsIcon, mapSettings.Tint, Vector2.Zero);
                        }

                        if (mapSettings.ShowInWorld)
                        {
                            DrawIconInWorld(e, mapSettings.Icon ?? ExpeditionIconsSettings.DefaultBadModsIcon, mapSettings.Tint, -Vector2.UnitY);
                        }

                        continue;
                    }

                    if (Settings.DrawGoodModsInWorld || Settings.DrawGoodModsOnMap)
                    {
                        var worldIcons = new HashSet<(MapIconsIndex, Color?)>();
                        var mapIcons = new HashSet<(MapIconsIndex, Color?)>();
                        var iconDescriptions = mods.SelectMany(mod =>
                            _relicModIconMapping.GetOrAdd(mod, s =>
                                Icons.ExpeditionRelicIcons.Where(icon =>
                                    icon.BaseEntityMetadataSubstrings.Any(s.Contains)).ToList())).Distinct();
                        foreach (var iconDescription in iconDescriptions)
                        {
                            var settings = Settings.IconMapping.GetValueOrDefault(iconDescription.IconPickerIndex, new IconDisplaySettings());
                            var icon = settings.Icon ?? iconDescription.DefaultIcon;
                            if (settings.ShowOnMap)
                            {
                                mapIcons.Add((icon, settings.Tint));
                            }

                            if (settings.ShowInWorld)
                            {
                                worldIcons.Add((icon, settings.Tint));
                            }
                        }

                        var offset = new Vector2(-worldIcons.Count * 0.5f + 0.5f, 0);
                        foreach (var (icon, tint) in worldIcons)
                        {
                            if (Settings.DrawGoodModsInWorld)
                            {
                                DrawIconInWorld(e, icon, tint, offset);
                            }

                            offset += Vector2.UnitX;
                        }

                        offset = new Vector2(-mapIcons.Count * 0.5f + 0.5f, 0);
                        foreach (var (icon, tint) in mapIcons)
                        {
                            if (Settings.DrawGoodModsOnMap)
                            {
                                DrawIconOnMap(e, icon, tint, offset);
                            }

                            offset += Vector2.UnitX;
                        }
                    }

                    break;
                }
            }
        }

        DrawRuneDisplay();

        if (EditedOrNativeScore is { PerPointScore.Count: > 0 } score)
        {
            var path = score.PerPointScore;
            var firstPoint = DetonatorPos?.Pos ?? _playerGridPos;
            var prevPoint = firstPoint;
            for (var i = 0; i < path.Count; i++)
            {
                var point = path[i].Point;
                if (_largeMapOpen)
                {
                    Graphics.DrawLine(GetMapScreenPosition(prevPoint), GetMapScreenPosition(point), 1, Settings.PlannerSettings.MapLineColor);
                }

                var worldPos = GetWorldScreenPosition(point);
                Graphics.DrawLine(GetWorldScreenPosition(prevPoint), worldPos, 1, Settings.PlannerSettings.WorldLineColor);
                var text = $"#{i}";
                using (Graphics.SetTextScale(Settings.PlannerSettings.TextMarkerScale))
                {
                    Graphics.DrawBox(worldPos, worldPos + Graphics.MeasureText(text), Color.Black);
                    Graphics.DrawText(text, worldPos, Color.White);
                    prevPoint = point;
                }
            }

            if (Settings.PlannerSettings.IsSearchRunning)
            {
                _scoreHistory.Add((float)score.TotalScore);
            }

            ShowSearchWindow(score);

            //Radii vary per point once an oil well fires, and chained blasts have their own,
            //so draw by radius group rather than assuming one circle size for the whole path.
            foreach (var group in path.GroupBy(x => x.Radius))
            {
                DrawCirclesInWorld(
                    positions: group.Select(x => ExpandWithTerrainHeight(x.Point)).ToList(),
                    radius: group.Key * GridToWorldMultiplier,
                    color: Settings.PlannerSettings.ExplosiveColor.Value);
            }

            foreach (var group in path.SelectMany(x => x.Blasts.Skip(1)).GroupBy(x => x.Radius))
            {
                DrawCirclesInWorld(
                    positions: group.Select(x => ExpandWithTerrainHeight(x.Pos)).ToList(),
                    radius: group.Key * GridToWorldMultiplier,
                    color: Settings.PlannerSettings.ChainedBlastColor.Value);
            }
        }
    }

    /// <summary>
    /// Rune encounter display, ported from Expedition2Good.Render. Three paths:
    /// the ranked recipe list under each encounter label, the top-pick value on the map,
    /// and the reward option overlay inside the Expedition2 window.
    /// </summary>
    private void DrawRuneDisplay()
    {
        var runeSettings = Settings.RuneSettings;
        if (!runeSettings.EnableRuneDisplay || _runePricer == null)
        {
            return;
        }

        var entities = GameController.EntityListWrapper.ValidEntitiesByType[EntityType.IngameIcon]
            .Where(x => x.Metadata?.StartsWith(RuneEncounterPath, StringComparison.Ordinal) == true)
            .ToList();

        if (_runePricer.Labels is { Count: > 0 } labels)
        {
            foreach (var (log, label) in labels)
            {
                var entity = log.ItemOnGround;
                //Matches Expedition2Good: a hidden activated encounter is skipped before it is
                //removed from the candidate list, so it still falls through to the "Unknown rune" pass.
                if (entity == null ||
                    runeSettings.DisplayOnlyNonActivated && RunePricer.IsEntityActivated(entity))
                {
                    continue;
                }

                entities.Remove(entity);

                if (!_runePricer.TryGetInfo(entity.Id, out var info))
                {
                    continue;
                }

                var recipes = info.Recipes;
                if (runeSettings.MinimumValueToShow > 0)
                {
                    recipes = recipes.Where(x => x.Value >= runeSettings.MinimumValueToShow).ToList();
                }

                if (runeSettings.MaxItemsToShow > 0)
                {
                    recipes = recipes.Take(runeSettings.MaxItemsToShow).ToList();
                }

                var bottomLeft = label.GetClientRect().BottomLeft;
                bottomLeft += new Vector2(runeSettings.RenderOffsetX, runeSettings.RenderOffsetY);
                var y = bottomLeft.Y;

                ExpectedChoices.TryGetValue(entity.Id, out var expectedChoice);
                //Once a path exists and covers this runestone there is exactly one recipe worth
                //highlighting. Every other line drops to the plain text colour so the plan is the
                //only thing standing out - otherwise the green top-price line competes with it.
                var hasPlan = expectedChoice != null;

                var first = true;
                foreach (var entry in recipes)
                {
                    var value = entry.Value;
                    var overridden = entry.IsOverridden;
                    //The planner's pick is often NOT the first line: below the value threshold the
                    //recipe is chosen for its runes, so it can sit anywhere in the list.
                    var isExpectedLine = hasPlan && ReferenceEquals(entry.Recipe, expectedChoice.Recipe);
                    var expectedMarker = isExpectedLine ? ">" : " ";
                    if (first && runeSettings.ShowOnMinimap)
                    {
                        //Threshold coloring, not TopPickColor: Expedition2Good reserves that for the list.
                        var mapColor = value >= runeSettings.ValuableColorThreshold
                            ? runeSettings.ValuableTextColor.Value
                            : runeSettings.TextColor.Value;
                        Graphics.DrawTextWithBackground($"Rune {(overridden ? "~" : "")}{value:F1} ({label.RuneCount} sockets)",
                            Graphics.GridToMap(entity.GridPos, entity.GridPos), mapColor, Color.Black);
                    }

                    var textColor = hasPlan
                        ? isExpectedLine ? runeSettings.TopPickColor.Value : runeSettings.TextColor.Value
                        : first
                            ? runeSettings.TopPickColor.Value
                            : value >= runeSettings.ValuableColorThreshold
                                ? runeSettings.ValuableTextColor.Value
                                : runeSettings.TextColor.Value;
                    var size = Graphics.DrawTextWithBackground(
                        $"{expectedMarker}{(overridden ? "~" : "")}{value,7:F2} {(string.IsNullOrWhiteSpace(entry.Recipe.Description) ? entry.Recipe.Reward?.BaseName : entry.Recipe.Description)} x{entry.Recipe.RewardCount}",
                        bottomLeft with { Y = y },
                        textColor, Color.Black);
                    y += size.Y;
                    first = false;
                }
            }
        }

        if (runeSettings.ShowOnMinimap)
        {
            foreach (var entity in entities)
            {
                if (RunePricer.GetSocketCount(entity) is { } runeCount)
                {
                    //Three-argument overload: Color.Black is the BACKGROUND, the text uses the default color.
                    Graphics.DrawTextWithBackground($"Unknown rune {runeCount} sockets",
                        Graphics.GridToMap(entity.GridPos, entity.GridPos), Color.Black);
                }
            }
        }

        DrawRuneWindowOverlay(runeSettings);
    }

    private void DrawRuneWindowOverlay(RuneDisplaySettings runeSettings)
    {
        if (GameController.IngameState.IngameUi.Expedition2Window is not { IsVisible: true } expedition2Window)
        {
            return;
        }

        var windowRect = expedition2Window.GetClientRectCache;
        if (!IsDrawableRect(windowRect) || expedition2Window.Options is not { } windowOptions)
        {
            return;
        }

        var options = windowOptions
            .Where(x => x is { IsValid: true, IsVisible: true, IsVisibleLocal: true, Recipe: not null })
            .Select(x => (Option: x, Price: _runePricer.GetPriceOrDefault(x.Recipe)))
            .OrderByDescending(x => x.Price.Value)
            .ToList();

        var expectedChoice = GetExpectedChoiceForOpenWindow();
        //With a plan in hand, only the option to click is highlighted. The bars and threshold
        //colours on the others are suppressed: a green top-price bar next to the planner's pick
        //is exactly the ambiguity this indicator exists to remove.
        var hasPlan = expectedChoice != null;
        var expectedWasOffered = false;

        var first = true;
        foreach (var (option, (value, overridden)) in options)
        {
            var optionRect = option.GetClientRectCache;
            var bounds = windowRect;
            if (!IsDrawableRect(optionRect) ||
                !bounds.Intersects(optionRect) ||
                !bounds.Contains(optionRect.TopLeft))
            {
                continue;
            }

            var isExpected = hasPlan && ReferenceEquals(option.Recipe, expectedChoice.Recipe);
            expectedWasOffered |= isExpected;
            var text = $"{(isExpected ? "> " : "")}{(overridden ? "~" : "")}{value,5:F2}";
            var textSize = Graphics.MeasureText(text);
            var position = ClampTextPosition(optionRect.TopRight, textSize, bounds);
            var textColor = hasPlan
                ? isExpected ? runeSettings.TopPickColor.Value : runeSettings.TextColor.Value
                : first
                    ? runeSettings.TopPickColor.Value
                    : value >= runeSettings.ValuableColorThreshold
                        ? runeSettings.ValuableTextColor.Value
                        : runeSettings.TextColor.Value;
            Graphics.DrawTextWithBackground(text, position, textColor, Color.Black);

            //Only the planned option keeps its bar once a plan exists.
            if (!hasPlan || isExpected)
            {
                Graphics.DrawLine(optionRect.TopRight.Translate(-3, 0), optionRect.BottomRight.Translate(-3, 0), 5, textColor);
            }

            first = false;
        }

        //The game rolls its own options, so the planned recipe may simply not be on offer.
        //Say so: an unmarked overlay would otherwise read as "the planner had no opinion".
        if (expectedChoice != null && !expectedWasOffered)
        {
            Graphics.DrawTextWithBackground("planned recipe not offered here",
                ClampTextPosition(windowRect.TopLeft.Translate(4, 4), Graphics.MeasureText("planned recipe not offered here"), windowRect),
                runeSettings.ValuableTextColor.Value, Color.Black);
        }
    }

    private static bool IsDrawableRect(RectangleF rect)
    {
        return rect.Width > 1 && rect.Height > 1;
    }

    private static Vector2 ClampTextPosition(Vector2 position, Vector2 textSize, RectangleF bounds)
    {
        var maxX = Math.Max(bounds.Left, bounds.Right - textSize.X);
        var maxY = Math.Max(bounds.Top, bounds.Bottom - textSize.Y);
        return new Vector2(Math.Clamp(position.X, bounds.Left, maxX), Math.Clamp(position.Y, bounds.Top, maxY));
    }

    /// <summary>
    /// Per-runestone audit of what the search assumed: the recipe it picked, what that recipe
    /// pays, and which runes it hands forward. Only runestones the path actually detonates appear.
    /// </summary>
    private void DrawExpectedChoiceTable(PathPlanner.DetailedLootScore score)
    {
        var choices = ExpectedChoices;
        if (choices.Count == 0 || !ImGui.TreeNode("Runestone recipe choices"))
        {
            return;
        }

        if (ImGui.BeginTable("Runestone choices", 4, ImGuiTableFlags.Hideable | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Recipe");
            ImGui.TableSetupColumn("Value");
            ImGui.TableSetupColumn("Recipe runes");
            ImGui.TableSetupColumn("Passed on");
            ImGui.TableHeadersRow();

            foreach (var (entityId, choice) in choices)
            {
                ImGui.TableNextRow();
                ImGui.PushID((int)entityId);

                ImGui.TableNextColumn();
                ImGui.Text(string.IsNullOrWhiteSpace(choice.Recipe?.Description)
                    ? choice.Recipe?.Reward?.BaseName ?? "?"
                    : choice.Recipe.Description);

                ImGui.TableNextColumn();
                ImGui.Text($"{choice.Price,8:F2}");

                var runes = choice.Recipe?.Runes;
                ImGui.TableNextColumn();
                ImGui.Text(runes == null ? "-" : string.Join(", ", runes.Select(x => x?.Id ?? "?")));

                //Passed-on positions are 0-based indices into the recipe's rune list.
                ImGui.TableNextColumn();
                if (runes != null && _runePricer != null && _runePricer.TryGetInfo(entityId, out var info) && info.PassedOnPositions is { Count: > 0 } positions)
                {
                    ImGui.Text(string.Join(", ", positions
                        .Where(x => x >= 0 && x < runes.Count)
                        .Select(x => runes[x]?.Id ?? "?")));
                }
                else
                {
                    ImGui.Text("-");
                }

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        ImGui.TreePop();
    }

    private void ShowSearchWindow(PathPlanner.DetailedLootScore score)
    {
        if (Settings.PlannerSettings.ShowScoreHistory &&
            (Settings.PlannerSettings.IsSearchRunning || Settings.PlannerSettings.ShowScoreHistoryAfterSearchEnds) &&
            ImGui.Begin("Expedition planning result"))
        {
            if (ImGui.TreeNode("Detailed view"))
            {
                PathPlanner.DetailedLootScore scoreDiff = null;
                if (_editedPath != null && _editedIndex is { } editedIndex)
                {
                    var pos = GameController.IngameState.ServerData.WorldMousePosition.WorldToGrid();
                    var pp = new PathPlanner(Settings.PlannerSettings);
                    pp.Init(score.Environment);
                    var path = _editedPath.Clone();
                    path.Points[editedIndex] = pos;
                    scoreDiff = pp.GetDetailedScore(path, score.Environment);
                    DrawCirclesInWorld([ExpandWithTerrainHeight(pos)], _explosiveRadius, Color.LightBlue);
                    Graphics.DrawLine(GetWorldScreenPosition(_editedPath.Points[editedIndex]), GetWorldScreenPosition(pos), 1, Settings.PlannerSettings.WorldLineColor);

                    if (Input.IsKeyDown(Settings.PlannerSettings.ConfirmEditorPlacementHotkey))
                    {
                        _editedPath.Points[editedIndex] = pos;
                        _editedPathEval = pp.GetDetailedScore(_editedPath, score.Environment);
                        _editedIndex = null;
                    }

                    if (Input.IsKeyDown(Keys.Escape))
                    {
                        _editedIndex = null;
                    }
                }

                if (ImGui.BeginTable("Change per explosive", 6, ImGuiTableFlags.Hideable | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("Id");
                    ImGui.TableSetupColumn("Running score");
                    ImGui.TableSetupColumn("Score diff");
                    ImGui.TableSetupColumn("New relic mods");
                    ImGui.TableSetupColumn("New loot");
                    ImGui.TableSetupColumn("Edit");
                    ImGui.TableHeadersRow();

                    var runningScore = 0.0;
                    var runningScoreAfterDiff = 0.0;
                    for (var i = 0; i < score.PerPointScore.Count; i++)
                    {
                        var perPointLootScore = score.PerPointScore[i];
                        var diffOrOld = scoreDiff?.PerPointScore[i] ?? perPointLootScore;
                        ImGui.TableNextRow();
                        ImGui.PushID(i);
                        ImGui.TableNextColumn();
                        ImGui.Text($"{i,2}");
                        ImGui.TableNextColumn();
                        runningScore += perPointLootScore.ScoreDiff;
                        if (scoreDiff != null)
                        {
                            runningScoreAfterDiff += scoreDiff.PerPointScore[i].ScoreDiff;
                            ImGui.Text($"{runningScoreAfterDiff,7:F2}");
                            var valueDiff = runningScoreAfterDiff - runningScore;
                            if (valueDiff != 0)
                            {
                                ImGui.SameLine();
                                ImGui.TextColored(GetCompareColor(runningScoreAfterDiff, runningScore), $"{valueDiff:(+0.00);(-0.00);''}");
                            }
                        }
                        else
                        {
                            ImGui.Text($"{runningScore,7:F2}");
                        }

                        ImGui.TableNextColumn();
                        ImGui.Text($"{diffOrOld.ScoreDiff,7:F2}");
                        if (scoreDiff != null)
                        {
                            var valueDiff = scoreDiff.PerPointScore[i].ScoreDiff - perPointLootScore.ScoreDiff;
                            if (valueDiff != 0)
                            {
                                ImGui.SameLine();
                                ImGui.TextColored(
                                    GetCompareColor(scoreDiff.PerPointScore[i].ScoreDiff, perPointLootScore.ScoreDiff),
                                    $"{valueDiff:(+0.00);(-0.00);''}");
                            }
                        }

                        ImGui.TableNextColumn();
                        ImGui.Text($"{diffOrOld.NewRelics}");
                        if (scoreDiff != null)
                        {
                            var valueDiff = scoreDiff.PerPointScore[i].NewRelics - perPointLootScore.NewRelics;
                            if (valueDiff != 0)
                            {
                                ImGui.SameLine();
                                ImGui.TextColored(
                                    GetCompareColor(scoreDiff.PerPointScore[i].NewRelics, perPointLootScore.NewRelics),
                                    $"{valueDiff:(+0);(-0);''}");
                            }
                        }

                        ImGui.TableNextColumn();
                        ImGui.Text($"{diffOrOld.Loot}");
                        if (scoreDiff != null)
                        {
                            var valueDiff = scoreDiff.PerPointScore[i].Loot - perPointLootScore.Loot;
                            if (valueDiff != 0)
                            {
                                ImGui.SameLine();
                                ImGui.TextColored(
                                    GetCompareColor(scoreDiff.PerPointScore[i].Loot, perPointLootScore.Loot),
                                    $"{valueDiff:(+0);(-0);''}");
                            }
                        }

                        ImGui.TableNextColumn();
                        if (i == _editedIndex)
                        {
                            ImGui.PushStyleColor(ImGuiCol.Button, Color.Green.ToImguiVec4());
                            if (ImGui.Button("Cancel"))
                            {
                                _editedIndex = null;
                            }

                            ImGui.PopStyleColor();
                        }
                        else if (ImGui.Button(" Edit "))
                        {
                            //Recipe choices are inherited from the searched path and frozen while
                            //editing: dragging a point can change which runestones are covered, and a
                            //newly covered one keeps whatever its genome slot held.
                            _editedPath ??= new PathCandidate(
                                score.PerPointScore.Select(x => x.Point).ToList(),
                                (int[])score.Candidate.Choices.Clone()) { CoveredMask = score.Candidate.CoveredMask };
                            var pp = new PathPlanner(Settings.PlannerSettings);
                            pp.Init(score.Environment);
                            _editedPathEval = pp.GetDetailedScore(_editedPath, score.Environment);
                            _editedIndex = i;
                        }

                        ImGui.PopID();
                    }

                    ImGui.EndTable();
                }

                DrawExpectedChoiceTable(score);

                if (_editedPath != null && ImGui.Button("Reset edited path"))
                {
                    _editedIndex = null;
                    _editedPath = null;
                    _editedPathEval = null;
                }
            }

            ImGui.PlotLines("Score over time", ref CollectionsMarshal.AsSpan(_scoreHistory)[0],
                _scoreHistory.Count, 0, "", 0, _scoreHistory.Max(),
                new Vector2(0, ImGui.GetContentRegionAvail().Y));
            ImGui.End();
        }
    }

    private static Vector4 GetCompareColor(double @new, double old)
    {
        return @new.CompareTo(old) switch
        {
            > 0 => Color.Green.ToImguiVec4(), 0 => Color.White.ToImguiVec4(), < 0 => Color.Red.ToImguiVec4()
        };
    }

    private bool ContainsWarnMods(List<string> mods)
    {
        return
            Settings.ModWarningSettings.WarnAvoidDamage && mods.Any(x => x.Contains("ExpeditionRelicDownsideAvoidDamage")) ||
            Settings.ModWarningSettings.WarnHexer && mods.Any(x => x.Contains("ExpeditionRelicDownsideElitesRandomCurseOnHit")) ||
            Settings.ModWarningSettings.WarnBreaksArmor && mods.Any(x => x.Contains("ExpeditionRelicDownsideArmourBreak")) ||
            Settings.ModWarningSettings.WarnRegen && mods.Any(x => x.Contains("ExpeditionRelicDownsideRegenerateLifeEveryFourSeconds")) ||
            Settings.ModWarningSettings.WarnEnrage && mods.Any(x => x.Contains("ExpeditionRelicDownsideDamageAttackCastMovementSpeedLowLife")) ||
            Settings.ModWarningSettings.WarnCICrit && mods.Any(x => x.Contains("ExpeditionRelicDownsideCriticalAgainstFullLife")) ||
            Settings.ModWarningSettings.WarnFirePen && mods.Any(x => x.Contains("ExpeditionRelicDownsideFirePenetration")) ||
            Settings.ModWarningSettings.WarnColdPen && mods.Any(x => x.Contains("ExpeditionRelicDownsideColdPenetration")) ||
            Settings.ModWarningSettings.WarnLightningPen && mods.Any(x => x.Contains("ExpeditionRelicDownsideLightningPenetration")) ||
            Settings.ModWarningSettings.WarnChaosPen && mods.Any(x => x.Contains("ExpeditionRelicDownsideChaosPenetration")) ||
            Settings.ModWarningSettings.WarnChaosExtra && mods.Any(x => x.Contains("ExpeditionRelicDownsideDamageAsChaos")) ||
            Settings.ModWarningSettings.WarnMoreAilments && mods.Any(x => x.Contains("ExpeditionRelicDownsideElementalAilmentChance")) ||
            Settings.ModWarningSettings.WarnSpeed && mods.Any(x => x.Contains("ExpeditionRelicDownsideIncreasedSpeed")) ||
            Settings.ModWarningSettings.WarnPhysImmune && mods.Any(x => x.Contains("ExpeditionRelicDownsideImmunePhysicalDamage")) ||
            Settings.ModWarningSettings.WarnFireImmune && mods.Any(x => x.Contains("ExpeditionRelicDownsideImmuneFireDamage")) ||
            Settings.ModWarningSettings.WarnColdImmune && mods.Any(x => x.Contains("ExpeditionRelicDownsideImmuneColdDamage")) ||
            Settings.ModWarningSettings.WarnLightningImmune && mods.Any(x => x.Contains("ExpeditionRelicDownsideImmuneLightningDamage")) ||
            Settings.ModWarningSettings.WarnChaosImmune && mods.Any(x => x.Contains("ExpeditionRelicDownsideImmuneChaosDamage")) ||
            Settings.ModWarningSettings.WarnCritImmune && mods.Any(x => x.Contains("ExpeditionRelicDownsideCannotBeCrit")) ||
            Settings.ModWarningSettings.WarnAilmentImmune && mods.Any(x => x.Contains("ExpeditionRelicDownsideImmuneElementalAilments")) ||
            Settings.ModWarningSettings.WarnArmorPen && mods.Any(x => x.Contains("ExpeditionRelicDownsideIgnoreArmour")) ||
            Settings.ModWarningSettings.WarnNoEvade && mods.Any(x => x.Contains("ExpeditionRelicDownsideHitsCannotBeEvaded")) ||
            Settings.ModWarningSettings.WarnNoLeech && mods.Any(x => x.Contains("ExpeditionRelicDownsideCannotBeLeechedFrom")) ||
            Settings.ModWarningSettings.WarnNoFlask && mods.Any(x => x.Contains("ExpeditionRelicDownsideGrantNoFlaskCharges")) ||
            Settings.ModWarningSettings.WarnPetrify && mods.Any(x => x.Contains("ExpeditionRelicDownsideElitesPetrifyOnHit")) ||
            Settings.ModWarningSettings.WarnCurseImmune && mods.Any(x => x.Contains("ExpeditionRelicDownsideImmuneToCurses")) ||
            Settings.ModWarningSettings.WarnCull && mods.Any(x => x.Contains("ExpeditionRelicDownsideCullingStrikeTwentyPercent")) ||
            Settings.ModWarningSettings.WarnMonsterBlock && mods.Any(x => x.Contains("ExpeditionRelicDownsideAttackBlockSpellBlockMaxBlockChance")) ||
            Settings.ModWarningSettings.WarnMonsterResist && mods.Any(x => x.Contains("ExpeditionRelicDownsideResistancesAndMaxResistances")) ||
            Settings.ModWarningSettings.WarnMonsterRegen && mods.Any(x => x.Contains("ExpeditionRelicDownsideElitesRegenerateLifeEveryFourSeconds")) ||
            Settings.ModWarningSettings.WarnAlwaysCrit && mods.Any(x => x.Contains("ExpeditionRelicDownsideAlwaysCrit")) ||
            Settings.ModWarningSettings.WarnReducedDamageTaken && mods.Any(x => x.Contains("ExpeditionRelicDownsideReducedDamageTaken")) ||
            Settings.ModWarningSettings.WarnBleed && mods.Any(x => x.Contains("ExpeditionRelicDownsideBleedOnHitBleedDuration")) ||
            Settings.ModWarningSettings.WarnCorrupted && mods.Any(x => x.Contains("ExpeditionRelicDownsideExpeditionCorruptedItemsElite")) ||
            Settings.ModWarningSettings.WarnPoison && mods.Any(x => x.Contains("ExpeditionRelicDownsideAllDamagePoisonsPoisonDuration")) ||
            Settings.ModWarningSettings.WarnPhysicalAsExtraChaos && mods.Any(x => x.Contains("ExpeditionRelicDownsideDamageAddedAsChaos")) ||
            false;
    }

    private void DrawIconOnMap(EntityCacheItem entity, MapIconsIndex icon, Color? color, Vector2 offset)
    {
        if (_largeMapOpen)
        {
            var halfsize = Settings.MapIconSize / 2.0f;
            var point = GetEntityPosOnMapScreen(entity) + offset * halfsize * 2;
            var entityPos = entity.Pos;
            var entityPos2 = new Vector2(entityPos.X, entityPos.Y);

            DrawIcon(icon, color, point, entityPos2,
                Settings.ExplosivesSettings.HideCapturedEntitiesOnMap,
                Settings.ExplosivesSettings.MarkCapturedEntitiesOnMap,
                Settings.ExplosivesSettings.CapturedEntityMapFrameColor,
                Settings.PlannerSettings.CapturedEntityMapFrameColor,
                Settings.ExplosivesSettings.CapturedEntityMapFrameThickness,
                Settings.MapIconSize);
        }
    }

    private void DrawIconInWorld(EntityCacheItem entity, MapIconsIndex icon, Color? color, Vector2 offset)
    {
        var halfsize = Settings.WorldIconSize / 2.0f;
        var entityPos = entity.Pos;
        var entityPos2 = new Vector2(entityPos.X, entityPos.Y);
        var point = Camera.WorldToScreen(entityPos) + offset * halfsize * 2;
        DrawIcon(icon, color, point, entityPos2,
            Settings.ExplosivesSettings.HideCapturedEntitiesInWorld,
            Settings.ExplosivesSettings.MarkCapturedEntitiesInWorld,
            Settings.ExplosivesSettings.CapturedEntityWorldFrameColor,
            Settings.PlannerSettings.CapturedEntityWorldFrameColor,
            Settings.ExplosivesSettings.CapturedEntityWorldFrameThickness,
            Settings.WorldIconSize);
    }

    private void DrawIcon(
        MapIconsIndex icon,
        Color? color,
        Vector2 displayPosition,
        Vector2 worldPosition,
        bool hideCaptured,
        bool markCaptured,
        Color capturedFrameColor,
        Color plannerCapturedFrameColor,
        int frameThickness,
        float iconSize)
    {
        var halfsize = iconSize / 2.0f;
        var rect = new RectangleF(displayPosition.X, displayPosition.Y, 0, 0);
        rect.Inflate(halfsize, halfsize);
        var calculateExplosiveFrameDisplay = hideCaptured || markCaptured;
        var isInExplosiveRadius = calculateExplosiveFrameDisplay &&
                                  _explosives2DPositions.Any(x => Vector2.Distance(x, worldPosition) < _explosiveRadius);
        var gridPosition = worldPosition.WorldToGrid();
        //Per blast, not a single path-wide radius: points grow after an oil well and chained
        //blasts carry their own.
        var isInPlannedExplosiveRadius = calculateExplosiveFrameDisplay &&
                                         EditedOrNativeScore is { PerPointScore.Count: > 0 } path &&
                                         path.PerPointScore.Any(x => x.Blasts.Any(b => Vector2.Distance(b.Pos, gridPosition) < b.Radius));

        if (markCaptured)
        {
            var plannedRect = rect;
            if (isInExplosiveRadius)
            {
                Graphics.DrawFrame(rect, capturedFrameColor, frameThickness);
                plannedRect.Inflate(frameThickness, frameThickness);
            }

            if (isInPlannedExplosiveRadius)
            {
                Graphics.DrawFrame(plannedRect, plannerCapturedFrameColor, frameThickness);
            }
        }

        if (!isInExplosiveRadius || !hideCaptured)
        {
            Graphics.DrawImage(TextureName, rect, SpriteHelper.GetUV(icon), color ?? Color.White);
        }
    }

    private Vector2 GetMapScreenPosition(Vector2 gridPos)
    {
        return _mapCenter + TranslateGridDeltaToMapDelta(gridPos - _playerGridPos, GameController.IngameState.Data.GetTerrainHeightAt(gridPos) - _playerZ);
    }

    private Vector2 GetWorldScreenPosition(Vector2 gridPos)
    {
        return Camera.WorldToScreen(ExpandWithTerrainHeight(gridPos));
    }

    private Vector2 GetEntityPosOnMapScreen(EntityCacheItem entity)
    {
        var point = _mapCenter + TranslateGridDeltaToMapDelta(entity.GridPos - _playerGridPos, (entity.RenderZ ?? 0) - _playerZ);
        return point;
    }

    private Vector2 TranslateGridDeltaToMapDelta(Vector2 delta, float deltaZ)
    {
        deltaZ /= GridToWorldMultiplier; //z is normally "world" units, translate to grid
        return (float)_mapScale * new Vector2((delta.X - delta.Y) * CameraAngleCos, (deltaZ - (delta.X + delta.Y)) * CameraAngleSin);
    }

    private enum ExpeditionEntityType
    {
        None,
        Relic,
        Marker,
        Cave,
        Boss,
        RuneEncounter,
        ChainExplosive,
        Strongbox,
        RuneSource,
    }

    private record EntityCacheItem(
        uint Id,
        string Path,
        Lazy<string> BaseAnimatedEntityMetadataCache,
        List<string> Mods,
        Vector3 Pos,
        Vector2 GridPos,
        float? RenderZ,
        float? RenderSize,
        bool? MinimapIconHide,
        string MinimapIconName,
        Lazy<List<string>> RewardItemModKeysCache)
    {
        public string BaseAnimatedEntityMetadata => BaseAnimatedEntityMetadataCache.Value;

        /// <summary>Mod keys on the reward item a Sentinel displays. Null for everything else.</summary>
        public List<string> RewardItemModKeys => RewardItemModKeysCache.Value;

        public EntityCacheItem Merge(EntityCacheItem other)
        {
            return new EntityCacheItem(
                Id,
                Path ?? other.Path,
                BaseAnimatedEntityMetadata == null ? other.BaseAnimatedEntityMetadataCache : BaseAnimatedEntityMetadataCache,
                Mods ?? other.Mods,
                Pos,
                GridPos,
                RenderZ ?? other.RenderZ,
                RenderSize ?? other.RenderSize,
                MinimapIconHide ?? other.MinimapIconHide,
                MinimapIconName ?? other.MinimapIconName,
                RewardItemModKeys == null ? other.RewardItemModKeysCache : RewardItemModKeysCache);
        }
    }


    public override void EntityAdded(Entity entity)
    {
        if (entity.Type is EntityType.IngameIcon or EntityType.Terrain or EntityType.Chest &&
            GetEntityType(entity.Path) != ExpeditionEntityType.None)
        {
            _cachedEntities[entity.Id] = BuildCacheItem(entity);
        }
    }

    private static EntityCacheItem BuildCacheItem(Entity entity)
    {
        return new EntityCacheItem(
            entity.Id,
            entity.Path,
            new Lazy<string>(() => entity.GetComponent<Animated>()?.BaseAnimatedObjectEntity?.Metadata, LazyThreadSafetyMode.None),
            entity.GetComponent<ObjectMagicProperties>()?.Mods,
            entity.Pos,
            entity.Pos.WorldToGrid(),
            entity.GetComponent<Render>()?.Z,
            entity.GetComponent<Render>()?.Bounds is { } b ? Math.Min(b.X, b.Y) : null,
            entity.GetComponent<MinimapIcon>()?.IsHide,
            entity.GetComponent<MinimapIcon>()?.Name,
            new Lazy<List<string>>(() => entity.GetComponent<HeistRewardDisplay>()?.RewardItem?
                .GetComponent<Mods>()?.ItemMods?
                .Select(x => x?.ModRecord?.Key)
                .Where(x => !string.IsNullOrEmpty(x))
                .ToList(), LazyThreadSafetyMode.None));
    }
}