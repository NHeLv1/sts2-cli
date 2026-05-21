using System.Reflection;
using System.Reflection.Emit;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace Sts2Headless;

/// <summary>
/// Synchronization context that executes continuations inline immediately.
/// Task.Yield() posts to SynchronizationContext.Current — by executing inline,
/// the yield becomes a no-op and the entire async chain runs synchronously.
/// Uses a recursion guard to queue nested posts and drain them after.
/// </summary>
internal class InlineSynchronizationContext : SynchronizationContext
{
    private readonly Queue<(SendOrPostCallback, object?)> _queue = new();
    private bool _executing;

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (_executing)
        {
            _queue.Enqueue((d, state));
            return;
        }
        // removed debug log

        // Execute inline immediately, then drain any nested posts
        _executing = true;
        try
        {
            d(state);
            // Drain any callbacks that were queued during execution
            while (_queue.Count > 0)
            {
                var (cb, st) = _queue.Dequeue();
                cb(st);
            }
        }
        finally
        {
            _executing = false;
        }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        d(state);
    }

    public void Pump()
    {
        // Drain any remaining queued callbacks
        while (_queue.Count > 0)
        {
            var (cb, st) = _queue.Dequeue();
            _executing = true;
            try { cb(st); }
            finally { _executing = false; }
        }
    }
}

/// <summary>
/// Bilingual localization lookup — loads eng/zhs JSON files for display names.
/// </summary>
internal class LocLookup
{
    private readonly Dictionary<string, Dictionary<string, string>> _eng = new();
    private readonly Dictionary<string, Dictionary<string, string>> _zhs = new();

    public LocLookup()
    {
        var baseDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        Load(Path.Combine(baseDir, "localization_eng"), _eng);
        Load(Path.Combine(baseDir, "localization_zhs"), _zhs);
    }

    private static void Load(string dir, Dictionary<string, Dictionary<string, string>> target)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
                if (data != null) target[name] = data;
            }
            catch { }
        }
    }

    /// <summary>Get bilingual name: "English / 中文" or just the key if not found.</summary>
    public string Name(string table, string key)
    {
        var en = _eng.GetValueOrDefault(table)?.GetValueOrDefault(key);
        var zh = _zhs.GetValueOrDefault(table)?.GetValueOrDefault(key);
        if (en != null && zh != null && en != zh) return $"{en} / {zh}";
        return en ?? zh ?? key;
    }

    public string? En(string table, string key) => _eng.GetValueOrDefault(table)?.GetValueOrDefault(key);
    public string? Zh(string table, string key) => _zhs.GetValueOrDefault(table)?.GetValueOrDefault(key);

    /// <summary>Strip BBCode tags like [gold], [/blue], [b], [sine], etc.</summary>
    private static string StripBBCode(string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[/?[a-zA-Z_][a-zA-Z0-9_=]*\]", "");
        return System.Text.RegularExpressions.Regex.Replace(text, @"#[A-Z](?=\{|[A-Za-z0-9])", "");
    }

    /// <summary>Language for JSON output: "en" or "zh". Default: "en".</summary>
    public string Lang { get; set; } = "en";

    /// <summary>Return localized string for JSON output based on Lang setting.</summary>
    public string Bilingual(string table, string key)
    {
        if (Lang == "zh")
        {
            var zh = _zhs.GetValueOrDefault(table)?.GetValueOrDefault(key);
            if (zh != null) return StripBBCode(zh);
        }
        var en = _eng.GetValueOrDefault(table)?.GetValueOrDefault(key) ?? key;
        return StripBBCode(en);
    }

    // Convenience helpers using ModelId
    public string Card(string entry) => Bilingual("cards", entry + ".title");
    public string Monster(string entry)
    {
        var key = entry + ".name";
        var result = Bilingual("monsters", key);
        // If no dedicated entry, fall back to the base segment key (e.g. DECIMILLIPEDE_SEGMENT_FRONT → DECIMILLIPEDE_SEGMENT)
        if (result == key)
        {
            var lastUnderscore = entry.LastIndexOf('_');
            if (lastUnderscore > 0)
            {
                var baseEntry = entry[..lastUnderscore];
                var baseKey = baseEntry + ".name";
                var baseResult = Bilingual("monsters", baseKey);
                if (baseResult != baseKey) return baseResult;
            }
        }
        return result;
    }
    public string Relic(string entry) => Bilingual("relics", entry + ".title");
    public string Potion(string entry) => Bilingual("potions", entry + ".title");
    public string Power(string entry) => PowerText(entry, ".title");
    public string PowerDescription(string entry) => PowerText(entry, ".description");
    public string Event(string entry) => Bilingual("events", entry + ".title");
    public string Act(string entry) => Bilingual("acts", entry + ".title");
    public string MonsterMove(string monsterEntry, string moveEntry)
    {
        foreach (var candidate in MonsterMoveKeyCandidates(moveEntry))
        {
            var titleKey = monsterEntry + ".moves." + candidate + ".title";
            var title = Bilingual("monsters", titleKey);
            if (title != titleKey)
                return title;

            var key = monsterEntry + ".moves." + candidate;
            var name = Bilingual("monsters", key);
            if (name != key)
                return name;
        }

        return HumanizeMoveEntry(moveEntry);
    }

    private static IEnumerable<string> MonsterMoveKeyCandidates(string moveEntry)
    {
        yield return moveEntry;
        if (moveEntry.EndsWith("_MOVE", StringComparison.Ordinal))
            yield return moveEntry[..^5];
    }

    private static string HumanizeMoveEntry(string moveEntry)
    {
        foreach (var candidate in MonsterMoveKeyCandidates(moveEntry).Reverse())
        {
            var words = candidate
                .Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(word => word.Length == 0
                    ? word
                    : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant());
            var title = string.Join(" ", words);
            if (!string.IsNullOrWhiteSpace(title))
                return title;
        }

        return moveEntry;
    }

    private string PowerText(string entry, string suffix)
    {
        var powerKey = entry + suffix;
        var result = Bilingual("powers", powerKey);
        if (result != powerKey)
            return result;

        if (entry.EndsWith("_POTION_POWER", StringComparison.Ordinal))
        {
            var potionEntry = entry[..^"_POWER".Length];
            var potionKey = potionEntry + suffix;
            var potionResult = Bilingual("potions", potionKey);
            if (potionResult != potionKey)
                return potionResult;
        }

        if (entry.EndsWith("_POWER", StringComparison.Ordinal))
        {
            var cardEntry = entry[..^"_POWER".Length];
            var cardKey = cardEntry + (suffix == ".title" ? ".title" : ".description");
            var cardResult = Bilingual("cards", cardKey);
            if (cardResult != cardKey)
                return cardResult;
        }

        return result;
    }

    /// <summary>Resolve a full loc key like "TABLE.KEY.SUB" by searching all tables.</summary>
    public string BilingualFromKey(string locKey)
    {
        if (Lang == "zh")
        {
            foreach (var tableName in _zhs.Keys)
            {
                var zh = _zhs.GetValueOrDefault(tableName)?.GetValueOrDefault(locKey);
                if (zh != null) return StripBBCode(zh);
            }
        }
        foreach (var tableName in _eng.Keys)
        {
            var en = _eng.GetValueOrDefault(tableName)?.GetValueOrDefault(locKey);
            if (en != null) return StripBBCode(en);
        }
        return locKey;
    }

    public IEnumerable<KeyValuePair<string, string>> Entries(string table)
    {
        var source = Lang == "zh" && _zhs.TryGetValue(table, out var zhsTable)
            ? zhsTable
            : _eng.GetValueOrDefault(table);
        if (source == null)
            yield break;

        foreach (var kv in source)
            yield return new KeyValuePair<string, string>(kv.Key, StripBBCode(kv.Value));
    }

    public bool IsLoaded => _eng.Count > 0;
}

/// <summary>
/// Full run simulator — manages the game lifecycle from character selection
/// through map navigation, combat, events, rest sites, shops, and act transitions.
/// Drives the engine forward until it hits a "decision point" requiring external input.
/// </summary>
public class RunSimulator
{
    private const string CliEnergyToken = "[E]";
    private const string CliStarToken = "[S]";
    private static readonly Dictionary<Type, int?> StaticAttackHitCountByCardType = new();
    private static readonly Dictionary<Type, bool> UsesAttackHitCountByCardType = new();
    private static readonly Dictionary<short, OpCode> OpCodeByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode))
        .Select(f => (OpCode)f.GetValue(null)!)
        .GroupBy(op => op.Value)
        .ToDictionary(g => g.Key, g => g.First());

    private static int? _expectedSaveSchemaVersion;
    private static bool _expectedSaveSchemaVersionReady;
    private static readonly object _expectedSaveSchemaVersionLock = new();

    private RunState? _runState;
    private static bool _modelDbInitialized;
    private static readonly InlineSynchronizationContext _syncCtx = new();
    private readonly ManualResetEventSlim _turnStarted = new(false);
    private readonly ManualResetEventSlim _combatEnded = new(false);
    private static readonly LocLookup _loc = new();
    private bool _eventOptionChosen;
    private int _lastEventOptionCount;
    private Task? _pendingEventOptionTask;
    private EventModel? _pendingEventChoiceAfterCombat;
    private EventModel? _pendingEventResult;
    private Task? _pendingShopPurchaseTask;
    private MerchantCardRemovalEntry? _pendingShopCardRemovalEntry;

    // Pending rewards for card selection (populated after combat, before proceeding)
    private List<Reward>? _pendingRewards;
    private CardReward? _pendingCardReward;
    private bool _rewardsProcessed;
    private int _goldBeforeCombat;
    private int _lastKnownHp;
    private readonly HeadlessCardSelector _cardSelector = new();
    private CardModel? _pendingCardSelectionSourceCard;
    private Dictionary<string, object?>? _pendingCardSelectionSourceEventOption;
    private Dictionary<string, object?>? _pendingCardSelectionSourceRoomOption;
    private Dictionary<string, object?>? _pendingCardSelectionSourcePotion;
    private readonly Dictionary<object, Dictionary<string, object?>> _shopItemSnapshots = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, int> _cardRuntimeIds = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, int> _creatureRuntimeIds = new(ReferenceEqualityComparer.Instance);
    private int _nextCardRuntimeId = 1;
    private int _nextCreatureRuntimeId = 1;
    private string? _preCurrentRoomSaveJson;
    private object? _starSpendTrackerCombatState;
    private int? _starSpendObservedRound;
    // Pending bundle selection (Scroll Boxes: pick 1 of N packs)
    private IReadOnlyList<IReadOnlyList<CardModel>>? _pendingBundles;
    private TaskCompletionSource<IEnumerable<CardModel>>? _pendingBundleTcs;

    public Dictionary<string, object?> StartRun(string character, int ascension = 0, string? seed = null, string lang = "en")
    {
        try
        {
            PrepareForRunReplacement();
            _loc.Lang = lang;
            EnsureModelDbInitialized();

            var player = CreatePlayer(character);
            if (player == null)
                return Error($"Unknown character: {character}");

            var seedStr = seed ?? "headless_" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Log($"Creating RunState with seed={seedStr}");

            // Use CreateForTest which properly handles mutable copies internally
            _runState = RunState.CreateForTest(
                players: new[] { player },
                ascensionLevel: ascension,
                seed: seedStr
            );

            // Set up RunManager with test mode
            var netService = new NetSingleplayerGameService();
            RunManager.Instance.SetUpTest(_runState, netService);
            LocalContext.NetId = netService.NetId;

            // Force Neow event (blessing selection at start)
            _runState.ExtraFields.StartedWithNeow = true;

            // Generate rooms for all acts
            RunManager.Instance.GenerateRooms();
            Log("Rooms generated");

            // Launch the run
            RunManager.Instance.Launch();
            Log("Run launched");

            // Register event handlers for combat turn transitions
            CombatManager.Instance.TurnStarted += _ => _turnStarted.Set();
            CombatManager.Instance.CombatEnded += _ => _combatEnded.Set();

            // Finalize starting relics
            RunManager.Instance.FinalizeStartingRelics().GetAwaiter().GetResult();
            Log("Starting relics finalized");

            // Enter first act (generates map)
            RunManager.Instance.EnterAct(0, doTransition: false).GetAwaiter().GetResult();
            Log("Entered Act 0");

            // Register card selector for cards that need player choice
            CardSelectCmd.UseSelector(_cardSelector);
            LocPatches._bundleSimRef = this;

            // Now we should be at the map — detect decision point
            return DetectDecisionPoint();
        }
        catch (Exception ex)
        {
            return ErrorWithTrace("StartRun failed", ex);
        }
    }

    // ─── Test/Debug commands ───

    private static readonly System.Reflection.BindingFlags NonPublic =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

    /// <summary>Get the backing List&lt;T&gt; behind an IReadOnlyList property via reflection.</summary>
    private static List<T>? GetBackingList<T>(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName, NonPublic);
        return field?.GetValue(obj) as List<T>;
    }

    private static System.Collections.IList? GetPotionSlots(Player player)
    {
        var field = player.GetType().GetField("_potionSlots", NonPublic);
        return field?.GetValue(player) as System.Collections.IList;
    }

    private static void SetField(object obj, string fieldName, object? value)
    {
        var field = obj.GetType().GetField(fieldName, NonPublic);
        field?.SetValue(obj, value);
    }

    public Dictionary<string, object?> SetPlayer(Dictionary<string, System.Text.Json.JsonElement> args)
    {
        try
        {
            if (_runState == null) return Error("No run in progress");
            var player = _runState.Players[0];

            if (args.TryGetValue("hp", out var hpEl) && player.Creature != null)
                SetField(player.Creature, "_currentHp", hpEl.GetInt32());
            if (args.TryGetValue("max_hp", out var mhpEl) && player.Creature != null)
                SetField(player.Creature, "_maxHp", mhpEl.GetInt32());
            if (args.TryGetValue("gold", out var goldEl))
                player.Gold = goldEl.GetInt32();

            if (args.TryGetValue("relics", out var relicsEl))
            {
                var list = GetBackingList<RelicModel>(player, "_relics");
                if (list != null)
                {
                    var directRelicSetup =
                        args.TryGetValue("relic_setup_mode", out var modeEl)
                        && string.Equals(modeEl.GetString(), "direct", StringComparison.OrdinalIgnoreCase);
                    list.Clear();
                    foreach (var rEl in relicsEl.EnumerateArray())
                    {
                        var id = rEl.GetString();
                        if (id == null) continue;
                        var model = ModelDb.GetById<RelicModel>(new ModelId("RELIC", id));
                        if (model != null)
                        {
                            if (directRelicSetup)
                            {
                                player.AddRelicInternal(model.ToMutable(), player.Relics.Count, silent: true);
                            }
                            else
                            {
                                RelicCmd.Obtain(model.ToMutable(), player, player.Relics.Count)
                                    .GetAwaiter()
                                    .GetResult();
                                _syncCtx.Pump();
                            }
                        }
                    }
                }
            }
            if (args.TryGetValue("deck", out var deckEl))
            {
                // Remove existing cards from RunState tracking
                foreach (var c in player.Deck.Cards.ToList())
                    _runState.RemoveCard(c);
                player.Deck.Clear(silent: true);
                // Add new cards via RunState.CreateCard (sets Owner + registers)
                foreach (var cEl in deckEl.EnumerateArray())
                {
                    var id = cEl.GetString();
                    if (id == null) continue;
                    var canonical = ModelDb.GetById<CardModel>(new ModelId("CARD", id));
                    if (canonical != null)
                    {
                        var card = _runState.CreateCard(canonical, player);
                        player.Deck.AddInternal(card, silent: true);
                    }
                }
            }
            if (args.TryGetValue("potions", out var potionsEl))
            {
                var slots = GetPotionSlots(player);
                if (slots != null)
                {
                    for (int i = 0; i < slots.Count; i++) slots[i] = null;
                    int idx = 0;
                    foreach (var pEl in potionsEl.EnumerateArray())
                    {
                        if (idx >= slots.Count) break;
                        var id = pEl.GetString();
                        if (id != null)
                        {
                            var model = ModelDb.GetById<PotionModel>(new ModelId("POTION", id));
                            if (model != null)
                            {
                                MegaCrit.Sts2.Core.Commands.PotionCmd
                                    .TryToProcure(model.ToMutable(), player, idx)
                                    .GetAwaiter()
                                    .GetResult();
                                _syncCtx.Pump();
                            }
                        }
                        idx++;
                    }
                }
            }

            Log($"SetPlayer: hp={player.Creature?.CurrentHp} gold={player.Gold} relics={player.Relics.Count} deck={player.Deck?.Cards?.Count}");
            return new Dictionary<string, object?>
            {
                ["type"] = "ok",
                ["player"] = PlayerSummary(player),
            };
        }
        catch (Exception ex) { return ErrorWithTrace("SetPlayer failed", ex); }
    }

    public Dictionary<string, object?> EnterRoom(string roomType, string? encounter, string? eventId)
    {
        try
        {
            if (_runState == null) return Error("No run in progress");
            var runState = _runState;
            Log($"EnterRoom: type={roomType} encounter={encounter} event={eventId}");

            AbstractRoom room;
            switch (roomType.ToLowerInvariant())
            {
                case "combat":
                case "monster":
                case "elite":
                {
                    if (string.IsNullOrEmpty(encounter))
                        encounter = "SHRINKER_BEETLE_WEAK"; // default encounter
                    var encModel = ModelDb.GetById<EncounterModel>(new ModelId("ENCOUNTER", encounter));
                    if (encModel == null) return Error($"Unknown encounter: {encounter}");
                    room = new CombatRoom(encModel.ToMutable(), runState);
                    break;
                }
                case "shop":
                    room = new MerchantRoom();
                    break;
                case "rest":
                case "rest_site":
                    room = new RestSiteRoom();
                    break;
                case "event":
                {
                    if (string.IsNullOrEmpty(eventId))
                        return Error("event requires 'event' parameter (e.g. CHANGELING_GROVE)");
                    var evModel = ModelDb.GetById<EventModel>(new ModelId("EVENT", eventId));
                    if (evModel == null) return Error($"Unknown event: {eventId}");
                    room = new EventRoom(evModel);
                    break;
                }
                case "treasure":
                    room = new TreasureRoom(_runState.CurrentActIndex);
                    break;
                default:
                    return Error($"Unknown room type: {roomType}");
            }

            RunManager.Instance.EnterRoom(room).GetAwaiter().GetResult();
            _syncCtx.Pump();
            WaitForActionExecutor();
            return DetectDecisionPoint();
        }
        catch (Exception ex) { return ErrorWithTrace("EnterRoom failed", ex); }
    }

    public Dictionary<string, object?> SetDrawOrder(List<string> cardIds)
    {
        try
        {
            if (_runState == null) return Error("No run in progress");
            var player = _runState.Players[0];
            var pcs = player.PlayerCombatState;
            if (pcs?.DrawPile == null) return Error("Not in combat");

            var drawList = GetBackingList<CardModel>(pcs.DrawPile, "_cards");
            if (drawList == null) return Error("Cannot access draw pile");

            var newOrder = new List<CardModel>();
            var available = new List<CardModel>(drawList);
            foreach (var cardId in cardIds)
            {
                var match = available.FirstOrDefault(c =>
                    c.Id.Entry.Equals(cardId, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    newOrder.Add(match);
                    available.Remove(match);
                }
            }
            newOrder.AddRange(available);

            drawList.Clear();
            drawList.AddRange(newOrder);

            Log($"SetDrawOrder: {newOrder.Count} cards, top={newOrder.FirstOrDefault()?.Id.Entry}");
            return new Dictionary<string, object?>
            {
                ["type"] = "ok",
                ["draw_pile_count"] = drawList.Count,
                ["top_cards"] = newOrder.Take(5).Select(c => _loc.Card(c.Id.Entry)).ToList(),
            };
        }
        catch (Exception ex) { return ErrorWithTrace("SetDrawOrder failed", ex); }
    }

    // ─── Game actions ───
    public Dictionary<string, object?> LoadSave(string saveJson, string lang = "en")
    {
        try
        {
            PrepareForRunReplacement();
            _loc.Lang = lang;
            EnsureModelDbInitialized();

            Log("Loading save file...");

            if (!ValidateSaveSchemaVersion(saveJson, out var schemaError))
                return Error($"Save schema mismatch: {schemaError}");

            var readResult = SaveManager.FromJson<SerializableRun>(saveJson);
            if (!readResult.Success || readResult.SaveData == null)
                return Error($"Failed to parse save file: {readResult.Status} {readResult.ErrorMessage}");

            var save = readResult.SaveData;
            Log($"Save loaded: seed={save.SerializableRng?.Seed}, act={save.CurrentActIndex}, ascension={save.Ascension}");

            _runState = RunState.FromSerializable(save);
            if (_runState == null)
                return Error("Failed to create RunState from save");

            Log($"RunState created, players={_runState.Players?.Count}");

            var netService = new NetSingleplayerGameService();
            RunManager.Instance.SetUpSavedSinglePlayer(_runState, save);
            LocalContext.NetId = netService.NetId;

            CombatManager.Instance.TurnStarted += _ => _turnStarted.Set();
            CombatManager.Instance.CombatEnded += _ => _combatEnded.Set();
            CardSelectCmd.UseSelector(_cardSelector);
            LocPatches._bundleSimRef = this;

            var savedRoom = _runState.CurrentRoom;

            // Save visited coords before Launch (EnterAct will clear them)
            var savedVisitedCoords = _runState.VisitedMapCoords?.ToList() ?? new List<MapCoord>();
            var shouldResumeInitialNeow = IsInitialNeowSave(saveJson);
            Log($"Save has {savedVisitedCoords.Count} visited coords");

            RunManager.Instance.Launch();
            Log("Run launched");

            if (savedRoom is MapRoom || savedRoom == null)
            {
                // Preserve Neow for saves created before the first blessing choice.
                // Once the run has visited at least one map node, re-entering Act 1
                // should not send the player back through the Ancient start node.
                if (_runState.CurrentActIndex == 0 && savedVisitedCoords.Count > 0)
                    _runState.ExtraFields.StartedWithNeow = false;
                RunManager.Instance.EnterAct(_runState.CurrentActIndex, doTransition: false).GetAwaiter().GetResult();
                _syncCtx.Pump();
                Log($"Entered Act {_runState.CurrentActIndex}");

                if (shouldResumeInitialNeow && _runState.Map?.StartingMapPoint != null)
                {
                    Log("Restoring initial Neow event");
                    RunManager.Instance.EnterMapCoord(_runState.Map.StartingMapPoint.coord).GetAwaiter().GetResult();
                    _syncCtx.Pump();
                }

                // EnterAct clears visited coords and ActFloor — restore them from save
                if (savedVisitedCoords.Count > 0)
                {
                    if (_runState.VisitedMapCoords == null || _runState.VisitedMapCoords.Count == 0)
                    {
                        foreach (var coord in savedVisitedCoords)
                            _runState.AddVisitedMapCoord(coord);
                    }
                    _runState.ActFloor = savedVisitedCoords.Count;
                    var last = savedVisitedCoords[^1];
                    Log($"Restored map position: floor={_runState.ActFloor}, coord=({last.col},{last.row})");
                }
            }
            else
            {
                Log($"Preserving saved room: {savedRoom.GetType().Name}");
            }

            return DetectDecisionPoint();
        }
        catch (Exception ex)
        {
            return ErrorWithTrace("LoadSave failed", ex);
        }
    }

    /// <summary>
    /// Expected run save <c>schema_version</c> (lazy: first load_save only, so StartRun never fails on reflection).
    /// Order: <c>STS2_SAVE_SCHEMA_VERSION</c> env → reflect sts2.dll → unknown, defer to SaveManager.
    /// </summary>
    private static int? GetExpectedSaveSchemaVersion()
    {
        if (_expectedSaveSchemaVersionReady)
            return _expectedSaveSchemaVersion;
        lock (_expectedSaveSchemaVersionLock)
        {
            if (_expectedSaveSchemaVersionReady)
                return _expectedSaveSchemaVersion;
            _expectedSaveSchemaVersion = ResolveExpectedSaveSchemaVersion();
            _expectedSaveSchemaVersionReady = true;
            return _expectedSaveSchemaVersion;
        }
    }

    private static int? ResolveExpectedSaveSchemaVersion()
    {
        var env = Environment.GetEnvironmentVariable("STS2_SAVE_SCHEMA_VERSION");
        if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env.Trim(), out var envVer))
            return envVer;

        var reflected = TryReflectLatestSaveSchemaVersion();
        if (reflected.HasValue)
            return reflected.Value;

        Console.Error.WriteLine(
            "[Sts2Headless] Could not read save schema from sts2.dll; deferring schema compatibility " +
            "to SaveManager.FromJson. Set STS2_SAVE_SCHEMA_VERSION to enforce a specific version.");
        return null;
    }

    /// <summary>Find static parameterless GetLatestSchemaVersion (or close) on sts2; supports int/uint/long.</summary>
    private static int? TryReflectLatestSaveSchemaVersion()
    {
        var asm = typeof(SerializableRun).Assembly;
        Type[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
        }

        var candidates = new List<(int score, string typeName, int value)>();
        foreach (var t in types)
        {
            MethodInfo? m;
            try
            {
                foreach (var name in new[] { "GetLatestSchemaVersion", "GetLatestVersion" })
                {
                    m = t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                        null, Type.EmptyTypes, null);
                    if (m == null) continue;
                    var tn = t.FullName ?? "";
                    // Avoid unrelated static GetLatestVersion() elsewhere in the assembly.
                    if (name == "GetLatestVersion" && !tn.Contains("Saves", StringComparison.Ordinal))
                        continue;

                    var conv = TryConvertSchemaNumber(m.Invoke(null, null));
                    if (!conv.HasValue) continue;

                    var score = name == "GetLatestSchemaVersion" ? 100 : 0;
                    if (tn.Contains("Saves", StringComparison.Ordinal)) score += 50;
                    if (tn.Contains("Schema", StringComparison.Ordinal) || tn.Contains("Migration", StringComparison.Ordinal))
                        score += 25;
                    candidates.Add((score, tn, conv.Value));
                }
            }
            catch
            {
                // type may not support full reflection on this runtime
            }
        }

        if (candidates.Count == 0)
            return null;

        var best = candidates.OrderByDescending(c => c.score).ThenBy(c => c.typeName).First();
        return best.value;
    }

    private static int? TryConvertSchemaNumber(object? value) => value switch
    {
        int i => i,
        uint u => u <= int.MaxValue ? (int)u : null,
        long l => l >= int.MinValue && l <= int.MaxValue ? (int)l : null,
        short s => s,
        ushort us => us,
        byte b => b,
        _ => null,
    };

    private static bool ValidateSaveSchemaVersion(string saveJson, out string error)
    {
        error = "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(saveJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("schema_version", out var versionElem))
            {
                error = "missing schema_version";
                return false;
            }

            if (versionElem.ValueKind != System.Text.Json.JsonValueKind.Number ||
                !versionElem.TryGetInt32(out var schemaVersion))
            {
                error = "schema_version is not a valid integer";
                return false;
            }

            var expected = GetExpectedSaveSchemaVersion();
            if (expected.HasValue && schemaVersion != expected.Value)
            {
                error = $"expected v{expected.Value}, got v{schemaVersion}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"could not inspect save: {ex.Message}";
            return false;
        }
    }

    private static bool TrySetPropertyValue(object target, string propertyName, object? value)
    {
        var prop = target.GetType().GetProperty(propertyName);
        if (prop?.CanWrite != true)
            return false;
        prop.SetValue(target, value);
        return true;
    }

    private static bool IsInitialNeowSave(string saveJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(saveJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("current_act_index", out var actIndexElem) || actIndexElem.GetInt32() != 0)
                return false;

            var hasVisitedCoords = root.TryGetProperty("visited_map_coords", out var visitedElem)
                                && visitedElem.ValueKind == System.Text.Json.JsonValueKind.Array
                                && visitedElem.GetArrayLength() > 0;
            if (hasVisitedCoords)
                return false;

            return root.TryGetProperty("extra_fields", out var extraFieldsElem)
                && extraFieldsElem.ValueKind == System.Text.Json.JsonValueKind.Object
                && extraFieldsElem.TryGetProperty("started_with_neow", out var startedElem)
                && startedElem.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRollbackSerializedSaveToPreRoom(SerializableRun serializableRun, out string error)
    {
        error = "";

        var saveType = serializableRun.GetType();
        var visitedProp = saveType.GetProperty("VisitedMapCoords");
        if (visitedProp == null)
        {
            error = "Save data is missing VisitedMapCoords";
            return false;
        }

        var visitedValue = visitedProp.GetValue(serializableRun);
        var visitedItems = new List<object?>();
        if (visitedValue is System.Collections.IEnumerable visitedEnumerable)
        {
            foreach (var item in visitedEnumerable)
                visitedItems.Add(item);
        }

        if (visitedItems.Count == 0)
        {
            error = "Cannot roll back save before the first room";
            return false;
        }

        visitedItems.RemoveAt(visitedItems.Count - 1);

        var visitedType = visitedProp.PropertyType;
        if (visitedType.IsArray)
        {
            var elementType = visitedType.GetElementType()!;
            var array = Array.CreateInstance(elementType, visitedItems.Count);
            for (int i = 0; i < visitedItems.Count; i++)
                array.SetValue(visitedItems[i], i);
            visitedProp.SetValue(serializableRun, array);
        }
        else if (visitedType.IsGenericType)
        {
            var elementType = visitedType.GetGenericArguments()[0];
            var listType = typeof(List<>).MakeGenericType(elementType);
            var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
            foreach (var item in visitedItems)
                list.Add(item);
            visitedProp.SetValue(serializableRun, list);
        }
        else
        {
            error = $"Unsupported VisitedMapCoords type: {visitedType.Name}";
            return false;
        }

        TrySetPropertyValue(serializableRun, "ActFloor", visitedItems.Count);
        TrySetPropertyValue(serializableRun, "CurrentMapCoord", visitedItems.Count > 0 ? visitedItems[^1] : null);
        TrySetPropertyValue(serializableRun, "PreFinishedRoom", null);
        TrySetPropertyValue(serializableRun, "CurrentRoom", null);
        return true;
    }

    public Dictionary<string, object?> SaveCheckpoint(string? outputPath)
    {
        try
        {
            if (_runState == null)
                return Error("No active run to save");

            if (string.IsNullOrEmpty(outputPath))
                return Error("No output path specified for quit save");

            var currentRoom = _runState.CurrentRoom;
            SerializableRun serializableRun;
            var checkpointScope = "current_room";
            string? rolledBackRoomType = null;
            string saveJson;

            if (currentRoom is MapRoom || currentRoom == null)
            {
                Log($"Saving map checkpoint (room={currentRoom?.GetType().Name ?? "null"}, outputPath={outputPath})...");
                serializableRun = RunManager.Instance.ToSave(currentRoom);
                saveJson = SaveManager.ToJson(serializableRun);
            }
            else if (HasPendingPostCombatRewards())
            {
                return Error("Cannot save checkpoint while combat rewards are pending; claim or skip rewards before saving.");
            }
            else if (_preCurrentRoomSaveJson != null)
            {
                Log($"Saving pre-room checkpoint snapshot from {currentRoom.GetType().Name} (outputPath={outputPath})...");
                checkpointScope = "pre_room";
                rolledBackRoomType = currentRoom.GetType().Name;
                saveJson = _preCurrentRoomSaveJson;
            }
            else
            {
                Log($"Saving pre-room checkpoint from {currentRoom.GetType().Name} (outputPath={outputPath})...");
                checkpointScope = "pre_room";
                rolledBackRoomType = currentRoom.GetType().Name;
                serializableRun = RunManager.Instance.ToSave(new MapRoom());
                if (!TryRollbackSerializedSaveToPreRoom(serializableRun, out var rollbackError))
                    return Error($"Cannot save checkpoint: {rollbackError}");
                saveJson = SaveManager.ToJson(serializableRun);
            }

            Log($"Serialized save: {saveJson.Length} chars");

            var dir = System.IO.Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(outputPath, saveJson);
            Log($"Save written to: {outputPath}");

            return new Dictionary<string, object?>
            {
                ["type"] = "save_result",
                ["success"] = true,
                ["path"] = outputPath,
                ["size"] = saveJson.Length,
                ["room_type"] = currentRoom?.GetType().Name,
                ["checkpoint_scope"] = checkpointScope,
                ["rolled_back_room_type"] = rolledBackRoomType,
            };
        }
        catch (Exception ex)
        {
            return ErrorWithTrace("SaveCheckpoint failed", ex);
        }
    }

    private bool HasPendingPostCombatRewards()
    {
        return !CombatManager.Instance.IsInProgress
            && (_pendingRewards != null || _pendingCardReward != null);
    }

    public Dictionary<string, object?> ExecuteAction(string action, Dictionary<string, object?>? args)
    {
        try
        {
            if (_runState == null)
                return Error("No run in progress");

            var player = _runState.Players[0];
            var pendingActionError = PendingDecisionActionError(action);
            if (pendingActionError != null)
                return Error(pendingActionError);

            switch (action)
            {
                case "select_map_node":
                    return DoMapSelect(player, args);
                case "play_card":
                    return DoPlayCard(player, args);
                case "end_turn":
                    return DoEndTurn(player);
                case "choose_option":
                    return DoChooseOption(player, args);
                case "select_card_reward":
                    return DoSelectCardReward(player, args);
                case "skip_card_reward":
                    return DoSkipCardReward(player);
                case "claim_reward":
                    return DoClaimCombatReward(player, args);
                case "skip_reward":
                    return DoSkipCombatReward(player, args);
                case "buy_card":
                    return DoBuyCard(player, args);
                case "buy_relic":
                    return DoBuyRelic(player, args);
                case "buy_potion":
                    return DoBuyPotion(player, args);
                case "remove_card":
                    return DoRemoveCard(player);
                case "select_bundle":
                    return DoSelectBundle(player, args);
                case "select_cards":
                    return DoSelectCards(player, args);
                case "skip_select":
                    return DoSkipSelect(player);
                case "use_potion":
                    return DoUsePotion(player, args);
                case "discard_potion":
                    return DoDiscardPotion(player, args);
                case "claim_relic":
                    return DoClaimTreasureRelic(player, args);
                case "leave_room":
                    return DoLeaveRoom(player);
                case "proceed":
                    return DoProceed(player);
                case "crystal_sphere_set_tool":
                    return DoCrystalSphereSetTool(args);
                case "crystal_sphere_click_cell":
                    return DoCrystalSphereClickCell(args);
                case "crystal_sphere_proceed":
                    return DoCrystalSphereProceed();
                default:
                    return Error($"Unknown action: {action}");
            }
        }
        catch (Exception ex)
        {
            return ErrorWithTrace($"Action '{action}' failed", ex);
        }
    }

    private string? PendingDecisionActionError(string action)
    {
        if (_cardSelector.HasPending
            && action != "select_cards"
            && action != "skip_select"
            && action != "end_turn")
        {
            var actions = _cardSelector.PendingCanSkip
                ? "select_cards, skip_select, or end_turn"
                : "select_cards or end_turn";
            return $"Cannot execute action while card selection is pending; use {actions}";
        }

        if (_cardSelector.HasPendingReward
            && action != "select_card_reward"
            && action != "skip_card_reward"
            && action != "end_turn"
            && !(action == "crystal_sphere_proceed"
                 && YieldPatches.ActiveCrystalSphereMinigame?.IsFinished == true))
        {
            return "Cannot execute action while card reward selection is pending; use select_card_reward, skip_card_reward, or end_turn";
        }

        if (_pendingBundleTcs != null
            && !_pendingBundleTcs.Task.IsCompleted
            && action != "select_bundle"
            && action != "end_turn")
        {
            return "Cannot execute action while bundle selection is pending; use select_bundle or end_turn";
        }

        if (YieldPatches.ActiveCrystalSphereMinigame != null
            && !_cardSelector.HasPending
            && !_cardSelector.HasPendingReward
            && !(_pendingBundleTcs != null && !_pendingBundleTcs.Task.IsCompleted)
            && !action.StartsWith("crystal_sphere_", StringComparison.Ordinal))
        {
            return "Cannot execute action while crystal sphere selection is pending; use crystal_sphere_* actions";
        }

        return null;
    }

    #region Actions

    private Dictionary<string, object?> DoMapSelect(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("col") || !args.ContainsKey("row"))
            return Error("select_map_node requires 'col' and 'row'");

        var col = Convert.ToInt32(args["col"]);
        var row = Convert.ToInt32(args["row"]);
        var coord = new MapCoord((byte)col, (byte)row);
        var legalCoords = CurrentMapChoiceCoords(player);
        if (!legalCoords.Any(c => c.col == coord.col && c.row == coord.row))
        {
            var legalText = string.Join(", ", legalCoords.Select(c => $"({(int)c.col},{(int)c.row})"));
            return Error($"Invalid map node ({col},{row}); legal choices: {legalText}");
        }

        // Reset tracking for new room
        _rewardsProcessed = false;
        _pendingCardReward = null;
        _eventOptionChosen = false;
        _lastEventOptionCount = 0;
        _pendingEventOptionTask = null;
        _pendingEventChoiceAfterCombat = null;
        _pendingEventResult = null;
        _pendingShopPurchaseTask = null;
        _pendingShopCardRemovalEntry = null;
        _pendingRewards = null;
        _pendingCardSelectionSourceEventOption = null;
        _pendingCardSelectionSourceRoomOption = null;
        _pendingCardSelectionSourcePotion = null;
        _lastKnownHp = player.Creature?.CurrentHp ?? 0;
        YieldPatches.ActiveCrystalSphereMinigame = null;

        Log($"Moving to map coord ({col},{row})");

        // BUG-013: Wait for any pending actions (relic sessions, etc.) to complete before entering new room
        WaitForActionExecutor();
        _syncCtx.Pump();
        _preCurrentRoomSaveJson = CapturePreRoomCheckpoint();

        // Call EnterMapCoord directly (same as what MoveToMapCoordAction does in TestMode)
        // This avoids the action executor which can swallow errors silently.
        RunManager.Instance.EnterMapCoord(coord).GetAwaiter().GetResult();
        _syncCtx.Pump();
        WaitForActionExecutor();

        return DetectDecisionPoint();
    }

    private List<MapCoord> CurrentMapChoiceCoords(Player player)
    {
        return CurrentMapChoicePoints(player)
            .Select(choice => choice.Point.coord)
            .ToList();
    }

    private List<(MapPoint Point, bool RequiresWingedBoots)> CurrentMapChoicePoints(Player player)
    {
        var map = _runState?.Map;
        if (map == null)
            return new List<(MapPoint Point, bool RequiresWingedBoots)>();

        var currentCoord = _runState!.CurrentMapCoord;
        if (currentCoord.HasValue)
        {
            var currentPoint = map.GetPoint(currentCoord.Value);
            var choices = (currentPoint?.Children ?? Enumerable.Empty<MapPoint>())
                .Select(child => (Point: child, RequiresWingedBoots: false))
                .ToList();

            if (HasWingedBootsCharge(player))
            {
                var connected = choices
                    .Select(choice => choice.Point.coord)
                    .ToHashSet();
                var nextRow = currentCoord.Value.row + 1;
                for (int rowIndex = 0; rowIndex < map.GetRowCount(); rowIndex++)
                {
                    foreach (var point in map.GetPointsInRow(rowIndex))
                    {
                        if (point == null || point.coord.row != nextRow)
                            continue;
                        if (connected.Contains(point.coord))
                            continue;
                        choices.Add((point, true));
                    }
                }
            }

            return choices;
        }

        var startPoint = map.StartingMapPoint;
        var startChoices = new List<(MapPoint Point, bool RequiresWingedBoots)> { (startPoint, false) };
        if (startPoint.Children != null)
            startChoices.AddRange(startPoint.Children.Select(child => (child, false)));
        return startChoices;
    }

    private static bool HasWingedBootsCharge(Player player)
    {
        return player.Relics?.Any(relic =>
            relic != null
            && relic.Id.Entry == "WINGED_BOOTS"
            && (!relic.ShowCounter || relic.DisplayAmount > 0)) == true;
    }

    private string? CapturePreRoomCheckpoint()
    {
        try
        {
            if (_runState?.CurrentRoom is not MapRoom mapRoom)
                return null;
            return SaveManager.ToJson(RunManager.Instance.ToSave(mapRoom));
        }
        catch (Exception ex)
        {
            Log($"CapturePreRoomCheckpoint failed: {ex.Message}");
            return null;
        }
    }

    private Dictionary<string, object?> DoPlayCard(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("card_index"))
            return Error("play_card requires 'card_index'");

        var cardIndex = Convert.ToInt32(args["card_index"]);
        var pcs = player.PlayerCombatState;
        if (pcs == null)
            return Error("Not in combat");

        var hand = pcs.Hand.Cards;
        if (cardIndex < 0 || cardIndex >= hand.Count)
            return Error($"Invalid card index {cardIndex}, hand has {hand.Count} cards");

        var card = hand[cardIndex];
        if (card.Type == CardType.None)
            return Error($"Cannot play card {card.GetType().Name}: uninitialized card type");
        var starCostBeforePlay = TryGetCurrentStarCost(card);
        var starsBeforePlay = pcs.Stars;

        // Determine target based on card's TargetType first.
        // Self/None/All cards: target = null (game handles internally).
        // AnyEnemy cards require an explicit target_index; the CLI must not choose silently.
        Creature? target = null;
        var cardTargetType = card.TargetType;
        if (cardTargetType == TargetType.AnyEnemy)
        {
            if (!args.TryGetValue("target_index", out var targetObj) || targetObj == null)
                return Error("play_card requires 'target_index' for AnyEnemy card");

            var targetIndex = Convert.ToInt32(targetObj);
            var state = CombatManager.Instance.DebugOnlyGetState();
            var enemies = state?.Enemies?.Where(e => e != null && e.IsAlive).ToList() ?? new();
            if (targetIndex < 0 || targetIndex >= enemies.Count)
                return Error($"Invalid target_index {targetIndex}, combat has {enemies.Count} alive enemies");
            target = enemies[targetIndex];
        }
        // All other target types (None, All, etc.) leave target as null.

        // Check if card can be played
        if (!card.CanPlay(out var reason, out var _))
        {
            return Error($"Cannot play card {card.GetType().Name}: {reason}");
        }

        Log($"Playing card {card.GetType().Name} (index {cardIndex}) targeting {(target != null ? target.Monster?.GetType().Name ?? "creature" : "none")}");

        var mutationBefore = CombatMutationSignature(player);

        var playAction = new PlayCardAction(card, target);
        RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(playAction);
        WaitForActionExecutor();
        if (starCostBeforePlay > 0 && starsBeforePlay >= starCostBeforePlay)
            MarkStarsSpentThisTurn();
        _pendingCardSelectionSourceCard = _cardSelector.HasPending ? card : null;
        if (_pendingCardSelectionSourceCard != null)
        {
            _pendingCardSelectionSourceEventOption = null;
            _pendingCardSelectionSourceRoomOption = null;
            _pendingCardSelectionSourcePotion = null;
        }

        // Some engine effects return the played card to hand. The CLI should
        // not treat hand removal as the success signal, but a completed play
        // must still mutate visible combat state.
        var mutationAfter = CombatMutationSignature(player);
        var handAfter = pcs.Hand.Cards;
        if (mutationAfter == mutationBefore
            && cardIndex < handAfter.Count
            && ReferenceEquals(handAfter[cardIndex], card))
        {
            return Error($"Card play produced no observable state change: {card.GetType().Name} [{card.Id}]");
        }

        return DetectDecisionPoint();
    }

    private string CombatMutationSignature(Player player)
    {
        static string CardsSig(IEnumerable<CardModel>? cards) =>
            cards == null
                ? ""
                : string.Join(",", cards.Select(c => $"{c.Id}:{c.IsUpgraded}:{c.GetType().Name}"));

        static string PowersSig(IEnumerable<PowerModel>? powers) =>
            powers == null
                ? ""
                : string.Join(",", powers.Select(p => $"{p.GetType().Name}:{p.Amount}").OrderBy(x => x));

        var pcs = player.PlayerCombatState;
        var combatState = CombatManager.Instance.DebugOnlyGetState();
        var enemies = combatState?.Enemies?
            .Where(e => e != null)
            .Select(e => $"{e.GetType().Name}:{e.CurrentHp}:{e.Block}:{e.IsAlive}:{PowersSig(e.Powers)}")
            .ToList() ?? new();

        var playerCreature = player.Creature;
        var osty = player.Osty;
        return string.Join("||", new[]
        {
            $"round={combatState?.RoundNumber ?? 0}",
            $"energy={pcs?.Energy ?? 0}",
            $"stars={pcs?.Stars ?? 0}",
            $"player={playerCreature?.CurrentHp ?? 0}:{playerCreature?.Block ?? 0}:{PowersSig(playerCreature?.Powers)}",
            $"osty={osty?.CurrentHp ?? 0}:{osty?.Block ?? 0}:{osty?.IsAlive ?? false}:{PowersSig(osty?.Powers)}",
            $"hand={CardsSig(pcs?.Hand?.Cards)}",
            $"draw={CardsSig(pcs?.DrawPile?.Cards)}",
            $"discard={CardsSig(pcs?.DiscardPile?.Cards)}",
            $"deck={CardsSig(player.Deck?.Cards)}",
            $"enemies={string.Join(";", enemies)}",
        });
    }

    private bool HasPendingHeadlessChoice()
    {
        return _cardSelector.HasPending || _cardSelector.HasPendingReward || _pendingBundles != null;
    }

    private Dictionary<string, object?> DoEndTurn(Player player)
    {
        if (HasPendingHeadlessChoice())
            return DetectDecisionPoint();

        if (!CombatManager.Instance.IsPlayPhase)
        {
            // Might be between phases — pump and check
            _syncCtx.Pump();
            if (!CombatManager.Instance.IsPlayPhase)
            {
                if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead)
                    return DetectDecisionPoint();
                // Brief wait for ThreadPool if sync context didn't catch it
                Thread.Sleep(100);
                _syncCtx.Pump();
                if (!CombatManager.Instance.IsPlayPhase)
                    return DetectDecisionPoint();
            }
        }

        // Ensure no actions are still running before ending turn
        WaitForActionExecutor();

        Log($"Ending turn (round={CombatManager.Instance.DebugOnlyGetState()?.RoundNumber ?? 0})");
        _turnStarted.Reset();
        _combatEnded.Reset();

        // Enable SuppressYield so Task.Yield() runs inline during enemy turn processing.
        // This prevents deadlocks during boss fights (e.g., Vantom) where continuations
        // would otherwise be posted to ThreadPool and never complete.
        // Keep SuppressYield=true through the initial fallback wait loop — multi-hit
        // attacks (e.g., 10x2) have continuations between hits that also need suppression.
        YieldPatches.SuppressYield = true;
        try
        {
            PlayerCmd.EndTurn(player, canBackOut: false);
            _syncCtx.Pump();
            if (HasPendingHeadlessChoice())
                return DetectDecisionPoint();

            // Fallback: if turn didn't complete synchronously, keep pumping with SuppressYield on
            if (CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsPlayPhase && !player.Creature.IsDead)
            {
                for (int i = 0; i < 50; i++)
                {
                    _syncCtx.Pump();
                    if (HasPendingHeadlessChoice()) break;
                    if (_turnStarted.IsSet || _combatEnded.IsSet) break;
                    if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead) break;
                    if (CombatManager.Instance.IsPlayPhase) break;
                    Thread.Sleep(5);
                }
            }
        }
        finally
        {
            YieldPatches.SuppressYield = false;
        }
        if (HasPendingHeadlessChoice())
            return DetectDecisionPoint();

        // Second fallback: if still stuck after SuppressYield window, cancel and retry.
        // The WaitUntilQueue TCS is likely deadlocked.
        if (CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsPlayPhase && !player.Creature.IsDead)
        {
            Log("EndTurn stuck, cancelling and retrying with SuppressYield...");
            try
            {
                RunManager.Instance.ActionExecutor.Cancel();
                _syncCtx.Pump();
                Thread.Sleep(50);
                _syncCtx.Pump();

                // Reset the player ready state and try again with SuppressYield
                CombatManager.Instance.UndoReadyToEndTurn(player);
                _syncCtx.Pump();

                YieldPatches.SuppressYield = true;
                try
                {
                    PlayerCmd.EndTurn(player, canBackOut: false);
                    _syncCtx.Pump();
                }
                finally
                {
                    YieldPatches.SuppressYield = false;
                }
                if (HasPendingHeadlessChoice())
                    return DetectDecisionPoint();

                for (int i = 0; i < 100; i++)
                {
                    _syncCtx.Pump();
                    if (HasPendingHeadlessChoice()) break;
                    if (_turnStarted.IsSet || _combatEnded.IsSet) break;
                    if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead) break;
                    if (CombatManager.Instance.IsPlayPhase) break;
                    Thread.Sleep(10);
                }
                if (HasPendingHeadlessChoice())
                    return DetectDecisionPoint();
            }
            catch (Exception ex) { Log($"Cancel retry: {ex.Message}"); }

            // NUCLEAR OPTION: If STILL stuck after 2 attempts, use ThreadPool to force
            // the enemy turn processing to complete with SuppressYield permanently on.
            if (CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsPlayPhase && !player.Creature.IsDead)
            {
                var stuckState = CombatManager.Instance.DebugOnlyGetState();
                var stuckEnemies = stuckState?.Enemies?.Where(e => e != null && e.IsAlive)
                    .Select(e => $"{e.Monster?.GetType().Name}(hp={e.CurrentHp})").ToList();
                Log($"EndTurn STILL stuck after retry — nuclear fallback. Round={stuckState?.RoundNumber}, " +
                    $"Enemies=[{string.Join(",", stuckEnemies ?? new())}], " +
                    $"IsPlayPhase={CombatManager.Instance.IsPlayPhase}, " +
                    $"IsInProgress={CombatManager.Instance.IsInProgress}, " +
                    $"ActionExecutor.IsRunning={RunManager.Instance.ActionExecutor.IsRunning}");
                try
                {
                    // Cancel again and undo
                    RunManager.Instance.ActionExecutor.Cancel();
                    _syncCtx.Pump();
                    CombatManager.Instance.UndoReadyToEndTurn(player);
                    _syncCtx.Pump();
                    Thread.Sleep(50);

                    // Run EndTurn on ThreadPool with SuppressYield permanently on
                    YieldPatches.SuppressYield = true;
                    var endTurnTask = Task.Run(() =>
                    {
                        PlayerCmd.EndTurn(player, canBackOut: false);
                    });

                    // Aggressively pump sync context while waiting (up to 5 seconds)
                    for (int i = 0; i < 500; i++)
                    {
                        _syncCtx.Pump();
                        if (HasPendingHeadlessChoice()) break;
                        if (endTurnTask.IsCompleted) break;
                        if (_turnStarted.IsSet || _combatEnded.IsSet) break;
                        if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead) break;
                        if (CombatManager.Instance.IsPlayPhase) break;
                        Thread.Sleep(10);
                    }
                    YieldPatches.SuppressYield = false;
                    if (HasPendingHeadlessChoice())
                        return DetectDecisionPoint();

                    // If still not play phase, try just waiting a bit more
                    if (CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsPlayPhase && !player.Creature.IsDead)
                    {
                        for (int i = 0; i < 200; i++)
                        {
                            _syncCtx.Pump();
                            if (HasPendingHeadlessChoice()) break;
                            Thread.Sleep(10);
                            if (CombatManager.Instance.IsPlayPhase || !CombatManager.Instance.IsInProgress || player.Creature.IsDead)
                                break;
                        }
                    }
                    if (HasPendingHeadlessChoice())
                        return DetectDecisionPoint();

                    if (CombatManager.Instance.IsPlayPhase)
                        Log("Nuclear fallback SUCCEEDED — play phase resumed");
                    else
                    {
                        Log("Nuclear fallback FAILED — forcing game_over to escape deadlock");
                        return GameOverState(false);
                    }
                }
                catch (Exception ex)
                {
                    Log($"Nuclear fallback error: {ex.Message}");
                    YieldPatches.SuppressYield = false;
                }
            }
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSelectCardReward(Player player, Dictionary<string, object?>? args)
    {
        // Handle event-triggered card reward (blocking GetSelectedCardReward)
        if (_cardSelector.HasPendingReward)
        {
            if (args == null || !args.ContainsKey("card_index"))
                return Error("select_card_reward requires 'card_index'");
            var idx = Convert.ToInt32(args["card_index"]);
            Log($"Resolving event card reward: index {idx}");
            _cardSelector.ResolveReward(idx);
            Thread.Sleep(50);
            _syncCtx.Pump();
            WaitForPendingEventOptionTask();
            WaitForActionExecutor();
            return DetectDecisionPoint();
        }

        if (_pendingCardReward == null)
            return Error("No pending card reward");
        if (args == null || !args.ContainsKey("card_index"))
            return Error("select_card_reward requires 'card_index'");

        var cardIndex = Convert.ToInt32(args["card_index"]);
        var cards = _pendingCardReward.Cards.ToList();
        if (cardIndex < 0 || cardIndex >= cards.Count)
            return Error($"Invalid card index {cardIndex}, {cards.Count} cards available");

        var card = cards[cardIndex];
        Log($"Selected card reward: {card.GetType().Name}");

        // Add card to deck
        try
        {
            MegaCrit.Sts2.Core.Commands.CardPileCmd
                .Add(card, MegaCrit.Sts2.Core.Entities.Cards.PileType.Deck)
                .GetAwaiter().GetResult();
            _syncCtx.Pump();
            RunManager.Instance.RewardSynchronizer.SyncLocalObtainedCard(card);
        }
        catch (Exception ex) { Log($"Add card to deck: {ex.Message}"); }

        // Check if more rewards pending
        _pendingRewards?.Remove(_pendingCardReward);
        _pendingCardReward = null;
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSkipCardReward(Player player)
    {
        if (_cardSelector.HasPendingReward)
        {
            Log("Skipping event card reward");
            _cardSelector.SkipReward();
            Thread.Sleep(50);
            _syncCtx.Pump();
            WaitForPendingEventOptionTask();
            WaitForActionExecutor();
            return DetectDecisionPoint();
        }
        if (_pendingCardReward != null)
        {
            Log("Skipping card reward");
            _pendingRewards?.Remove(_pendingCardReward);
            _pendingCardReward.OnSkipped();
            _pendingCardReward = null;
        }
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoClaimCombatReward(Player player, Dictionary<string, object?>? args)
    {
        if (_pendingRewards == null || _pendingRewards.Count == 0)
            return Error("No pending combat rewards");
        if (args == null || !args.ContainsKey("reward_index"))
            return Error("claim_reward requires 'reward_index'");

        var rewardIndex = Convert.ToInt32(args["reward_index"]);
        if (rewardIndex < 0 || rewardIndex >= _pendingRewards.Count)
            return Error($"Invalid reward_index {rewardIndex}, {_pendingRewards.Count} rewards pending");

        var reward = _pendingRewards[rewardIndex];
        if (reward is CardReward cardReward)
        {
            _pendingCardReward = cardReward;
            return CardRewardState(player, _runState?.CurrentRoom as CombatRoom);
        }
        if (reward is MegaCrit.Sts2.Core.Rewards.PotionReward && !HasOpenPotionSlot(player))
            return Error("Potion slots are full; use discard_potion before claiming this potion reward");

        try
        {
            Log($"Claiming combat reward {rewardIndex}: {reward.GetType().Name}");
            reward.OnSelectWrapper().GetAwaiter().GetResult();
            _syncCtx.Pump();
            _pendingRewards.RemoveAt(rewardIndex);
        }
        catch (Exception ex)
        {
            return Error($"Claim reward failed: {ex.Message}");
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSkipCombatReward(Player player, Dictionary<string, object?>? args)
    {
        if (_pendingRewards == null || _pendingRewards.Count == 0)
            return Error("No pending combat rewards");
        if (args == null || !args.ContainsKey("reward_index"))
            return Error("skip_reward requires 'reward_index'");

        var rewardIndex = Convert.ToInt32(args["reward_index"]);
        if (rewardIndex < 0 || rewardIndex >= _pendingRewards.Count)
            return Error($"Invalid reward_index {rewardIndex}, {_pendingRewards.Count} rewards pending");

        var reward = _pendingRewards[rewardIndex];
        if (!CanSkipCombatReward(reward))
            return Error($"Reward {rewardIndex} ({reward.GetType().Name}) cannot be skipped");

        Log($"Skipping combat reward {rewardIndex}: {reward.GetType().Name}");
        _pendingRewards.RemoveAt(rewardIndex);
        return DetectDecisionPoint();
    }

    private static bool CanSkipCombatReward(Reward reward)
    {
        if (reward is MegaCrit.Sts2.Core.Rewards.PotionReward)
            return true;
        if (reward is CardReward cardReward)
            return cardReward.CanSkip;
        return false;
    }

    private static bool HasOpenPotionSlot(Player player)
    {
        var slots = GetPotionSlots(player);
        if (slots == null)
            return true;

        foreach (var slot in slots)
        {
            if (slot == null)
                return true;
        }
        return false;
    }

    private Dictionary<string, object?> DoBuyCard(Player player, Dictionary<string, object?>? args)
    {
        if (_runState?.CurrentRoom is not MerchantRoom merchantRoom)
            return Error("Not in a shop");
        if (args == null || !args.ContainsKey("card_index"))
            return Error("buy_card requires 'card_index'");

        var idx = Convert.ToInt32(args["card_index"]);
        var allEntries = merchantRoom.Inventory.CharacterCardEntries
            .Concat(merchantRoom.Inventory.ColorlessCardEntries).ToList();
        if (idx < 0 || idx >= allEntries.Count)
            return Error($"Invalid card index {idx}");

        var entry = allEntries[idx];
        if (!entry.IsStocked) return Error("Card already purchased");
        if (player.Gold < entry.Cost) return Error("Not enough gold");

        try
        {
            entry.OnTryPurchaseWrapper(merchantRoom.Inventory).GetAwaiter().GetResult();
            _syncCtx.Pump();
            Log($"Bought card: {entry.CreationResult?.Card?.GetType().Name ?? "?"} for {entry.Cost}g");
        }
        catch (Exception ex) { return Error($"Buy card failed: {ex.Message}"); }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoBuyRelic(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("relic_index"))
            return Error("buy_relic requires 'relic_index'");

        var idx = Convert.ToInt32(args["relic_index"]);
        if (_runState?.CurrentRoom is MerchantRoom merchantRoom)
            return DoBuyRelicEntry(player, merchantRoom.Inventory, merchantRoom.Inventory.RelicEntries, idx);

        if (TryGetFakeMerchant(out var fakeMerchant))
            return DoBuyRelicEntry(player, fakeMerchant.Inventory, fakeMerchant.Inventory.RelicEntries, idx);

        return Error("Not in a shop");
    }

    private Dictionary<string, object?> DoBuyRelicEntry(
        Player player,
        MerchantInventory inventory,
        IReadOnlyList<MerchantRelicEntry> entries,
        int idx)
    {
        if (idx < 0 || idx >= entries.Count) return Error($"Invalid relic index {idx}");

        var entry = entries[idx];
        if (!entry.IsStocked) return Error("Relic already purchased");
        if (player.Gold < entry.Cost) return Error("Not enough gold");

        try
        {
            var task = Task.Run(() => entry.OnTryPurchaseWrapper(inventory));
            _pendingShopPurchaseTask = task;
            for (int i = 0; i < 100; i++)
            {
                _syncCtx.Pump();
                if (HasPendingHeadlessChoice()) break;
                if (task.IsCompleted) break;
                Thread.Sleep(10);
            }
            if (HasPendingHeadlessChoice())
            {
                WaitForActionExecutor();
                return DetectDecisionPoint();
            }
            if (!task.IsCompleted) task.Wait(2000);
            _syncCtx.Pump();
            if (task.IsFaulted)
                return Error($"Buy relic failed: {task.Exception?.GetBaseException().Message}");
            _pendingShopPurchaseTask = null;
            Log($"Bought relic: {entry.Model?.GetType().Name ?? "unknown"} for {entry.Cost}g");
        }
        catch (Exception ex) { return Error($"Buy relic failed: {ex.Message}"); }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoBuyPotion(Player player, Dictionary<string, object?>? args)
    {
        if (_runState?.CurrentRoom is not MerchantRoom merchantRoom)
            return Error("Not in a shop");
        if (args == null || !args.ContainsKey("potion_index"))
            return Error("buy_potion requires 'potion_index'");

        var idx = Convert.ToInt32(args["potion_index"]);
        var entries = merchantRoom.Inventory.PotionEntries;
        if (idx < 0 || idx >= entries.Count) return Error($"Invalid potion index {idx}");

        var entry = entries[idx];
        if (!entry.IsStocked) return Error("Potion already purchased");
        if (player.Gold < entry.Cost) return Error("Not enough gold");

        try
        {
            entry.OnTryPurchaseWrapper(merchantRoom.Inventory).GetAwaiter().GetResult();
            _syncCtx.Pump();
            Log($"Bought potion: {entry.Model?.GetType().Name ?? "unknown"} for {entry.Cost}g");
        }
        catch (Exception ex)
        {
            // Potion purchase sometimes NullRefs in headless (missing potion slot UI)
            Log($"Buy potion failed: {ex.Message}");
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoRemoveCard(Player player)
    {
        if (_runState?.CurrentRoom is not MerchantRoom merchantRoom)
            return Error("Not in a shop");

        var removal = merchantRoom.Inventory.CardRemovalEntry;
        if (removal == null) return Error("No card removal available");
        if (!removal.IsStocked) return Error("Card removal already purchased");
        if (player.Gold < removal.Cost) return Error("Not enough gold");

        try
        {
            var sourceRoomOption = ShopCardRemovalSelectionContext(removal.Cost);
            // Run on background thread so card selection can pause (same pattern as event options)
            var task = Task.Run(() => removal.OnTryPurchaseWrapper(merchantRoom.Inventory));
            _pendingShopPurchaseTask = task;
            _pendingShopCardRemovalEntry = removal;
            for (int i = 0; i < 100; i++)
            {
                _syncCtx.Pump();
                if (_cardSelector.HasPending) break;
                if (task.IsCompleted) break;
                Thread.Sleep(10);
            }
            if (_cardSelector.HasPending)
            {
                _pendingCardSelectionSourceCard = null;
                _pendingCardSelectionSourceEventOption = null;
                _pendingCardSelectionSourceRoomOption = sourceRoomOption;
                _pendingCardSelectionSourcePotion = null;
                WaitForActionExecutor();
                return DetectDecisionPoint();
            }
            if (!task.IsCompleted) task.Wait(2000);
            _syncCtx.Pump();
            if (task.IsCompletedSuccessfully && task.Result)
                removal.SetUsed();
            _pendingShopPurchaseTask = null;
            _pendingShopCardRemovalEntry = null;
            Log($"Removed card for {removal.Cost}g");
        }
        catch (Exception ex)
        {
            _pendingShopPurchaseTask = null;
            _pendingShopCardRemovalEntry = null;
            return Error($"Remove card failed: {ex.Message}");
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSelectBundle(Player player, Dictionary<string, object?>? args)
    {
        if (_pendingBundleTcs == null || _pendingBundles == null)
            return Error("No pending bundle selection");
        if (args == null || !args.ContainsKey("bundle_index"))
            return Error("select_bundle requires 'bundle_index'");

        var idx = Convert.ToInt32(args["bundle_index"]);
        Log($"Bundle selection: pack {idx}");
        var bundles = _pendingBundles;
        var tcs = _pendingBundleTcs;
        _pendingBundles = null;
        _pendingBundleTcs = null;

        // Set result directly (no ContinueWith/ThreadPool)
        var selected = (idx >= 0 && idx < bundles.Count) ? bundles[idx] : bundles[0];
        tcs.TrySetResult(selected);

        _syncCtx.Pump();
        WaitForPendingEventOptionTask();
        WaitForActionExecutor();
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSelectCards(Player player, Dictionary<string, object?>? args)
    {
        if (!_cardSelector.HasPending)
            return Error("No pending card selection");
        if (args == null || !args.ContainsKey("indices"))
            return Error("select_cards requires 'indices' (comma-separated card indices)");

        var indices = ParseSelectionIndices(args["indices"]);
        var selectedCount = _cardSelector.CountValidSelectedIndices(indices);
        if (selectedCount < _cardSelector.PendingMinSelect)
            return Error($"Current card selection requires at least {_cardSelector.PendingMinSelect} card(s)");
        if (selectedCount > _cardSelector.PendingMaxSelect)
            return Error($"Current card selection allows at most {_cardSelector.PendingMaxSelect} card(s)");

        Log($"Card selection: indices [{string.Join(",", indices)}]");
        _cardSelector.ResolvePendingByIndices(indices);
        _pendingCardSelectionSourceCard = null;
        _pendingCardSelectionSourceEventOption = null;
        _pendingCardSelectionSourceRoomOption = null;
        _pendingCardSelectionSourcePotion = null;
        _syncCtx.Pump();
        WaitForPendingEventOptionTask();
        WaitForActionExecutor();

        // Extra wait for rest-site SMITH: the background ChooseLocalOption task
        // needs time to complete the upgrade after card selection resolves.
        if (_runState?.CurrentRoom is RestSiteRoom)
        {
            Thread.Sleep(200);
            _syncCtx.Pump();
            WaitForActionExecutor();
            // Force to map after SMITH completes (same pattern as HEAL)
            Log("Card selection in rest site (SMITH), forcing to map");
            ForceToMap();
            return MapSelectState();
        }

        // Extra wait for shop card removal: the purchase task needs to finish
        if (_runState?.CurrentRoom is MerchantRoom)
            WaitForPendingShopPurchaseTask();

        return DetectDecisionPoint();
    }

    private static int[] ParseSelectionIndices(object? indicesArg)
    {
        if (indicesArg is IEnumerable<object?> values)
        {
            return values
                .Select(ParseSelectionIndex)
                .Where(i => i >= 0)
                .ToArray();
        }

        var indicesStr = indicesArg?.ToString()?.Trim() ?? "";
        if (indicesStr.StartsWith("[", StringComparison.Ordinal) &&
            indicesStr.EndsWith("]", StringComparison.Ordinal))
        {
            indicesStr = indicesStr[1..^1];
        }

        return indicesStr.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s.Trim(), out var v) ? v : -1)
            .Where(i => i >= 0)
            .ToArray();
    }

    private static int ParseSelectionIndex(object? value)
    {
        if (value == null) return -1;
        if (value is int i) return i;
        return int.TryParse(value.ToString(), out var parsed) ? parsed : -1;
    }

    private Dictionary<string, object?> DoSkipSelect(Player player)
    {
        if (_cardSelector.HasPending)
        {
            if (!_cardSelector.PendingCanSkip)
                return Error("Current card selection cannot be skipped");
            Log("Skipping card selection");
            _cardSelector.CancelPending();
            _pendingCardSelectionSourceCard = null;
            _pendingCardSelectionSourceEventOption = null;
            _pendingCardSelectionSourceRoomOption = null;
            _pendingCardSelectionSourcePotion = null;
            _syncCtx.Pump();
            WaitForPendingEventOptionTask();
            WaitForActionExecutor();
            if (_runState?.CurrentRoom is MerchantRoom)
                WaitForPendingShopPurchaseTask();
        }
        return DetectDecisionPoint();
    }

    private void WaitForPendingShopPurchaseTask()
    {
        var shopTask = _pendingShopPurchaseTask;
        if (shopTask != null)
        {
            for (int i = 0; i < 300; i++)
            {
                _syncCtx.Pump();
                WaitForActionExecutor();
                if (shopTask.IsCompleted) break;
                if (HasPendingHeadlessChoice()) break;
                Thread.Sleep(10);
            }
            if (shopTask.IsCompleted)
            {
                if (shopTask.IsFaulted)
                    Log($"Shop purchase task failed: {shopTask.Exception?.GetBaseException().Message}");
                else if (!shopTask.IsCanceled && shopTask is Task<bool> boolTask && boolTask.Result)
                    _pendingShopCardRemovalEntry?.SetUsed();
                _pendingShopPurchaseTask = null;
                _pendingShopCardRemovalEntry = null;
            }
        }
        else
        {
            Thread.Sleep(200);
            _syncCtx.Pump();
            WaitForActionExecutor();
        }
        Log("Card selection in shop, refreshing shop state");
    }

    private Dictionary<string, object?> DoUsePotion(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("potion_index"))
            return Error("use_potion requires 'potion_index'");

        var idx = Convert.ToInt32(args["potion_index"]);
        var potionsList = player.Potions?.ToList() ?? new();
        if (idx < 0 || idx >= potionsList.Count) return Error($"Invalid potion index {idx}");
        var potion = potionsList[idx];
        if (potion == null) return Error($"No potion at index {idx}");
        var sourcePotion = PotionInfo(potion, idx);
        _pendingCardSelectionSourcePotion = null;

        // Determine target based on potion's TargetType. Enemy-targeted potions
        // require target_index; the CLI must not choose silently.
        Creature? target = null;
        var potionTargetType = potion.TargetType;

        // Self-targeting potions (Flex, Fortifier, etc.) ALWAYS target the player
        // regardless of any target_index the caller provides
        var potionTargetName = potionTargetType.ToString();
        if (potionTargetType == TargetType.Self
            || potionTargetType == TargetType.TargetedNoCreature
            || potionTargetName.Contains("Player", StringComparison.OrdinalIgnoreCase))
        {
            target = player.Creature;
        }
        else if (potionTargetType == TargetType.AnyEnemy)
        {
            if (!args.TryGetValue("target_index", out var tObj) || tObj == null)
                return Error("use_potion requires 'target_index' for AnyEnemy potion");

            var targetIdx = Convert.ToInt32(tObj);
            var combatState = CombatManager.Instance.DebugOnlyGetState();
            var enemies = combatState?.Enemies?.Where(e => e != null && e.IsAlive).ToList() ?? new();
            if (targetIdx < 0 || targetIdx >= enemies.Count)
                return Error($"Invalid target_index {targetIdx}, combat has {enemies.Count} alive enemies");
            target = enemies[targetIdx];
        }
        // All other target types (None, All, etc.) leave target as null.

        Log($"Using potion: {potion.GetType().Name} at slot {idx} target={target?.GetType().Name ?? "none"}");
        try
        {
            var action = new MegaCrit.Sts2.Core.GameActions.UsePotionAction(potion, target, CombatManager.Instance.IsInProgress);
            RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(action);
            WaitForActionExecutor();
            _syncCtx.Pump();

            // Effect may require card_select before the potion slot clears — do not discard as "stuck".
            if (_cardSelector.HasPending || _cardSelector.HasPendingReward)
            {
                if (_cardSelector.HasPending)
                {
                    _pendingCardSelectionSourceCard = null;
                    _pendingCardSelectionSourceEventOption = null;
                    _pendingCardSelectionSourceRoomOption = null;
                    _pendingCardSelectionSourcePotion = sourcePotion;
                }
                return DetectDecisionPoint();
            }

            // Verify potion was consumed
            var afterPotions = player.Potions?.ToList() ?? new();
            if (afterPotions.Contains(potion))
            {
                Log("Potion action completed but potion was not consumed");
                return Error("Potion action completed but potion was not consumed; state left unchanged");
            }
        }
        catch (Exception ex)
        {
            Log($"Use potion failed: {ex.Message}");
            return Error($"Use potion failed: {ex.Message}");
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoDiscardPotion(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("potion_index"))
            return Error("discard_potion requires 'potion_index'");

        var idx = Convert.ToInt32(args["potion_index"]);
        var potionsList = player.Potions?.ToList() ?? new();
        if (idx < 0 || idx >= potionsList.Count) return Error($"Invalid potion index {idx}");
        var potion = potionsList[idx];
        if (potion == null) return Error($"No potion at index {idx}");

        MegaCrit.Sts2.Core.Commands.PotionCmd.Discard(potion).GetAwaiter().GetResult();
        _syncCtx.Pump();
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoChooseOption(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("option_index"))
            return Error("choose_option requires 'option_index'");

        var optionIndex = Convert.ToInt32(args["option_index"]);
        Log($"Choosing option {optionIndex}");

        if (_pendingEventResult != null)
        {
            if (optionIndex != 0)
                return Error($"Invalid event result option {optionIndex}");
            return DoProceed(player);
        }

        // Dispatch based on ROOM TYPE (not event state) to avoid cross-contamination
        if (_runState?.CurrentRoom is RestSiteRoom restSiteRoom)
        {
            Log($"Rest site: choosing option {optionIndex}");
            try
            {
                var sourceRoomOption = RestSiteOptionSelectionContext(restSiteRoom, optionIndex);
                // Run on background thread so Smith card selection can pause
                var task = Task.Run(() => RunManager.Instance.RestSiteSynchronizer.ChooseLocalOption(optionIndex));
                for (int i = 0; i < 100; i++)
                {
                    _syncCtx.Pump();
                    if (_cardSelector.HasPending) break;
                    if (task.IsCompleted) break;
                    Thread.Sleep(10);
                }
                if (_cardSelector.HasPending)
                {
                    if (sourceRoomOption != null)
                    {
                        _pendingCardSelectionSourceCard = null;
                        _pendingCardSelectionSourceEventOption = null;
                        _pendingCardSelectionSourceRoomOption = sourceRoomOption;
                        _pendingCardSelectionSourcePotion = null;
                    }
                    WaitForActionExecutor();
                    return DetectDecisionPoint();
                }
                if (!task.IsCompleted) task.Wait(2000);
                _syncCtx.Pump();
            }
            catch (Exception ex)
            {
                Log($"Rest site ChooseLocalOption failed: {ex.Message}");
            }

            // After non-Smith rest site options (HEAL, etc.), the options may not clear.
            // Wait for the action to complete (heal/dig), then force transition to map.
            if (!_cardSelector.HasPending)
            {
                Log("Rest site: option chosen (non-Smith), waiting for action then forcing to map");
                // Give the action time to complete (heal HP, dig for relic, etc.)
                WaitForActionExecutor();
                _syncCtx.Pump();
                Thread.Sleep(200);
                _syncCtx.Pump();
                WaitForActionExecutor();
                ForceToMap();
                return MapSelectState();
            }
        }
        // For events, run the original EventOption.Chosen() task directly so
        // headless can wait for selections and completion without reimplementing
        // event effects.
        else if (_runState?.CurrentRoom is EventRoom || _pendingEventChoiceAfterCombat != null)
        {
            var eventSync = RunManager.Instance.EventSynchronizer;
            var localEvent = eventSync?.GetLocalEvent();
            if (localEvent != null && !localEvent.IsFinished)
            {
                var options = localEvent.CurrentOptions;
                var optCountBefore = options?.Count ?? 0;
                if (options != null && optionIndex >= 0 && optionIndex < options.Count)
                {
                    var sourceEventOption = _runState?.CurrentRoom is EventRoom eventRoom
                        ? EventOptionSelectionContext(eventRoom, optionIndex)
                        : null;
                    var selectedTextKey = options[optionIndex].TextKey;
                    var previousSuppressYield = YieldPatches.SuppressYield;
                    try
                    {
                        _eventOptionChosen = true;
                        _lastEventOptionCount = options.Count;
                        YieldPatches.ActiveCrystalSphereMinigame = null;
                        YieldPatches.SuppressYield = previousSuppressYield;
                        var task = Task.Run(() => options[optionIndex].Chosen());
                        _pendingEventOptionTask = task;
                        for (int i = 0; i < 100; i++)
                        {
                            _syncCtx.Pump();
                            if (_cardSelector.HasPending || _cardSelector.HasPendingReward) break;
                            if (_pendingBundles != null) break;
                            if (YieldPatches.ActiveCrystalSphereMinigame != null) break;
                            if (task.IsCompleted) break;
                            Thread.Sleep(10);
                        }
                        if (YieldPatches.ActiveCrystalSphereMinigame != null)
                        {
                            YieldPatches.SuppressYield = previousSuppressYield;
                            return DetectDecisionPoint();
                        }
                        if (_cardSelector.HasPending || _cardSelector.HasPendingReward || _pendingBundles != null)
                        {
                            if (_cardSelector.HasPending && sourceEventOption != null)
                            {
                                _pendingCardSelectionSourceCard = null;
                                _pendingCardSelectionSourceEventOption = sourceEventOption;
                                _pendingCardSelectionSourceRoomOption = null;
                                _pendingCardSelectionSourcePotion = null;
                            }
                            YieldPatches.SuppressYield = previousSuppressYield;
                            WaitForActionExecutor();
                            return DetectDecisionPoint();
                        }
                        if (!task.IsCompleted) task.Wait(2000);
                        _syncCtx.Pump();
                        if (task.IsCompleted)
                        {
                            if (task.IsFaulted)
                                Log($"Event choose failed: {task.Exception?.GetBaseException().GetType().FullName}: {task.Exception?.GetBaseException().Message}");
                            _pendingEventOptionTask = null;
                        }
                        if (IsVictoryProceedOption(selectedTextKey))
                        {
                            CompleteVictoryRoomTransition();
                        }
                        if (_runState?.CurrentRoom is CombatRoom && CombatManager.Instance.IsInProgress)
                        {
                            _pendingEventChoiceAfterCombat = null;
                        }
                        else if (_runState?.CurrentRoom is CombatRoom && !localEvent.IsFinished)
                        {
                            _pendingEventChoiceAfterCombat = localEvent;
                        }
                    }
                    catch (Exception ex) { Log($"Event choose: {ex.Message}"); }
                    finally
                    {
                        YieldPatches.SuppressYield = previousSuppressYield;
                    }
                }

                var optCountAfter = localEvent.CurrentOptions?.Count ?? 0;
                if (!localEvent.IsFinished && optCountAfter == optCountBefore && optCountAfter > 0)
                    Log($"Event {localEvent.GetType().Name}: option count unchanged after choice");
            }
        }

        WaitForActionExecutor();
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoLeaveRoom(Player player)
    {
        Log("Leaving room");
        if (_pendingEventChoiceAfterCombat != null)
        {
            return Error("Cannot leave this event; choose an available event option");
        }
        try { RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult(); }
        catch { }
        _syncCtx.Pump();
        WaitForActionExecutor();

        // If still in a non-combat room, force to map
        var room = _runState?.CurrentRoom;
        if (room is EventRoom && !TryGetFakeMerchant(out _))
            return Error("Cannot leave this event; choose an available event option");

        if (room is RestSiteRoom || room is MerchantRoom || room is TreasureRoom || room is EventRoom)
        {
            Log("Force leaving non-combat room to map");
            try
            {
                RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult();
                _syncCtx.Pump();
                WaitForActionExecutor();
            }
            catch (Exception ex) { Log($"Force leave: {ex.Message}"); }
        }
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoProceed(Player player)
    {
        Log("Proceeding");

        // Check if we need to move to next act (boss defeated)
        var room = _runState?.CurrentRoom;
        if (_pendingEventResult != null)
        {
            _pendingEventResult = null;
            ForceToMap(skipTerminalProceed: true);
            return MapSelectState();
        }
        if (room is MerchantRoom)
            return DoLeaveRoom(player);
        if (room is EventRoom && TryGetFakeMerchant(out _))
            return DoLeaveRoom(player);
        if (room is TreasureRoom)
        {
            CompleteEmptyTreasureRelicSessionIfNeeded();
            return DoLeaveRoom(player);
        }

        if (room is CombatRoom combatRoom && combatRoom.RoomType == RoomType.Boss)
        {
            if (combatRoom.IsPreFinished || !CombatManager.Instance.IsInProgress)
            {
                RunManager.Instance.EnterNextAct().GetAwaiter().GetResult();
                WaitForActionExecutor();
                return DetectDecisionPoint();
            }
        }

        RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult();
        WaitForActionExecutor();
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoCrystalSphereSetTool(Dictionary<string, object?>? args)
    {
        var minigame = YieldPatches.ActiveCrystalSphereMinigame;
        if (minigame == null)
            return Error("No Crystal Sphere minigame is active");
        if (minigame.IsFinished)
            return CrystalSphereState(minigame);
        if (args == null || !args.TryGetValue("tool", out var toolObj) || toolObj == null)
            return Error("crystal_sphere_set_tool requires 'tool'");

        var tool = Convert.ToString(toolObj)?.Trim().ToLowerInvariant();
        if (tool == "big")
            minigame.SetTool(CrystalSphereMinigame.CrystalSphereToolType.Big);
        else if (tool == "small")
            minigame.SetTool(CrystalSphereMinigame.CrystalSphereToolType.Small);
        else
            return Error("tool must be 'big' or 'small'");

        return CrystalSphereState(minigame);
    }

    private Dictionary<string, object?> DoCrystalSphereClickCell(Dictionary<string, object?>? args)
    {
        var minigame = YieldPatches.ActiveCrystalSphereMinigame;
        if (minigame == null)
            return Error("No Crystal Sphere minigame is active");
        if (minigame.IsFinished)
            return CrystalSphereState(minigame);
        if (args == null || !args.ContainsKey("x") || !args.ContainsKey("y"))
            return Error("crystal_sphere_click_cell requires 'x' and 'y'");

        var x = Convert.ToInt32(args["x"]);
        var y = Convert.ToInt32(args["y"]);
        if (x < 0 || x >= minigame.GridSize.X || y < 0 || y >= minigame.GridSize.Y)
            return Error($"Crystal Sphere cell [{x},{y}] is outside the {minigame.GridSize.X}x{minigame.GridSize.Y} grid");

        var cell = minigame.cells[x, y];
        if (!cell.IsHidden)
            return Error($"Crystal Sphere cell [{x},{y}] is already revealed");

        minigame.CellClicked(cell).GetAwaiter().GetResult();
        _syncCtx.Pump();
        return CrystalSphereState(minigame);
    }

    private Dictionary<string, object?> DoCrystalSphereProceed()
    {
        var minigame = YieldPatches.ActiveCrystalSphereMinigame;
        if (minigame == null)
            return Error("No Crystal Sphere minigame is active");
        if (!minigame.IsFinished)
            return Error("Crystal Sphere still has divinations remaining");

        var task = _pendingEventOptionTask;
        if (task != null)
        {
            for (int i = 0; i < 300; i++)
            {
                _syncCtx.Pump();
                WaitForActionExecutor();
                if (task.IsCompleted || HasPendingHeadlessChoice())
                    break;
                Thread.Sleep(10);
            }

            if (task.IsFaulted)
                return Error($"Crystal Sphere event failed: {task.Exception?.GetBaseException().Message}");
            if (task.IsCompleted)
                _pendingEventOptionTask = null;
        }

        YieldPatches.ActiveCrystalSphereMinigame = null;
        _syncCtx.Pump();
        WaitForActionExecutor();
        return DetectDecisionPoint();
    }

    #endregion

    #region Decision Point Detection

    private Dictionary<string, object?> DetectDecisionPoint()
    {
        if (_runState == null)
            return Error("No run in progress");

        var player = _runState.Players[0];

        if (RunManager.Instance.IsGameOver && _runState.CurrentRoom?.IsVictoryRoom == true)
        {
            return GameOverState(true);
        }

        // Check game over (death)
        if (player.Creature != null && player.Creature.IsDead)
        {
            return GameOverState(false);
        }

        // Check if there's a pending bundle selection (Scroll Boxes: pick 1 of N packs)
        if (_pendingBundles != null && _pendingBundleTcs != null && !_pendingBundleTcs.Task.IsCompleted)
        {
            var bundles = _pendingBundles.Select((bundle, i) => new Dictionary<string, object?>
            {
                ["index"] = i,
                ["cards"] = bundle.Select(card =>
                {
                    var stats = ExtractCardStats(card, player);
                    var bkws = card.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToList();
                    var cardInfo = new Dictionary<string, object?>
                    {
                        ["name"] = _loc.Card(card.Id.Entry),
                        ["cost"] = GetEnergyCostDisplay(card),
                        ["type"] = card.Type.ToString(),
                        ["rarity"] = card.Rarity.ToString(),
                        ["description"] = CardDescription(card, stats),
                        ["stats"] = stats.Count > 0 ? stats : null,
                        ["keywords"] = bkws?.Count > 0 ? bkws : null,
                    };
                    AddCardVars(cardInfo, card);
                    AddEnergyCostDetails(cardInfo, card);
                    AddStarCostDetails(cardInfo, card);
                    AddCardEnhancements(cardInfo, card);
                    AddCardHoverTips(cardInfo, card);
                    return cardInfo;
                }).ToList(),
            }).ToList();

            return new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "bundle_select",
                ["context"] = RunContext(),
                ["bundles"] = bundles,
                ["player"] = PlayerSummary(player),
            };
        }

        // Check if there's a pending card reward from event (GetSelectedCardReward blocking)
        if (_cardSelector.HasPendingReward)
        {
            var rewardCards = _cardSelector.PendingRewardCards!;
            var cards = rewardCards.Select((cr, i) =>
            {
                var stats = ExtractCardStats(cr.Card, player);
                var rrkws = cr.Card.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToList();
                var cardInfo = new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["id"] = cr.Card.Id.ToString(),
                    ["name"] = _loc.Card(cr.Card.Id.Entry),
                    ["cost"] = GetEnergyCostDisplay(cr.Card),
                    ["type"] = cr.Card.Type.ToString(),
                    ["rarity"] = cr.Card.Rarity.ToString(),
                    ["upgraded"] = cr.Card.IsUpgraded,
                    ["description"] = CardDescription(cr.Card, stats),
                    ["stats"] = stats.Count > 0 ? stats : null,
                    ["keywords"] = rrkws?.Count > 0 ? rrkws : null,
                    ["after_upgrade"] = GetUpgradedInfo(cr.Card, player),
                };
                AddCardVars(cardInfo, cr.Card);
                AddEnergyCostDetails(cardInfo, cr.Card);
                AddStarCostDetails(cardInfo, cr.Card);
                AddCardEnhancements(cardInfo, cr.Card);
                AddCardHoverTips(cardInfo, cr.Card);
                return cardInfo;
            }).ToList();

            return new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "card_reward",
                ["context"] = RunContext(),
                ["cards"] = cards,
                ["can_skip"] = true,
                ["from_event"] = true,
                ["player"] = PlayerSummary(_runState!.Players[0]),
            };
        }

        // Check if there's a pending card selection (upgrade, remove, transform, start-of-turn powers)
        checkCardSelect:
        if (_cardSelector.HasPending && _cardSelector.PendingOptions != null)
        {
            var opts = _cardSelector.PendingOptions.Select((card, i) =>
            {
                var includeCombatPreview = ShouldExportCombatPreviewState(card);
                var includeTargetRows = ShouldExportCombatCardState(card);
                var useSourceDynamicContext = CombatManager.Instance.IsInProgress;
                var stats = ExtractCardStats(
                    card,
                    player,
                    applyCombatModifiers: includeCombatPreview,
                    includeTargetRows: includeTargetRows);
                var selkws = card.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToList();
                var cardInfo = new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["id"] = card.Id.ToString(),
                    ["name"] = _loc.Card(card.Id.Entry),
                    ["cost"] = GetEnergyCostDisplay(card),
                    ["type"] = card.Type.ToString(),
                    ["rarity"] = card.Rarity.ToString(),
                    ["upgraded"] = card.IsUpgraded,
                    ["stats"] = stats.Count > 0 ? stats : null,
                    ["description"] = CardDescription(card, stats, includeCombatText: includeCombatPreview),
                    ["keywords"] = selkws?.Count > 0 ? selkws : null,
                    ["after_upgrade"] = GetUpgradedInfo(
                        card,
                        player,
                        applyCombatModifiers: includeCombatPreview,
                        includeTargetRows: includeTargetRows,
                        useSourceDynamicContext: useSourceDynamicContext),
                };
                AddCardVars(cardInfo, card, includePreviewStats: includeCombatPreview);
                AddEnergyCostDetails(cardInfo, card, includeCurrentXValue: includeTargetRows);
                AddStarCostDetails(cardInfo, card);
                AddCardEnhancements(cardInfo, card);
                AddCardHoverTips(cardInfo, card);
                return cardInfo;
            }).ToList();

            var sourceModel = _cardSelector.PendingSourceModel;
            var sourceCard = _pendingCardSelectionSourceCard ?? sourceModel as CardModel;
            var sourcePower = sourceModel as PowerModel;
            if (sourceCard == null
                && sourcePower == null
                && _pendingCardSelectionSourceEventOption == null
                && _pendingCardSelectionSourceRoomOption == null
                && _pendingCardSelectionSourcePotion == null)
            {
                sourcePower = InferPendingCardSelectionSourcePower(player);
            }
            var prompt = CardSelectionPrompt(_pendingCardSelectionSourceEventOption)
                         ?? CardSelectionPrompt(_pendingCardSelectionSourceRoomOption)
                         ?? CardSelectionPrompt(_pendingCardSelectionSourcePotion)
                         ?? CardSelectionPrompt(sourceCard)
                         ?? CardSelectionPrompt(sourcePower);
            var state = new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "card_select",
                ["context"] = RunContext(),
                ["cards"] = opts,
                ["min_select"] = _cardSelector.PendingMinSelect,
                ["max_select"] = _cardSelector.PendingMaxSelect,
                ["can_skip"] = _cardSelector.PendingCanSkip,
                ["player"] = PlayerSummary(player),
            };
            if (prompt != null)
            {
                state["prompt"] = prompt;
            }
            if (sourceCard != null)
            {
                state["source_card"] = CardSummary(
                    sourceCard,
                    applyCombatModifiers: ShouldExportCombatPreviewState(sourceCard),
                    includeTargetRows: ShouldExportCombatCardState(sourceCard));
            }
            if (sourcePower != null)
            {
                state["source_power"] = PowerInfo(sourcePower);
            }
            if (CombatManager.Instance.IsInProgress)
            {
                state["combat"] = CombatSelectionContext(player);
            }
            if (_pendingCardSelectionSourceEventOption != null)
            {
                state["source_event_option"] = _pendingCardSelectionSourceEventOption;
            }
            if (_pendingCardSelectionSourceRoomOption != null)
            {
                state["source_room_option"] = _pendingCardSelectionSourceRoomOption;
            }
            if (_pendingCardSelectionSourcePotion != null)
            {
                state["source_potion"] = _pendingCardSelectionSourcePotion;
            }
            return state;
        }

        if (YieldPatches.ActiveCrystalSphereMinigame != null)
        {
            return CrystalSphereState(YieldPatches.ActiveCrystalSphereMinigame);
        }

        if (_pendingEventResult != null)
        {
            return EventResultState(_pendingEventResult);
        }

        if (_pendingEventChoiceAfterCombat != null
            && !CombatManager.Instance.IsInProgress)
        {
            return EventChoiceState(new EventRoom(_pendingEventChoiceAfterCombat.CanonicalInstance));
        }

        // Check if there's a pending card reward
        if (_pendingCardReward != null)
        {
            return CardRewardState(player, _runState.CurrentRoom as CombatRoom);
        }

        // Check if RunManager reports game over (victory)
        if (RunManager.Instance.IsGameOver)
        {
            return GameOverState(true);
        }

        var room = _runState.CurrentRoom;

        // Map room — need to select a node
        if (room is MapRoom || room == null)
        {
            return MapSelectState();
        }

        // Combat room
        if (room is CombatRoom combatRoom)
        {
            // With Task.Yield() patched, combat init should be synchronous
            _syncCtx.Pump();
            WaitForActionExecutor();

            // Re-check for pending card selections AFTER pump (BUG-024: start-of-turn effects
            // like Tools of Trade create card selections during Pump, AFTER the initial HasPending check)
            if (_cardSelector.HasPending && _cardSelector.PendingOptions != null)
            {
                goto checkCardSelect;  // Jump back to card_select handling
            }

            if (CombatManager.Instance.IsInProgress
                && CombatManager.Instance.IsPlayPhase
                && !CombatHasAliveEnemies())
            {
                if (TryResolveCombatWithNoAliveEnemies(player))
                    return DetectPostCombatState(player, combatRoom);
            }

            if (CombatManager.Instance.IsInProgress && CombatManager.Instance.IsPlayPhase)
            {
                return CombatPlayState(player);
            }
            if (!CombatManager.Instance.IsInProgress || (player.Creature != null && player.Creature.IsDead))
            {
                return DetectPostCombatState(player, combatRoom);
            }
            // Fallback: brief wait
            for (int i = 0; i < 20; i++)
            {
                _syncCtx.Pump();
                Thread.Sleep(5);
                if (CombatManager.Instance.IsPlayPhase) return CombatPlayState(player);
                if (!CombatManager.Instance.IsInProgress) return DetectPostCombatState(player, combatRoom);
            }
            return CombatPlayState(player);
        }

        // Event room
        if (room is EventRoom eventRoom)
        {
            return EventChoiceState(eventRoom);
        }

        // Rest site
        if (room is RestSiteRoom restRoom)
        {
            return RestSiteState(restRoom);
        }

        // Merchant/Shop
        if (room is MerchantRoom merchantRoom)
        {
            return ShopState(merchantRoom, player);
        }

        // Treasure room
        if (room is TreasureRoom treasureRoom)
        {
            return TreasureState(treasureRoom);
        }

        // Fallback
        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "unknown",
            ["context"] = RunContext(),
            ["room_type"] = room?.GetType().Name,
            ["message"] = "Unknown room type or state",
        };
    }

    private Dictionary<string, object?> MapSelectState()
    {
        var map = _runState?.Map;
        if (map == null)
        {
            Log("Map is null, generating...");
            try
            {
                RunManager.Instance.GenerateMap().GetAwaiter().GetResult();
                _syncCtx.Pump();
                map = _runState?.Map;
            }
            catch (Exception ex)
            {
                Log($"GenerateMap failed: {ex.Message}");
            }
            if (map == null)
                return Error("No map available");
        }
        var currentCoord = _runState!.CurrentMapCoord;
        var player = _runState.Players[0];

        List<Dictionary<string, object?>> choices;
        if (currentCoord.HasValue)
        {
            var currentPoint = map.GetPoint(currentCoord.Value);
            if (currentPoint == null)
            {
                Log($"GetPoint returned null for coord ({currentCoord.Value.col},{currentCoord.Value.row}), falling back to start");
                // Current coord is invalid (stale after forced room transition); treat as no position
                choices = new List<Dictionary<string, object?>>();
                var sp = map.StartingMapPoint;
                if (sp?.Children != null)
                {
                    foreach (var child in sp.Children)
                    {
                        choices.Add(new Dictionary<string, object?>
                        {
                            ["col"] = (int)child.coord.col,
                            ["row"] = (int)child.coord.row,
                            ["type"] = child.PointType.ToString(),
                        });
                    }
                }
            }
            else
            {
                choices = CurrentMapChoicePoints(player)
                    .Select(choice =>
                    {
                        var item = new Dictionary<string, object?>
                        {
                            ["col"] = (int)choice.Point.coord.col,
                            ["row"] = (int)choice.Point.coord.row,
                            ["type"] = choice.Point.PointType.ToString(),
                        };
                        if (choice.RequiresWingedBoots)
                            item["requires_winged_boots"] = true;
                        return item;
                    })
                    .ToList();
            }
        }
        else
        {
            // Starting point — pick from starting row
            var startPoint = map.StartingMapPoint;
            choices = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["col"] = (int)startPoint.coord.col,
                    ["row"] = (int)startPoint.coord.row,
                    ["type"] = startPoint.PointType.ToString(),
                }
            };
            // Add all children of start point as well since we can travel to them
            if (startPoint.Children != null)
            {
                foreach (var child in startPoint.Children)
                {
                    choices.Add(new Dictionary<string, object?>
                    {
                        ["col"] = (int)child.coord.col,
                        ["row"] = (int)child.coord.row,
                        ["type"] = child.PointType.ToString(),
                    });
                }
            }
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "map_select",
            ["context"] = RunContext(),
            ["choices"] = choices,
            ["player"] = PlayerSummary(player),
            ["act"] = _runState.CurrentActIndex + 1,
            ["act_name"] = _loc.Act(_runState.Act?.Id.Entry ?? "OVERGROWTH"),
            ["floor"] = _runState.ActFloor,
        };
    }

    private int CardRuntimeId(CardModel card)
    {
        if (!_cardRuntimeIds.TryGetValue(card, out var id))
        {
            id = _nextCardRuntimeId++;
            _cardRuntimeIds[card] = id;
        }
        return id;
    }

    private int CreatureRuntimeId(object creature)
    {
        if (!_creatureRuntimeIds.TryGetValue(creature, out var id))
        {
            id = _nextCreatureRuntimeId++;
            _creatureRuntimeIds[creature] = id;
        }
        return id;
    }

    private Dictionary<string, object?> CombatPlayState(Player player)
    {
        var pcs = player.PlayerCombatState;
        var combatState = CombatManager.Instance.DebugOnlyGetState();

        // Track last known HP for accurate game_over reporting (BUG-005)
        if (player.Creature != null && player.Creature.CurrentHp > 0)
            _lastKnownHp = player.Creature.CurrentHp;

        var hand = pcs?.Hand?.Cards?.Select((c, i) =>
        {
            var stats = ExtractCardStats(c, player, applyCombatModifiers: true);

            // Use CurrentStarCost (combat-modified) for UI/can_play; BaseStarCost ignores temporary reductions.
            var starCost = c.CurrentStarCost;
            var cardInfo = new Dictionary<string, object?>
            {
                ["index"] = i,
                ["instance_id"] = CardRuntimeId(c),
                ["id"] = c.Id.ToString(),
                ["name"] = _loc.Card(c.Id.Entry),
                ["cost"] = GetEnergyCostDisplay(c),
                ["type"] = c.Type.ToString(),
                ["rarity"] = c.Rarity.ToString(),
                ["can_play"] = c.Type != CardType.None && c.CanPlay(out _, out _),
                ["target_type"] = c.TargetType.ToString(),
                ["stats"] = stats.Count > 0 ? stats : null,
                ["description"] = CardDescription(c, stats, includeCombatText: true),
            };
            AddCardVars(cardInfo, c, includePreviewStats: true);
            AddEnergyCostDetails(cardInfo, c, includeCurrentXValue: true);
            AddStarCostDetails(cardInfo, c);
            if (starCost > 0)
            {
                // BUG-007: Override can_play for star-cost cards when player lacks stars
                if (pcs != null && pcs.Stars < starCost)
                    cardInfo["can_play"] = false;
            }
            var kws = c.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToList();
            if (kws?.Count > 0) cardInfo["keywords"] = kws;
            AddCardEnhancements(cardInfo, c);
            AddCardHoverTips(cardInfo, c);
            return cardInfo;
        }).ToList() ?? new();

        var playerCreatures = combatState?.PlayerCreatures?.ToList();

        var allEnemies = combatState?.Enemies?
            .Where(e => e != null)
            .ToList() ?? new();

        var enemies = allEnemies
            .Where(e => e.IsAlive)
            .Select((e, i) =>
            {
                // Extract detailed intent info
                var intents = new List<Dictionary<string, object?>>();
                try
                {
                    if (e.Monster?.NextMove?.Intents != null)
                    {
                        foreach (var intent in e.Monster.NextMove.Intents)
                        {
                            var intentInfo = new Dictionary<string, object?>
                            {
                                ["type"] = intent.IntentType.ToString(),
                            };
                            // Get damage for attack intents
                            if (intent is MegaCrit.Sts2.Core.MonsterMoves.Intents.AttackIntent atk && playerCreatures != null)
                            {
                                try
                                {
                                    var totalDamage = atk.GetTotalDamage(playerCreatures, e);
                                    if (atk.Repeats > 1)
                                    {
                                        intentInfo["damage"] = totalDamage / atk.Repeats;
                                        intentInfo["hits"] = atk.Repeats;
                                        intentInfo["total_damage"] = totalDamage;
                                    }
                                    else
                                    {
                                        intentInfo["damage"] = totalDamage;
                                    }
                                }
                                catch { }
                            }
                            intents.Add(intentInfo);
                        }
                    }
                }
                catch { }

                // Enemy powers
                var enemyName = MonsterDisplayName(e.Monster, e);
                var ePowers = e.Powers?.Select(PowerInfo).ToList();

                var enemyInfo = new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["instance_id"] = CreatureRuntimeId(e),
                    ["combat_index"] = allEnemies.IndexOf(e),
                    ["name"] = enemyName,
                    ["hp"] = e.CurrentHp,
                    ["max_hp"] = e.MaxHp,
                    ["block"] = e.Block,
                    ["alive"] = true,
                    ["targetable"] = true,
                    ["intents"] = intents.Count > 0 ? intents : null,
                    ["intends_attack"] = e.Monster?.IntendsToAttack ?? false,
                    ["powers"] = ePowers?.Count > 0 ? ePowers : null,
                };

                var monsterEntry = e.Monster?.Id.Entry ?? "UNKNOWN";
                var moveEntry = MoveEntry(e.Monster?.NextMove);
                if (!string.IsNullOrWhiteSpace(moveEntry))
                {
                    enemyInfo["move_id"] = moveEntry;
                    enemyInfo["move_name"] = _loc.MonsterMove(monsterEntry, moveEntry);
                }

                return enemyInfo;
            }).ToList();

        var inactiveEnemies = allEnemies
            .Select((e, combatIndex) => new { Enemy = e, CombatIndex = combatIndex })
            .Where(entry => !entry.Enemy.IsAlive)
            .Select(entry =>
            {
                var e = entry.Enemy;
                var ePowers = e.Powers?.Select(PowerInfo).ToList();

                var enemyInfo = new Dictionary<string, object?>
                {
                    ["instance_id"] = CreatureRuntimeId(e),
                    ["combat_index"] = entry.CombatIndex,
                    ["name"] = MonsterDisplayName(e.Monster, e),
                    ["hp"] = e.CurrentHp,
                    ["max_hp"] = e.MaxHp,
                    ["block"] = e.Block,
                    ["alive"] = false,
                    ["targetable"] = false,
                    ["powers"] = ePowers?.Count > 0 ? ePowers : null,
                };

                var monsterEntry = e.Monster?.Id.Entry ?? "UNKNOWN";
                var moveEntry = MoveEntry(e.Monster?.NextMove);
                if (!string.IsNullOrWhiteSpace(moveEntry))
                {
                    enemyInfo["move_id"] = moveEntry;
                    enemyInfo["move_name"] = _loc.MonsterMove(monsterEntry, moveEntry);
                }

                return enemyInfo;
            }).ToList();

        // Player powers/buffs
        var playerPowers = player.Creature?.Powers?.Select(PowerInfo).ToList();

        var drawPile = CombatPileInfo(pcs?.DrawPile?.Cards, player);
        var discardPile = CombatPileInfo(pcs?.DiscardPile?.Cards, player);
        var exhaustPile = CombatPileInfo(pcs?.ExhaustPile?.Cards, player);

        var result = new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "combat_play",
            ["context"] = RunContext(),
            ["round"] = combatState?.RoundNumber ?? 0,
            ["energy"] = pcs?.Energy ?? 0,
            ["max_energy"] = pcs?.MaxEnergy ?? 0,
            ["hand"] = hand,
            ["enemies"] = enemies,
            ["inactive_enemies"] = inactiveEnemies.Count > 0 ? inactiveEnemies : null,
            ["player"] = PlayerSummary(player),
            ["player_powers"] = playerPowers?.Count > 0 ? playerPowers : null,
            ["draw_pile_count"] = pcs?.DrawPile?.Cards?.Count ?? 0,
            ["discard_pile_count"] = pcs?.DiscardPile?.Cards?.Count ?? 0,
            ["exhaust_pile_count"] = pcs?.ExhaustPile?.Cards?.Count ?? 0,
            ["draw_pile"] = drawPile,
            ["discard_pile"] = discardPile,
            ["exhaust_pile"] = exhaustPile,
        };

        // Character-specific mechanics
        try
        {
            // Defect: Orbs
            var orbQueue = pcs?.OrbQueue;
            if (orbQueue?.Orbs?.Count > 0)
            {
                var orbCount = orbQueue.Orbs.Count;
                result["orbs"] = orbQueue.Orbs.Select((orb, i) =>
                {
                    var isRightmost = i == 0;
                    var isLeftmost = i == orbCount - 1;
                    var positionLabel = isRightmost
                        ? "rightmost"
                        : isLeftmost
                            ? "leftmost"
                            : $"{i + 1} from right";
                    return new Dictionary<string, object?>
                    {
                        ["index"] = i,
                        ["name"] = _loc.Bilingual("orbs", orb.Id.Entry + ".title"),
                        ["type"] = orb.GetType().Name.Replace("Orb", ""),
                        ["passive"] = (int)orb.PassiveVal,
                        ["evoke"] = (int)orb.EvokeVal,
                        ["evoke_order"] = i + 1,
                        ["is_next_to_evoke"] = isRightmost,
                        ["is_rightmost"] = isRightmost,
                        ["is_leftmost"] = isLeftmost,
                        ["position_from_right"] = i,
                        ["position_from_left"] = orbCount - 1 - i,
                        ["position_label"] = positionLabel,
                    };
                }).ToList();
                result["orb_slots"] = orbQueue.Capacity;
            }

            // Stars can matter when off-color Regent cards enter another character's deck.
            var shouldExportStars = player.Character?.Id.Entry == "REGENT";
            if (pcs != null && pcs.Stars > 0)
                shouldExportStars = true;
            try
            {
                if (pcs?.Hand?.Cards?.Any(c => c != null && TryGetCurrentStarCost(c) > 0) == true)
                    shouldExportStars = true;
            }
            catch { }
            if (pcs != null && pcs.Stars >= 0 && shouldExportStars)
            {
                result["stars"] = pcs.Stars;
            }

            // Necrobinder: Osty (minion)
            var osty = player.Osty;
            if (osty != null)
            {
                result["osty"] = new Dictionary<string, object?>
                {
                    ["name"] = _loc.Monster(osty.Monster?.Id.Entry ?? "OSTY"),
                    ["hp"] = osty.CurrentHp,
                    ["max_hp"] = osty.MaxHp,
                    ["block"] = osty.Block,
                    ["alive"] = osty.IsAlive,
                };
            }
            else if (player.Character?.Id.Entry == "NECROBINDER")
            {
                result["osty"] = new Dictionary<string, object?> { ["alive"] = false };
            }
        }
        catch (Exception ex)
        {
            Log($"Character-specific data: {ex.Message}");
        }

        return result;
    }

    private Dictionary<string, object?> CombatSelectionContext(Player player)
    {
        var combat = CombatPlayState(player);
        combat.Remove("type");
        combat.Remove("decision");
        combat.Remove("context");
        combat.Remove("player");
        return combat;
    }

    private string? CardSelectionPrompt(CardModel? sourceCard)
    {
        var pendingPrompt = CleanResolvedEngineText(_cardSelector.PendingPrompt);
        if (pendingPrompt != null)
            return pendingPrompt;

        if (sourceCard == null)
            return null;

        var key = sourceCard.Id.Entry + ".selectionScreenPrompt";
        var raw = _loc.Bilingual("cards", key);
        var interpolated = InterpolateDynamicVars(raw, ExportDynamicVars(sourceCard)) ?? raw;
        return CleanResolvedEngineText(interpolated);
    }

    private PowerModel? InferPendingCardSelectionSourcePower(Player player)
    {
        var powers = player.Creature?.Powers;
        if (powers == null)
            return null;

        var candidates = powers
            .Where(power => CardSelectionPrompt(power) != null)
            .Take(2)
            .ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private string? CardSelectionPrompt(PowerModel? sourcePower)
    {
        if (sourcePower == null)
            return null;

        string? raw = null;
        foreach (var key in PowerLocKeyCandidates(sourcePower.Id.Entry, "selectionScreenPrompt"))
        {
            var candidate = _loc.Bilingual("powers", key);
            if (candidate != key)
            {
                raw = candidate;
                break;
            }
        }
        if (raw == null)
            return null;

        var interpolated = InterpolateDynamicVars(raw, PowerDescriptionVars(sourcePower)) ?? raw;
        return CleanResolvedEngineText(interpolated);
    }

    private static IEnumerable<string> PowerLocKeyCandidates(string entry, string suffix)
    {
        yield return entry + "." + suffix;
        if (!entry.EndsWith("_POWER", StringComparison.Ordinal))
            yield return entry + "_POWER." + suffix;
    }

    private static string? CardSelectionPrompt(Dictionary<string, object?>? sourceOption)
    {
        if (sourceOption == null)
            return null;

        var title = CleanResolvedEngineText(sourceOption.GetValueOrDefault("title")?.ToString())
                    ?? CleanResolvedEngineText(sourceOption.GetValueOrDefault("name")?.ToString());
        var description = CleanResolvedEngineText(sourceOption.GetValueOrDefault("description")?.ToString());
        if (title != null && description != null)
            return $"{title}: {description}";
        return title ?? description;
    }

    private Dictionary<string, object?> CardSummary(
        CardModel card,
        bool applyCombatModifiers = false,
        bool includeTargetRows = true)
    {
        var stats = ExtractCardStats(
            card,
            _runState?.Players.FirstOrDefault(),
            applyCombatModifiers: applyCombatModifiers,
            includeTargetRows: includeTargetRows);
        var summary = new Dictionary<string, object?>
        {
            ["id"] = card.Id.ToString(),
            ["name"] = _loc.Card(card.Id.Entry),
            ["cost"] = GetEnergyCostDisplay(card),
            ["type"] = card.Type.ToString(),
            ["upgraded"] = card.IsUpgraded,
            ["description"] = CardDescription(card, stats, includeCombatText: applyCombatModifiers),
        };
        if (stats.Count > 0)
            summary["stats"] = stats;
        AddCardVars(summary, card, includePreviewStats: applyCombatModifiers);
        AddEnergyCostDetails(summary, card, includeCurrentXValue: applyCombatModifiers);
        AddStarCostDetails(summary, card);
        AddCardHoverTips(summary, card);
        return summary;
    }

    private static bool ShouldExportCombatPreviewState(CardModel card)
    {
        try
        {
            return CombatManager.Instance.IsInProgress
                   && card.CombatState != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool ShouldExportCombatCardState(CardModel card)
    {
        try
        {
            return CombatManager.Instance.IsInProgress
                   && card.CombatState != null
                   && card.Pile?.Type == PileType.Hand;
        }
        catch
        {
            return false;
        }
    }

    private static bool CombatHasAliveEnemies()
    {
        try
        {
            return CombatManager.Instance.DebugOnlyGetState()?.Enemies?
                .Any(enemy => enemy != null && enemy.IsAlive) == true;
        }
        catch
        {
            return true;
        }
    }

    private bool TryResolveCombatWithNoAliveEnemies(Player player)
    {
        if (!CombatManager.Instance.IsInProgress || !CombatManager.Instance.IsPlayPhase)
            return false;

        Log("Combat has no alive enemies during play phase; resolving engine combat cleanup");
        _turnStarted.Reset();
        _combatEnded.Reset();
        YieldPatches.SuppressYield = true;
        try
        {
            PlayerCmd.EndTurn(player, canBackOut: false);
            for (int i = 0; i < 100; i++)
            {
                _syncCtx.Pump();
                WaitForActionExecutor();
                if (!CombatManager.Instance.IsInProgress || _combatEnded.IsSet)
                    break;
                Thread.Sleep(5);
            }
        }
        catch (Exception ex)
        {
            Log($"Resolve no-enemy combat cleanup failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            YieldPatches.SuppressYield = false;
        }

        _syncCtx.Pump();
        WaitForActionExecutor();
        return !CombatManager.Instance.IsInProgress;
    }

    private Dictionary<string, object?> DetectPostCombatState(Player player, CombatRoom combatRoom)
    {
        Log($"Post-combat: RoomType={combatRoom.RoomType}, IsPreFinished={combatRoom.IsPreFinished}");
        _syncCtx.Pump();

        if (_pendingEventResult != null)
            return EventResultState(_pendingEventResult);

        if (combatRoom.ParentEventId != null && combatRoom.ShouldResumeParentEventAfterCombat)
            return ResumeParentEventAfterCombat(player, combatRoom);

        // Generate rewards manually instead of using TestMode auto-accept
        if (_pendingRewards == null && !_rewardsProcessed)
        {
            _goldBeforeCombat = player.Gold;
            try
            {
                var rewardsSet = new RewardsSet(player).WithRewardsFromRoom(combatRoom);
                var rewards = rewardsSet.GenerateWithoutOffering().GetAwaiter().GetResult();
                _syncCtx.Pump();

                _pendingRewards = rewards;
            }
            catch (Exception ex) { Log($"Generate rewards: {ex.Message}"); }
        }

        // Resolve pending rewards before returning to the map or next act.
        if (_pendingCardReward != null)
            return CardRewardState(player, combatRoom);

        if (_pendingRewards != null)
        {
            var claimableRewards = _pendingRewards
                .Select((reward, i) => CombatRewardInfo(reward, i, player))
                .ToList();

            if (claimableRewards.Count > 0)
                return CombatRewardState(player, claimableRewards);

            var nextCardReward = _pendingRewards.OfType<CardReward>().FirstOrDefault();
            if (nextCardReward != null)
            {
                _pendingCardReward = nextCardReward;
                return CardRewardState(player, combatRoom);
            }
        }

        _pendingCardReward = null;
        _pendingRewards = null;
        _rewardsProcessed = true;

        // Boss → next act
        if (combatRoom.RoomType == RoomType.Boss)
        {
            Log("Boss defeated, entering next act");
            try
            {
                RunManager.Instance.EnterNextAct().GetAwaiter().GetResult();
                _syncCtx.Pump();
                WaitForActionExecutor();
            }
            catch (Exception ex) { Log($"EnterNextAct: {ex.Message}"); }
            return DetectDecisionPoint();
        }

        // Normal → go to map
        ForceToMap();
        return MapSelectState();
    }

    private Dictionary<string, object?> ResumeParentEventAfterCombat(Player player, CombatRoom combatRoom)
    {
        var localEvent = RunManager.Instance.EventSynchronizer?.GetLocalEvent();
        if (localEvent == null)
        {
            return Error($"Combat room expects parent event {combatRoom.ParentEventId}, but no local event is active");
        }

        if (combatRoom.ParentEventId != null && localEvent.Id != combatRoom.ParentEventId)
        {
            return Error($"Combat room parent event {combatRoom.ParentEventId} does not match active event {localEvent.Id}");
        }

        try
        {
            localEvent.Resume(combatRoom).GetAwaiter().GetResult();
            _syncCtx.Pump();
            WaitForPendingEventOptionTask();
            WaitForActionExecutor();
        }
        catch (Exception ex)
        {
            return ErrorWithTrace($"Resuming parent event {localEvent.Id} after combat failed", ex);
        }

        if (YieldPatches.ActiveCrystalSphereMinigame != null
            || _cardSelector.HasPending
            || _cardSelector.HasPendingReward
            || _pendingBundles != null)
        {
            return DetectDecisionPoint();
        }

        if (localEvent.IsFinished)
        {
            _pendingEventChoiceAfterCombat = null;
            _pendingEventResult = localEvent;
            return EventResultState(localEvent);
        }

        _pendingEventChoiceAfterCombat = localEvent;
        return EventChoiceState(new EventRoom(localEvent.CanonicalInstance));
    }

    private Dictionary<string, object?> CombatRewardState(
        Player player,
        List<Dictionary<string, object?>> rewards)
    {
        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "combat_reward",
            ["context"] = RunContext(),
            ["rewards"] = rewards,
            ["gold_earned"] = player.Gold - _goldBeforeCombat,
            ["player"] = PlayerSummary(player),
        };
    }

    private Dictionary<string, object?> CombatRewardInfo(Reward reward, int index, Player? player = null)
    {
        var kind = CombatRewardKind(reward);
        var info = new Dictionary<string, object?>
        {
            ["index"] = index,
            ["kind"] = kind,
            ["type_name"] = reward.GetType().Name,
        };
        var canSkip = CanSkipCombatReward(reward);
        info["can_skip"] = canSkip;
        info["can_claim"] = true;

        if (kind == "gold")
        {
            var amount = TryGetIntMember(reward, "Amount", "Gold", "GoldAmount", "Value");
            if (amount != null)
                info["amount"] = amount.Value;
        }
        else if (kind == "relic")
        {
            var relic = TryGetModelMember<RelicModel>(reward, "Relic", "RelicModel", "Model");
            if (relic != null)
            {
                foreach (var kv in RelicInfo(relic, index))
                    info[kv.Key] = kv.Value;
            }
        }
        else if (kind == "potion")
        {
            var potion = TryGetModelMember<PotionModel>(reward, "Potion", "PotionModel", "Model");
            if (potion != null)
            {
                foreach (var kv in PotionInfo(potion, index))
                    info[kv.Key] = kv.Value;
            }
            if (player != null && !HasOpenPotionSlot(player))
            {
                info["can_claim"] = false;
                info["blocked_reason"] = "potion_slots_full";
            }
        }
        else if (kind == "card")
        {
            var card = TryGetModelMember<CardModel>(reward, "Card", "CardModel", "Model");
            if (card != null)
            {
                foreach (var kv in SingleCardRewardInfo(card, index))
                    info[kv.Key] = kv.Value;
            }
        }
        else if (kind == "card_reward" && reward is CardReward cardReward)
        {
            info["name"] = "Card Reward";
            info["count"] = cardReward.Cards.Count();
            info["can_skip"] = cardReward.CanSkip;
        }
        return info;
    }

    private static string CombatRewardKind(Reward reward)
    {
        if (reward is GoldReward) return "gold";
        if (reward is MegaCrit.Sts2.Core.Rewards.RelicReward) return "relic";
        if (reward is MegaCrit.Sts2.Core.Rewards.PotionReward) return "potion";
        if (reward is CardReward) return "card_reward";
        if (TryGetModelMember<CardModel>(reward, "Card", "CardModel", "Model") != null) return "card";
        return reward.GetType().Name;
    }

    private Dictionary<string, object?> SingleCardRewardInfo(CardModel card, int? index = null)
    {
        var stats = ExtractCardStats(card, _runState?.Players[0]);
        var keywords = card.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToList();
        var info = new Dictionary<string, object?>
        {
            ["id"] = card.Id.ToString(),
            ["name"] = _loc.Card(card.Id.Entry),
            ["cost"] = GetEnergyCostDisplay(card),
            ["type"] = card.Type.ToString(),
            ["rarity"] = card.Rarity.ToString(),
            ["upgraded"] = card.IsUpgraded,
            ["description"] = CardDescription(card, stats),
            ["stats"] = stats.Count > 0 ? stats : null,
            ["keywords"] = keywords?.Count > 0 ? keywords : null,
            ["after_upgrade"] = GetUpgradedInfo(card, _runState?.Players[0]),
        };
        if (index.HasValue)
            info["index"] = index.Value;
        AddCardVars(info, card);
        AddEnergyCostDetails(info, card);
        AddStarCostDetails(info, card);
        AddCardEnhancements(info, card);
        return info;
    }

    private Dictionary<string, object?> CardRewardState(Player player, CombatRoom? combatRoom)
    {
        if (_pendingCardReward == null)
            return DetectPostCombatState(player, combatRoom ?? (_runState?.CurrentRoom as CombatRoom)!);

        var cards = _pendingCardReward.Cards.Select((c, i) =>
        {
            var stats = ExtractCardStats(c, player);
            var crkws = c.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToList();
            var cardInfo = new Dictionary<string, object?>
            {
                ["index"] = i,
                ["id"] = c.Id.ToString(),
                ["name"] = _loc.Card(c.Id.Entry),
                ["cost"] = GetEnergyCostDisplay(c),
                ["type"] = c.Type.ToString(),
                ["rarity"] = c.Rarity.ToString(),
                ["upgraded"] = c.IsUpgraded,
                ["description"] = CardDescription(c, stats),
                ["stats"] = stats.Count > 0 ? stats : null,
                ["keywords"] = crkws?.Count > 0 ? crkws : null,
                ["after_upgrade"] = GetUpgradedInfo(c, player),
            };
            AddCardVars(cardInfo, c);
            AddEnergyCostDetails(cardInfo, c);
            AddStarCostDetails(cardInfo, c);
            AddCardEnhancements(cardInfo, c);
            AddCardHoverTips(cardInfo, c);
            return cardInfo;
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "card_reward",
            ["context"] = RunContext(),
            ["cards"] = cards,
            ["can_skip"] = _pendingCardReward.CanSkip,
            ["gold_earned"] = _runState!.Players[0].Gold - _goldBeforeCombat,
            ["player"] = PlayerSummary(_runState!.Players[0]),
        };
    }

    private void ForceToMap(bool skipTerminalProceed = false)
    {
        if (!skipTerminalProceed)
        {
            try
            {
                RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult();
                _syncCtx.Pump();
            }
            catch { }
        }

        if (_runState?.CurrentRoom is not MapRoom)
        {
            try { RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult(); _syncCtx.Pump(); }
            catch (Exception ex) { Log($"ForceToMap: {ex.Message}"); }
        }
    }

    private Dictionary<string, object?> EventResultState(EventModel localEvent)
    {
        var eventEntry = localEvent.Id?.Entry ?? localEvent.GetType().Name.ToUpperInvariant();
        var eventVars = ExportEventVars(eventEntry, localEvent);
        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "event_result",
            ["context"] = RunContext(),
            ["event_id"] = eventEntry,
            ["event_name"] = EventDisplayName(eventEntry),
            ["description"] = EventDescription(localEvent, eventVars),
            ["vars"] = eventVars?.Count > 0 ? eventVars : null,
            ["options"] = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["index"] = 0,
                    ["title"] = "Proceed",
                    ["is_locked"] = false,
                }
            },
            ["can_proceed"] = true,
            ["player"] = PlayerSummary(_runState!.Players[0]),
        };
    }

    private static string EventDisplayName(string eventEntry)
    {
        var eventName = _loc.Bilingual("ancients", eventEntry + ".title");
        if (eventName == eventEntry + ".title")
            eventName = _loc.Event(eventEntry);
        return eventName;
    }

    private string? EventDescription(EventModel localEvent, Dictionary<string, object?>? eventVars)
    {
        if (localEvent.Description == null)
            return null;

        var d = ResolveLocString(localEvent.Description, eventVars)
                ?? _loc.Bilingual(localEvent.Description.LocTable, localEvent.Description.LocEntryKey);
        d = CleanResolvedEngineText(InterpolateDynamicVars(d, eventVars) ?? d);
        return d != localEvent.Description.LocEntryKey ? d : null;
    }

    private Dictionary<string, object?> EventChoiceState(EventRoom eventRoom)
    {
        var localEvent = RunManager.Instance.EventSynchronizer?.GetLocalEvent();
        _syncCtx.Pump();

        if (localEvent is FakeMerchant fakeMerchant && fakeMerchant.Inventory != null && !fakeMerchant.StartedFight)
            return FakeMerchantShopState(fakeMerchant);

        // If an event option leaves us on an interactive page, keep exposing it.
        // Same-count options are not proof of a stuck event.
        if (_eventOptionChosen && localEvent != null && !localEvent.IsFinished)
        {
            _eventOptionChosen = false;
        }

        // If event is finished, proceed to map
        if (localEvent == null || localEvent.IsFinished)
        {
            _pendingEventChoiceAfterCombat = null;
            if (eventRoom.IsVictoryRoom)
            {
                CompleteVictoryRoomTransition();
                if (RunManager.Instance.IsGameOver)
                    return GameOverState(true);
                return Error("Victory event finished without completing the run");
            }

            Log($"Event {localEvent?.GetType().Name ?? "null"} finished, proceeding");
            try
            {
                RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult();
                _syncCtx.Pump();
            }
            catch { }
            // Force to map if still in event room
            if (_runState?.CurrentRoom is EventRoom)
            {
                try { RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult(); _syncCtx.Pump(); }
                catch { }
            }
            return _runState?.CurrentRoom is MapRoom ? MapSelectState() : DetectDecisionPoint();
        }

        var eventEntry = localEvent.Id?.Entry ?? localEvent.GetType().Name.ToUpperInvariant();
        var currentOptions = localEvent.CurrentOptions;
        if (currentOptions == null || currentOptions.Count == 0)
        {
            Log($"Event {localEvent.GetType().Name} has no options and is not finished");
            var player = _runState!.Players[0];
            return new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "event_blocked",
                ["context"] = RunContext(),
                ["event_id"] = eventEntry,
                ["event_name"] = _loc.Bilingual("events", eventEntry + ".title"),
                ["message"] = "Event has no current options but is not finished; no headless default was applied",
                ["player"] = PlayerSummary(player),
            };
        }

        var options = currentOptions
            .Select((opt, i) =>
            {
                var engineTitle = EventOptionLocString(localEvent, opt.TextKey, "GetOptionTitle");
                var engineDescription = EventOptionLocString(localEvent, opt.TextKey, "GetOptionDescription");

                // Try to resolve title via loc tables
                string? title = ResolveLocString(engineTitle ?? opt.Title);
                var titleFromEngine = title != null;
                if (opt.Title != null)
                {
                    var t = _loc.Bilingual(opt.Title.LocTable, opt.Title.LocEntryKey);
                    // Check if we actually found a translation (not just the key echoed back)
                    if (title == null && t != opt.Title.LocEntryKey)
                        title = t;
                }
                // Fallback: try to extract option ID from the key and look up as relic/card/potion
                if (title == null && opt.TextKey != null)
                {
                    // TextKey like "NEOW.pages.INITIAL.options.STONE_HUMIDIFIER" → extract "STONE_HUMIDIFIER"
                    var parts = opt.TextKey.Split('.');
                    var optionId = parts.Length > 0 ? parts[^1] : opt.TextKey;
                    // Try relic, then card, then just use the optionId
                    var relic = _loc.Relic(optionId);
                    if (relic != optionId + ".title")
                        title = relic;
                    else
                    {
                        var card = _loc.Card(optionId);
                        if (card != optionId + ".title")
                            title = card;
                        else
                            title = optionId.Replace("_", " ");
                    }
                }
                title ??= $"option_{i}";

                // Description: try loc table first
                string? optDesc = ResolveLocString(engineDescription ?? opt.Description);
                var descriptionFromEngine = optDesc != null;
                if (opt.Description != null && !string.IsNullOrEmpty(opt.Description.LocEntryKey))
                {
                    var d = _loc.Bilingual(opt.Description.LocTable, opt.Description.LocEntryKey);
                    if (optDesc == null && d != opt.Description.LocEntryKey)
                        optDesc = d;
                }
                // Fallback: try relic/card description
                if (optDesc == null && opt.TextKey != null)
                {
                    var parts = opt.TextKey.Split('.');
                    var optionId = parts.Length > 0 ? parts[^1] : opt.TextKey;
                    var rd = _loc.Bilingual("relics", optionId + ".description");
                    if (rd != optionId + ".description")
                        optDesc = rd;
                }

                // Extract vars from the event and any option-specific engine state.
                var optVars = ExportEventOptionVars(eventEntry, localEvent, opt, i);
                // Also try relic vars (for Neow options)
                if (opt.TextKey != null)
                {
                    try
                    {
                        var parts = opt.TextKey.Split('.');
                        var optionId = parts.Length > 0 ? parts[^1] : opt.TextKey;
                        var relicModel = ModelDb.GetById<RelicModel>(new ModelId("RELIC", optionId));
                        if (relicModel != null)
                        {
                            optVars ??= new Dictionary<string, object?>();
                            var mutable = relicModel.ToMutable();
                            foreach (var dv in mutable.DynamicVars.Values)
                                optVars[dv.Name] = (int)dv.BaseValue;
                        }
                    }
                    catch { }
                }
                if (optVars != null)
                    AddEventOptionHoverTipNameVars(optVars, opt);

                if (opt.Title != null)
                {
                    var formattedTitle = ResolveLocString(engineTitle ?? opt.Title, optVars);
                    if (formattedTitle != null)
                    {
                        title = formattedTitle;
                        titleFromEngine = true;
                    }
                }
                if (opt.Description != null)
                {
                    var formattedDescription = ResolveLocString(engineDescription ?? opt.Description, optVars);
                    if (formattedDescription != null)
                    {
                        optDesc = formattedDescription;
                        descriptionFromEngine = true;
                    }
                }
                if (!descriptionFromEngine && opt.TextKey != null)
                {
                    var parts = opt.TextKey.Split('.');
                    var optionId = parts.Length > 0 ? parts[^1] : opt.TextKey;
                    var relicDescription =
                        EngineLocStringText(new LocString("relics", optionId + ".description"), optVars)
                        ?? RelicOptionDescription(optionId);
                    if (relicDescription != null)
                    {
                        optDesc = relicDescription;
                        descriptionFromEngine = true;
                    }
                }
                if (!titleFromEngine)
                    title = InterpolateDynamicVars(title, optVars) ?? title;
                if (!descriptionFromEngine)
                    optDesc = CleanResolvedEngineText(InterpolateDynamicVars(optDesc, optVars) ?? optDesc);

                var ancientDialogue = ResolveAncientDialogueOption(eventEntry, opt.TextKey);
                if (ancientDialogue != null)
                {
                    if (IsUninformativeEventOptionTitle(title, opt.TextKey, i) && !string.IsNullOrWhiteSpace(ancientDialogue.Value.title))
                        title = ancientDialogue.Value.title;
                    if (string.IsNullOrWhiteSpace(optDesc) && !string.IsNullOrWhiteSpace(ancientDialogue.Value.description))
                        optDesc = ancientDialogue.Value.description;
                }

                var exportedOption = new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["title"] = title,
                    ["description"] = optDesc,
                    ["text_key"] = opt.TextKey,
                    ["is_locked"] = opt.IsLocked,
                    ["vars"] = optVars?.Count > 0 ? optVars : null,
                };

                var relicTrade = BuildRelicTradePreview(eventEntry, localEvent, opt, i);
                if (relicTrade != null)
                    exportedOption["relic_trade"] = relicTrade;
                var hoverTips = EventOptionHoverTips(opt, relicTrade);
                if (hoverTips != null)
                    exportedOption["hover_tips"] = hoverTips;

                return exportedOption;
            }).ToList();

        // Resolve event name — try ancients table first (for Neow), then events
        var eventName = _loc.Bilingual("ancients", eventEntry + ".title");
        if (eventName == eventEntry + ".title")
            eventName = _loc.Event(eventEntry);

        // Resolve event description, suppress if key not found
        var eventVars = ExportEventVars(eventEntry, localEvent);
        string? eventDesc = null;
        if (localEvent.Description != null)
        {
            var d = ResolveLocString(localEvent.Description, eventVars)
                    ?? _loc.Bilingual(localEvent.Description.LocTable, localEvent.Description.LocEntryKey);
            d = CleanResolvedEngineText(InterpolateDynamicVars(d, eventVars) ?? d);
            if (d != localEvent.Description.LocEntryKey)
                eventDesc = d;
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "event_choice",
            ["context"] = RunContext(),
            ["event_name"] = eventName,
            ["description"] = eventDesc,
            ["vars"] = eventVars?.Count > 0 ? eventVars : null,
            ["options"] = options,
            ["can_leave"] = false,
            ["player"] = PlayerSummary(_runState!.Players[0]),
        };
    }

    private bool TryGetFakeMerchant(out FakeMerchant fakeMerchant)
    {
        fakeMerchant = null!;
        if (_runState?.CurrentRoom is not EventRoom)
            return false;

        if (RunManager.Instance.EventSynchronizer?.GetLocalEvent() is not FakeMerchant localEvent)
            return false;

        if (localEvent.Inventory == null || localEvent.StartedFight)
            return false;

        fakeMerchant = localEvent;
        return true;
    }

    private Dictionary<string, object?> FakeMerchantShopState(FakeMerchant fakeMerchant)
    {
        var player = _runState!.Players[0];
        var eventEntry = fakeMerchant.Id?.Entry ?? "FAKE_MERCHANT";
        var eventVars = ExportEventVars(eventEntry, fakeMerchant);
        var description = EventDescription(fakeMerchant, eventVars);
        if (description == "Placeholder")
            description = null;
        var foulPotion = player.Potions?
            .Select((p, i) => new { potion = p, index = i })
            .FirstOrDefault(item => item.potion is FoulPotion);

        var relics = fakeMerchant.Inventory.RelicEntries.Select((e, i) =>
        {
            var exported = e.Model != null
                ? RelicInfo(e.Model, index: i)
                : new Dictionary<string, object?> { ["index"] = i, ["name"] = "?", ["description"] = null };
            exported["cost"] = e.Cost;
            exported["gold_cost"] = e.Cost;
            exported["is_stocked"] = e.IsStocked;
            exported["can_buy"] = e.IsStocked && player.Gold >= e.Cost;
            return ShopItemState(e, exported, e.Model != null);
        }).ToList();

        var state = new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "fake_merchant_shop",
            ["context"] = RunContext(),
            ["event_name"] = EventDisplayName(eventEntry),
            ["description"] = description,
            ["vars"] = eventVars?.Count > 0 ? eventVars : null,
            ["relics"] = relics,
            ["can_leave"] = true,
            ["can_throw_foul_potion"] = foulPotion != null,
            ["player"] = PlayerSummary(player),
        };

        if (foulPotion != null)
            state["foul_potion_action"] = new Dictionary<string, object?>
            {
                ["action"] = "use_potion",
                ["potion_index"] = foulPotion.index,
            };

        return state;
    }

    private Dictionary<string, object?>? EventOptionSelectionContext(EventRoom eventRoom, int optionIndex)
    {
        var state = EventChoiceState(eventRoom);
        if (!state.TryGetValue("options", out var optionsObj)
            || optionsObj is not IEnumerable<Dictionary<string, object?>> options)
            return null;

        var option = options.FirstOrDefault(item =>
            item.TryGetValue("index", out var indexObj)
            && Convert.ToInt32(indexObj) == optionIndex);
        if (option == null)
            return null;

        var source = new Dictionary<string, object?>(option);
        if (state.TryGetValue("event_name", out var eventName))
            source["event_name"] = eventName;
        if (state.TryGetValue("description", out var eventDescription))
            source["event_description"] = eventDescription;
        return source;
    }

    private Dictionary<string, object?>? RestSiteOptionSelectionContext(RestSiteRoom restSiteRoom, int optionIndex)
    {
        var state = RestSiteState(restSiteRoom);
        if (!state.TryGetValue("options", out var optionsObj)
            || optionsObj is not IEnumerable<Dictionary<string, object?>> options)
            return null;

        var option = options.FirstOrDefault(item =>
            item.TryGetValue("index", out var indexObj)
            && Convert.ToInt32(indexObj) == optionIndex);
        return option == null ? null : new Dictionary<string, object?>(option);
    }

    private Dictionary<string, object?> ShopCardRemovalSelectionContext(int cost)
    {
        var vars = new Dictionary<string, object?> { ["Amount"] = 1 };
        var promptKey = "TO_REMOVE";
        var rawPrompt = _loc.Bilingual("card_selection", promptKey);
        var prompt = CleanResolvedEngineText(InterpolateDynamicVars(rawPrompt, vars) ?? rawPrompt)
                     ?? rawPrompt;

        return new Dictionary<string, object?>
        {
            ["category"] = "card_removal",
            ["title"] = prompt,
            ["text_key"] = $"card_selection.{promptKey}",
            ["cost"] = cost,
        };
    }

    private (string? title, string? description)? ResolveAncientDialogueOption(string eventEntry, string? textKey)
    {
        var optionKey = EventOptionKey(textKey);
        if (!int.TryParse(optionKey, out var step))
            return null;

        var characterEntry = _runState?.Players.FirstOrDefault()?.Character?.Id.Entry;
        if (string.IsNullOrWhiteSpace(characterEntry))
            return null;

        for (var route = 0; route < 10; route++)
        {
            foreach (var suffix in new[] { "", "r" })
            {
                var prefix = $"{eventEntry}.talk.{characterEntry}.{route}-{step}{suffix}";
                var description =
                    AncientText(prefix + ".char")
                    ?? AncientText(prefix + ".ancient");
                var title = AncientText(prefix + ".next");

                if (description != null || title != null)
                    return (title, description);
            }
        }

        return null;
    }

    private string? AncientText(string key)
    {
        var text = _loc.Bilingual("ancients", key);
        return text == key ? null : CleanResolvedEngineText(text);
    }

    private static bool IsUninformativeEventOptionTitle(string? title, string? textKey, int optionIndex)
    {
        if (string.IsNullOrWhiteSpace(title))
            return true;

        var optionKey = EventOptionKey(textKey);
        return string.Equals(title, optionKey, StringComparison.Ordinal)
            || string.Equals(title, $"option_{optionIndex}", StringComparison.Ordinal)
            || int.TryParse(title, out _);
    }

    private string? RelicOptionDescription(string optionId)
    {
        try
        {
            var relicModel = ModelDb.GetById<RelicModel>(new ModelId("RELIC", optionId));
            if (relicModel == null)
                return null;

            var info = RelicInfo(relicModel.ToMutable());
            return info.TryGetValue("description", out var description)
                ? CleanResolvedEngineText(description as string)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private Dictionary<string, object?>? ExportEventVars(string eventEntry, object localEvent)
    {
        var vars = new Dictionary<string, object?>();
        AddEventDynamicVars(vars, eventEntry, GetPropertyValue(localEvent, "DynamicVars"));
        return vars.Count > 0 ? vars : null;
    }

    private Dictionary<string, object?>? ExportEventOptionVars(
        string eventEntry,
        object localEvent,
        EventOption option,
        int optionIndex)
    {
        var vars = new Dictionary<string, object?>();
        AddEventDynamicVars(vars, eventEntry, GetPropertyValue(localEvent, "CanonicalVars"));
        AddEventDynamicVars(vars, eventEntry, GetPropertyValue(localEvent, "DynamicVars"));
        AddEventDynamicVars(vars, eventEntry, GetPropertyValue(option, "CanonicalVars"));
        AddEventDynamicVars(vars, eventEntry, GetPropertyValue(option, "DynamicVars"));
        var engineTitle = EventOptionLocString(localEvent, option.TextKey, "GetOptionTitle");
        var engineDescription = EventOptionLocString(localEvent, option.TextKey, "GetOptionDescription");
        MergeLocStringTokenVars(vars, engineTitle);
        MergeLocStringTokenVars(vars, engineDescription);
        MergeLocStringTokenVars(vars, option.Title);
        MergeLocStringTokenVars(vars, option.Description);
        AddFormatTokenMemberVars(vars, engineTitle, localEvent, option);
        AddFormatTokenMemberVars(vars, engineDescription, localEvent, option);
        AddFormatTokenMemberVars(vars, option.Title, localEvent, option);
        AddFormatTokenMemberVars(vars, option.Description, localEvent, option);
        AddPlayerStateFormatTokenVars(vars, engineTitle);
        AddPlayerStateFormatTokenVars(vars, engineDescription);
        AddPlayerStateFormatTokenVars(vars, option.Title);
        AddPlayerStateFormatTokenVars(vars, option.Description);
        AddPotionConversionOptionVars(vars, localEvent, option, optionIndex);
        KeepOnlyVisibleOptionVars(
            vars,
            EventOptionFormatTokenNames(engineTitle, engineDescription, option.Title, option.Description));
        return vars.Count > 0 ? vars : null;
    }

    private void AddEventOptionHoverTipNameVars(Dictionary<string, object?> vars, EventOption option)
    {
        if (!vars.TryGetValue("EnchantmentName", out var value) || !IsNumericDisplayValue(value))
            return;

        var tips = EventOptionHoverTips(option);
        var title = tips?
            .Select(tip => tip.TryGetValue("title", out var rawTitle) ? rawTitle as string : null)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        if (!string.IsNullOrWhiteSpace(title))
            vars["EnchantmentName"] = title;
    }

    private static void MergeLocStringTokenVars(
        Dictionary<string, object?> vars,
        LocString? locString)
    {
        var exported = ExportLocStringVariables(locString);
        if (exported == null || exported.Count == 0)
            return;

        foreach (var token in LocStringTokenNames(locString))
        {
            if (exported.TryGetValue(token, out var value))
                vars[token] = value;
        }
    }

    private static HashSet<string> LocStringTokenNames(LocString? locString)
    {
        try
        {
            return FormatTokenNames(locString?.GetRawText());
        }
        catch
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static HashSet<string> EventOptionFormatTokenNames(params LocString?[] locStrings)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var locString in locStrings)
            names.UnionWith(LocStringTokenNames(locString));
        return names;
    }

    private static void KeepOnlyVisibleOptionVars(
        Dictionary<string, object?> vars,
        HashSet<string> visibleTokenNames)
    {
        if (vars.Count == 0)
            return;

        if (visibleTokenNames.Count == 0)
        {
            vars.Clear();
            return;
        }

        foreach (var key in vars.Keys.ToList())
        {
            if (!visibleTokenNames.Contains(key))
                vars.Remove(key);
        }
    }

    private void AddPlayerStateFormatTokenVars(
        Dictionary<string, object?> vars,
        LocString? locString)
    {
        string? rawText;
        try
        {
            rawText = locString?.GetRawText();
        }
        catch
        {
            return;
        }

        foreach (var token in FormatTokenNames(rawText))
        {
            if (vars.ContainsKey(token))
                continue;
            if (string.Equals(token, "Gold", StringComparison.OrdinalIgnoreCase))
                vars[token] = _runState?.Players[0].Gold;
        }
    }

    private static LocString? EventOptionLocString(object localEvent, string? textKey, string methodName)
    {
        var optionKey = EventOptionKey(textKey);
        if (string.IsNullOrWhiteSpace(optionKey))
            return null;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            var method = localEvent.GetType()
                .GetMethods(flags)
                .FirstOrDefault(m =>
                    string.Equals(m.Name, methodName, StringComparison.Ordinal)
                    && m.ReturnType == typeof(LocString)
                    && m.GetParameters() is [{ ParameterType: var parameterType }]
                    && parameterType == typeof(string));

            return method?.Invoke(localEvent, new object[] { optionKey }) as LocString;
        }
        catch
        {
            return null;
        }
    }

    private static string? EventOptionKey(string? textKey)
    {
        if (string.IsNullOrWhiteSpace(textKey))
            return null;
        var parts = textKey.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? textKey : parts[^1];
    }

    private static void AddFormatTokenMemberVars(
        Dictionary<string, object?> vars,
        LocString? locString,
        params object?[] sources)
    {
        string? rawText;
        try
        {
            rawText = locString?.GetRawText();
        }
        catch
        {
            return;
        }

        foreach (var token in FormatTokenNames(rawText))
        {
            if (vars.ContainsKey(token))
                continue;

            foreach (var source in sources)
            {
                if (TryGetSimpleMemberValue(source, token, out var value))
                {
                    vars[token] = value;
                    break;
                }
            }
        }
    }

    private static bool TryGetSimpleMemberValue(object? source, string token, out object? value)
    {
        value = null;
        if (source == null)
            return false;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        try
        {
            foreach (var prop in source.GetType().GetProperties(flags))
            {
                if (prop.GetIndexParameters().Length != 0 || !MemberNameMatchesToken(prop.Name, token))
                    continue;
                value = ExportLocStringVariableValue(prop.GetValue(source));
                return true;
            }

            foreach (var field in source.GetType().GetFields(flags))
            {
                if (!MemberNameMatchesToken(field.Name, token))
                    continue;
                value = ExportLocStringVariableValue(field.GetValue(source));
                return true;
            }
        }
        catch { }

        return false;
    }

    private static bool MemberNameMatchesToken(string memberName, string token)
    {
        if (string.Equals(memberName, token, StringComparison.OrdinalIgnoreCase))
            return true;

        var normalized = memberName.TrimStart('_');
        if (string.Equals(normalized, token, StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(
            memberName,
            $"<{token}>k__BackingField",
            StringComparison.OrdinalIgnoreCase);
    }

    private List<Dictionary<string, object?>>? EventOptionHoverTips(
        EventOption option,
        Dictionary<string, object?>? relicTrade = null)
    {
        var rawTips = TryGetMember(option, "HoverTips") as System.Collections.IEnumerable;
        if (rawTips == null)
            return null;

        var tips = new List<Dictionary<string, object?>>();
        foreach (var rawTip in rawTips)
        {
            var tip = EventOptionHoverTipInfo(rawTip);
            if (tip != null)
            {
                if (!ApplyRelicTradeHoverTipState(tip, relicTrade))
                    ApplyOwnedRelicHoverTipState(tip);
                tips.Add(tip);
            }
        }
        return tips.Count > 0 ? tips : null;
    }

    private static bool ApplyRelicTradeHoverTipState(
        Dictionary<string, object?> tip,
        Dictionary<string, object?>? relicTrade)
    {
        if (relicTrade == null)
            return false;
        if (!tip.TryGetValue("kind", out var kind)
            || !string.Equals(kind as string, "relic", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!tip.TryGetValue("id", out var idValue) || idValue is not string id)
            return false;

        foreach (var key in new[] { "owned", "new" })
        {
            if (!relicTrade.TryGetValue(key, out var tradeValue)
                || tradeValue is not Dictionary<string, object?> tradeRelic)
                continue;
            if (!tradeRelic.TryGetValue("id", out var tradeId)
                || !string.Equals(tradeId as string, id, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var field in new[] { "name", "description", "vars", "show_counter", "display_amount" })
            {
                if (tradeRelic.TryGetValue(field, out var value))
                    tip[field] = value;
            }
            return true;
        }
        return false;
    }

    private bool ApplyOwnedRelicHoverTipState(Dictionary<string, object?> tip)
    {
        if (!tip.TryGetValue("kind", out var kind)
            || !string.Equals(kind as string, "relic", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!tip.TryGetValue("id", out var idValue) || idValue is not string id)
            return false;

        var player = _runState?.Players[0];
        var ownedRelic = player?.Relics?.FirstOrDefault(
            relic => string.Equals(relic.Id.Entry, id, StringComparison.OrdinalIgnoreCase));
        if (ownedRelic == null)
            return false;

        var ownedInfo = RelicInfo(ownedRelic);
        foreach (var field in new[] { "name", "description", "vars", "show_counter", "display_amount" })
        {
            if (ownedInfo.TryGetValue(field, out var value))
                tip[field] = value;
        }
        return true;
    }

    private Dictionary<string, object?>? EventOptionHoverTipInfo(object? rawTip)
    {
        if (rawTip == null)
            return null;

        if (TryGetMember(rawTip, "Card") is CardModel instancedCard)
        {
            var info = SingleCardRewardInfo(instancedCard);
            info["kind"] = "card";
            return info;
        }

        var canonicalModel = TryGetMember(rawTip, "CanonicalModel");
        if (canonicalModel is RelicModel relic)
        {
            var info = RelicInfo(relic);
            info["kind"] = "relic";
            return info;
        }
        if (canonicalModel is PotionModel potion)
        {
            var info = PotionInfo(potion);
            info["kind"] = "potion";
            return info;
        }
        if (canonicalModel is CardModel card)
        {
            var info = SingleCardRewardInfo(card);
            info["kind"] = "card";
            return info;
        }

        var title = ResolveHoverTipText(rawTip, "Title");
        var description = ResolveHoverTipText(rawTip, "Description");
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(description))
            return null;

        return new Dictionary<string, object?>
        {
            ["kind"] = rawTip.GetType().Name,
            ["title"] = string.IsNullOrWhiteSpace(title) ? null : title,
            ["description"] = string.IsNullOrWhiteSpace(description) ? null : description,
        };
    }

    private void AddCardHoverTips(Dictionary<string, object?> info, CardModel card)
    {
        var description = info.TryGetValue("description", out var rawDescription)
            ? CleanResolvedEngineText(rawDescription?.ToString())
            : null;
        var tips = CardHoverTips(card, description);
        if (tips.Count > 0)
            info["hover_tips"] = tips;
    }

    private List<Dictionary<string, object?>> CardHoverTips(CardModel card, string? description)
    {
        var tips = new List<Dictionary<string, object?>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var rawTips = TryGetMember(card, "HoverTips") as System.Collections.IEnumerable;
        if (rawTips != null)
        {
            foreach (var rawTip in rawTips)
            {
                var tip = EventOptionHoverTipInfo(rawTip);
                AddHoverTipIfNew(tips, seen, tip);
            }
        }

        var textParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(description))
            textParts.Add(description);
        foreach (var keyword in card.Keywords?.Where(k => k != CardKeyword.None) ?? Enumerable.Empty<CardKeyword>())
        {
            var entry = keyword.ToString().ToUpperInvariant();
            var title = _loc.Bilingual("card_keywords", entry + ".title");
            if (title != entry + ".title")
                textParts.Add(title);
        }
        var visibleText = string.Join("\n", textParts);
        if (string.IsNullOrWhiteSpace(visibleText))
            return tips;

        AddMentionedLocTips(tips, seen, visibleText, "card_keyword", "card_keywords");
        AddMentionedLocTips(
            tips,
            seen,
            visibleText,
            "static_hover_tip",
            "static_hover_tips",
            entry => !entry.StartsWith("ROOM_", StringComparison.Ordinal));
        AddMentionedLocTips(tips, seen, visibleText, "orb", "orbs");
        AddMentionedLocTips(tips, seen, visibleText, "power", "powers");

        return tips;
    }

    private void AddMentionedLocTips(
        List<Dictionary<string, object?>> tips,
        HashSet<string> seen,
        string visibleText,
        string kind,
        string table,
        Func<string, bool>? includeEntry = null)
    {
        foreach (var titleEntry in _loc.Entries(table).Where(kv => kv.Key.EndsWith(".title", StringComparison.Ordinal)))
        {
            var entry = titleEntry.Key[..^".title".Length];
            if (includeEntry != null && !includeEntry(entry))
                continue;
            var title = titleEntry.Value;
            if (string.IsNullOrWhiteSpace(title) || !TextMentionsTitle(visibleText, title))
                continue;

            var descriptionKey = entry + ".description";
            var description = CleanEngineText(_loc.Bilingual(table, descriptionKey));
            if (string.IsNullOrWhiteSpace(description) || description == descriptionKey)
                continue;

            AddHoverTipIfNew(tips, seen, new Dictionary<string, object?>
            {
                ["kind"] = kind,
                ["id"] = entry,
                ["title"] = title,
                ["description"] = description,
            });
        }
    }

    private static bool TextMentionsTitle(string text, string title)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(title))
            return false;

        var escaped = System.Text.RegularExpressions.Regex.Escape(title);
        var pattern = title.All(char.IsLetter)
            ? $@"(?<![A-Za-z]){escaped}(?:ed|s|ing)?(?![A-Za-z])"
            : escaped;
        return System.Text.RegularExpressions.Regex.IsMatch(
            text,
            pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private static void AddHoverTipIfNew(
        List<Dictionary<string, object?>> tips,
        HashSet<string> seen,
        Dictionary<string, object?>? tip)
    {
        if (tip == null)
            return;
        var id = tip.TryGetValue("id", out var rawId) ? rawId?.ToString() : null;
        var title = tip.TryGetValue("title", out var rawTitle) ? rawTitle?.ToString() : null;
        var name = tip.TryGetValue("name", out var rawName) ? rawName?.ToString() : null;
        var kind = tip.TryGetValue("kind", out var rawKind) ? rawKind?.ToString() : null;
        var key = $"{kind}|{id ?? title ?? name}";
        if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
            return;
        tips.Add(tip);
    }

    private string? ResolveHoverTipText(object rawTip, string memberName)
    {
        var rawText = TryGetMember(rawTip, memberName);
        var vars = ExportHoverTipVars(rawTip);
        if (rawText is LocString locString)
            return ResolveLocString(locString, vars);

        var text = rawText as string;
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var staticTip = _loc.Bilingual("static_hover_tips", text);
        if (staticTip != text)
            return CleanResolvedEngineText(InterpolateDynamicVars(staticTip, vars) ?? staticTip);

        var resolved = _loc.BilingualFromKey(text);
        var formatted = string.IsNullOrWhiteSpace(resolved) ? text : resolved;
        return CleanResolvedEngineText(InterpolateDynamicVars(formatted, vars) ?? formatted);
    }

    private static Dictionary<string, object?>? ExportHoverTipVars(object rawTip)
    {
        var vars = new Dictionary<string, object?>();
        MergeVars(vars, ExportDynamicVars(rawTip));

        foreach (var prop in rawTip.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length != 0)
                continue;
            object? value;
            try { value = prop.GetValue(rawTip); }
            catch { continue; }
            AddHoverTipVar(vars, prop.Name, value);
        }

        foreach (var field in rawTip.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            object? value;
            try { value = field.GetValue(rawTip); }
            catch { continue; }
            AddHoverTipVar(vars, field.Name, value);
        }

        return vars.Count > 0 ? vars : null;
    }

    private static void AddHoverTipVar(
        Dictionary<string, object?> vars,
        string memberName,
        object? value)
    {
        if (value == null)
            return;

        if (value is LocString locString)
        {
            MergeVars(vars, ExportLocStringVariables(locString));
            return;
        }

        if (value is DynamicVar dynamicVar)
        {
            var name = dynamicVar.Name;
            if (!string.IsNullOrWhiteSpace(name))
                vars[name] = ExportLocStringVariableValue(dynamicVar);
            return;
        }

        if (value is CardModel card)
        {
            MergeVars(vars, ExportCardDescriptionVars(card, includePreviewStats: true));
            return;
        }

        if (value is string)
            return;

        if (value is bool or int or long or short or byte or float or double or decimal)
        {
            vars[memberName] = value;
            return;
        }

        var baseValue = value.GetType().GetProperty("BaseValue")?.GetValue(value);
        if (baseValue is string or bool or int or long or short or byte or float or double or decimal)
        {
            vars[memberName] = baseValue;
            return;
        }

        var amount = value.GetType().GetProperty("Amount")?.GetValue(value);
        if (amount is string or bool or int or long or short or byte or float or double or decimal)
            vars[memberName] = amount;
    }

    private Dictionary<string, object?>? BuildRelicTradePreview(
        string eventEntry,
        object localEvent,
        EventOption option,
        int optionIndex)
    {
        if (!string.Equals(eventEntry, "RELIC_TRADER", StringComparison.OrdinalIgnoreCase))
            return null;

        var tradeIndex = RelicTraderTradeIndex(option.TextKey, optionIndex);
        if (!tradeIndex.HasValue)
            return null;

        var ownedRelics = TryGetRelicListMember(localEvent, "OwnedRelics", "_ownedRelics");
        var newRelics = TryGetRelicListMember(localEvent, "NewRelics", "_newRelics");
        if (ownedRelics == null || newRelics == null)
            return null;
        if (tradeIndex.Value < 0 || tradeIndex.Value >= ownedRelics.Count || tradeIndex.Value >= newRelics.Count)
            return null;

        return new Dictionary<string, object?>
        {
            ["owned"] = RelicInfo(ownedRelics[tradeIndex.Value]),
            ["new"] = RelicInfo(newRelics[tradeIndex.Value]),
        };
    }

    private static int? RelicTraderTradeIndex(string? textKey, int optionIndex)
    {
        if (textKey?.EndsWith(".TOP", StringComparison.OrdinalIgnoreCase) == true)
            return 0;
        if (textKey?.EndsWith(".MIDDLE", StringComparison.OrdinalIgnoreCase) == true)
            return 1;
        if (textKey?.EndsWith(".BOTTOM", StringComparison.OrdinalIgnoreCase) == true)
            return 2;

        return optionIndex is >= 0 and < 3 ? optionIndex : null;
    }

    private static IReadOnlyList<RelicModel>? TryGetRelicListMember(object obj, params string[] names)
    {
        foreach (var name in names)
        {
            var relics = TryGetRelicList(TryGetMember(obj, name));
            if (relics != null)
                return relics;
        }
        return null;
    }

    private static IReadOnlyList<RelicModel>? TryGetRelicList(object? value)
    {
        if (value is IReadOnlyList<RelicModel> direct)
            return direct;

        if (value is not System.Collections.IEnumerable items)
            return null;

        var relics = new List<RelicModel>();
        foreach (var item in items)
        {
            if (item is RelicModel relic)
                relics.Add(relic);
        }
        return relics.Count > 0 ? relics : null;
    }

    private void AddEventDynamicVars(
        Dictionary<string, object?> vars,
        string eventEntry,
        object? dynamicVarSet)
    {
        try
        {
            var values = dynamicVarSet?.GetType().GetProperty("Values")?.GetValue(dynamicVarSet)
                         as System.Collections.IEnumerable;
            if (values == null)
                return;

            foreach (var value in values)
            {
                if (value is DynamicVar dynamicVar)
                    vars[dynamicVar.Name] = ExportEventDynamicVar(eventEntry, dynamicVar);
            }
        }
        catch { }
    }

    private void AddPotionConversionOptionVars(
        Dictionary<string, object?> vars,
        object localEvent,
        EventOption option,
        int optionIndex)
    {
        if (option.TextKey?.EndsWith(".POTION", StringComparison.OrdinalIgnoreCase) != true)
            return;

        var potionToCardType = GetPropertyValue(localEvent, "PotionToCardType");
        if (potionToCardType == null)
            return;

        var potions = _runState?.Players[0].Potions?.Where(p => p != null).ToList();
        if (potions == null || optionIndex < 0 || optionIndex >= potions.Count)
            return;

        var potion = potions[optionIndex];
        var potionEntry = potion.Id.Entry;
        vars["Potion"] = _loc.Potion(potionEntry);

        var rarity = GetPropertyValue(potion, "Rarity")?.ToString();
        if (!string.IsNullOrWhiteSpace(rarity))
            vars["Rarity"] = HumanizeEnumToken(rarity);

        var cardType = TryGetPotionConversionCardType(potionToCardType, potion);
        if (!string.IsNullOrWhiteSpace(cardType))
            vars["Type"] = HumanizeEnumToken(cardType);
    }

    private static string? TryGetPotionConversionCardType(object potionToCardType, PotionModel potion)
    {
        var potionEntry = potion.Id.Entry;
        if (potionToCardType is System.Collections.IEnumerable entries)
        {
            foreach (var entry in entries)
            {
                var key = GetPropertyValue(entry, "Key");
                var value = GetPropertyValue(entry, "Value");
                if (key == null)
                    continue;
                if (ReferenceEquals(key, potion)
                    || string.Equals(ModelEntry(key), potionEntry, StringComparison.OrdinalIgnoreCase))
                {
                    return value?.ToString();
                }
            }
        }

        return null;
    }

    private static string HumanizeEnumToken(string value)
    {
        return value.Replace("_", " ", StringComparison.Ordinal);
    }

    private Dictionary<string, object?> CrystalSphereState(CrystalSphereMinigame minigame)
    {
        var width = minigame.GridSize.X;
        var height = minigame.GridSize.Y;
        var cells = new List<Dictionary<string, object?>>();
        var clickableCells = new List<Dictionary<string, object?>>();
        var itemIndexes = minigame.Items
            .Select((item, index) => new { item, index })
            .ToDictionary(entry => entry.item, entry => entry.index);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var cell = minigame.cells[x, y];
                var isClickable = cell.IsHidden && !minigame.IsFinished;
                var exported = new Dictionary<string, object?>
                {
                    ["x"] = x,
                    ["y"] = y,
                    ["is_hidden"] = cell.IsHidden,
                    ["is_clickable"] = isClickable,
                    ["is_highlighted"] = cell.IsHighlighted,
                    ["is_hovered"] = cell.IsHovered,
                };
                if (!cell.IsHidden && cell.Item != null)
                {
                    exported["item_index"] = itemIndexes.TryGetValue(cell.Item, out var itemIndex) ? itemIndex : null;
                    exported["item_type"] = cell.Item.GetType().Name;
                    exported["item_kind"] = CrystalSphereItemKind(cell.Item);
                    exported["is_good"] = cell.Item.IsGood;
                }
                cells.Add(exported);

                if (isClickable)
                {
                    clickableCells.Add(new Dictionary<string, object?>
                    {
                        ["x"] = x,
                        ["y"] = y,
                    });
                }
            }
        }

        var visibleItems = minigame.Items
            .Select((item, index) => CrystalSphereItemState(minigame, item, index))
            .Where(item => ((List<Dictionary<string, object?>>)item["visible_cells"]!).Count > 0)
            .ToList();

        var revealedItems = minigame.Items
            .Select((item, index) => CrystalSphereItemState(minigame, item, index))
            .Where(item => (bool)item["is_fully_revealed"]!)
            .ToList();

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "crystal_sphere",
            ["context"] = RunContext(),
            ["event_name"] = _loc.Event("CRYSTAL_SPHERE"),
            ["grid_width"] = width,
            ["grid_height"] = height,
            ["divinations_remaining"] = minigame.DivinationCount,
            ["tool"] = CrystalSphereToolName(minigame.CrystalSphereTool),
            ["can_use_big_tool"] = !minigame.IsFinished,
            ["can_use_small_tool"] = !minigame.IsFinished,
            ["can_proceed"] = minigame.IsFinished,
            ["cells"] = cells,
            ["clickable_cells"] = clickableCells,
            ["visible_items"] = visibleItems,
            ["revealed_items"] = revealedItems,
            ["player"] = PlayerSummary(_runState!.Players[0]),
        };
    }

    private static bool IsVictoryProceedOption(string? textKey)
    {
        return string.Equals(textKey, "PROCEED", StringComparison.OrdinalIgnoreCase);
    }

    private void CompleteVictoryRoomTransition()
    {
        if (_runState?.CurrentRoom?.IsVictoryRoom != true || RunManager.Instance.IsGameOver)
            return;

        for (var i = 0; i < 30; i++)
        {
            _syncCtx.Pump();
            WaitForActionExecutor();
            if (RunManager.Instance.IsGameOver)
                return;
            Thread.Sleep(10);
        }

        try
        {
            RunManager.Instance.EnterNextAct().GetAwaiter().GetResult();
            _syncCtx.Pump();
            WaitForActionExecutor();
        }
        catch (Exception ex)
        {
            Log($"Victory transition failed: {ex.GetType().FullName}: {ex.Message}");
        }
    }

    private static bool IsCrystalSphereItemRevealed(CrystalSphereMinigame minigame, CrystalSphereItem item)
    {
        for (var dx = 0; dx < item.Size.X; dx++)
        {
            for (var dy = 0; dy < item.Size.Y; dy++)
            {
                var x = item.Position.X + dx;
                var y = item.Position.Y + dy;
                if (x < 0 || x >= minigame.GridSize.X || y < 0 || y >= minigame.GridSize.Y)
                    return false;
                if (minigame.cells[x, y].IsHidden)
                    return false;
            }
        }
        return true;
    }

    private static Dictionary<string, object?> CrystalSphereItemState(
        CrystalSphereMinigame minigame,
        CrystalSphereItem item,
        int index)
    {
        var visibleCells = new List<Dictionary<string, object?>>();
        for (var dx = 0; dx < item.Size.X; dx++)
        {
            for (var dy = 0; dy < item.Size.Y; dy++)
            {
                var x = item.Position.X + dx;
                var y = item.Position.Y + dy;
                if (x < 0 || x >= minigame.GridSize.X || y < 0 || y >= minigame.GridSize.Y)
                    continue;
                if (!minigame.cells[x, y].IsHidden)
                {
                    visibleCells.Add(new Dictionary<string, object?>
                    {
                        ["x"] = x,
                        ["y"] = y,
                    });
                }
            }
        }

        var isFullyRevealed = visibleCells.Count == item.Size.X * item.Size.Y;
        var state = new Dictionary<string, object?>
        {
            ["index"] = index,
            ["item_type"] = item.GetType().Name,
            ["item_kind"] = CrystalSphereItemKind(item),
            ["is_good"] = item.IsGood,
            ["visible_cells"] = visibleCells,
            ["revealed_cells"] = visibleCells.Count,
            ["total_cells"] = item.Size.X * item.Size.Y,
            ["is_fully_revealed"] = isFullyRevealed,
        };
        AddCrystalSphereRewardPreview(state, item);
        if (isFullyRevealed)
        {
            state["x"] = item.Position.X;
            state["y"] = item.Position.Y;
            state["width"] = item.Size.X;
            state["height"] = item.Size.Y;
        }
        return state;
    }

    private static void AddCrystalSphereRewardPreview(Dictionary<string, object?> state, CrystalSphereItem item)
    {
        var kind = Convert.ToString(state["item_kind"]) ?? "unknown";
        var preview = new Dictionary<string, object?>
        {
            ["category"] = kind,
        };

        switch (kind)
        {
            case "card_reward":
            {
                var rarity = GetPrivateFieldValue(item, "_rarity")?.ToString() ?? "Unknown";
                state["card_rarity"] = rarity;
                state["visual_variant"] = $"{rarity.ToLowerInvariant()}_card_reward";
                preview["card_rarity"] = rarity;
                preview["card_choices"] = 3;
                break;
            }
            case "potion":
            {
                var potion = GetPrivateFieldValue(item, "_potion");
                var rarity = GetPropertyValue(potion, "Rarity")?.ToString() ?? "Unknown";
                state["potion_rarity"] = rarity;
                state["visual_variant"] = $"{rarity.ToLowerInvariant()}_potion";
                preview["potion_rarity"] = rarity;
                break;
            }
            case "gold":
            {
                var isBig = GetPrivateFieldValue(item, "_isBig") is bool value && value;
                var amount = isBig ? 30 : 10;
                var size = isBig ? "big" : "small";
                state["gold_amount"] = amount;
                state["gold_size"] = size;
                state["visual_variant"] = isBig ? "big_gold" : "gold";
                preview["amount"] = amount;
                preview["size"] = size;
                break;
            }
            case "curse":
                state["curse_card"] = "Doubt";
                state["visual_variant"] = "curse";
                preview["curse_card"] = "Doubt";
                break;
            case "relic":
                state["visual_variant"] = "relic";
                break;
            default:
                state["visual_variant"] = kind;
                break;
        }

        state["reward_preview"] = preview;
    }

    private static object? GetPrivateFieldValue(object? instance, string fieldName)
    {
        if (instance == null)
            return null;
        return instance.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(instance);
    }

    private static object? GetPropertyValue(object? instance, string propertyName)
    {
        if (instance == null)
            return null;
        return instance.GetType()
            .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(instance);
    }

    private static string CrystalSphereItemKind(CrystalSphereItem item)
    {
        var typeName = item.GetType().Name;
        if (typeName.EndsWith("Relic", StringComparison.Ordinal))
            return "relic";
        if (typeName.EndsWith("Potion", StringComparison.Ordinal))
            return "potion";
        if (typeName.EndsWith("CardReward", StringComparison.Ordinal))
            return "card_reward";
        if (typeName.EndsWith("Curse", StringComparison.Ordinal))
            return "curse";
        if (typeName.EndsWith("Gold", StringComparison.Ordinal))
            return "gold";
        return typeName;
    }

    private static string CrystalSphereToolName(CrystalSphereMinigame.CrystalSphereToolType tool)
    {
        return tool switch
        {
            CrystalSphereMinigame.CrystalSphereToolType.Big => "big",
            CrystalSphereMinigame.CrystalSphereToolType.Small => "small",
            _ => "none",
        };
    }

    private string CardDescription(
        CardModel card,
        Dictionary<string, object?>? stats = null,
        bool includeCombatText = false)
    {
        var engineDescription = EngineCardDescription(card, includeCombatText);
        if (!string.IsNullOrWhiteSpace(engineDescription))
            return ResolveEngineCardDescriptionFormatters(
                engineDescription,
                card,
                includePreviewStats: includeCombatText);

        var raw = _loc.Bilingual("cards", card.Id.Entry + ".description");
        var vars = ExportCardDescriptionVars(card, includePreviewStats: includeCombatText);
        var formatted = InterpolateDynamicVars(raw, vars) ?? raw;
        return ResolveEngineCardDescriptionFormatters(
            CleanEngineText(formatted) ?? formatted,
            card,
            includePreviewStats: includeCombatText);
    }

    private static string? EngineCardDescription(CardModel card, bool includeCombatText)
    {
        try
        {
            if (includeCombatText)
            {
                try
                {
                    card.DynamicVars.ClearPreview();
                    card.UpdateDynamicVarPreview(CardPreviewMode.Normal, target: null, card.DynamicVars);
                }
                catch
                {
                    // Preview text is best-effort; GetDescriptionForPile still provides the engine fallback.
                }
            }
            var pileType = card.Pile?.Type ?? (includeCombatText ? PileType.Hand : PileType.None);
            var text = card.GetDescriptionForPile(
                pileType,
                includeCombatText ? card.CurrentTarget : null);
            return CleanEngineText(text);
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveEngineCardDescriptionFormatters(
        string text,
        CardModel card,
        bool includePreviewStats = false,
        bool preferDisplayVars = true)
    {
        text = ApplyBooleanChoiceFormatter(text, "IfUpgraded", card.IsUpgraded);

        var vars = ExportCardDescriptionVars(card, includePreviewStats: includePreviewStats);
        if (preferDisplayVars && vars != null && vars.Count > 0)
            text = PreferDisplayVarInterpolation(card.Description, vars, text) ?? text;
        if (vars != null && vars.Count > 0)
            text = ExpandResolvedEnergyIcons(text, card, vars);

        if (!ContainsSmartFormatToken(text))
            return text;

        if (vars == null || vars.Count == 0)
            return text;

        AddSingleMissingDisplayAlias(text, vars, CardRawDescriptionTokenNames(card));
        text = InterpolateDynamicVars(text, vars) ?? text;
        return ExpandResolvedEnergyIcons(text, card, vars);
    }

    private static string ExpandResolvedEnergyIcons(
        string text,
        CardModel card,
        Dictionary<string, object?> vars)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        try
        {
            return ExpandResolvedEnergyIcons(text, card.Description.GetRawText(), vars);
        }
        catch
        {
            return text;
        }
    }

    private static string ExpandResolvedEnergyIcons(
        string text,
        string? rawText,
        Dictionary<string, object?> vars)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(rawText))
            return text;

        foreach (System.Text.RegularExpressions.Match token in System.Text.RegularExpressions.Regex.Matches(
                     rawText,
                     @"\{(?<key>[A-Za-z][A-Za-z0-9_]*)\:energyIcons\(\)\}"))
        {
            var key = token.Groups["key"].Value;
            if (!vars.TryGetValue(key, out var value) || value == null)
                continue;

            int count;
            try
            {
                count = System.Convert.ToInt32(value);
            }
            catch
            {
                continue;
            }
            if (count < 1)
                continue;

            var prefix = System.Text.RegularExpressions.Regex.Escape(count.ToString());
            var pattern = @"(?<![A-Za-z0-9_])" + prefix
                + @"(?<icon>(?:res://[A-Za-z0-9_./-]+/)?[A-Za-z0-9_]*energy_icon\.png)";
            var regex = new System.Text.RegularExpressions.Regex(pattern);
            text = regex.Replace(
                text,
                _ => CliEnergyTokens(count),
                1);
        }

        return text;
    }

    private static string CliEnergyTokens(int count)
    {
        return count <= 0 ? "" : string.Concat(Enumerable.Repeat(CliEnergyToken, count));
    }

    private static string CliStarTokens(int count)
    {
        return count <= 0 ? "" : string.Concat(Enumerable.Repeat(CliStarToken, count));
    }

    private static string CliIconTokens(string formatterName, int count)
    {
        return string.Equals(formatterName, "starIcons", StringComparison.Ordinal)
            ? CliStarTokens(count)
            : CliEnergyTokens(count);
    }

    private static Dictionary<string, object?>? ExportCardDescriptionVars(
        CardModel card,
        bool includePreviewStats = false)
    {
        var vars = new Dictionary<string, object?>();
        MergeVars(vars, ExportLocStringVariables(card.Description));
        MergeVars(vars, ExportDynamicVars(card));
        if (includePreviewStats)
            MergePreviewStatsForDescriptionVars(vars, card);
        return vars.Count > 0 ? vars : null;
    }

    private static void MergePreviewStatsForDescriptionVars(
        Dictionary<string, object?> vars,
        CardModel card)
    {
        var stats = TryGetCardPreviewStats(card, CardPreviewMode.Normal, target: null);
        if (stats == null || stats.Count == 0)
            return;

        try
        {
            foreach (var dynamicVar in card.DynamicVars.Values)
            {
                var name = dynamicVar.Name;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var stat = stats.FirstOrDefault(pair =>
                    string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(stat.Key) || !IsNumericDisplayValue(stat.Value))
                    continue;

                vars[name] = stat.Value;
            }
        }
        catch { }
    }

    private static void MergeVars(
        Dictionary<string, object?> destination,
        Dictionary<string, object?>? source)
    {
        if (source == null)
            return;

        foreach (var (key, value) in source)
            destination[key] = value;
    }

    private static bool ContainsSmartFormatToken(string text)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(
            text,
            @"\{[A-Za-z][A-Za-z0-9_]*(?:[:}]|$)");
    }

    private static HashSet<string> CardRawDescriptionTokenNames(CardModel card)
    {
        try
        {
            return FormatTokenNames(card.Description.GetRawText());
        }
        catch
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static HashSet<string> FormatTokenNames(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return new HashSet<string>(StringComparer.Ordinal);

        return System.Text.RegularExpressions.Regex.Matches(
                text,
                @"\{(?<key>[A-Za-z][A-Za-z0-9_]*)(?=[:}])")
            .Select(match => match.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static void AddSingleMissingDisplayAlias(
        string text,
        Dictionary<string, object?> vars,
        HashSet<string> sourceTokenNames)
    {
        var usedNames = FormatTokenNames(text);

        var missingNames = usedNames
            .Where(name => !vars.ContainsKey(name))
            .ToList();
        if (missingNames.Count != 1)
            return;

        var referencedNames = new HashSet<string>(sourceTokenNames, StringComparer.Ordinal);
        referencedNames.UnionWith(usedNames);

        var unusedNumericVars = vars
            .Where(pair => !referencedNames.Contains(pair.Key) && IsNumericDisplayValue(pair.Value))
            .ToList();
        if (unusedNumericVars.Count != 1)
            return;

        vars[missingNames[0]] = unusedNumericVars[0].Value;
    }

    private static bool IsNumericDisplayValue(object? value)
    {
        return value is byte or sbyte
            or short or ushort
            or int or uint
            or long or ulong
            or float or double
            or decimal;
    }

    private static string? InterpolateDynamicVars(string? text, Dictionary<string, object?>? vars)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        text = text.Replace("{singleStarIcon}", CliStarToken, StringComparison.Ordinal);

        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"\{(?<key>[A-Za-z][A-Za-z0-9_]*)\:(?<formatter>energyIcons|starIcons)\((?<count>\d*)\)\}",
            match =>
            {
                var explicitCount = match.Groups["count"].Value;
                if (!string.IsNullOrWhiteSpace(explicitCount)
                    && int.TryParse(explicitCount, out var literalCount))
                    return CliIconTokens(match.Groups["formatter"].Value, literalCount);

                if (vars == null || vars.Count == 0)
                    return match.Value;

                var key = match.Groups["key"].Value;
                if (!vars.TryGetValue(key, out var value) || value == null)
                    return match.Value;

                try
                {
                    return CliIconTokens(match.Groups["formatter"].Value, System.Convert.ToInt32(value));
                }
                catch
                {
                    return match.Value;
                }
            });

        if (vars == null || vars.Count == 0)
            return text;

        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"\{(?<key>[A-Za-z][A-Za-z0-9_]*)\:diff\(\)\}",
            match =>
            {
                var key = match.Groups["key"].Value;
                return vars.TryGetValue(key, out var value) && value != null
                    ? value.ToString() ?? ""
                    : match.Value;
            });

        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"\{(?<key>[A-Za-z][A-Za-z0-9_]*)\:plural\:(?<singular>[^|{}]*)\|(?<plural>(?:\{\}|[^{}])*)\}",
            match =>
            {
                var key = match.Groups["key"].Value;
                if (!vars.TryGetValue(key, out var value) || value == null)
                    return match.Value;

                var choice = IsSingularValue(value)
                    ? match.Groups["singular"].Value
                    : match.Groups["plural"].Value;
                return choice.Replace("{}", value.ToString(), StringComparison.Ordinal);
            });

        foreach (var (key, value) in vars)
        {
            if (value == null)
                continue;
            text = text.Replace("{" + key + "}", value.ToString(), StringComparison.Ordinal);
        }

        return text;
    }

    private static bool IsSingularValue(object value)
    {
        try
        {
            return Convert.ToDecimal(value) == 1m;
        }
        catch
        {
            return string.Equals(value.ToString(), "1", StringComparison.Ordinal);
        }
    }

    private string MonsterDisplayName(object? monster, object? creature = null)
    {
        var entry = ModelEntry(monster) ?? "UNKNOWN";
        var vars = ExportDynamicVars(monster);
        if (monster is MonsterModel monsterModel)
            MergeVars(vars ??= new Dictionary<string, object?>(), ExportLocStringVariables(monsterModel.Title));
        var creatureVars = ExportDynamicVars(creature);
        if (creatureVars != null)
        {
            vars ??= new Dictionary<string, object?>();
            foreach (var (key, value) in creatureVars)
                vars[key] = value;
        }

        if (monster is MonsterModel model)
        {
            var engineName = EngineLocStringText(model.Title, vars);
            if (!string.IsNullOrWhiteSpace(engineName))
                return engineName;
        }

        var name = _loc.Monster(entry);

        if (entry == "TEST_SUBJECT" && name.Contains("{Count}", StringComparison.Ordinal))
        {
            var adaptableAmount = GetPowerAmount(creature, "ADAPTABLE_POWER");
            if (adaptableAmount.HasValue)
            {
                vars ??= new Dictionary<string, object?>();
                vars.TryAdd("Count", adaptableAmount.Value);
            }
        }
        return InterpolateDynamicVars(name, vars) ?? name;
    }

    private string BossEncounterDisplayName(string bossIdEntry)
    {
        var encounterKey = bossIdEntry + ".title";
        var encounterName = _loc.Bilingual("encounters", encounterKey);
        if (encounterName != encounterKey)
            return encounterName;

        var monsterKey = bossIdEntry.EndsWith("_BOSS", StringComparison.Ordinal)
            ? bossIdEntry[..^5]
            : bossIdEntry;
        if (monsterKey == "THE_KIN")
            monsterKey = "KIN_PRIEST";
        return _loc.Monster(monsterKey);
    }

    private Dictionary<string, object?> PowerInfo(PowerModel power)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = power.Id.Entry,
            ["name"] = PowerName(power),
            ["description"] = PowerDescription(power),
            ["amount"] = power.Amount,
            ["type"] = power.Type.ToString(),
        };
    }

    private string PowerName(PowerModel power)
    {
        var entry = power.Id.Entry;
        return PowerOwnHoverTipTitle(power)
               ?? EngineLocStringText(power.Title)
               ?? _loc.Power(entry);
    }

    private string PowerDescription(PowerModel power)
    {
        var entry = power.Id.Entry;
        var description = EnginePowerDescription(power)
                          ?? LocalPowerDescription(power)
                          ?? PowerOwnHoverTipDescription(power);
        if (!string.IsNullOrWhiteSpace(description))
            return description;

        var raw = _loc.PowerDescription(entry);
        var vars = ExportDynamicVars(power);
        return InterpolateDynamicVars(raw, vars) ?? raw;
    }

    private string? LocalPowerDescription(PowerModel power)
    {
        var entry = power.Id.Entry;
        var vars = PowerDescriptionVars(power);
        var smartKey = entry + ".smartDescription";
        var text = _loc.Bilingual("powers", smartKey);
        if (text == smartKey)
        {
            var descriptionKey = entry + ".description";
            text = _loc.Bilingual("powers", descriptionKey);
            if (text == descriptionKey)
                return null;
        }

        return CleanResolvedEngineText(InterpolateDynamicVars(text, vars) ?? text);
    }

    private static string? EnginePowerDescription(PowerModel power)
    {
        try
        {
            var description = power.HasSmartDescription
                ? power.SmartDescription
                : power.Description;
            if (power.Applier != null
                && !LocalContext.IsMe(power.Applier)
                && power.HasRemoteDescription)
            {
                description = power.RemoteDescription;
            }

            return EngineLocStringText(description, PowerDescriptionVars(power));
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, object?> PowerDescriptionVars(PowerModel power)
    {
        var vars = ExportDynamicVars(power) ?? new Dictionary<string, object?>();
        try
        {
            var owner = power.Owner;
            var playerCount = owner?.CombatState?.Players?.Count ?? 1;
            vars["Amount"] = power.Amount;
            vars["OnPlayer"] = owner?.IsPlayer ?? false;
            vars["IsMultiplayer"] = playerCount > 1;
            vars["PlayerCount"] = playerCount;
            vars["OwnerName"] = CreatureTitle(owner);
            if (power.Applier != null)
                vars["ApplierName"] = CreatureTitle(power.Applier);
            if (power.Target != null)
                vars["TargetName"] = CreatureTitle(power.Target);
        }
        catch { }
        return vars;
    }

    private static string CreatureTitle(Creature? creature)
    {
        if (creature == null)
            return "";
        if (creature.IsPlayer)
        {
            var character = creature.Player?.Character;
            return EngineLocStringText(character?.Title) ?? character?.Id.Entry ?? "Player";
        }

        var monster = creature.Monster;
        return EngineLocStringText(monster?.Title) ?? monster?.Id.Entry ?? "Enemy";
    }

    private static string? PowerOwnHoverTipTitle(PowerModel power)
    {
        return PowerOwnHoverTipText(power, "Title");
    }

    private static string? PowerOwnHoverTipDescription(PowerModel power)
    {
        return PowerOwnHoverTipText(power, "Description");
    }

    private static string? PowerOwnHoverTipText(PowerModel power, string memberName)
    {
        try
        {
            foreach (var tip in power.HoverTips)
            {
                if (!HoverTipMatchesPower(tip, power))
                    continue;

                var text = CleanResolvedEngineText(TryGetMember(tip, memberName) as string);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }

            var dumbHoverTip = power.DumbHoverTip;
            if (HoverTipMatchesPower(dumbHoverTip, power))
                return CleanResolvedEngineText(TryGetMember(dumbHoverTip, memberName) as string);
        }
        catch
        {
        }

        return null;
    }

    private static bool HoverTipMatchesPower(object? tip, PowerModel power)
    {
        if (tip == null)
            return false;

        var powerEntry = power.Id.Entry;
        var powerId = power.Id.ToString();
        var tipId = TryGetMember(tip, "Id")?.ToString();
        if (string.Equals(tipId, powerEntry, StringComparison.Ordinal)
            || string.Equals(tipId, powerId, StringComparison.Ordinal))
        {
            return true;
        }

        var canonicalModel = TryGetMember(tip, "CanonicalModel");
        if (ReferenceEquals(canonicalModel, power))
            return true;

        var canonicalEntry = ModelEntry(canonicalModel);
        return string.Equals(canonicalEntry, powerEntry, StringComparison.Ordinal);
    }

    private static string? ModelEntry(object? model)
    {
        try
        {
            var id = model?.GetType().GetProperty("Id")?.GetValue(model);
            if (id is string idString)
                return idString;
            return id?.GetType().GetProperty("Entry")?.GetValue(id)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string? MoveEntry(object? move)
    {
        return MoveEntry(move, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    private static string? MoveEntry(object? move, int depth, HashSet<object> seen)
    {
        var entry = ModelEntry(move);
        if (!string.IsNullOrWhiteSpace(entry))
            return entry;

        try
        {
            var typeName = move?.GetType().Name;
            if (string.IsNullOrWhiteSpace(typeName))
                return null;
            if (typeName != "MoveState" && typeName.EndsWith("Move", StringComparison.Ordinal))
                return MoveTypeToEntry(typeName);

            if (move == null || depth >= 2 || !seen.Add(move))
                return typeName == "MoveState" ? null : MoveTypeToEntry(typeName);

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var prop in move.GetType().GetProperties(flags))
            {
                if (prop.GetIndexParameters().Length > 0)
                    continue;
                var child = SafeGetProperty(prop, move);
                var childEntry = MoveEntry(child, depth + 1, seen);
                if (!string.IsNullOrWhiteSpace(childEntry))
                    return childEntry;
            }

            foreach (var field in move.GetType().GetFields(flags))
            {
                var child = SafeGetField(field, move);
                var childEntry = MoveEntry(child, depth + 1, seen);
                if (!string.IsNullOrWhiteSpace(childEntry))
                    return childEntry;
            }

            return typeName == "MoveState" ? null : MoveTypeToEntry(typeName);
        }
        catch
        {
            return null;
        }
    }

    private static object? SafeGetProperty(PropertyInfo prop, object target)
    {
        try
        {
            if (IsMoveEntryLeaf(prop.PropertyType))
                return null;
            return prop.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static object? SafeGetField(FieldInfo field, object target)
    {
        try
        {
            if (IsMoveEntryLeaf(field.FieldType))
                return null;
            return field.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsMoveEntryLeaf(Type type)
    {
        return type.IsPrimitive
            || type.IsEnum
            || type == typeof(string)
            || typeof(Delegate).IsAssignableFrom(type)
            || typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
    }

    private static string MoveTypeToEntry(string typeName)
    {
        if (typeName.EndsWith("Move", StringComparison.Ordinal))
            typeName = typeName[..^4];
        return System.Text.RegularExpressions.Regex
            .Replace(typeName, "([a-z0-9])([A-Z])", "$1_$2")
            .ToUpperInvariant();
    }

    private static Dictionary<string, object?>? ExportDynamicVars(object? model)
    {
        try
        {
            var dynamicVars = model?.GetType().GetProperty("DynamicVars")?.GetValue(model);
            var values = dynamicVars?.GetType().GetProperty("Values")?.GetValue(dynamicVars)
                         as System.Collections.IEnumerable;
            if (values == null)
                return null;

            var vars = new Dictionary<string, object?>();
            foreach (var value in values)
            {
                if (value == null)
                    continue;
                var name = value.GetType().GetProperty("Name")?.GetValue(value)?.ToString();
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                if (value is StringVar)
                {
                    vars[name] = value.ToString();
                    continue;
                }

                var baseValue = value.GetType().GetProperty("BaseValue")?.GetValue(value);
                vars[name] = baseValue;
            }
            return vars.Count > 0 ? vars : null;
        }
        catch
        {
            return null;
        }
    }

    private static int? GetPowerAmount(object? creature, string powerEntry)
    {
        try
        {
            var powers = creature?.GetType().GetProperty("Powers")?.GetValue(creature)
                         as System.Collections.IEnumerable;
            if (powers == null)
                return null;

            foreach (var power in powers)
            {
                var entry = ModelEntry(power);
                if (!string.Equals(entry, powerEntry, StringComparison.Ordinal))
                    continue;
                var amount = power?.GetType().GetProperty("Amount")?.GetValue(power);
                return amount is int intAmount ? intAmount : Convert.ToInt32(amount);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private Dictionary<string, object?> RestSiteState(RestSiteRoom restRoom)
    {
        var options = restRoom.Options;
        var player = _runState!.Players[0];

        if (options == null || options.Count == 0)
        {
            // Options empty = choice already made (synchronizer cleared them), go to map
            Log("Rest site: options empty, proceeding to map");
            ForceToMap();
            return MapSelectState();
        }

        var optionList = options.Select((opt, i) =>
        {
            var vars = ExportLocStringVariables(opt.Title);
            var descriptionVars = ExportLocStringVariables(opt.Description);
            if (descriptionVars != null)
            {
                vars ??= new Dictionary<string, object?>();
                foreach (var (key, value) in descriptionVars)
                    vars[key] = value;
            }

            var title = ResolveLocString(opt.Title, vars) ?? opt.OptionId;
            var description = ResolveLocString(opt.Description, vars);
            return new Dictionary<string, object?>
            {
                ["index"] = i,
                ["option_id"] = opt.OptionId,
                ["name"] = opt.GetType().Name,
                ["title"] = title,
                ["description"] = description,
                ["is_enabled"] = opt.IsEnabled,
                ["vars"] = vars?.Count > 0 ? vars : null,
            };
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "rest_site",
            ["context"] = RunContext(),
            ["options"] = optionList,
            ["player"] = PlayerSummary(player),
        };
    }

    private string? ResolveLocString(LocString? locString, Dictionary<string, object?>? vars = null)
    {
        if (locString == null || string.IsNullOrWhiteSpace(locString.LocEntryKey))
            return null;

        var engineText = EngineLocStringText(locString, vars);
        if (!string.IsNullOrWhiteSpace(engineText))
            return PreferDisplayVarInterpolation(locString, vars, engineText);

        var text = _loc.Bilingual(locString.LocTable, locString.LocEntryKey);
        if (text == locString.LocEntryKey)
            return null;
        var interpolated = InterpolateDynamicVars(text, vars);
        return CleanResolvedEngineText(interpolated);
    }

    private static string? LocalizedTableText(
        string table,
        string key,
        Dictionary<string, object?>? vars = null)
    {
        var text = _loc.Bilingual(table, key);
        if (text == key)
            return null;
        return CleanResolvedEngineText(InterpolateDynamicVars(text, vars));
    }

    private static string? PreferDisplayVarInterpolation(
        LocString? locString,
        Dictionary<string, object?>? vars,
        string engineText)
    {
        if (locString == null || vars == null || vars.Count == 0)
            return engineText;

        string? rawText;
        try
        {
            rawText = locString.GetRawText();
        }
        catch
        {
            return engineText;
        }

        var tokenNames = FormatTokenNames(rawText);
        if ((rawText.Contains("energyIcons", StringComparison.Ordinal)
             || rawText.Contains("starIcons", StringComparison.Ordinal)
             || rawText.Contains("singleStarIcon", StringComparison.Ordinal))
            && !engineText.Contains(CliEnergyToken, StringComparison.Ordinal)
            && !engineText.Contains(CliStarToken, StringComparison.Ordinal))
        {
            var interpolated = CleanResolvedEngineText(InterpolateDynamicVars(rawText, vars));
            if (!string.IsNullOrWhiteSpace(interpolated)
                && (interpolated.Contains(CliEnergyToken, StringComparison.Ordinal)
                    || interpolated.Contains(CliStarToken, StringComparison.Ordinal)))
                return interpolated;
        }

        var hasMissingStringDisplay = vars.Any(kv =>
            kv.Value is string textValue
            && tokenNames.Contains(kv.Key)
            && !string.IsNullOrWhiteSpace(textValue)
            && !engineText.Contains(textValue, StringComparison.Ordinal));

        var hasMissingNumericDisplay = vars.Any(kv =>
            IsNumericDisplayValue(kv.Value)
            && tokenNames.Contains(kv.Key)
            && !engineText.Contains(Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "", StringComparison.Ordinal));
        if (!hasMissingStringDisplay && !hasMissingNumericDisplay)
            return engineText;

        return CleanResolvedEngineText(InterpolateDynamicVars(rawText, vars)) ?? engineText;
    }

    private static string? EngineLocStringText(LocString? locString, Dictionary<string, object?>? vars = null)
    {
        try
        {
            if (locString == null)
                return null;

            var formattedLocString = locString;
            if (vars != null && vars.Count > 0)
            {
                formattedLocString = new LocString(locString.LocTable, locString.LocEntryKey);
                formattedLocString.AddVariablesFrom(locString);
                foreach (var (key, value) in vars)
                {
                    if (value != null)
                        formattedLocString.AddObj(key, value);
                }
            }

            var text = CleanResolvedEngineText(formattedLocString.GetFormattedText());
            if (text == locString.LocEntryKey)
                return null;
            return text;
        }
        catch
        {
            return null;
        }
    }

    private static string? CleanEngineText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"res://[A-Za-z0-9_./-]+/([A-Za-z0-9_.-]+\.(?:png|webp|jpg|jpeg))",
            "$1",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[(?![ES]\])/?[a-zA-Z_][a-zA-Z0-9_=]*\]", "");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"#[A-Z](?=\{|[A-Za-z0-9])", "");
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"(?<count>\d+)(?:[A-Za-z0-9_]*energy_icon\.png)",
            match => int.TryParse(match.Groups["count"].Value, out var count)
                ? count == 0 ? $"0{CliEnergyToken}" : CliEnergyTokens(count)
                : match.Value,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"(?<count>\d+)(?:[A-Za-z0-9_]*star_icon\.png)",
            match => int.TryParse(match.Groups["count"].Value, out var count)
                ? count == 0 ? $"0{CliStarToken}" : CliStarTokens(count)
                : match.Value,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"[A-Za-z0-9_]*energy_icon\.png",
            CliEnergyToken,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"[A-Za-z0-9_]*star_icon\.png",
            CliStarToken,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]{2,}", " ");
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? CleanResolvedEngineText(string? text)
    {
        var cleaned = CleanEngineText(text);
        return LooksLikeMojibake(cleaned)
               || LooksLikeUnresolvedLocKey(cleaned)
               || LooksLikeUnresolvedFormatterToken(cleaned)
            ? null
            : cleaned;
    }

    private static bool LooksLikeMojibake(string? text)
    {
        return !string.IsNullOrWhiteSpace(text)
               && (text.Contains('\uFFFD')
                   || text.Contains("鈥", StringComparison.Ordinal)
                   || text.Contains("馃", StringComparison.Ordinal)
                   || text.Contains("锛", StringComparison.Ordinal));
    }

    private static bool LooksLikeUnresolvedLocKey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsWhiteSpace))
            return false;

        return text.EndsWith(".title", StringComparison.Ordinal)
               || text.EndsWith(".description", StringComparison.Ordinal)
               || text.EndsWith(".smartDescription", StringComparison.Ordinal)
               || text.EndsWith(".remoteDescription", StringComparison.Ordinal)
               || text.EndsWith(".selectionScreenPrompt", StringComparison.Ordinal);
    }

    private static bool LooksLikeUnresolvedFormatterToken(string? text)
    {
        return !string.IsNullOrWhiteSpace(text)
               && System.Text.RegularExpressions.Regex.IsMatch(
                   text,
                   @"\{[^}]*\}");
    }

    private static Dictionary<string, object?>? ExportLocStringVariables(LocString? locString)
    {
        try
        {
            var variables = locString?.Variables;
            if (variables == null || variables.Count == 0)
                return null;

            var exported = new Dictionary<string, object?>();
            foreach (var (key, value) in variables)
                exported[key] = ExportLocStringVariableValue(value);
            return exported.Count > 0 ? exported : null;
        }
        catch
        {
            return null;
        }
    }

    private static object? ExportLocStringVariableValue(object? value)
    {
        if (value == null)
            return null;
        if (value is string or bool or int or long or short or byte or float or double or decimal)
            return value;
        if (value is DynamicVar dynamicVar)
        {
            if (dynamicVar is StringVar)
                return dynamicVar.ToString();
            return (int)dynamicVar.BaseValue;
        }

        var baseValue = value.GetType().GetProperty("BaseValue")?.GetValue(value);
        if (baseValue is string or bool or int or long or short or byte or float or double or decimal)
            return baseValue;

        var amount = value.GetType().GetProperty("Amount")?.GetValue(value);
        if (amount is string or bool or int or long or short or byte or float or double or decimal)
            return amount;

        return value.ToString();
    }

    private Dictionary<string, object?> ShopItemState(object entryKey, Dictionary<string, object?> current, bool hasDisplayModel)
    {
        if (hasDisplayModel)
        {
            _shopItemSnapshots[entryKey] = new Dictionary<string, object?>(current);
            return current;
        }

        if (!_shopItemSnapshots.TryGetValue(entryKey, out var snapshot))
            return current;

        var restored = new Dictionary<string, object?>(snapshot);
        if (restored.ContainsKey("card_cost"))
        {
            restored["price"] = current.GetValueOrDefault("price");
            restored["gold_cost"] = current.GetValueOrDefault("gold_cost");
        }
        else
        {
            restored["cost"] = current.GetValueOrDefault("cost");
            restored["price"] = current.GetValueOrDefault("price");
            restored["gold_cost"] = current.GetValueOrDefault("gold_cost");
        }
        restored["is_stocked"] = current.GetValueOrDefault("is_stocked");
        if (current.ContainsKey("can_buy"))
            restored["can_buy"] = current.GetValueOrDefault("can_buy");
        if (current.ContainsKey("on_sale"))
            restored["on_sale"] = current.GetValueOrDefault("on_sale");
        return restored;
    }

    private Dictionary<string, object?> ShopState(MerchantRoom merchantRoom, Player player)
    {
        var inv = merchantRoom.Inventory;
        if (inv == null) { ForceToMap(); return MapSelectState(); }

        var cards = inv.CharacterCardEntries.Concat(inv.ColorlessCardEntries)
            .Select((e, i) =>
            {
                var card = e.CreationResult?.Card;
                var entry = card?.Id.Entry ?? "?";
                var stats = new Dictionary<string, object?>();
                object cardCost = 0;
                try
                {
                    if (card != null)
                    {
                        cardCost = GetEnergyCostDisplay(card);
                        var mutable = ModelDb.GetById<CardModel>(card.Id).ToMutable();
                        stats = ExtractCardStats(mutable, _runState?.Players[0], card);
                    }
                }
                catch { }
                var shopkws = card?.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToList();
                var exported = new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["name"] = _loc.Card(entry),
                    ["type"] = card?.Type.ToString() ?? "?",
                    ["rarity"] = card?.Rarity.ToString() ?? "?",
                    ["cost"] = cardCost,
                    ["card_cost"] = cardCost,
                    ["description"] = card != null
                        ? CardDescription(card, stats)
                        : _loc.Bilingual("cards", entry + ".description"),
                    ["stats"] = stats.Count > 0 ? stats : null,
                    ["keywords"] = shopkws?.Count > 0 ? shopkws : null,
                    ["after_upgrade"] = card != null ? GetUpgradedInfo(card, _runState?.Players[0]) : null,
                    ["price"] = e.Cost,
                    ["gold_cost"] = e.Cost,
                    ["is_stocked"] = e.IsStocked,
                    ["on_sale"] = e.IsOnSale,
                    ["can_buy"] = e.IsStocked && player.Gold >= e.Cost,
                };
                if (card != null)
                {
                    AddCardVars(exported, card);
                    AddEnergyCostDetails(exported, card);
                    AddStarCostDetails(exported, card);
                    AddCardEnhancements(exported, card);
                    AddCardHoverTips(exported, card);
                }
                return ShopItemState(e, exported, card != null);
            }).ToList();

        var relics = inv.RelicEntries.Select((e, i) =>
        {
            var exported = e.Model != null
                ? RelicInfo(e.Model, index: i)
                : new Dictionary<string, object?> { ["index"] = i, ["name"] = "?", ["description"] = null };
            exported["cost"] = e.Cost;
            exported["price"] = e.Cost;
            exported["gold_cost"] = e.Cost;
            exported["is_stocked"] = e.IsStocked;
            exported["can_buy"] = e.IsStocked && player.Gold >= e.Cost;
            return ShopItemState(e, exported, e.Model != null);
        }).ToList();

        var potions = inv.PotionEntries.Select((e, i) =>
        {
            var exported = e.Model != null
                ? PotionInfo(e.Model, index: i)
                : new Dictionary<string, object?> { ["index"] = i, ["name"] = "?", ["description"] = null };
            exported["cost"] = e.Cost;
            exported["price"] = e.Cost;
            exported["gold_cost"] = e.Cost;
            exported["is_stocked"] = e.IsStocked;
            exported["can_buy"] = e.IsStocked && player.Gold >= e.Cost;
            return ShopItemState(e, exported, e.Model != null);
        }).ToList();

        var removal = merchantRoom.Inventory.CardRemovalEntry;
        var removalCost = removal != null && removal.IsStocked ? removal.Cost : (int?)null;

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "shop",
            ["context"] = RunContext(),
            ["cards"] = cards,
            ["relics"] = relics,
            ["potions"] = potions,
            ["card_removal_cost"] = removalCost,
            ["player"] = PlayerSummary(player),
        };
    }

    private Dictionary<string, object?> TreasureState(TreasureRoom treasureRoom)
    {
        // Treasure rooms give relics via TreasureRoomRelicSynchronizer
        Log("Treasure room: collecting rewards");

        // BUG-013: Ensure any pending relic picking session is complete before starting new one
        WaitForActionExecutor();
        _syncCtx.Pump();

        var synchronizer = RunManager.Instance.TreasureRoomRelicSynchronizer;
        if (synchronizer?.CurrentRelics == null)
        {
            try
            {
                treasureRoom.DoNormalRewards().GetAwaiter().GetResult();
                _syncCtx.Pump();
                treasureRoom.DoExtraRewardsIfNeeded().GetAwaiter().GetResult();
                _syncCtx.Pump();
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("relic picking session"))
            {
                // BUG-013: Relic session conflict means a choice is already pending.
                Log($"Relic session already pending: {ex.Message}");
            }
            catch (Exception ex) { Log($"Treasure rewards: {ex.Message}"); }
        }

        synchronizer = RunManager.Instance.TreasureRoomRelicSynchronizer;
        var relics = synchronizer?.CurrentRelics;
        if (relics == null)
        {
            return TreasureEmptyState("Treasure room produced no relic choices after engine rewards resolved");
        }

        if (relics.Count == 0)
        {
            CompleteEmptyTreasureRelicSessionIfNeeded();
            return TreasureEmptyState("Treasure relic session contains no choices");
        }

        var exportedRelics = relics.Select((relic, i) =>
        {
            if (relic == null)
                return new Dictionary<string, object?> { ["index"] = i, ["id"] = "?", ["name"] = "?", ["description"] = null };
            return RelicInfo(relic, index: i);
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "treasure",
            ["context"] = RunContext(),
            ["relics"] = exportedRelics,
            ["player"] = PlayerSummary(_runState!.Players[0]),
        };
    }

    private Dictionary<string, object?> TreasureEmptyState(string message)
    {
        Log($"Treasure room empty: {message}");
        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "treasure",
            ["context"] = RunContext(),
            ["message"] = message,
            ["relics"] = new List<object?>(),
            ["can_proceed"] = true,
            ["player"] = PlayerSummary(_runState!.Players[0]),
        };
    }

    private void CompleteEmptyTreasureRelicSessionIfNeeded()
    {
        try
        {
            var synchronizer = RunManager.Instance.TreasureRoomRelicSynchronizer;
            if (synchronizer.CurrentRelics != null && synchronizer.CurrentRelics.Count == 0)
            {
                Log("Treasure room: completing empty relic session");
                synchronizer.CompleteWithNoRelics();
                _syncCtx.Pump();
            }
        }
        catch (Exception ex)
        {
            Log($"Complete empty treasure relic session: {ex.Message}");
        }
    }

    private Dictionary<string, object?> DoClaimTreasureRelic(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("relic_index"))
            return Error("claim_relic requires 'relic_index'");

        var synchronizer = RunManager.Instance.TreasureRoomRelicSynchronizer;
        var relics = synchronizer?.CurrentRelics;
        if (relics == null)
            return Error("No pending treasure relic choices");

        var idx = Convert.ToInt32(args["relic_index"]);
        if (idx < 0 || idx >= relics.Count)
            return Error($"Invalid relic_index {idx}, treasure has {relics.Count} relic choices");

        try
        {
            if (relics.Count == 0)
            {
                Log("Treasure room: completing empty relic session");
                synchronizer!.CompleteWithNoRelics();
            }
            else
            {
                var relic = relics[idx];
                if (relic == null)
                    return Error($"Treasure relic choice {idx} is null");

                Log($"Treasure room: claiming relic {idx} ({relic.Id.Entry})");
                RelicCmd.Obtain(relic.ToMutable(), player, player.Relics.Count).GetAwaiter().GetResult();
                EndTreasureRelicVoting(synchronizer!);
            }

            _syncCtx.Pump();
            WaitForActionExecutor();
            _syncCtx.Pump();
            ForceToMap();
        }
        catch (Exception ex)
        {
            return Error($"Claim treasure relic failed: {ex.Message}");
        }

        return MapSelectState();
    }

    private static void EndTreasureRelicVoting(object synchronizer)
    {
        var endMethod = synchronizer.GetType().GetMethod("EndRelicVoting", NonPublic);
        endMethod?.Invoke(synchronizer, null);
    }

    private Dictionary<string, object?> GameOverState(bool isVictory)
    {
        var player = _runState!.Players[0];
        var summary = PlayerSummary(player);
        // Terminal rooms can mutate Creature HP after combat. Keep game_over summaries tied
        // to the last live combat HP while preserving death as zero.
        if (isVictory)
        {
            var exportedHp = summary.TryGetValue("hp", out var hpObj) && hpObj is int hp ? hp : 0;
            if (exportedHp <= 0 && _lastKnownHp > 0)
                summary["hp"] = _lastKnownHp;
        }
        else
        {
            summary["hp"] = _lastKnownHp > 0 ? 0 : (player.Creature?.CurrentHp ?? 0);
        }
        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "game_over",
            ["context"] = RunContext(),
            ["victory"] = isVictory,
            ["player"] = summary,
            ["act"] = _runState.CurrentActIndex + 1,
            ["floor"] = _runState.ActFloor,
        };
    }

    #endregion

    #region Helpers

    private void WaitForActionExecutor()
    {
        try
        {
            // Ensure sync context is set for this thread
            SynchronizationContext.SetSynchronizationContext(_syncCtx);

            // Pump the synchronization context to execute any pending continuations
            _syncCtx.Pump();

            // Executor may stay "running" while the game awaits headless card selection / reward (e.g. Attack Potion).
            // Spinning here would time out and downstream code could mis-handle an in-flight potion use (BUG-026).
            if (_cardSelector.HasPending || _cardSelector.HasPendingReward)
                return;

            var executor = RunManager.Instance.ActionExecutor;
            if (executor.IsRunning)
            {
                // Pump while waiting for executor
                int maxPumps = 1000;
                for (int i = 0; i < maxPumps; i++)
                {
                    _syncCtx.Pump();
                    if (!executor.IsRunning) break;
                    Thread.Sleep(1);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"WaitForActionExecutor exception: {ex.Message}");
        }
    }

    private void WaitForPendingEventOptionTask()
    {
        var task = _pendingEventOptionTask;
        if (task == null)
            return;

        for (int i = 0; i < 300; i++)
        {
            _syncCtx.Pump();
            WaitForActionExecutor();
            if (task.IsCompleted)
                break;
            if (_cardSelector.HasPending || _cardSelector.HasPendingReward || _pendingBundles != null)
                break;
            Thread.Sleep(10);
        }

        if (!task.IsCompleted)
            return;

        if (task.IsFaulted)
            Log($"Event option task failed: {task.Exception?.GetBaseException().Message}");
        _pendingEventOptionTask = null;
    }

    private void SpinWaitForCombatStable()
    {
        int maxIterations = 200;
        for (int i = 0; i < maxIterations; i++)
        {
            _syncCtx.Pump();
            if (!CombatManager.Instance.IsInProgress) return;
            if (CombatManager.Instance.IsPlayPhase) return;
            WaitForActionExecutor();
            if (CombatManager.Instance.IsPlayPhase || !CombatManager.Instance.IsInProgress) return;
            Thread.Sleep(5);
        }
    }

    private Dictionary<string, object?> ExtractCardStats(
        CardModel card,
        Player? player = null,
        CardModel? countAsCard = null,
        bool applyCombatModifiers = false,
        bool includeTargetRows = true)
    {
        var stats = new Dictionary<string, object?>();
        try
        {
            foreach (var dv in card.DynamicVars.Values)
                stats[dv.Name.ToLowerInvariant()] = (int)dv.BaseValue;
        }
        catch { }

        if (string.Equals(card.Id.Entry, "SPITE", StringComparison.OrdinalIgnoreCase)
            && player?.PlayerCombatState != null
            && player.Creature != null
            && !LostHpThisTurn(player.Creature))
        {
            stats["repeat"] = 1;
        }

        RemoveNonAttackRepeatStat(stats, card);

        if (applyCombatModifiers)
        {
            ApplyCardPreviewStats(stats, card, CardPreviewMode.Normal, target: null);
            AddEnergyXAttackRepeat(stats, card);
            AddHandExhaustAttackRepeat(stats, card, player);
            RemoveNonAttackRepeatStat(stats, card);
            if (includeTargetRows)
            {
                AddCalculatedDamageByTarget(stats, card, player);
                AddCalculatedDynamicVarsByTarget(stats, card);
                AddAttackDamageByTarget(stats, card, player);
            }
        }

        return stats;
    }

    private static bool ApplyCardPreviewStats(
        Dictionary<string, object?> stats,
        CardModel card,
        CardPreviewMode previewMode,
        Creature? target)
    {
        var previewStats = TryGetCardPreviewStats(card, previewMode, target);
        if (previewStats == null)
            return false;

        foreach (var (key, value) in previewStats)
        {
            if (HasCardSpecificOverride(stats, card, key))
                continue;
            stats[key] = value;
        }
        return true;
    }

    private static bool HasCardSpecificOverride(Dictionary<string, object?> stats, CardModel card, string key)
    {
        if (!stats.TryGetValue(key, out var currentValue) || currentValue == null)
            return false;

        try
        {
            var dynamicVar = card.DynamicVars.Values
                .FirstOrDefault(dv => string.Equals(dv.Name, key, StringComparison.OrdinalIgnoreCase));
            return dynamicVar != null && Convert.ToInt32(currentValue) != (int)dynamicVar.BaseValue;
        }
        catch
        {
            return false;
        }
    }

    private static void AddEnergyXAttackRepeat(Dictionary<string, object?> stats, CardModel card)
    {
        if (card.Type != CardType.Attack
            || !stats.ContainsKey("damage")
            || card.EnergyCost?.CostsX != true)
        {
            return;
        }

        try
        {
            stats["repeat"] = GetEnergyXAttackRepeat(card);
        }
        catch { }
    }

    private static void AddHandExhaustAttackRepeat(
        Dictionary<string, object?> stats,
        CardModel card,
        Player? player)
    {
        if (card.Type != CardType.Attack
            || !stats.ContainsKey("damage")
            || card is not FiendFire
            || card.Pile?.Type != PileType.Hand)
        {
            return;
        }

        try
        {
            var hand = player?.PlayerCombatState?.Hand?.Cards;
            if (hand == null)
                return;

            stats["repeat"] = Math.Max(0, hand.Count(c => c != null && !ReferenceEquals(c, card)));
        }
        catch { }
    }

    private static void RemoveNonAttackRepeatStat(Dictionary<string, object?> stats, CardModel card)
    {
        if (card.Type != CardType.Attack || !stats.ContainsKey("repeat"))
            return;
        if (UsesRepeatAsAttackHits(card))
            return;
        stats.Remove("repeat");
    }

    private static object GetEnergyCostDisplay(CardModel card)
    {
        try
        {
            if (card.EnergyCost?.CostsX == true)
                return "X";
            return card.EnergyCost?.GetResolved() ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int GetEnergyAmountToSpend(CardModel card)
    {
        try
        {
            return Math.Max(0, card.EnergyCost?.GetAmountToSpend() ?? 0);
        }
        catch
        {
            return 0;
        }
    }

    private static int GetEnergyXValue(CardModel card)
    {
        var amount = GetEnergyAmountToSpend(card);
        try
        {
            if (card.EnergyCost?.CostsX == true && card.CombatState != null)
                amount = Hook.ModifyXValue(card.CombatState, card, amount);
        }
        catch { }
        return Math.Max(0, amount);
    }

    private static int GetEnergyXAttackRepeat(CardModel card)
    {
        var repeat = GetEnergyXValue(card);
        if (repeat <= 0)
            return repeat;

        if (card is HeavenlyDrill
            && GetCardDynamicVarInt(card, "Energy") is { } threshold
            && repeat >= threshold)
        {
            return repeat * 2;
        }

        return repeat;
    }

    private static void AddEnergyCostDetails(
        Dictionary<string, object?> cardInfo,
        CardModel card,
        bool includeCurrentXValue = false)
    {
        try
        {
            if (card.EnergyCost?.CostsX != true)
                return;
            if (!includeCurrentXValue)
                return;

            cardInfo["energy_cost"] = GetEnergyAmountToSpend(card);
            cardInfo["x_value"] = GetEnergyXValue(card);
        }
        catch { }
    }

    private static void AddStarCostDetails(Dictionary<string, object?> cardInfo, CardModel card)
    {
        try
        {
            var starCost = TryGetCurrentStarCost(card);
            if (starCost > 0)
                cardInfo["star_cost"] = starCost;
        }
        catch { }
    }

    private static void AddCardVars(
        Dictionary<string, object?> cardInfo,
        CardModel card,
        bool includePreviewStats = false)
    {
        var vars = ExportCardDescriptionVars(card, includePreviewStats: includePreviewStats);
        if (vars != null && vars.Count > 0)
            cardInfo["vars"] = vars;
    }

    private static Dictionary<string, int>? TryGetCardPreviewStats(
        CardModel card,
        CardPreviewMode previewMode,
        Creature? target)
    {
        try
        {
            var dynamicVars = card.DynamicVars.Clone(card);
            dynamicVars.ClearPreview();
            card.UpdateDynamicVarPreview(previewMode, target, dynamicVars);

            var stats = new Dictionary<string, int>();
            foreach (var dv in dynamicVars.Values)
                stats[dv.Name.ToLowerInvariant()] = (int)dv.PreviewValue;
            return stats;
        }
        catch
        {
            return null;
        }
    }

    private static bool LostHpThisTurn(Creature creature)
    {
        try
        {
            return CombatManager.Instance.History.Entries
                .OfType<DamageReceivedEntry>()
                .Any(entry => entry.HappenedThisTurn(creature.CombatState)
                              && entry.Receiver == creature
                              && entry.Result.UnblockedDamage > 0);
        }
        catch
        {
            return false;
        }
    }

    private void AddAttackDamageByTarget(Dictionary<string, object?> stats, CardModel card, Player? player)
    {
        if (card.Type != CardType.Attack
            || !stats.TryGetValue("damage", out var damageObj)
            || damageObj == null)
        {
            return;
        }

        var rows = new List<Dictionary<string, object?>>();
        try
        {
            var combatState = CombatManager.Instance.DebugOnlyGetState();
            var enemies = combatState?.Enemies?
                .Where(e => e != null && e.IsAlive)
                .ToList();
            if (enemies == null || enemies.Count == 0)
                return;

            var repeat = GetStatInt(stats, "repeat", 1);
            var usesRepeatAsAttackHits = UsesRepeatAsAttackHits(card);
            var pendingStrengthDelta = GetPendingStarSpendStrengthDelta(card, player);
            var pendingRepeatDelta = GetPendingOnPlayAttackRepeatDelta(card, player);
            for (int i = 0; i < enemies.Count; i++)
            {
                var enemy = enemies[i];
                var previewStats = TryGetCardPreviewStats(card, CardPreviewMode.MultiCreatureTargeting, enemy);
                var vulnerable = GetCreaturePowerAmount(enemy, "VULNERABLE", "Vulnerable");
                var slow = GetCreaturePowerAmount(enemy, "SLOW", "Slow");
                var intangible = GetCreaturePowerAmount(enemy, "INTANGIBLE", "Intangible") > 0;
                var untargetedDamage = Convert.ToInt32(damageObj);
                var targetDamage = previewStats?.GetValueOrDefault("damage") ?? untargetedDamage;
                if (pendingStrengthDelta > 0 && !intangible)
                {
                    targetDamage = AdjustTargetDamageForPendingStrength(
                        untargetedDamage,
                        targetDamage,
                        pendingStrengthDelta,
                        vulnerable);
                }
                var previewRepeat = usesRepeatAsAttackHits
                    && previewStats != null
                    && previewStats.TryGetValue("repeat", out var repeatValue)
                    ? repeatValue
                    : repeat;
                if (stats.ContainsKey("calculatedhits"))
                    previewRepeat = GetStatInt(stats, "calculatedhits", previewRepeat);
                if (HasCardSpecificOverride(stats, card, "repeat"))
                    previewRepeat = repeat;
                var targetRepeat = GetTargetAttackRepeat(card, enemy, previewRepeat + pendingRepeatDelta);
                var totalDamage = targetDamage * targetRepeat;
                var slippery = GetCreaturePowerAmount(enemy, "SLIPPERY", "Slippery");
                if (!intangible
                    && slippery > 0
                    && targetRepeat > slippery
                    && targetDamage <= 1)
                {
                    var uncappedTargetDamage = EstimateUncappedTargetDamage(
                        untargetedDamage,
                        pendingStrengthDelta,
                        vulnerable);
                    totalDamage = targetDamage * slippery
                        + uncappedTargetDamage * (targetRepeat - slippery);
                }
                var block = Math.Max(0, enemy.Block);
                var row = new Dictionary<string, object?>
                {
                    ["target_index"] = i,
                    ["target_name"] = MonsterDisplayName(enemy.Monster, enemy),
                    ["vulnerable"] = vulnerable,
                    ["block"] = block,
                    ["damage"] = targetDamage,
                };
                if (slow > 0)
                {
                    row["slow"] = slow;
                }
                if (pendingStrengthDelta > 0)
                {
                    row["pre_attack_strength_delta"] = pendingStrengthDelta;
                }
                if (pendingRepeatDelta > 0)
                {
                    row["pending_on_play_repeat_delta"] = pendingRepeatDelta;
                }
                if (targetRepeat != 1)
                {
                    row["repeat"] = targetRepeat;
                    row["total_damage"] = totalDamage;
                    row["unblocked_total_damage"] = Math.Max(0, totalDamage - block);
                }
                else
                {
                    row["unblocked_damage"] = Math.Max(0, targetDamage - block);
                }
                rows.Add(row);
            }
        }
        catch
        {
            return;
        }

        if (rows.Count > 0)
            stats["damage_by_target"] = rows;
    }

    private static int EstimateUncappedTargetDamage(
        int untargetedDamage,
        int pendingStrengthDelta,
        int vulnerable)
    {
        var damage = Math.Max(0, untargetedDamage + Math.Max(0, pendingStrengthDelta));
        if (vulnerable > 0)
            damage = (int)Math.Floor(damage * 1.5m);
        return damage;
    }

    private static int AdjustTargetDamageForPendingStrength(
        int untargetedDamage,
        int targetDamage,
        int strengthDelta,
        int vulnerable)
    {
        var knownCurrentTargetDamage = untargetedDamage;
        var knownAdjustedTargetDamage = untargetedDamage + strengthDelta;

        if (vulnerable > 0)
        {
            knownCurrentTargetDamage = (int)Math.Floor(knownCurrentTargetDamage * 1.5m);
            knownAdjustedTargetDamage = (int)Math.Floor(knownAdjustedTargetDamage * 1.5m);
        }

        var otherTargetAdjustment = targetDamage - knownCurrentTargetDamage;
        return Math.Max(0, knownAdjustedTargetDamage + otherTargetAdjustment);
    }

    private static int TryGetCurrentStarCost(CardModel card)
    {
        try
        {
            return card.CurrentStarCost;
        }
        catch
        {
            return 0;
        }
    }

    private void SyncStarSpendTrackerToCurrentCombat(object? combatState)
    {
        if (combatState == null)
        {
            _starSpendTrackerCombatState = null;
            _starSpendObservedRound = null;
            return;
        }

        if (!ReferenceEquals(_starSpendTrackerCombatState, combatState))
        {
            _starSpendTrackerCombatState = combatState;
            _starSpendObservedRound = null;
        }
    }

    private void MarkStarsSpentThisTurn()
    {
        var combatState = CombatManager.Instance.DebugOnlyGetState();
        SyncStarSpendTrackerToCurrentCombat(combatState);
        if (combatState != null)
            _starSpendObservedRound = TryGetIntMember(combatState, "RoundNumber", "Round");
    }

    private int GetPendingStarSpendStrengthDelta(CardModel card, Player? player)
    {
        if (card.Type != CardType.Attack || player?.PlayerCombatState == null)
            return 0;

        int starCost;
        try
        {
            starCost = card.CurrentStarCost;
        }
        catch
        {
            return 0;
        }

        if (starCost <= 0 || player.PlayerCombatState.Stars < starCost)
            return 0;

        var combatState = CombatManager.Instance.DebugOnlyGetState();
        SyncStarSpendTrackerToCurrentCombat(combatState);
        var round = combatState != null ? TryGetIntMember(combatState, "RoundNumber", "Round") : null;
        if (round.HasValue && _starSpendObservedRound == round.Value)
            return 0;

        var starsSpentThisTurn = TryGetIntMember(
            player.PlayerCombatState,
            "StarsSpent",
            "_starsSpent",
            "LastStarsSpent",
            "_lastStarsSpent");
        if (starsSpentThisTurn.GetValueOrDefault() > 0)
            return 0;

        var delta = 0;
        try
        {
            foreach (var relic in player.Relics ?? Enumerable.Empty<RelicModel>())
            {
                if (!string.Equals(relic.Id.Entry, "MINI_REGENT", StringComparison.OrdinalIgnoreCase))
                    continue;

                var vars = RelicVars(relic);
                if (vars.TryGetValue("StrengthPower", out var value) && value != null)
                    delta += Convert.ToInt32(value);
            }
        }
        catch { }

        return delta;
    }

    private int GetPendingOnPlayAttackRepeatDelta(CardModel card, Player? player)
    {
        if (card.Type != CardType.Attack || player?.Creature == null)
            return 0;
        if (!string.Equals(card.Id.Entry, "RADIATE", StringComparison.OrdinalIgnoreCase))
            return 0;

        return GetCreaturePowerAmount(
            player.Creature,
            "THE_SEALED_THRONE_POWER",
            "The Sealed Throne");
    }

    private void AddCalculatedDamageByTarget(Dictionary<string, object?> stats, CardModel card, Player? player)
    {
        if (!HasDynamicVar(card, "CalculatedDamage"))
            return;

        var rows = new List<Dictionary<string, object?>>();
        try
        {
            var combatState = CombatManager.Instance.DebugOnlyGetState();
            var enemies = combatState?.Enemies?
                .Where(e => e != null && e.IsAlive)
                .ToList();
            if (enemies == null || enemies.Count == 0)
                return;

            for (int i = 0; i < enemies.Count; i++)
            {
                var enemy = enemies[i];
                var previewStats = TryGetCardPreviewStats(card, CardPreviewMode.MultiCreatureTargeting, enemy);
                if (previewStats == null || !previewStats.TryGetValue("calculateddamage", out var calculatedDamage))
                    continue;

                var row = new Dictionary<string, object?>
                {
                    ["target_index"] = i,
                    ["target_name"] = MonsterDisplayName(enemy.Monster, enemy),
                    ["vulnerable"] = GetCreaturePowerAmount(enemy, "VULNERABLE", "Vulnerable"),
                    ["block"] = Math.Max(0, enemy.Block),
                    ["calculateddamage"] = calculatedDamage,
                };
                rows.Add(row);
            }
        }
        catch
        {
            return;
        }

        if (rows.Count > 0)
            stats["calculateddamage_by_target"] = rows;
    }

    private void AddCalculatedDynamicVarsByTarget(Dictionary<string, object?> stats, CardModel card)
    {
        List<string> statKeys;
        try
        {
            statKeys = card.DynamicVars.Values
                .Select(dv => dv.Name)
                .Where(name => name.StartsWith("Calculated", StringComparison.OrdinalIgnoreCase)
                               && !string.Equals(name, "CalculatedDamage", StringComparison.OrdinalIgnoreCase))
                .Select(name => name.ToLowerInvariant())
                .Distinct()
                .ToList();
        }
        catch
        {
            return;
        }

        if (statKeys.Count == 0)
            return;

        try
        {
            var combatState = CombatManager.Instance.DebugOnlyGetState();
            var enemies = combatState?.Enemies?
                .Where(e => e != null && e.IsAlive)
                .ToList();
            if (enemies == null || enemies.Count == 0)
                return;

            foreach (var statKey in statKeys)
            {
                var rows = new List<Dictionary<string, object?>>();
                for (int i = 0; i < enemies.Count; i++)
                {
                    var enemy = enemies[i];
                    var previewStats = TryGetCardPreviewStats(card, CardPreviewMode.MultiCreatureTargeting, enemy);
                    if (previewStats == null || !previewStats.TryGetValue(statKey, out var value))
                        continue;

                    if (string.Equals(statKey, "calculateddoom", StringComparison.OrdinalIgnoreCase))
                        value = GetTargetCalculatedDoom(card, enemy, value);

                    rows.Add(new Dictionary<string, object?>
                    {
                        ["target_index"] = i,
                        ["target_name"] = MonsterDisplayName(enemy.Monster, enemy),
                        [statKey] = value,
                    });
                }

                if (rows.Count > 0)
                    stats[$"{statKey}_by_target"] = rows;
            }
        }
        catch
        {
            return;
        }
    }

    private int GetTargetCalculatedDoom(CardModel card, Creature target, int previewValue)
    {
        var baseDoom = GetCardDynamicVarInt(card, "CalculationBase");
        var threshold = GetCardDynamicVarInt(card, "DoomThreshold");
        var extra = GetCardDynamicVarInt(card, "CalculationExtra");
        if (!baseDoom.HasValue || !threshold.HasValue || threshold.Value <= 0 || !extra.HasValue)
            return previewValue;

        var currentDoom = GetCreaturePowerAmount(target, "DOOM", "Doom");
        return baseDoom.Value + extra.Value * (currentDoom / threshold.Value);
    }

    private static int GetTargetAttackRepeat(CardModel card, Creature target, int baseRepeat)
    {
        var repeat = Math.Max(0, baseRepeat);
        var staticHitCount = GetStaticAttackHitCount(card);
        if (staticHitCount.HasValue && repeat == 1)
            repeat = staticHitCount.Value;
        if (repeat > 0
            && card is MegaCrit.Sts2.Core.Models.Cards.Dismantle
            && target.HasPower<VulnerablePower>())
            repeat *= 2;
        return repeat;
    }

    private static bool UsesRepeatAsAttackHits(CardModel card)
    {
        if (card.Type != CardType.Attack)
            return false;
        if (card.EnergyCost?.CostsX == true)
            return true;
        if (card is FiendFire)
            return true;
        if (CardUsesAttackHitCount(card))
            return true;
        return false;
    }

    private static bool CardUsesAttackHitCount(CardModel card)
    {
        var type = card.GetType();
        if (UsesAttackHitCountByCardType.TryGetValue(type, out var cached))
            return cached;

        var usesHitCount = FindUsesAttackHitCount(type);
        UsesAttackHitCountByCardType[type] = usesHitCount;
        return usesHitCount;
    }

    private static bool FindUsesAttackHitCount(Type cardType)
    {
        var withHitCount = typeof(AttackCommand).GetMethod(
            nameof(AttackCommand.WithHitCount),
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: new[] { typeof(int) },
            modifiers: null);
        if (withHitCount == null)
            return false;

        var methods = cardType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Select(t => t.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic))
            .Where(m => m != null);

        return methods.Any(method => MethodCalls(method!, withHitCount));
    }

    private static int? GetStaticAttackHitCount(CardModel card)
    {
        var type = card.GetType();
        if (StaticAttackHitCountByCardType.TryGetValue(type, out var cached))
            return cached;

        var hitCount = FindStaticAttackHitCount(type);
        StaticAttackHitCountByCardType[type] = hitCount;
        return hitCount;
    }

    private static int? FindStaticAttackHitCount(Type cardType)
    {
        var withHitCount = typeof(AttackCommand).GetMethod(
            nameof(AttackCommand.WithHitCount),
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: new[] { typeof(int) },
            modifiers: null);
        if (withHitCount == null)
            return null;

        var methods = cardType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Select(t => t.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic))
            .Where(m => m != null);

        foreach (var method in methods)
        {
            var hitCount = FindConstantArgumentForCall(method!, withHitCount);
            if (hitCount.HasValue && hitCount.Value > 1)
                return hitCount.Value;
        }

        return null;
    }

    private static int? FindConstantArgumentForCall(MethodInfo method, MethodInfo target)
    {
        byte[]? il;
        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch
        {
            return null;
        }

        if (il == null)
            return null;

        int? previousIntConstant = null;
        OpCode? previousOpCode = null;
        OpCode? opCodeBeforePrevious = null;
        var module = method.Module;
        for (var offset = 0; offset < il.Length;)
        {
            if (!TryReadOpCode(il, ref offset, out var opCode))
                return null;

            var operandOffset = offset;
            var operandSize = GetOperandSize(opCode, il, operandOffset);
            if (operandSize < 0 || operandOffset + operandSize > il.Length)
                return null;

            if ((opCode == OpCodes.Call || opCode == OpCodes.Callvirt) && operandSize == 4)
            {
                var token = BitConverter.ToInt32(il, operandOffset);
                if (IsResolvedMethod(module, token, target)
                    && previousIntConstant.HasValue
                    && opCodeBeforePrevious.HasValue
                    && IsLikelyAttackCommandReceiverLoad(opCodeBeforePrevious.Value))
                {
                    return previousIntConstant.Value;
                }
            }

            var currentIntConstant = TryReadIntConstant(opCode, il, operandOffset, operandSize);
            offset = operandOffset + operandSize;
            opCodeBeforePrevious = previousOpCode;
            previousOpCode = opCode;
            previousIntConstant = currentIntConstant;
        }

        return null;
    }

    private static bool MethodCalls(MethodInfo method, MethodInfo target)
    {
        byte[]? il;
        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch
        {
            return false;
        }

        if (il == null)
            return false;

        var module = method.Module;
        for (var offset = 0; offset < il.Length;)
        {
            if (!TryReadOpCode(il, ref offset, out var opCode))
                return false;

            var operandOffset = offset;
            var operandSize = GetOperandSize(opCode, il, operandOffset);
            if (operandSize < 0 || operandOffset + operandSize > il.Length)
                return false;

            if ((opCode == OpCodes.Call || opCode == OpCodes.Callvirt) && operandSize == 4)
            {
                var token = BitConverter.ToInt32(il, operandOffset);
                if (IsResolvedMethod(module, token, target))
                    return true;
            }

            offset = operandOffset + operandSize;
        }

        return false;
    }

    private static bool IsLikelyAttackCommandReceiverLoad(OpCode opCode)
    {
        return opCode == OpCodes.Call
               || opCode == OpCodes.Callvirt
               || opCode == OpCodes.Dup
               || opCode == OpCodes.Ldloc
               || opCode == OpCodes.Ldloc_S
               || opCode == OpCodes.Ldloc_0
               || opCode == OpCodes.Ldloc_1
               || opCode == OpCodes.Ldloc_2
               || opCode == OpCodes.Ldloc_3
               || opCode == OpCodes.Ldarg
               || opCode == OpCodes.Ldarg_S
               || opCode == OpCodes.Ldarg_0
               || opCode == OpCodes.Ldarg_1
               || opCode == OpCodes.Ldarg_2
               || opCode == OpCodes.Ldarg_3;
    }

    private static bool TryReadOpCode(byte[] il, ref int offset, out OpCode opCode)
    {
        opCode = default;
        if (offset >= il.Length)
            return false;

        var value = il[offset++];
        short key;
        if (value == 0xFE)
        {
            if (offset >= il.Length)
                return false;
            key = (short)(0xFE00 | il[offset++]);
        }
        else
        {
            key = value;
        }

        return OpCodeByValue.TryGetValue(key, out opCode);
    }

    private static int GetOperandSize(OpCode opCode, byte[] il, int operandOffset)
    {
        return opCode.OperandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineI => 1,
            OperandType.ShortInlineVar => 1,
            OperandType.ShortInlineBrTarget => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI => 4,
            OperandType.InlineBrTarget => 4,
            OperandType.InlineField => 4,
            OperandType.InlineMethod => 4,
            OperandType.InlineSig => 4,
            OperandType.InlineString => 4,
            OperandType.InlineTok => 4,
            OperandType.InlineType => 4,
            OperandType.ShortInlineR => 4,
            OperandType.InlineI8 => 8,
            OperandType.InlineR => 8,
            OperandType.InlineSwitch => GetInlineSwitchSize(il, operandOffset),
            _ => -1,
        };
    }

    private static int GetInlineSwitchSize(byte[] il, int operandOffset)
    {
        if (operandOffset + 4 > il.Length)
            return -1;
        var count = BitConverter.ToInt32(il, operandOffset);
        if (count < 0)
            return -1;
        return 4 + count * 4;
    }

    private static int? TryReadIntConstant(OpCode opCode, byte[] il, int operandOffset, int operandSize)
    {
        if (opCode == OpCodes.Ldc_I4_M1) return -1;
        if (opCode == OpCodes.Ldc_I4_0) return 0;
        if (opCode == OpCodes.Ldc_I4_1) return 1;
        if (opCode == OpCodes.Ldc_I4_2) return 2;
        if (opCode == OpCodes.Ldc_I4_3) return 3;
        if (opCode == OpCodes.Ldc_I4_4) return 4;
        if (opCode == OpCodes.Ldc_I4_5) return 5;
        if (opCode == OpCodes.Ldc_I4_6) return 6;
        if (opCode == OpCodes.Ldc_I4_7) return 7;
        if (opCode == OpCodes.Ldc_I4_8) return 8;
        if (opCode == OpCodes.Ldc_I4_S && operandSize == 1)
            return (sbyte)il[operandOffset];
        if (opCode == OpCodes.Ldc_I4 && operandSize == 4)
            return BitConverter.ToInt32(il, operandOffset);
        return null;
    }

    private static bool IsResolvedMethod(Module module, int token, MethodInfo target)
    {
        try
        {
            return module.ResolveMethod(token) is MethodInfo method
                   && method.Module == target.Module
                   && method.MetadataToken == target.MetadataToken;
        }
        catch
        {
            return false;
        }
    }

    private int GetCreaturePowerAmount(Creature? creature, params string[] powerKeys)
    {
        var total = 0;
        try
        {
            var powers = creature?.Powers;
            if (powers == null)
                return 0;

            foreach (var power in powers)
            {
                try
                {
                    var entry = power.Id.Entry;
                    var normalizedEntry = entry.EndsWith("_POWER", StringComparison.OrdinalIgnoreCase)
                        ? entry[..^"_POWER".Length]
                        : entry;
                    var localizedName = _loc.Power(entry);
                    if (powerKeys.Any(key => string.Equals(localizedName, key, StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(entry, key, StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(normalizedEntry, key, StringComparison.OrdinalIgnoreCase)
                                             || entry.Contains(key, StringComparison.OrdinalIgnoreCase)))
                    {
                        total += power.Amount;
                    }
                }
                catch { }
            }
        }
        catch
        {
            return total;
        }
        return total;
    }

    private void AddCardEnhancements(Dictionary<string, object?> cardInfo, CardModel card)
    {
        if (card.Enchantment != null)
        {
            var entry = card.Enchantment.Id.Entry;
            cardInfo["enchantment"] =
                EngineLocStringText(card.Enchantment.Title)
                ?? LocalizedTableText("enchantments", entry + ".title")
                ?? entry;
            cardInfo["enchantment_id"] = entry;
            var vars = ExportEnhancementVars(card.Enchantment);
            cardInfo["enchantment_description"] =
                EngineLocStringText(card.Enchantment.DynamicDescription, vars)
                ?? LocalizedTableText("enchantments", entry + ".description", vars);
            cardInfo["enchantment_vars"] = vars;
            try
            {
                if (card.Enchantment.Amount != 0)
                    cardInfo["enchantment_amount"] = card.Enchantment.Amount;
            }
            catch { }
        }

        if (card.Affliction != null)
        {
            var entry = card.Affliction.Id.Entry;
            cardInfo["affliction"] =
                EngineLocStringText(card.Affliction.Title)
                ?? LocalizedTableText("afflictions", entry + ".title")
                ?? entry;
            cardInfo["affliction_id"] = entry;
            var vars = ExportEnhancementVars(card.Affliction);
            cardInfo["affliction_description"] =
                EngineLocStringText(card.Affliction.DynamicDescription, vars)
                ?? LocalizedTableText("afflictions", entry + ".description", vars);
            cardInfo["affliction_vars"] = vars;
            try
            {
                if (card.Affliction.Amount != 0)
                    cardInfo["affliction_amount"] = card.Affliction.Amount;
            }
            catch { }
        }
    }

    private static Dictionary<string, object?>? ExportEnhancementVars(object enhancement)
    {
        var vars = ExportDynamicVars(enhancement) ?? new Dictionary<string, object?>();
        try
        {
            var amount = enhancement.GetType().GetProperty("Amount")?.GetValue(enhancement);
            if (amount != null)
                vars["Amount"] = amount;
        }
        catch { }
        return vars.Count > 0 ? vars : null;
    }

    private static int GetStatInt(Dictionary<string, object?> stats, string key, int fallback)
    {
        return stats.TryGetValue(key, out var value) && value != null
            ? Convert.ToInt32(value)
            : fallback;
    }

    private static bool HasDynamicVar(CardModel card, string name)
    {
        try
        {
            return card.DynamicVars.Values.Any(dv => string.Equals(dv.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static int? GetCardDynamicVarInt(CardModel card, string name)
    {
        try
        {
            return card.DynamicVars.Values
                .FirstOrDefault(dv => string.Equals(dv.Name, name, StringComparison.OrdinalIgnoreCase))
                ?.IntValue;
        }
        catch
        {
            return null;
        }
    }

    private object? ExportEventDynamicVar(string eventEntry, DynamicVar dynamicVar)
    {
        var name = dynamicVar.Name;

        if (string.Equals(eventEntry, "LOST_WISP", StringComparison.OrdinalIgnoreCase)
            && string.Equals(name, "Curse", StringComparison.OrdinalIgnoreCase))
        {
            return _loc.Card("DECAY");
        }

        if (string.Equals(eventEntry, "LOST_WISP", StringComparison.OrdinalIgnoreCase)
            && string.Equals(name, "Relic", StringComparison.OrdinalIgnoreCase))
        {
            return _loc.Relic("LOST_WISP");
        }

        if (dynamicVar is StringVar)
        {
            var stringValue = dynamicVar.ToString();
            if (!string.IsNullOrWhiteSpace(stringValue))
                return _loc.BilingualFromKey(stringValue);
        }

        var rawValue = (int)dynamicVar.BaseValue;

        if (string.Equals(name, "RandomCard", StringComparison.OrdinalIgnoreCase))
        {
            var cards = _runState?.Players[0].Deck?.Cards?.Where(c => c != null).ToList();
            if (cards != null && rawValue >= 0 && rawValue < cards.Count)
                return _loc.Card(cards[rawValue].Id.Entry);
        }

        if (string.Equals(eventEntry, "BYRDONIS_NEST", StringComparison.OrdinalIgnoreCase)
            && string.Equals(name, "Card", StringComparison.OrdinalIgnoreCase))
        {
            return _loc.Card("BYRDONIS_EGG");
        }

        if (string.Equals(eventEntry, "BUGSLAYER", StringComparison.OrdinalIgnoreCase)
            && string.Equals(name, "Card1", StringComparison.OrdinalIgnoreCase))
        {
            return _loc.Card("EXTERMINATE");
        }

        if (string.Equals(eventEntry, "BUGSLAYER", StringComparison.OrdinalIgnoreCase)
            && string.Equals(name, "Card2", StringComparison.OrdinalIgnoreCase))
        {
            return _loc.Card("SQUASH");
        }

        if (string.Equals(eventEntry, "LOST_WISP", StringComparison.OrdinalIgnoreCase)
            && string.Equals(name, "Curse", StringComparison.OrdinalIgnoreCase))
        {
            return _loc.Card("DECAY");
        }

        if (string.Equals(eventEntry, "LOST_WISP", StringComparison.OrdinalIgnoreCase)
            && string.Equals(name, "Relic", StringComparison.OrdinalIgnoreCase))
        {
            return _loc.Relic("LOST_WISP");
        }

        if (string.Equals(eventEntry, "RANWID_THE_ELDER", StringComparison.OrdinalIgnoreCase)
            && string.Equals(name, "Potion", StringComparison.OrdinalIgnoreCase))
        {
            var potions = _runState?.Players[0].Potions?.Where(p => p != null).ToList();
            if (potions != null && rawValue >= 0 && rawValue < potions.Count)
                return _loc.Potion(potions[rawValue].Id.Entry);
        }

        if (string.Equals(eventEntry, "RANWID_THE_ELDER", StringComparison.OrdinalIgnoreCase)
            && string.Equals(name, "Relic", StringComparison.OrdinalIgnoreCase))
        {
            var relics = _runState?.Players[0].Relics?.Where(r => r != null).ToList();
            if (relics != null && rawValue >= 0 && rawValue < relics.Count)
                return _loc.Relic(relics[rawValue].Id.Entry);
        }

        return rawValue;
    }

    /// <summary>Compute what a card would look like after upgrading (stats + cost + description).</summary>
    private Dictionary<string, object?>? GetUpgradedInfo(
        CardModel card,
        Player? player = null,
        bool applyCombatModifiers = false,
        bool includeTargetRows = true,
        bool useSourceDynamicContext = false)
    {
        if (!card.IsUpgradable) return null;
        try
        {
            var clone = (CardModel)card.MutableClone();
            clone.UpgradeInternal();
            clone.FinalizeUpgradeInternal();

            var stats = ExtractCardStats(
                clone,
                player,
                card,
                applyCombatModifiers: applyCombatModifiers,
                includeTargetRows: includeTargetRows);
            if (useSourceDynamicContext)
                ApplySourceDynamicUpgradeContext(stats, card, clone, player, applyCombatModifiers, includeTargetRows);

            // Compare keywords before/after upgrade
            var oldKws = card.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToHashSet() ?? new();
            var newKws = clone.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToHashSet() ?? new();
            var addedKws = newKws.Except(oldKws).ToList();
            var removedKws = oldKws.Except(newKws).ToList();

            var info = new Dictionary<string, object?>
            {
                ["cost"] = GetEnergyCostDisplay(clone),
                ["stats"] = stats.Count > 0 ? stats : null,
                ["description"] = CardDescriptionWithSourceEnhancements(
                    clone,
                    card,
                    stats,
                    includeCombatText: applyCombatModifiers,
                    preferStatsText: useSourceDynamicContext),
                ["added_keywords"] = addedKws.Count > 0 ? addedKws : null,
                ["removed_keywords"] = removedKws.Count > 0 ? removedKws : null,
            };
            AddCardVars(info, clone, includePreviewStats: applyCombatModifiers);
            AddEnergyCostDetails(info, clone);
            AddStarCostDetails(info, clone);
            AddCardEnhancements(info, card);
            return info;
        }
        catch { return null; }
    }

    private void ApplySourceDynamicUpgradeContext(
        Dictionary<string, object?> upgradedStats,
        CardModel sourceCard,
        CardModel upgradedCard,
        Player? player,
        bool applyCombatModifiers,
        bool includeTargetRows)
    {
        // Upgrade clones can lose the source card's pile-derived dynamic context.
        // Keep the preview tied to the source card's exported dynamic vars.
        var sourceStats = ExtractCardStats(
            sourceCard,
            player,
            applyCombatModifiers: applyCombatModifiers,
            includeTargetRows: includeTargetRows);
        ApplyLinearDynamicUpgradeContext(
            upgradedStats,
            sourceStats,
            calculatedKey: "calculateddamage",
            baseKey: "calculationbase",
            incrementKey: "extradamage");
        var changedHits = ApplyLinearDynamicUpgradeContext(
            upgradedStats,
            sourceStats,
            calculatedKey: "calculatedhits",
            baseKey: "calculationbase",
            incrementKey: "calculationextra");
        if (changedHits && applyCombatModifiers && includeTargetRows)
        {
            upgradedStats.Remove("damage_by_target");
            AddAttackDamageByTarget(upgradedStats, upgradedCard, player);
        }
    }

    private static bool ApplyLinearDynamicUpgradeContext(
        Dictionary<string, object?> upgradedStats,
        Dictionary<string, object?> sourceStats,
        string calculatedKey,
        string baseKey,
        string incrementKey)
    {
        if (!TryGetStat(sourceStats, calculatedKey, out var sourceCalculated)
            || !TryGetStat(sourceStats, baseKey, out var sourceBase)
            || !TryGetStat(sourceStats, incrementKey, out var sourceIncrement)
            || !TryGetStat(upgradedStats, baseKey, out var upgradedBase)
            || !TryGetStat(upgradedStats, incrementKey, out var upgradedIncrement)
            || sourceIncrement == 0
            || sourceCalculated <= sourceBase)
        {
            return false;
        }

        var delta = sourceCalculated - sourceBase;
        if (delta % sourceIncrement != 0)
            return false;

        upgradedStats[calculatedKey] = upgradedBase + upgradedIncrement * (delta / sourceIncrement);
        return true;
    }

    private static bool TryGetStat(Dictionary<string, object?> stats, string key, out int value)
    {
        value = 0;
        if (!stats.TryGetValue(key, out var obj) || obj == null)
            return false;
        try
        {
            value = Convert.ToInt32(obj);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string CardDescriptionWithSourceEnhancements(
        CardModel upgradedCard,
        CardModel sourceCard,
        Dictionary<string, object?>? stats = null,
        bool includeCombatText = false,
        bool preferStatsText = false)
    {
        var description = preferStatsText
            ? CardDescriptionFromStats(upgradedCard, stats, includeCombatText)
            : CardDescription(upgradedCard, stats, includeCombatText: includeCombatText);
        return AppendSourceEnhancementDescriptions(description, sourceCard, upgradedCard);
    }

    private string CardDescriptionFromStats(
        CardModel card,
        Dictionary<string, object?>? stats,
        bool includeCombatText)
    {
        if (stats == null || stats.Count == 0)
            return CardDescription(card, stats, includeCombatText: includeCombatText);

        var raw = _loc.Bilingual("cards", card.Id.Entry + ".description");
        raw = ApplyBooleanChoiceFormatter(raw, "InCombat", includeCombatText);
        var vars = ExportDynamicVars(card) ?? new Dictionary<string, object?>();
        try
        {
            foreach (var dv in card.DynamicVars.Values)
            {
                var name = dv.Name;
                if (string.IsNullOrEmpty(name))
                    continue;
                if (stats.TryGetValue(name.ToLowerInvariant(), out var value))
                    vars[name] = value;
            }
        }
        catch { }

        var formatted = InterpolateDynamicVars(raw, vars);
        if (!string.IsNullOrWhiteSpace(formatted))
        {
            var resolved = ResolveEngineCardDescriptionFormatters(
                CleanEngineText(formatted) ?? formatted,
                card,
                preferDisplayVars: false);
            if (!ContainsSmartFormatToken(resolved))
                return resolved;

            var engineDescription = CardDescription(card, stats, includeCombatText: includeCombatText);
            if (!string.IsNullOrWhiteSpace(engineDescription) && !ContainsSmartFormatToken(engineDescription))
                return engineDescription;

            return resolved;
        }

        return CardDescription(card, stats, includeCombatText: includeCombatText);
    }

    private static string ApplyBooleanChoiceFormatter(string text, string key, bool useTrueBranch)
    {
        var marker = "{" + key + ":";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return text;

        var output = new System.Text.StringBuilder(text.Length);
        var cursor = 0;
        while (start >= 0)
        {
            output.Append(text, cursor, start - cursor);

            var bodyStart = start + marker.Length;
            if (text.AsSpan(bodyStart).StartsWith("show:", StringComparison.Ordinal))
                bodyStart += "show:".Length;

            var depth = 0;
            var split = -1;
            var end = -1;
            for (var i = bodyStart; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch == '{')
                {
                    depth++;
                }
                else if (ch == '}')
                {
                    if (depth == 0)
                    {
                        end = i;
                        break;
                    }
                    depth--;
                }
                else if (ch == '|' && depth == 0 && split < 0)
                {
                    split = i;
                }
            }

            if (end < 0)
            {
                output.Append(text, start, text.Length - start);
                return output.ToString();
            }

            var trueEnd = split >= 0 ? split : end;
            if (useTrueBranch)
                output.Append(text, bodyStart, trueEnd - bodyStart);
            else if (split >= 0)
                output.Append(text, split + 1, end - split - 1);

            cursor = end + 1;
            start = text.IndexOf(marker, cursor, StringComparison.Ordinal);
        }

        output.Append(text, cursor, text.Length - cursor);
        return output.ToString();
    }

    private string AppendSourceEnhancementDescriptions(
        string description,
        CardModel sourceCard,
        CardModel upgradedCard)
    {
        foreach (var enhancementText in SourceEnhancementDescriptions(sourceCard, upgradedCard))
        {
            if (string.IsNullOrWhiteSpace(enhancementText))
                continue;
            if (description.Contains(enhancementText, StringComparison.Ordinal))
                continue;
            description = string.IsNullOrWhiteSpace(description)
                ? enhancementText
                : description + "\n" + enhancementText;
        }
        return description;
    }

    private IEnumerable<string> SourceEnhancementDescriptions(CardModel sourceCard, CardModel upgradedCard)
    {
        if (sourceCard.Enchantment != null)
        {
            if (upgradedCard.Enchantment?.Id.Entry != sourceCard.Enchantment.Id.Entry)
            {
                var entry = sourceCard.Enchantment.Id.Entry;
                var vars = ExportEnhancementVars(sourceCard.Enchantment);
                var text = EngineLocStringText(sourceCard.Enchantment.DynamicDescription, vars)
                           ?? LocalizedTableText("enchantments", entry + ".description", vars);
                if (!string.IsNullOrWhiteSpace(text))
                    yield return text;
            }
        }

        if (sourceCard.Affliction != null)
        {
            if (upgradedCard.Affliction?.Id.Entry != sourceCard.Affliction.Id.Entry)
            {
                var entry = sourceCard.Affliction.Id.Entry;
                var vars = ExportEnhancementVars(sourceCard.Affliction);
                var text = EngineLocStringText(sourceCard.Affliction.DynamicDescription, vars)
                           ?? LocalizedTableText("afflictions", entry + ".description", vars);
                if (!string.IsNullOrWhiteSpace(text))
                    yield return text;
            }
        }
    }

    private Dictionary<string, object?> PotionInfo(PotionModel potion, int? index = null)
    {
        var entry = potion.Id.Entry;
        var vars = new Dictionary<string, object?>();
        try
        {
            foreach (var dv in potion.DynamicVars.Values)
                vars[dv.Name] = (int)dv.BaseValue;
        }
        catch { }

        var info = new Dictionary<string, object?>
        {
            ["id"] = potion.Id.Entry,
            ["name"] = EngineLocStringText(potion.Title) ?? _loc.Potion(entry),
            ["description"] =
                CleanResolvedEngineText(potion.HoverTip.Description)
                ?? EngineLocStringText(potion.DynamicDescription)
                ?? InterpolateDynamicVars(
                    _loc.Bilingual("potions", entry + ".description"),
                    vars.Count > 0 ? vars : null),
            ["vars"] = vars.Count > 0 ? vars : null,
            ["target_type"] = potion.TargetType.ToString(),
        };
        if (index.HasValue)
            info["index"] = index.Value;
        return info;
    }

    private static int? TryGetIntMember(object obj, params string[] names)
    {
        foreach (var name in names)
        {
            var value = TryGetMember(obj, name);
            if (value is int i) return i;
            if (value is long l) return checked((int)l);
            if (value is short s) return s;
            if (value is byte b) return b;
            if (value is float f) return checked((int)f);
            if (value is double d) return checked((int)d);
            if (value is decimal m) return checked((int)m);
        }
        return null;
    }

    private static T? TryGetModelMember<T>(object obj, params string[] names) where T : class
    {
        foreach (var name in names)
        {
            var value = TryGetMember(obj, name);
            var model = TryGetModelFromValue<T>(value);
            if (model != null)
                return model;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            foreach (var prop in obj.GetType().GetProperties(flags))
            {
                if (prop.GetIndexParameters().Length != 0)
                    continue;
                var model = TryGetModelFromValue<T>(prop.GetValue(obj));
                if (model != null)
                    return model;
            }

            foreach (var field in obj.GetType().GetFields(flags))
            {
                var model = TryGetModelFromValue<T>(field.GetValue(obj));
                if (model != null)
                    return model;
            }
        }
        catch { }
        return null;
    }

    private static T? TryGetModelFromValue<T>(object? value) where T : class
    {
        if (value is T typed)
            return typed;

        var nestedModel = value != null ? TryGetMember(value, "Model") : null;
        return nestedModel as T;
    }

    private static object? TryGetMember(object obj, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            var prop = obj.GetType().GetProperty(name, flags);
            if (prop != null && prop.GetIndexParameters().Length == 0)
                return prop.GetValue(obj);

            var field = obj.GetType().GetField(name, flags);
            if (field != null)
                return field.GetValue(obj);
        }
        catch { }
        return null;
    }

    private Dictionary<string, object?> RelicInfo(RelicModel relic, int? index = null)
    {
        var entry = relic.Id.Entry;
        var vars = RelicVars(relic);
        var varsOrNull = vars.Count > 0 ? vars : null;
        var description =
            CleanResolvedEngineText(relic.HoverTip.Description)
            ?? EngineLocStringText(relic.DynamicDescription)
            ?? InterpolateDynamicVars(_loc.Bilingual("relics", entry + ".description"), varsOrNull);
        if (description != null && varsOrNull != null)
        {
            var rawDescription = RelicRawDescription(relic)
                ?? _loc.Bilingual("relics", entry + ".description");
            description = ExpandResolvedEnergyIcons(description, rawDescription, vars);
        }
        var info = new Dictionary<string, object?>
        {
            ["id"] = entry,
            ["name"] = EngineLocStringText(relic.Title) ?? _loc.Relic(entry),
            ["description"] = description,
            ["vars"] = varsOrNull,
            ["show_counter"] = relic.ShowCounter,
        };
        if (relic.ShowCounter)
            info["display_amount"] = relic.DisplayAmount;
        if (index.HasValue)
            info["index"] = index.Value;
        return info;
    }

    private static string? RelicRawDescription(RelicModel relic)
    {
        try
        {
            return relic.DynamicDescription?.GetRawText();
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, object?> RelicVars(RelicModel relic)
    {
        var vars = new Dictionary<string, object?>();
        try
        {
            foreach (var dv in relic.DynamicVars.Values)
                vars[dv.Name] = (int)dv.BaseValue;
        }
        catch { }
        return vars;
    }

    private Dictionary<string, object?> PlayerSummary(Player player)
    {
        var potions = player.Potions?
            .Select((p, i) => p == null ? null : PotionInfo(p, i))
            .Where(x => x != null)
            .Cast<Dictionary<string, object?>>()
            .ToList()
            ?? new List<Dictionary<string, object?>>();
        var potionSlotCount = GetPotionSlots(player)?.Count ?? potions.Count;

        return new Dictionary<string, object?>
        {
            ["name"] = _loc.Bilingual("characters", (player.Character?.Id.Entry ?? "IRONCLAD") + ".title"),
            ["hp"] = player.Creature?.CurrentHp ?? 0,
            ["max_hp"] = player.Creature?.MaxHp ?? 0,
            ["block"] = player.Creature?.Block ?? 0,
            ["gold"] = player.Gold,
            ["relics"] = player.Relics?.Select(r => RelicInfo(r)).ToList(),
            ["potions"] = potions,
            ["potion_slots"] = potionSlotCount,
            ["potion_empty_slots"] = Math.Max(0, potionSlotCount - potions.Count),
            ["deck_size"] = player.Deck?.Cards?.Count(c => c != null) ?? 0,
            ["deck"] = player.Deck?.Cards?.Where(c => c != null).Select(c =>
            {
                var dstats = ExtractCardStats(c, player);
                var dkws = c.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToList();
                var cardInfo = new Dictionary<string, object?>
                {
                    ["id"] = c.Id.ToString(),
                    ["name"] = _loc.Card(c.Id.Entry),
                    ["cost"] = GetEnergyCostDisplay(c),
                    ["type"] = c.Type.ToString(),
                    ["upgraded"] = c.IsUpgraded,
                    ["description"] = CardDescription(c, dstats),
                    ["stats"] = dstats.Count > 0 ? dstats : null,
                    ["keywords"] = dkws?.Count > 0 ? dkws : null,
                    ["after_upgrade"] = GetUpgradedInfo(c, player),
                };
                AddCardVars(cardInfo, c);
                AddEnergyCostDetails(cardInfo, c);
                AddStarCostDetails(cardInfo, c);
                AddCardEnhancements(cardInfo, c);
                AddCardHoverTips(cardInfo, c);
                return cardInfo;
            }).ToList(),
        };
    }

    private List<Dictionary<string, object?>> CombatPileInfo(IEnumerable<CardModel>? cards, Player player)
    {
        if (cards == null)
            return new();

        return cards.Where(c => c != null).Select((card, i) =>
        {
            var stats = ExtractCardStats(card, player, applyCombatModifiers: true);
            var keywords = card.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToList();
            var cardInfo = new Dictionary<string, object?>
            {
                ["index"] = i,
                ["id"] = card.Id.ToString(),
                ["name"] = _loc.Card(card.Id.Entry),
                ["cost"] = GetEnergyCostDisplay(card),
                ["type"] = card.Type.ToString(),
                ["rarity"] = card.Rarity.ToString(),
                ["upgraded"] = card.IsUpgraded,
                ["description"] = CardDescription(card, stats, includeCombatText: true),
                ["stats"] = stats.Count > 0 ? stats : null,
                ["keywords"] = keywords?.Count > 0 ? keywords : null,
                ["after_upgrade"] = GetUpgradedInfo(
                    card,
                    player,
                    applyCombatModifiers: true,
                    useSourceDynamicContext: true),
            };
            AddCardVars(cardInfo, card, includePreviewStats: true);
            AddEnergyCostDetails(cardInfo, card, includeCurrentXValue: true);
            AddStarCostDetails(cardInfo, card);
            AddCardEnhancements(cardInfo, card);
            AddCardHoverTips(cardInfo, card);
            return cardInfo;
        }).ToList();
    }

    /// <summary>Common context added to every decision point.</summary>
    private Dictionary<string, object?> RunContext()
    {
        if (_runState == null) return new();
        var mapFloor = _runState.CurrentMapCoord.HasValue
            ? (int?)_runState.CurrentMapCoord.Value.row
            : null;
        var ctx = new Dictionary<string, object?>
        {
            ["act"] = _runState.CurrentActIndex + 1,
            ["act_name"] = _loc.Act(_runState.Act?.Id.Entry ?? "OVERGROWTH"),
            ["floor"] = _runState.ActFloor,
            ["engine_floor"] = _runState.ActFloor,
            ["display_floor"] = mapFloor ?? _runState.ActFloor,
            ["room_type"] = _runState.CurrentRoom?.RoomType.ToString(),
        };
        if (mapFloor.HasValue)
            ctx["map_floor"] = mapFloor.Value;

        // Boss encounter info — use BossEncounter?.Id?.Entry
        try
        {
            var bossIdEntry = _runState.Act?.BossEncounter?.Id?.Entry;
            if (!string.IsNullOrEmpty(bossIdEntry))
            {
                ctx["boss"] = new Dictionary<string, object?>
                {
                    ["id"] = bossIdEntry,
                    ["name"] = BossEncounterDisplayName(bossIdEntry),
                };
            }
        }
        catch { }

        return ctx;
    }

    private static void EnsureModelDbInitialized()
    {
        if (_modelDbInitialized) return;
        _modelDbInitialized = true;

        TestMode.IsOn = true;

        // Install inline sync context on main thread
        SynchronizationContext.SetSynchronizationContext(_syncCtx);

        // Initialize PlatformServices before anything touches PlatformUtil
        try
        {
            // Try to access PlatformUtil to trigger its static init
            // If it fails, it won't be available but most code checks SteamInitializer.Initialized
            var _ = MegaCrit.Sts2.Core.Platform.PlatformUtil.PrimaryPlatform;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] PlatformUtil init: {ex.Message}");
        }

        // Initialize SaveManager with a dummy profile for save/load support
        try { SaveManager.Instance.InitProfileId(0); }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] SaveManager.InitProfileId: {ex.Message}"); }

        // Some card effects read settings/prefs during OnPlay even in headless
        // mode. Initialize test settings so those engine paths can run normally.
        try { SaveManager.Instance.InitSettingsDataForTest(); }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] InitSettingsDataForTest: {ex.Message}"); }
        try { SaveManager.Instance.InitPrefsDataForTest(); }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] InitPrefsDataForTest: {ex.Message}"); }

        // Initialize progress data for epoch/timeline tracking
        try { SaveManager.Instance.InitProgressData(); }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] InitProgressData: {ex.Message}"); }

        // Install the Task.Yield patch but keep SuppressYield=false by default.
        // SuppressYield is toggled to true only during EndTurn to prevent boss fight deadlocks.
        PatchTaskYield();

        // Patch Cmd.Wait to be a no-op in headless mode.
        // Cmd.Wait(duration) is used for UI animations (e.g., PreviewCardPileAdd during
        // Vantom's Dismember move adding Wounds). In headless mode, these never complete
        // because there's no Godot scene tree, causing the ActionExecutor to deadlock.
        PatchCmdWait();
        PatchCardPileAddVisuals();
        PatchCardSelectContext();
        PatchTalkCmdPlay();
        PatchSoulNexusPresentation();
        PatchQueenPresentation();
        PatchDecimillipedePresentation();
        PatchSlumberingBeetlePresentation();
        PatchLagavulinMatriarchPresentation();
        PatchTestSubjectPresentation();
        PatchKaiserCrabPresentation();
        PatchCrystalSpherePresentation();
        PatchTrialPresentation();

        // Initialize localization system (needed for events, cards, etc.)
        InitLocManager();

        var subtypes = MegaCrit.Sts2.Core.Models.AbstractModelSubtypes.All;
        int registered = 0, failed = 0;
        for (int i = 0; i < subtypes.Count; i++)
        {
            try
            {
                ModelDb.Inject(subtypes[i]);
                registered++;
            }
            catch (Exception ex)
            {
                failed++;
                // Only log first few failures to reduce noise
                if (failed <= 5)
                    Console.Error.WriteLine($"[WARN] Failed to register {subtypes[i].Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Console.Error.WriteLine($"[INFO] ModelDb: {registered} registered, {failed} failed out of {subtypes.Count}");

        // Initialize net ID serialization cache (needed for combat actions)
        try
        {
            ModelIdSerializationCache.Init();
            Console.Error.WriteLine("[INFO] ModelIdSerializationCache initialized");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] ModelIdSerializationCache.Init: {ex.Message}");
        }
    }

    private Player? CreatePlayer(string characterName)
    {
        return characterName.ToLowerInvariant() switch
        {
            "ironclad" => Player.CreateForNewRun<Ironclad>(UnlockState.all, 1uL),
            "silent" => Player.CreateForNewRun<Silent>(UnlockState.all, 1uL),
            "defect" => Player.CreateForNewRun<Defect>(UnlockState.all, 1uL),
            "regent" => Player.CreateForNewRun<Regent>(UnlockState.all, 1uL),
            "necrobinder" => Player.CreateForNewRun<Necrobinder>(UnlockState.all, 1uL),
            _ => null
        };
    }

    private static void PatchCmdWait()
    {
        try
        {
            var harmony = new Harmony("sts2headless.cmdwait");
            // Find Cmd.Wait(float) — it's in MegaCrit.Sts2.Core.Commands namespace
            // Find Cmd type via CardPileCmd's assembly (both are in same namespace)
            var cmdPileType = typeof(MegaCrit.Sts2.Core.Commands.CardPileCmd);
            var cmdAsm = cmdPileType.Assembly;
            Type? cmdType = cmdAsm.GetType("MegaCrit.Sts2.Core.Commands.Cmd");
            // If not found by exact name, search by namespace + "Wait" method
            if (cmdType == null)
            {
                foreach (var t in cmdAsm.GetTypes())
                {
                    if (t.Namespace == "MegaCrit.Sts2.Core.Commands")
                    {
                        var waitM = t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
                            .Where(m => m.Name == "Wait").ToList();
                        if (waitM.Count > 0)
                        {
                            cmdType = t;
                            Console.Error.WriteLine($"[INFO] Found Wait() in {t.FullName}");
                            break;
                        }
                    }
                }
            }
            if (cmdType != null)
            {
                var waitMethod = cmdType.GetMethod("Wait",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null, new[] { typeof(float) }, null);
                if (waitMethod != null)
                {
                    var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.CmdWaitPrefix),
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                    if (prefix != null)
                    {
                        harmony.Patch(waitMethod, new HarmonyMethod(prefix));
                        Console.Error.WriteLine("[INFO] Patched Cmd.Wait() to no-op (prevents boss fight deadlocks)");
                    }
                }
                else
                {
                    // Try to find any Wait method
                    var methods = cmdType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                        .Where(m => m.Name == "Wait" || m.Name == "CustomScaledWait")
                        .ToList();
                    foreach (var m in methods)
                    {
                        Console.Error.WriteLine($"[INFO] Found Cmd.{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})");
                        var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.CmdWaitPrefix),
                            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                        if (prefix != null)
                        {
                            harmony.Patch(m, new HarmonyMethod(prefix));
                            Console.Error.WriteLine($"[INFO] Patched Cmd.Wait variant");
                        }
                    }
                }
            }
            else
            {
                Console.Error.WriteLine("[WARN] Could not find MegaCrit.Sts2.Core.Commands.Cmd type");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch Cmd.Wait: {ex.Message}");
        }
    }

    private static void PatchTalkCmdPlay()
    {
        try
        {
            var harmony = new Harmony("sts2headless.talkcmd");
            var taskPrefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.TalkCmdPlayTaskPrefix),
                BindingFlags.Static | BindingFlags.Public);
            var voidPrefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.TalkCmdPlayVoidPrefix),
                BindingFlags.Static | BindingFlags.Public);
            if (taskPrefix == null || voidPrefix == null) return;

            var patched = 0;
            var exactMethod = AccessTools.Method("MegaCrit.Sts2.Core.Commands.TalkCmd:Play");
            if (exactMethod != null && PatchTalkPlayMethod(harmony, exactMethod, taskPrefix, voidPrefix))
                patched++;
            var transpiler = typeof(YieldPatches).GetMethod(nameof(YieldPatches.StripHeadlessPresentationCalls),
                BindingFlags.Static | BindingFlags.Public);
            var talkMoveMethodNames = new[]
            {
                "MegaCrit.Sts2.Core.Models.Monsters.KinPriest:RitualMove",
                "MegaCrit.Sts2.Core.Models.Monsters.BygoneEffigy:WakeMove",
                "MegaCrit.Sts2.Core.Models.Monsters.Chomper:ScreechMove",
            };
            var transpiled = new HashSet<MethodInfo>();
            if (transpiler != null)
            {
                foreach (var methodName in talkMoveMethodNames)
                {
                    var talkMoveMethod = AccessTools.Method(methodName);
                    if (talkMoveMethod == null || !transpiled.Add(talkMoveMethod))
                        continue;
                    try
                    {
                        harmony.Patch(talkMoveMethod, transpiler: new HarmonyMethod(transpiler));
                        patched++;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[WARN] Failed to patch {methodName}: {ex.Message}");
                    }
                }
            }

            var commandTypes = GetLoadableTypes(typeof(CardPileCmd).Assembly);
            if (transpiler != null)
            {
                foreach (var method in commandTypes
                    .Where(IsHeadlessPresentationPatchTarget)
                    .SelectMany(GetDeclaredMethods)
                    .Where(m => m.Name == "MoveNext" && m.GetMethodBody() != null))
                {
                    if (transpiled.Add(method))
                    {
                        try
                        {
                            harmony.Patch(method, transpiler: new HarmonyMethod(transpiler));
                            patched++;
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[WARN] Failed to patch {method.DeclaringType?.FullName}.{method.Name}: {ex.Message}");
                        }
                    }
                }
            }
            foreach (var method in commandTypes
                .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                .Where(m => m.Name == "Play" && LooksLikeTalkPlay(m)))
            {
                if (!Equals(method, exactMethod) && PatchTalkPlayMethod(harmony, method, taskPrefix, voidPrefix))
                    patched++;
            }

            Console.Error.WriteLine($"[INFO] Patched TalkCmd.Play ({patched} overloads) to no-op in headless");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch TalkCmd.Play: {ex.Message}");
        }
    }

    private static void PatchCardPileAddVisuals()
    {
        try
        {
            var harmony = new Harmony("sts2headless.cardpilevisuals");
            var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.ForceSkipVisualsPrefix),
                BindingFlags.Static | BindingFlags.Public);
            if (prefix == null)
                return;

            var patched = 0;
            foreach (var method in typeof(CardPileCmd)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "Add"
                            && m.GetParameters().Any(p =>
                                p.Name == "skipVisuals" && p.ParameterType == typeof(bool))))
            {
                harmony.Patch(method, new HarmonyMethod(prefix));
                patched++;
            }
            Console.Error.WriteLine($"[INFO] Patched CardPileCmd.Add skipVisuals on {patched} overloads");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch CardPileCmd.Add visuals: {ex.Message}");
        }
    }

    private static void PatchCardSelectContext()
    {
        try
        {
            var harmony = new Harmony("sts2headless.cardselectcontext");
            var prefsPrefix = typeof(YieldPatches).GetMethod(
                nameof(YieldPatches.CardSelectionPrefsPrefix),
                BindingFlags.Static | BindingFlags.Public);
            var sourcePrefix = typeof(YieldPatches).GetMethod(
                nameof(YieldPatches.CardSelectionPrefsSourcePrefix),
                BindingFlags.Static | BindingFlags.Public);
            var finalizer = typeof(YieldPatches).GetMethod(
                nameof(YieldPatches.CardSelectionPrefsFinalizer),
                BindingFlags.Static | BindingFlags.Public);
            if (prefsPrefix == null || sourcePrefix == null || finalizer == null)
                return;

            var patched = 0;
            foreach (var method in typeof(CardSelectCmd).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var parameters = method.GetParameters();
                if (!parameters.Any(p => p.ParameterType == typeof(CardSelectorPrefs)))
                    continue;

                var hasSource = parameters.Any(p => p.ParameterType == typeof(AbstractModel));
                harmony.Patch(
                    method,
                    prefix: new HarmonyMethod(hasSource ? sourcePrefix : prefsPrefix),
                    finalizer: new HarmonyMethod(finalizer));
                patched++;
            }

            Console.Error.WriteLine($"[INFO] Patched CardSelectCmd selection context ({patched} methods)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch CardSelectCmd selection context: {ex.Message}");
        }
    }

    private static void PatchSoulNexusPresentation()
    {
        try
        {
            var harmony = new Harmony("sts2headless.soulnexus.presentation");
            var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.SkipPresentationVoidPrefix),
                BindingFlags.Static | BindingFlags.Public);
            var soulNexusType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.Monsters.SoulNexus");
            if (prefix == null || soulNexusType == null)
                return;

            var patched = 0;
            var methods = new[]
            {
                soulNexusType.GetMethod("AfterDeath",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    types: new[] { typeof(Creature) },
                    modifiers: null),
                soulNexusType.GetMethod("BeforeRemovedFromRoom",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null),
            };
            foreach (var method in methods)
            {
                if (method == null)
                    continue;
                harmony.Patch(method, new HarmonyMethod(prefix));
                patched++;
            }
            Console.Error.WriteLine($"[INFO] Patched SoulNexus presentation cleanup ({patched} methods)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch SoulNexus presentation cleanup: {ex.Message}");
        }
    }

    private static void PatchQueenPresentation()
    {
        try
        {
            var harmony = new Harmony("sts2headless.queen.presentation");
            var transpiler = typeof(YieldPatches).GetMethod(nameof(YieldPatches.StripQueenHeadlessPresentationCalls),
                BindingFlags.Static | BindingFlags.Public);
            var skipPrefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.SkipPresentationVoidPrefix),
                BindingFlags.Static | BindingFlags.Public);
            var queenType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.Monsters.Queen");
            if (queenType == null)
                return;

            var patched = 0;
            var afterDeath = queenType.GetMethod("AfterDeath",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(PlayerChoiceContext), typeof(Creature), typeof(bool), typeof(float) },
                modifiers: null);
            if (afterDeath != null && transpiler != null)
            {
                harmony.Patch(afterDeath, transpiler: new HarmonyMethod(transpiler));
                patched++;
            }

            var beforeRemoved = queenType.GetMethod("BeforeRemovedFromRoom",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            if (beforeRemoved != null && skipPrefix != null)
            {
                harmony.Patch(beforeRemoved, new HarmonyMethod(skipPrefix));
                patched++;
            }

            Console.Error.WriteLine($"[INFO] Patched Queen presentation cleanup ({patched} methods)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch Queen presentation cleanup: {ex.Message}");
        }
    }

    private static void PatchDecimillipedePresentation()
    {
        try
        {
            var harmony = new Harmony("sts2headless.decimillipede.presentation");
            var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.SkipPresentationVoidPrefix),
                BindingFlags.Static | BindingFlags.Public);
            var transpiler = typeof(YieldPatches).GetMethod(nameof(YieldPatches.StripHeadlessPresentationCalls),
                BindingFlags.Static | BindingFlags.Public);
            var segmentType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.Monsters.DecimillipedeSegment");
            if (prefix == null || segmentType == null)
                return;

            var patched = 0;
            foreach (var method in segmentType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.Name == "ChangePhobiaModeTexture" && method.ReturnType == typeof(void)))
            {
                harmony.Patch(method, new HarmonyMethod(prefix));
                patched++;
            }
            var reattachPowerType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.Powers.ReattachPower");
            if (reattachPowerType != null && transpiler != null)
            {
                foreach (var method in reattachPowerType
                    .GetNestedTypes(BindingFlags.NonPublic)
                    .Where(type => type.Name.Contains("AfterDeath", StringComparison.Ordinal))
                    .SelectMany(GetDeclaredMethods)
                    .Where(method => method.Name == "MoveNext" && method.GetMethodBody() != null))
                {
                    harmony.Patch(method, transpiler: new HarmonyMethod(transpiler));
                    patched++;
                }
            }

            Console.Error.WriteLine($"[INFO] Patched Decimillipede presentation texture changes ({patched} methods)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch Decimillipede presentation texture changes: {ex.Message}");
        }
    }

    private static void PatchSlumberingBeetlePresentation()
    {
        try
        {
            var harmony = new Harmony("sts2headless.slumberingbeetle.presentation");
            var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.SlumberingBeetleAfterAddedToRoomPrefix),
                BindingFlags.Static | BindingFlags.Public);
            var afterAdded = AccessTools.Method("MegaCrit.Sts2.Core.Models.Monsters.SlumberingBeetle:AfterAddedToRoom");
            if (prefix == null || afterAdded == null)
                return;

            harmony.Patch(afterAdded, new HarmonyMethod(prefix));
            Console.Error.WriteLine("[INFO] Patched Slumbering Beetle sleep presentation");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch Slumbering Beetle sleep presentation: {ex.Message}");
        }
    }

    private static void PatchLagavulinMatriarchPresentation()
    {
        try
        {
            var harmony = new Harmony("sts2headless.lagavulinmatriarch.presentation");
            var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.LagavulinMatriarchAfterAddedToRoomPrefix),
                BindingFlags.Static | BindingFlags.Public);
            var afterAdded = AccessTools.Method("MegaCrit.Sts2.Core.Models.Monsters.LagavulinMatriarch:AfterAddedToRoom");
            if (prefix == null || afterAdded == null)
                return;

            harmony.Patch(afterAdded, new HarmonyMethod(prefix));
            Console.Error.WriteLine("[INFO] Patched Lagavulin Matriarch sleep presentation");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch Lagavulin Matriarch sleep presentation: {ex.Message}");
        }
    }

    private static void PatchTestSubjectPresentation()
    {
        try
        {
            var harmony = new Harmony("sts2headless.testsubject.presentation");
            var transpiler = typeof(YieldPatches).GetMethod(nameof(YieldPatches.StripHeadlessPresentationCalls),
                BindingFlags.Static | BindingFlags.Public);
            var burningGrowlPrefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.TestSubjectBurningGrowlMovePrefix),
                BindingFlags.Static | BindingFlags.Public);
            var testSubjectType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.Monsters.TestSubject");
            if (testSubjectType == null || transpiler == null)
                return;

            var patched = 0;
            var burningGrowlMove = AccessTools.Method(testSubjectType, "BurningGrowlMove");
            if (burningGrowlMove != null && burningGrowlPrefix != null)
            {
                harmony.Patch(burningGrowlMove, prefix: new HarmonyMethod(burningGrowlPrefix));
                patched++;
            }

            foreach (var method in GetDeclaredMethods(testSubjectType)
                .Where(method => method.GetMethodBody() != null)
                .Where(method => method.Name is "AfterDeath" or "AfterPowerApplied" or "AfterPowerRemoved"))
            {
                harmony.Patch(method, transpiler: new HarmonyMethod(transpiler));
                patched++;
            }

            foreach (var method in testSubjectType
                .GetNestedTypes(BindingFlags.NonPublic)
                .SelectMany(GetDeclaredMethods)
                .Where(method => method.Name == "MoveNext" && method.GetMethodBody() != null))
            {
                harmony.Patch(method, transpiler: new HarmonyMethod(transpiler));
                patched++;
            }

            Console.Error.WriteLine($"[INFO] Patched Test Subject presentation ({patched} methods)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch Test Subject color presentation: {ex.Message}");
        }
    }

    private static void PatchKaiserCrabPresentation()
    {
        try
        {
            var harmony = new Harmony("sts2headless.kaisercrab.presentation");
            var transpiler = typeof(YieldPatches).GetMethod(nameof(YieldPatches.StripHeadlessPresentationCalls),
                BindingFlags.Static | BindingFlags.Public);
            if (transpiler == null)
                return;

            var patched = 0;
            var monsterTypeNames = new[]
            {
                "MegaCrit.Sts2.Core.Models.Monsters.Crusher",
                "MegaCrit.Sts2.Core.Models.Monsters.Rocket",
            };
            foreach (var typeName in monsterTypeNames)
            {
                var monsterType = AccessTools.TypeByName(typeName);
                if (monsterType == null)
                    continue;

                foreach (var method in GetDeclaredMethods(monsterType)
                    .Where(m => (m.Name == "AfterCurrentHpChanged" || m.Name == "BeforeDeath")
                                && m.GetMethodBody() != null))
                {
                    harmony.Patch(method, transpiler: new HarmonyMethod(transpiler));
                    patched++;
                }
            }

            foreach (var method in GetLoadableTypes(typeof(CardPileCmd).Assembly)
                .Where(IsKaiserCrabPresentationStateMachine)
                .SelectMany(GetDeclaredMethods)
                .Where(m => m.Name == "MoveNext" && m.GetMethodBody() != null))
            {
                harmony.Patch(method, transpiler: new HarmonyMethod(transpiler));
                patched++;
            }

            Console.Error.WriteLine($"[INFO] Patched Kaiser Crab background presentation ({patched} methods)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch Kaiser Crab background presentation: {ex.Message}");
        }
    }

    private static void PatchCrystalSpherePresentation()
    {
        try
        {
            var harmony = new Harmony("sts2headless.crystalsphere.presentation");
            var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.CrystalSphereShowScreenPrefix),
                BindingFlags.Static | BindingFlags.Public);
            var showScreen = typeof(NCrystalSphereScreen).GetMethod(nameof(NCrystalSphereScreen.ShowScreen),
                BindingFlags.Static | BindingFlags.Public,
                binder: null,
                types: new[] { typeof(CrystalSphereMinigame) },
                modifiers: null);
            if (prefix == null || showScreen == null)
                return;

            harmony.Patch(showScreen, new HarmonyMethod(prefix));
            Console.Error.WriteLine("[INFO] Patched Crystal Sphere screen presentation for headless control");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch Crystal Sphere presentation: {ex.Message}");
        }
    }

    private static void PatchTrialPresentation()
    {
        try
        {
            var harmony = new Harmony("sts2headless.trial.presentation");
            var trialType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.Events.Trial");
            var acceptMethod = AccessTools.Method("MegaCrit.Sts2.Core.Models.Events.Trial:Accept");
            var addVfxMethod = trialType?.GetMethod("AddVfxAnchoredToPortrait",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(string) },
                modifiers: null);
            var transpiler = typeof(YieldPatches).GetMethod(nameof(YieldPatches.TrialAcceptHeadlessTranspiler),
                BindingFlags.Static | BindingFlags.Public);
            var skipPrefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.SkipPresentationVoidPrefix),
                BindingFlags.Static | BindingFlags.Public);

            var patched = 0;
            if (acceptMethod != null && transpiler != null)
            {
                harmony.Patch(acceptMethod, transpiler: new HarmonyMethod(transpiler));
                patched++;
            }
            if (addVfxMethod != null && skipPrefix != null)
            {
                harmony.Patch(addVfxMethod, new HarmonyMethod(skipPrefix));
                patched++;
            }

            Console.Error.WriteLine($"[INFO] Patched Trial headless presentation ({patched} methods)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch Trial presentation: {ex.Message}");
        }
    }

    private static bool PatchTalkPlayMethod(Harmony harmony, MethodInfo method, MethodInfo taskPrefix, MethodInfo voidPrefix)
    {
        if (typeof(Task).IsAssignableFrom(method.ReturnType))
        {
            harmony.Patch(method, new HarmonyMethod(taskPrefix));
            return true;
        }

        if (method.ReturnType == typeof(void))
        {
            harmony.Patch(method, new HarmonyMethod(voidPrefix));
            return true;
        }

        return false;
    }

    private static bool LooksLikeTalkPlay(MethodInfo method)
    {
        var parameters = method.GetParameters();
        return parameters.Any(p => p.ParameterType == typeof(LocString))
            && parameters.Any(p => p.ParameterType == typeof(Creature));
    }

    private static bool IsHeadlessPresentationPatchTarget(Type type)
    {
        var fullName = type.FullName ?? "";
        return fullName.Contains("MegaCrit.Sts2.Core.Models.Events.", StringComparison.Ordinal)
            || fullName.Contains("MegaCrit.Sts2.Core.Commands.CreatureCmd+<Heal", StringComparison.Ordinal)
            || fullName.Contains("RitualMove", StringComparison.Ordinal)
            || fullName.Contains("WakeMove", StringComparison.Ordinal)
            || fullName.Contains("ScreechMove", StringComparison.Ordinal)
            || fullName.Contains("MegaCrit.Sts2.Core.Models.Monsters.TestSubject+<", StringComparison.Ordinal)
            || fullName.Contains("Vantom+<DismemberMove", StringComparison.Ordinal);
    }

    private static bool IsKaiserCrabPresentationStateMachine(Type type)
    {
        var fullName = type.FullName ?? "";
        return fullName.Contains("MegaCrit.Sts2.Core.Models.Monsters.Crusher+<", StringComparison.Ordinal)
            || fullName.Contains("MegaCrit.Sts2.Core.Models.Monsters.Rocket+<", StringComparison.Ordinal);
    }

    private static IEnumerable<MethodInfo> GetDeclaredMethods(Type type)
    {
        try
        {
            return type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        }
        catch
        {
            return Array.Empty<MethodInfo>();
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null)!;
        }
    }

    private static void PatchTaskYield()
    {
        try
        {
            var harmony = new Harmony("sts2headless.yieldpatch");

            // Patch YieldAwaitable.YieldAwaiter.IsCompleted to return true
            // This makes `await Task.Yield()` execute synchronously (continuation runs inline)
            var yieldAwaiterType = typeof(System.Runtime.CompilerServices.YieldAwaitable)
                .GetNestedType("YieldAwaiter");
            if (yieldAwaiterType != null)
            {
                var isCompletedProp = yieldAwaiterType.GetProperty("IsCompleted");
                if (isCompletedProp != null)
                {
                    var getter = isCompletedProp.GetGetMethod();
                    var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.IsCompletedPrefix),
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                    if (getter != null && prefix != null)
                    {
                        harmony.Patch(getter, new HarmonyMethod(prefix));
                        Console.Error.WriteLine("[INFO] Patched Task.Yield() to be synchronous");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch Task.Yield: {ex.Message}");
        }
    }

    /// <summary>
    /// Card selector for headless mode — picks first available card for any selection prompt.
    /// Used by cards like Headbutt, Armaments, etc. that need player to choose a card.
    /// </summary>
    /// <summary>
    /// Card selector that creates a pending selection decision point.
    /// When the game needs the player to choose cards (upgrade, remove, transform, bundle pick),
    /// this stores the options and waits for the main loop to provide the answer.
    /// </summary>
    internal class HeadlessCardSelector : MegaCrit.Sts2.Core.TestSupport.ICardSelector
    {
        // Pending card selection — set by game engine, read by main loop
        public List<CardModel>? PendingOptions { get; private set; }
        public int PendingMinSelect { get; private set; }
        public int PendingMaxSelect { get; private set; }
        public bool PendingCanSkip { get; private set; }
        public string PendingPrompt { get; private set; } = "";
        public AbstractModel? PendingSourceModel { get; private set; }
        private TaskCompletionSource<IEnumerable<CardModel>>? _pendingTcs;

        public bool HasPending => _pendingTcs != null && !_pendingTcs.Task.IsCompleted;

        public Task<IEnumerable<CardModel>> GetSelectedCards(
            IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            var optList = options.ToList();
            if (optList.Count == 0)
                return Task.FromResult<IEnumerable<CardModel>>(Array.Empty<CardModel>());

            // Store pending selection and wait
            PendingOptions = optList;
            var context = YieldPatches.ConsumeCardSelectionContext();
            PendingMinSelect = context?.MinSelect ?? minSelect;
            PendingMaxSelect = context?.MaxSelect ?? maxSelect;
            PendingCanSkip = PendingMinSelect == 0 || context?.Cancelable == true;
            PendingPrompt = context?.Prompt ?? "";
            PendingSourceModel = context?.Source;
            _pendingTcs = new TaskCompletionSource<IEnumerable<CardModel>>();

            Console.Error.WriteLine($"[SIM] Card selection pending: {optList.Count} options, select {minSelect}-{maxSelect}");

            // Return the task — the main loop will complete it
            return _pendingTcs.Task;
        }

        public void ResolvePending(IEnumerable<CardModel> selected)
        {
            var pendingTcs = _pendingTcs;
            PendingOptions = null;
            PendingMinSelect = 0;
            PendingMaxSelect = 0;
            PendingCanSkip = false;
            PendingPrompt = "";
            PendingSourceModel = null;
            _pendingTcs = null;
            pendingTcs?.TrySetResult(selected);
        }

        public int CountValidSelectedIndices(int[] indices)
        {
            if (PendingOptions == null)
                return 0;
            return indices.Count(i => i >= 0 && i < PendingOptions.Count);
        }

        public void ResolvePendingByIndices(int[] indices)
        {
            if (PendingOptions == null) return;
            var selected = indices
                .Where(i => i >= 0 && i < PendingOptions.Count)
                .Select(i => PendingOptions[i])
                .ToList();
            ResolvePending(selected);
        }

        public void CancelPending()
        {
            var pendingTcs = _pendingTcs;
            PendingOptions = null;
            PendingMinSelect = 0;
            PendingMaxSelect = 0;
            PendingCanSkip = false;
            PendingPrompt = "";
            PendingSourceModel = null;
            _pendingTcs = null;
            pendingTcs?.TrySetResult(Array.Empty<CardModel>());
        }

        // Pending card reward from events (GetSelectedCardReward blocks until resolved)
        public List<MegaCrit.Sts2.Core.Entities.Cards.CardCreationResult>? PendingRewardCards { get; private set; }
        private ManualResetEventSlim? _rewardWait;
        private int _rewardChoice = -1;

        public CardModel? GetSelectedCardReward(
            IReadOnlyList<MegaCrit.Sts2.Core.Entities.Cards.CardCreationResult> options,
            IReadOnlyList<CardRewardAlternative> alternatives)
        {
            if (options.Count == 0) return null;

            // Store pending and block until main loop resolves
            PendingRewardCards = options.ToList();
            _rewardChoice = -1;
            _rewardWait = new ManualResetEventSlim(false);

            Console.Error.WriteLine($"[SIM] Card reward pending: {options.Count} cards (blocking)");
            _rewardWait.Wait(TimeSpan.FromSeconds(300)); // Wait up to 5 min

            var choice = _rewardChoice;
            PendingRewardCards = null;
            _rewardWait = null;

            if (choice >= 0 && choice < options.Count)
                return options[choice].Card;
            return null;  // Skip
        }

        public bool HasPendingReward => PendingRewardCards != null && _rewardWait != null;

        public void ResolveReward(int index)
        {
            _rewardChoice = index;
            _rewardWait?.Set();
        }

        public void SkipReward()
        {
            _rewardChoice = -1;
            _rewardWait?.Set();
        }

        public void Reset()
        {
            _pendingTcs?.TrySetResult(Array.Empty<CardModel>());
            PendingOptions = null;
            PendingMinSelect = 0;
            PendingMaxSelect = 0;
            PendingCanSkip = false;
            PendingPrompt = "";
            PendingSourceModel = null;
            _pendingTcs = null;

            _rewardChoice = -1;
            _rewardWait?.Set();
            PendingRewardCards = null;
            _rewardWait = null;
        }
    }

    internal static class YieldPatches
    {
        // Only suppress Task.Yield() when this flag is set (during end_turn processing)
        public static volatile bool SuppressYield;
        public static CrystalSphereMinigame? ActiveCrystalSphereMinigame;
        private static readonly object CardSelectionContextLock = new();
        private static PendingCardSelectionContext? _pendingCardSelectionContext;

        public sealed class PendingCardSelectionContext
        {
            public string? Prompt { get; init; }
            public AbstractModel? Source { get; init; }
            public int MinSelect { get; init; }
            public int MaxSelect { get; init; }
            public bool Cancelable { get; init; }
        }

        public static void CardSelectionPrefsPrefix(CardSelectorPrefs prefs)
        {
            SetCardSelectionContext(prefs, null);
        }

        public static void CardSelectionPrefsSourcePrefix(CardSelectorPrefs prefs, AbstractModel source)
        {
            SetCardSelectionContext(prefs, source);
        }

        public static void CardSelectionPrefsFinalizer()
        {
            ClearCardSelectionContext();
        }

        private static void SetCardSelectionContext(CardSelectorPrefs prefs, AbstractModel? source)
        {
            var prompt = EngineLocStringText(prefs.Prompt);
            lock (CardSelectionContextLock)
            {
                _pendingCardSelectionContext = new PendingCardSelectionContext
                {
                    Prompt = prompt,
                    Source = source,
                    MinSelect = prefs.MinSelect,
                    MaxSelect = prefs.MaxSelect,
                    Cancelable = prefs.Cancelable,
                };
            }
        }

        public static PendingCardSelectionContext? ConsumeCardSelectionContext()
        {
            lock (CardSelectionContextLock)
            {
                var context = _pendingCardSelectionContext;
                _pendingCardSelectionContext = null;
                return context;
            }
        }

        private static void ClearCardSelectionContext()
        {
            lock (CardSelectionContextLock)
            {
                _pendingCardSelectionContext = null;
            }
        }

        public static bool IsCompletedPrefix(ref bool __result)
        {
            if (SuppressYield)
            {
                __result = true;
                return false;
            }
            return true; // Let normal Yield behavior run
        }

        /// <summary>Harmony prefix: make Cmd.Wait() return completed task immediately (no-op in headless).</summary>
        public static bool CmdWaitPrefix(ref Task __result)
        {
            __result = Task.CompletedTask;
            return false; // Skip original method
        }

        /// <summary>Harmony prefix: replace the Crystal Sphere Godot screen with CLI-controlled state.</summary>
        public static bool CrystalSphereShowScreenPrefix(CrystalSphereMinigame grid, ref NCrystalSphereScreen? __result)
        {
            ActiveCrystalSphereMinigame = grid;
            __result = null;
            return false;
        }

        /// <summary>Harmony prefix: skip dialogue VFX/speech bubbles in headless mode.</summary>
        public static bool TalkCmdPlayTaskPrefix(ref Task __result)
        {
            __result = Task.CompletedTask;
            return false; // Skip original method
        }

        /// <summary>Harmony prefix: skip dialogue VFX/speech bubbles in headless mode.</summary>
        public static bool TalkCmdPlayVoidPrefix()
        {
            return false; // Skip original method
        }

        /// <summary>Harmony prefix: skip UI-only presentation hooks in headless mode.</summary>
        public static bool SkipPresentationVoidPrefix()
        {
            return false;
        }

        /// <summary>Harmony prefix: preserve Slumbering Beetle setup powers while skipping sleep audio/VFX nodes.</summary>
        public static bool SlumberingBeetleAfterAddedToRoomPrefix(MonsterModel __instance, ref Task __result)
        {
            __result = SlumberingBeetleAfterAddedToRoomHeadless(__instance);
            return false;
        }

        private static async Task SlumberingBeetleAfterAddedToRoomHeadless(MonsterModel monster)
        {
            var platingAmount = 15m;
            var prop = monster.GetType().GetProperty("PlatingAmount", BindingFlags.Instance | BindingFlags.NonPublic);
            if (prop?.GetValue(monster) is int amount)
                platingAmount = amount;

            await PowerCmd.Apply<PlatingPower>(monster.Creature, platingAmount, monster.Creature, null);
            await PowerCmd.Apply<SlumberPower>(monster.Creature, 3m, monster.Creature, null);
        }

        /// <summary>Harmony prefix: preserve Lagavulin Matriarch setup powers while skipping sleep audio/VFX nodes.</summary>
        public static bool LagavulinMatriarchAfterAddedToRoomPrefix(MonsterModel __instance, ref Task __result)
        {
            __result = LagavulinMatriarchAfterAddedToRoomHeadless(__instance);
            return false;
        }

        private static async Task LagavulinMatriarchAfterAddedToRoomHeadless(MonsterModel monster)
        {
            await PowerCmd.Apply<PlatingPower>(monster.Creature, 12m, monster.Creature, null);
            await PowerCmd.Apply<AsleepPower>(monster.Creature, 3m, monster.Creature, null);
        }

        /// <summary>Harmony prefix: preserve Test Subject's growl effects while skipping room VFX/audio/animation.</summary>
        public static bool TestSubjectBurningGrowlMovePrefix(MonsterModel __instance, IReadOnlyList<Creature> targets, ref Task __result)
        {
            __result = TestSubjectBurningGrowlMoveHeadless(__instance, targets);
            return false;
        }

        private static async Task TestSubjectBurningGrowlMoveHeadless(MonsterModel monster, IReadOnlyList<Creature> targets)
        {
            var burnCount = GetNonPublicIntProperty(monster, "BurningGrowlBurnCount", 3);
            var strengthGain = GetNonPublicIntProperty(monster, "BurningGrowlStrengthGain", 2);

            await CardPileCmd.AddToCombatAndPreview<Burn>(targets, PileType.Discard, burnCount, addedByPlayer: false);
            await PowerCmd.Apply<StrengthPower>(monster.Creature, strengthGain, monster.Creature, null);
        }

        private static int GetNonPublicIntProperty(object obj, string name, int fallback)
        {
            var prop = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic);
            return prop?.GetValue(obj) is int value ? value : fallback;
        }

        /// <summary>Harmony prefix: disable card pile animations in headless while preserving pile logic.</summary>
        public static void ForceSkipVisualsPrefix(ref bool skipVisuals)
        {
            skipVisuals = true;
        }

        public static IEnumerable<CodeInstruction> StripHeadlessPresentationCalls(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if (instruction.operand is MethodInfo method && IsHeadlessPresentationCall(method))
                {
                    var argCount = method.GetParameters().Length + (method.IsStatic ? 0 : 1);
                    var replacement = new List<CodeInstruction>();
                    for (var i = 0; i < argCount; i++)
                        replacement.Add(new CodeInstruction(OpCodes.Pop));

                    if (typeof(Task).IsAssignableFrom(method.ReturnType))
                    {
                        var completedTaskGetter = typeof(Task).GetProperty(nameof(Task.CompletedTask))?.GetGetMethod();
                        if (completedTaskGetter != null)
                            replacement.Add(new CodeInstruction(OpCodes.Call, completedTaskGetter));
                    }
                    else if (method.ReturnType == typeof(int))
                    {
                        replacement.Add(new CodeInstruction(OpCodes.Ldc_I4_M1));
                    }
                    else if (!method.ReturnType.IsValueType)
                    {
                        replacement.Add(new CodeInstruction(OpCodes.Ldnull));
                    }
                    else if (method.ReturnType != typeof(void))
                    {
                        yield return instruction;
                        continue;
                    }

                    if (replacement.Count > 0)
                    {
                        replacement[0].labels.AddRange(instruction.labels);
                        replacement[0].blocks.AddRange(instruction.blocks);
                        foreach (var replacementInstruction in replacement)
                            yield return replacementInstruction;
                    }
                    continue;
                }

                yield return instruction;
            }
        }

        public static IEnumerable<CodeInstruction> TrialAcceptHeadlessTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if (instruction.operand is MethodInfo method
                    && method.Name == "IsMe"
                    && method.DeclaringType?.FullName == "MegaCrit.Sts2.Core.Context.LocalContext"
                    && method.GetParameters().Length == 1)
                {
                    var pop = new CodeInstruction(OpCodes.Pop);
                    var loadFalse = new CodeInstruction(OpCodes.Ldc_I4_0);
                    pop.labels.AddRange(instruction.labels);
                    pop.blocks.AddRange(instruction.blocks);
                    yield return pop;
                    yield return loadFalse;
                    continue;
                }

                yield return instruction;
            }
        }

        public static IEnumerable<CodeInstruction> StripQueenHeadlessPresentationCalls(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if (instruction.operand is MethodInfo method && IsCombatRoomGetCreatureNode(method))
                {
                    var argCount = method.GetParameters().Length + (method.IsStatic ? 0 : 1);
                    var replacement = new List<CodeInstruction>();
                    for (var i = 0; i < argCount; i++)
                        replacement.Add(new CodeInstruction(OpCodes.Pop));

                    if (!method.ReturnType.IsValueType)
                    {
                        replacement.Add(new CodeInstruction(OpCodes.Ldnull));
                    }
                    else
                    {
                        yield return instruction;
                        continue;
                    }

                    replacement[0].labels.AddRange(instruction.labels);
                    replacement[0].blocks.AddRange(instruction.blocks);
                    foreach (var replacementInstruction in replacement)
                        yield return replacementInstruction;
                    continue;
                }

                yield return instruction;
            }
        }

        private static bool IsHeadlessPresentationCall(MethodInfo method)
        {
            return IsTalkCmdPlay(method)
                || IsDebugAudioPlay(method)
                || IsDebugAudioStop(method)
                || IsScreenRumble(method)
                || IsNGameHitStop(method)
                || IsNGameScreenShakeTrauma(method)
                || IsFullscreenHealVfxPlay(method)
                || IsCreatureTriggerAnim(method)
                || IsSfxCmdPlay(method)
                || IsRunMusicUpdateParameter(method)
                || IsCreatureNodeSetDefaultScale(method)
                || IsReattachFadeOut(method)
                || IsTestSubjectColorPresentation(method)
                || IsKaiserCrabBackgroundPresentation(method);
        }

        private static bool IsTalkCmdPlay(MethodInfo method)
        {
            return method.Name == "Play"
                && method.DeclaringType?.Name == "TalkCmd";
        }

        private static bool IsDebugAudioPlay(MethodInfo method)
        {
            return method.Name == "Play"
                && (method.DeclaringType?.FullName ?? "").Contains("NDebugAudioManager", StringComparison.Ordinal);
        }

        private static bool IsDebugAudioStop(MethodInfo method)
        {
            return method.Name == "Stop"
                && (method.DeclaringType?.FullName ?? "").Contains("NDebugAudioManager", StringComparison.Ordinal);
        }

        private static bool IsScreenRumble(MethodInfo method)
        {
            return method.Name == "ScreenRumble"
                && (method.DeclaringType?.FullName ?? "").Contains("NGame", StringComparison.Ordinal);
        }

        private static bool IsNGameHitStop(MethodInfo method)
        {
            return method.Name == "DoHitStop"
                && (method.DeclaringType?.FullName ?? "").Contains("NGame", StringComparison.Ordinal);
        }

        private static bool IsNGameScreenShakeTrauma(MethodInfo method)
        {
            return method.Name == "ScreenShakeTrauma"
                && (method.DeclaringType?.FullName ?? "").Contains("NGame", StringComparison.Ordinal);
        }

        private static bool IsFullscreenHealVfxPlay(MethodInfo method)
        {
            return method.Name == "Play"
                && (method.DeclaringType?.FullName ?? "").Contains("PlayerFullscreenHealVfx", StringComparison.Ordinal);
        }

        private static bool IsCreatureTriggerAnim(MethodInfo method)
        {
            return method.Name == "TriggerAnim"
                && method.DeclaringType?.FullName == "MegaCrit.Sts2.Core.Commands.CreatureCmd";
        }

        private static bool IsSfxCmdPlay(MethodInfo method)
        {
            return method.Name == "Play"
                && method.DeclaringType?.FullName == "MegaCrit.Sts2.Core.Commands.SfxCmd";
        }

        private static bool IsRunMusicUpdateParameter(MethodInfo method)
        {
            return method.Name == "UpdateMusicParameter"
                && method.DeclaringType?.FullName == "MegaCrit.Sts2.Core.Nodes.Audio.NRunMusicController";
        }

        private static bool IsCreatureNodeSetDefaultScale(MethodInfo method)
        {
            return method.Name == "SetDefaultScaleTo"
                && (method.DeclaringType?.FullName ?? "").Contains("MegaCrit.Sts2.Core.Nodes.Combat.NCreature", StringComparison.Ordinal);
        }

        private static bool IsReattachFadeOut(MethodInfo method)
        {
            return method.Name == "DoFadeOutOnAllSegments"
                && method.DeclaringType?.FullName == "MegaCrit.Sts2.Core.Models.Powers.ReattachPower";
        }

        private static bool IsTestSubjectColorPresentation(MethodInfo method)
        {
            return method.Name == "SetColor"
                && method.DeclaringType?.FullName == "MegaCrit.Sts2.Core.Models.Monsters.TestSubject";
        }

        private static bool IsKaiserCrabBackgroundPresentation(MethodInfo method)
        {
            var declaringType = method.DeclaringType?.FullName ?? "";
            if (declaringType.Contains("NKaiserCrabBossBackground", StringComparison.Ordinal))
                return method.Name.StartsWith("Play", StringComparison.Ordinal);

            return method.Name == "get_Background"
                && (declaringType == "MegaCrit.Sts2.Core.Models.Monsters.Crusher"
                    || declaringType == "MegaCrit.Sts2.Core.Models.Monsters.Rocket");
        }

        private static bool IsCombatRoomGetCreatureNode(MethodInfo method)
        {
            return method.Name == "GetCreatureNode"
                && (method.DeclaringType?.FullName ?? "").Contains("NCombatRoom", StringComparison.Ordinal);
        }
    }

    private static void InitLocManager()
    {
        // Create a LocManager instance with stub tables via reflection.
        // LocManager.Initialize() fails because PlatformUtil isn't available,
        // and Harmony can't patch some LocString methods due to JIT issues.
        // Solution: create an uninitialized LocManager, set its _tables, and
        // use Harmony only for the simple LocTable.GetRawText fallback.
        try
        {
            // Create uninitialized LocManager and set Instance
            var instanceProp = typeof(LocManager).GetProperty("Instance",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LocManager));
            instanceProp!.SetValue(null, instance);

            // Load real localization data from repo JSON files. The game engine's
            // LocString path should resolve in the requested CLI language, not
            // always English.
            var tablesField = typeof(LocManager).GetField("_tables",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var repoRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
            var locFolder = _loc.Lang == "zh" ? "localization_zhs" : "localization_eng";
            var locDir = Path.Combine(repoRoot, locFolder);
            var tables = LoadLocTables(locDir);
            tablesField!.SetValue(instance, tables);

            // Set Language
            var langProp = typeof(LocManager).GetProperty("Language",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            try { langProp?.SetValue(instance, _loc.Lang == "zh" ? "zhs" : "eng"); } catch { }

            // Set CultureInfo
            var cultureProp = typeof(LocManager).GetProperty("CultureInfo",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            try
            {
                cultureProp?.SetValue(instance,
                    _loc.Lang == "zh"
                        ? System.Globalization.CultureInfo.GetCultureInfo("zh-Hans")
                        : System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { }

            // Initialize _smartFormatter — the game uses `new SmartFormatter()`
            try
            {
                var sfField = typeof(LocManager).GetField("_smartFormatter",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                // Dump ALL fields (instance + static)
                foreach (var f in typeof(LocManager).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
                    Console.Error.WriteLine($"[DEBUG] LocManager {(f.IsStatic?"static":"inst")} field: {f.Name} ({f.FieldType.Name})");
                Console.Error.WriteLine($"[DEBUG] sfField: {sfField?.Name ?? "null"} type: {sfField?.FieldType?.Name ?? "null"}");
                if (sfField != null)
                {
                    try
                    {
                        // List constructors to find the right one
                        var ctors = sfField.FieldType.GetConstructors(
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        Console.Error.WriteLine($"[DEBUG] SmartFormatter has {ctors.Length} constructors:");
                        foreach (var ctor in ctors)
                        {
                            var ps = ctor.GetParameters();
                            Console.Error.WriteLine($"  ({string.Join(", ", ps.Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
                        }
                        // Try the one with fewest params
                        var bestCtor = ctors.OrderBy(c => c.GetParameters().Length).First();
                        var args2 = bestCtor.GetParameters().Select(p =>
                            p.HasDefaultValue ? p.DefaultValue :
                            p.ParameterType.FullName == "SmartFormat.Core.Settings.SmartSettings"
                                ? Activator.CreateInstance(p.ParameterType)
                                : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null
                        ).ToArray();
                        var sf = bestCtor.Invoke(args2);
                        sfField.SetValue(null, sf);
                        // Register extensions using the game's own LoadLocFormatters logic
                        // Call it via reflection on LocManager instance
                        try
                        {
                            var loadMethod = typeof(LocManager).GetMethod("LoadLocFormatters",
                                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                            if (loadMethod != null)
                            {
                                loadMethod.Invoke(instance, null);
                                Console.Error.WriteLine("[INFO] SmartFormatter initialized via LoadLocFormatters");
                            }
                            else
                            {
                                Console.Error.WriteLine("[INFO] SmartFormatter set (no LoadLocFormatters found)");
                            }
                        }
                        catch (Exception lfEx)
                        {
                            Console.Error.WriteLine($"[WARN] LoadLocFormatters failed: {lfEx.InnerException?.Message ?? lfEx.Message}");
                        }
                    }
                    catch (Exception sfEx)
                    {
                        Console.Error.WriteLine($"[WARN] SmartFormatter create failed: {sfEx.GetType().Name}: {sfEx.Message}");
                        if (sfEx.InnerException != null)
                            Console.Error.WriteLine($"  Inner: {sfEx.InnerException.GetType().Name}: {sfEx.InnerException.Message}");
                    }
                }
                else
                {
                    Console.Error.WriteLine("[WARN] _smartFormatter field not found in LocManager");
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WARN] _smartFormatter init: {ex.GetType().Name}: {ex.Message}\n{ex.InnerException?.Message}"); }

            // Initialize _engTables to point to _tables (avoid null ref in fallback)
            try
            {
                var engTablesField = typeof(LocManager).GetField("_engTables",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var engTables = _loc.Lang == "zh"
                    ? LoadLocTables(Path.Combine(repoRoot, "localization_eng"))
                    : tables;
                engTablesField?.SetValue(instance, engTables);
            }
            catch { }

            Console.Error.WriteLine($"[INFO] LocManager initialized with {locFolder} tables");

            // Use Harmony to patch methods that need fallback behavior
            var harmony = new Harmony("sts2headless.locpatch");

            // With real loc data loaded, we only need fallback patches for:
            // 1. LocTable.GetRawText — return key for missing entries instead of throwing
            // 2. LocManager.SmartFormat — _smartFormatter is null, return raw text instead
            // We do NOT patch GetFormattedText/GetRawText on LocString anymore
            // so the real localization pipeline works (needed for Neow event etc.)

            var getRawText = typeof(LocTable).GetMethod("GetRawText",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
                null, new[] { typeof(string) }, null);
            var prefix = typeof(LocPatches).GetMethod(nameof(LocPatches.GetRawTextPrefix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (getRawText != null && prefix != null)
            {
                harmony.Patch(getRawText, new HarmonyMethod(prefix));
                Console.Error.WriteLine("[INFO] Patched LocTable.GetRawText");
            }

            // Patch GetLocString to not throw
            var getLocString = typeof(LocTable).GetMethod("GetLocString");
            var glsPrefix = typeof(LocPatches).GetMethod(nameof(LocPatches.GetLocStringPrefix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (getLocString != null && glsPrefix != null)
            {
                try { harmony.Patch(getLocString, new HarmonyMethod(glsPrefix)); }
                catch (Exception ex4) { Console.Error.WriteLine($"[WARN] Failed to patch GetLocString: {ex4.Message}"); }
            }

            // Patch FromChooseABundleScreen to use our card selector
            try
            {
                var bundleMethod = typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd).GetMethod("FromChooseABundleScreen",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                var bundlePrefix = typeof(LocPatches).GetMethod(nameof(LocPatches.BundleScreenPrefix),
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                if (bundleMethod != null && bundlePrefix != null)
                {
                    harmony.Patch(bundleMethod, new HarmonyMethod(bundlePrefix));
                    Console.Error.WriteLine("[INFO] Patched FromChooseABundleScreen");
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WARN] Bundle patch: {ex.Message}"); }

            // Patch Neutralize.OnPlay to avoid NullRef in DamageCmd.Attack().Execute()
            try
            {
                var neutralizeType = typeof(MegaCrit.Sts2.Core.Models.Cards.Neutralize);
                var neutralizeOnPlay = neutralizeType.GetMethod("OnPlay",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (neutralizeOnPlay != null)
                {
                    var neutPrefix = typeof(LocPatches).GetMethod(nameof(LocPatches.NeutralizePrefix),
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                    if (neutPrefix != null)
                    {
                        harmony.Patch(neutralizeOnPlay, new HarmonyMethod(neutPrefix));
                        Console.Error.WriteLine("[INFO] Patched Neutralize.OnPlay");
                    }
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WARN] Neutralize patch: {ex.Message}"); }

            // Patch HasEntry to always return true
            PatchMethod(harmony, typeof(LocTable), "HasEntry", nameof(LocPatches.HasEntryPrefix));

            // Patch IsLocalKey to always return true
            PatchMethod(harmony, typeof(LocTable), "IsLocalKey", nameof(LocPatches.HasEntryPrefix));

            // Patch LocString.Exists (static) to always return true
            var locStringExists = typeof(LocString).GetMethod("Exists",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (locStringExists != null)
            {
                PatchMethod(harmony, locStringExists, nameof(LocPatches.HasEntryPrefix));
            }

            // Patch LocTable.GetLocStringsWithPrefix to return empty list
            PatchMethod(harmony, typeof(LocTable), "GetLocStringsWithPrefix", nameof(LocPatches.GetLocStringsWithPrefixPrefix));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] InitLocManager failed: {ex.Message}");
        }
    }

    private static Dictionary<string, LocTable> LoadLocTables(string locDir)
    {
        var tables = new Dictionary<string, LocTable>();
        if (Directory.Exists(locDir))
        {
            foreach (var file in Directory.GetFiles(locDir, "*.json"))
            {
                try
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                        File.ReadAllText(file));
                    if (data != null)
                        tables[name] = new LocTable(name, data);
                }
                catch { }
            }
            Console.Error.WriteLine($"[INFO] Loaded {tables.Count} localization tables from {locDir}");
            return tables;
        }

        Console.Error.WriteLine($"[WARN] Localization dir not found: {locDir}");
        var tableNames = new[] {
            "achievements","acts","afflictions","ancients","ascension",
            "bestiary","card_keywords","card_library","card_reward_ui",
            "card_selection","cards","characters","combat_messages",
            "credits","enchantments","encounters","epochs","eras",
            "events","ftues","game_over_screen","gameplay_ui",
            "inspect_relic_screen","intents","main_menu_ui","map",
            "merchant_room","modifiers","monsters","orbs","potion_lab",
            "potions","powers","relic_collection","relics","rest_site_ui",
            "run_history","settings_ui","static_hover_tips","stats_screen",
            "timeline","vfx"
        };
        foreach (var name in tableNames)
            tables[name] = new LocTable(name, new Dictionary<string, string>());
        return tables;
    }

    private static void PatchMethod(Harmony harmony, Type type, string methodName, string patchName)
    {
        try
        {
            var method = type.GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            PatchMethod(harmony, method, patchName);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] Failed to patch {type.Name}.{methodName}: {ex.Message}"); }
    }

    private static void PatchMethod(Harmony harmony, System.Reflection.MethodInfo? method, string patchName)
    {
        if (method == null) return;
        try
        {
            var prefix = typeof(LocPatches).GetMethod(patchName, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (prefix != null) harmony.Patch(method, new HarmonyMethod(prefix));
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] Failed to patch {method.Name}: {ex.Message}"); }
    }

    internal static class LocPatches
    {
        public static bool GetRawTextPrefix(LocTable __instance, string key, ref string __result)
        {
            var tableName = GetLocTableName(__instance);
            if (_loc.IsLoaded == true && !string.IsNullOrWhiteSpace(tableName))
            {
                var resolved = _loc.Bilingual(tableName, key);
                if (resolved != key)
                {
                    __result = resolved;
                    return false;
                }
            }

            __result = key;
            return false;
        }

        private static string? GetLocTableName(LocTable table)
        {
            try
            {
                var nameField = typeof(LocTable).GetField("_name",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                return nameField?.GetValue(table) as string;
            }
            catch
            {
                return null;
            }
        }

        public static bool GetFormattedTextPrefix(LocString __instance, ref string __result)
        {
            __result = __instance?.LocEntryKey ?? "";
            return false;
        }

        public static bool GetRawTextInstancePrefix(LocString __instance, ref string __result)
        {
            __result = __instance?.LocEntryKey ?? "";
            return false;
        }


        /// <summary>Harmony prefix: replace Neutralize.OnPlay with safe damage+weak.</summary>
        public static bool NeutralizePrefix(CardModel __instance, ref Task __result,
            PlayerChoiceContext choiceContext, CardPlay cardPlay)
        {
            if (cardPlay.Target == null) { __result = Task.CompletedTask; return false; }
            __result = NeutralizeSafe(__instance, choiceContext, cardPlay);
            return false;
        }

        private static async Task NeutralizeSafe(CardModel card, PlayerChoiceContext ctx, CardPlay play)
        {
            try
            {
                await CreatureCmd.Damage(ctx, play.Target!, card.DynamicVars.Damage.BaseValue,
                    MegaCrit.Sts2.Core.ValueProps.ValueProp.Move, card);
                await PowerCmd.Apply<WeakPower>(play.Target!, card.DynamicVars["WeakPower"].BaseValue,
                    card.Owner.Creature, card);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WARN] Neutralize safe: {ex.Message}"); }
        }

        public static bool HasEntryPrefix(ref bool __result)
        {
            __result = true;
            return false;
        }

        public static bool GetLocStringPrefix(LocTable __instance, string key, ref LocString __result)
        {
            var nameField = typeof(LocTable).GetField("_name",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var tableName = nameField?.GetValue(__instance) as string ?? "_unknown";
            __result = new LocString(tableName, key);
            return false;
        }

        /// <summary>
        /// Intercept bundle selection — store bundles and wait for player to pick a pack index.
        /// </summary>
        public static bool BundleScreenPrefix(
            MegaCrit.Sts2.Core.Entities.Players.Player player,
            IReadOnlyList<IReadOnlyList<CardModel>> bundles,
            ref Task<IEnumerable<CardModel>> __result)
        {
            if (bundles.Count == 0)
            {
                __result = Task.FromResult<IEnumerable<CardModel>>(Array.Empty<CardModel>());
                return false;
            }

            // Store pending bundles for the main loop to present
            var sim = _bundleSimRef;
            if (sim != null)
            {
                sim._pendingBundles = bundles;
                sim._pendingBundleTcs = new TaskCompletionSource<IEnumerable<CardModel>>();
                Console.Error.WriteLine($"[SIM] Bundle selection pending: {bundles.Count} packs");

                __result = sim._pendingBundleTcs.Task;
                return false;
            }

            __result = Task.FromResult<IEnumerable<CardModel>>(bundles[0]);
            return false;
        }

        // Static reference so Harmony patch can access the simulator instance
        internal static RunSimulator? _bundleSimRef;

        public static bool GetLocStringsWithPrefixPrefix(ref IReadOnlyList<LocString> __result)
        {
            __result = new List<LocString>();
            return false;
        }
    }

    private static void Log(string message)
    {
        Console.Error.WriteLine($"[SIM] {message}");
    }

    private static Dictionary<string, object?> Error(string message) =>
        new() { ["type"] = "error", ["message"] = message };

    private static Dictionary<string, object?> ErrorWithTrace(string context, Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null) inner = inner.InnerException;
        return new Dictionary<string, object?>
        {
            ["type"] = "error",
            ["message"] = $"{context}: {inner.GetType().Name}: {inner.Message}",
            ["stack_trace"] = inner.StackTrace,
        };
    }

    public Dictionary<string, object?> GetFullMap()
    {
        if (_runState?.Map == null)
            return Error("No map available");

        var map = _runState.Map;
        var rows = new List<List<Dictionary<string, object?>>>();
        var currentCoord = _runState.CurrentMapCoord;
        var visited = _runState.VisitedMapCoords;

        for (int row = 0; row < map.GetRowCount(); row++)
        {
            var rowNodes = new List<Dictionary<string, object?>>();
            foreach (var point in map.GetPointsInRow(row))
            {
                if (point == null) continue;
                var children = point.Children?.Select(ch => new Dictionary<string, object?>
                {
                    ["col"] = (int)ch.coord.col,
                    ["row"] = (int)ch.coord.row,
                }).ToList();

                var isVisited = visited?.Any(v => v.col == point.coord.col && v.row == point.coord.row) ?? false;
                var isCurrent = currentCoord.HasValue &&
                    currentCoord.Value.col == point.coord.col && currentCoord.Value.row == point.coord.row;

                rowNodes.Add(new Dictionary<string, object?>
                {
                    ["col"] = (int)point.coord.col,
                    ["row"] = (int)point.coord.row,
                    ["type"] = point.PointType.ToString(),
                    ["children"] = children,
                    ["visited"] = isVisited,
                    ["current"] = isCurrent,
                });
            }
            if (rowNodes.Count > 0)
                rows.Add(rowNodes);
        }

        // Boss node
        var bossNode = new Dictionary<string, object?>
        {
            ["col"] = (int)map.BossMapPoint.coord.col,
            ["row"] = (int)map.BossMapPoint.coord.row,
            ["type"] = map.BossMapPoint.PointType.ToString(),
        };

        // Add boss name/id — use BossEncounter?.Id?.Entry
        try
        {
            var bossIdEntry = _runState.Act?.BossEncounter?.Id?.Entry;
            if (!string.IsNullOrEmpty(bossIdEntry))
            {
                bossNode["id"] = bossIdEntry;
                bossNode["name"] = BossEncounterDisplayName(bossIdEntry);
            }
        }
        catch { }

        return new Dictionary<string, object?>
        {
            ["type"] = "map",
            ["context"] = RunContext(),
            ["rows"] = rows,
            ["boss"] = bossNode,
            ["current_coord"] = currentCoord.HasValue ? new Dictionary<string, object?>
            {
                ["col"] = (int)currentCoord.Value.col,
                ["row"] = (int)currentCoord.Value.row,
            } : null,
        };
    }

    public void CleanUp()
    {
        try
        {
            if (RunManager.Instance.IsInProgress)
                RunManager.Instance.CleanUp(graceful: true);
        }
        catch (Exception ex)
        {
            Log($"CleanUp exception: {ex.Message}");
        }
        finally
        {
            _runState = null;
            ResetTransientHeadlessState();
            ResetHeadlessCommandState();
        }
    }

    private void PrepareForRunReplacement()
    {
        if (_runState != null || RunManager.Instance.IsInProgress)
        {
            CleanUp();
            return;
        }

        ResetTransientHeadlessState();
        ResetHeadlessCommandState();
    }

    private void ResetTransientHeadlessState()
    {
        _eventOptionChosen = false;
        _lastEventOptionCount = 0;
        _pendingEventOptionTask = null;
        _pendingEventChoiceAfterCombat = null;
        _pendingEventResult = null;
        _pendingShopPurchaseTask = null;
        _pendingShopCardRemovalEntry = null;
        _pendingRewards = null;
        _pendingCardReward = null;
        _rewardsProcessed = false;
        _goldBeforeCombat = 0;
        _lastKnownHp = 0;
        _pendingCardSelectionSourceCard = null;
        _pendingCardSelectionSourceEventOption = null;
        _pendingCardSelectionSourceRoomOption = null;
        _pendingCardSelectionSourcePotion = null;
        _shopItemSnapshots.Clear();
        _cardRuntimeIds.Clear();
        _creatureRuntimeIds.Clear();
        _nextCardRuntimeId = 1;
        _nextCreatureRuntimeId = 1;
        _preCurrentRoomSaveJson = null;
        _pendingBundles = null;
        _pendingBundleTcs = null;
        _cardSelector.Reset();
        YieldPatches.ActiveCrystalSphereMinigame = null;
    }

    internal static void ResetHeadlessCommandState()
    {
        LocPatches._bundleSimRef = null;
        YieldPatches.ActiveCrystalSphereMinigame = null;
        ResetCardSelectCmdSelector();
    }

    private static void ResetCardSelectCmdSelector()
    {
        var resetMethod = typeof(CardSelectCmd).GetMethod(
            "Reset",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null)
            ?? typeof(CardSelectCmd).GetMethod(
                "ResetForTests",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
        if (resetMethod != null)
        {
            resetMethod.Invoke(null, null);
        }

        var selectorType = typeof(MegaCrit.Sts2.Core.TestSupport.ICardSelector);
        foreach (var property in typeof(CardSelectCmd).GetProperties(
                     BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (property.SetMethod == null)
                continue;
            if (property.PropertyType.IsAssignableFrom(selectorType) ||
                selectorType.IsAssignableFrom(property.PropertyType) ||
                property.PropertyType.Name.Contains("ICardSelector", StringComparison.Ordinal))
            {
                property.SetValue(null, null);
            }
        }
        foreach (var field in typeof(CardSelectCmd).GetFields(
                     BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (field.IsLiteral)
                continue;
            var fieldValue = field.GetValue(null);
            if (fieldValue != null &&
                field.FieldType.IsGenericType &&
                field.FieldType.GetGenericTypeDefinition() == typeof(Stack<>) &&
                selectorType.IsAssignableFrom(field.FieldType.GetGenericArguments()[0]))
            {
                field.FieldType.GetMethod("Clear")?.Invoke(fieldValue, null);
                continue;
            }
            if (field.IsInitOnly)
                continue;
            if (field.FieldType.IsAssignableFrom(selectorType) ||
                selectorType.IsAssignableFrom(field.FieldType) ||
                field.FieldType.Name.Contains("ICardSelector", StringComparison.Ordinal))
            {
                field.SetValue(null, null);
            }
        }
    }

    #endregion
}
