using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatReconnect;

public enum SavedCombatHistoryEntryKind
{
    BlockGained,
    CardAfflicted,
    CardDiscarded,
    CardDrawn,
    CardExhausted,
    CardGenerated,
    CardPlayFinished,
    CardPlayStarted,
    CreatureAttacked,
    DamageReceived,
    EnergySpent,
    MonsterPerformedMove,
    OrbChanneled,
    PotionUsed,
    PowerReceived,
    StarsModified,
    Summoned
}

public struct CombatHistoryPlayerTurnState : IPacketSerializable
{
    public ulong playerId;
    public int turnNumber;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(playerId);
        writer.WriteInt(turnNumber);
    }

    public void Deserialize(PacketReader reader)
    {
        playerId = reader.ReadULong();
        turnNumber = reader.ReadInt();
    }
}

public struct CombatHistoryCreatureState : IPacketSerializable
{
    public uint combatId;
    public bool isPlayer;
    public ulong playerId;
    public bool isMonster;
    public ModelId monsterId;
    public bool hasPetOwner;
    public ulong petOwnerId;
    public CombatSide side;
    public string slotName;
    public int currentHp;
    public int maxHp;
    public int block;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(combatId);
        writer.WriteBool(isPlayer);
        if (isPlayer) writer.WriteULong(playerId);
        writer.WriteBool(isMonster);
        if (isMonster) writer.WriteModelEntry(monsterId);
        writer.WriteBool(hasPetOwner);
        if (hasPetOwner) writer.WriteULong(petOwnerId);
        writer.WriteEnum(side);
        writer.WriteString(slotName ?? string.Empty);
        writer.WriteInt(currentHp);
        writer.WriteInt(maxHp);
        writer.WriteInt(block);
    }

    public void Deserialize(PacketReader reader)
    {
        combatId = reader.ReadUInt();
        isPlayer = reader.ReadBool();
        if (isPlayer) playerId = reader.ReadULong();
        isMonster = reader.ReadBool();
        if (isMonster) monsterId = reader.ReadModelIdAssumingType<MonsterModel>();
        hasPetOwner = reader.ReadBool();
        if (hasPetOwner) petOwnerId = reader.ReadULong();
        side = reader.ReadEnum<CombatSide>();
        slotName = reader.ReadString();
        currentHp = reader.ReadInt();
        maxHp = reader.ReadInt();
        block = reader.ReadInt();
    }
}

public struct CombatHistoryCardState : IPacketSerializable
{
    public bool hasCombatCardId;
    public uint combatCardId;
    public bool hasOwner;
    public ulong ownerId;
    public SerializableCard card;
    public ExtendedCardState runtime;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteBool(hasCombatCardId);
        if (hasCombatCardId) writer.WriteUInt(combatCardId);
        writer.WriteBool(hasOwner);
        if (hasOwner) writer.WriteULong(ownerId);
        writer.Write(card);
        writer.Write(runtime);
    }

    public void Deserialize(PacketReader reader)
    {
        hasCombatCardId = reader.ReadBool();
        if (hasCombatCardId) combatCardId = reader.ReadUInt();
        hasOwner = reader.ReadBool();
        if (hasOwner) ownerId = reader.ReadULong();
        card = reader.Read<SerializableCard>();
        runtime = reader.Read<ExtendedCardState>();
    }
}

public struct CombatHistoryCardPlayState : IPacketSerializable
{
    public int cardIndex;
    public ulong playerId;
    public bool hasTarget;
    public uint targetCombatId;
    public PileType resultPile;
    public int energySpent;
    public int energyValue;
    public int starsSpent;
    public int starValue;
    public bool isAutoPlay;
    public int playIndex;
    public int playCount;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(cardIndex);
        writer.WriteULong(playerId);
        writer.WriteBool(hasTarget);
        if (hasTarget) writer.WriteUInt(targetCombatId);
        writer.WriteEnum(resultPile);
        writer.WriteInt(energySpent);
        writer.WriteInt(energyValue);
        writer.WriteInt(starsSpent);
        writer.WriteInt(starValue);
        writer.WriteBool(isAutoPlay);
        writer.WriteInt(playIndex);
        writer.WriteInt(playCount);
    }

    public void Deserialize(PacketReader reader)
    {
        cardIndex = reader.ReadInt();
        playerId = reader.ReadULong();
        hasTarget = reader.ReadBool();
        if (hasTarget) targetCombatId = reader.ReadUInt();
        resultPile = reader.ReadEnum<PileType>();
        energySpent = reader.ReadInt();
        energyValue = reader.ReadInt();
        starsSpent = reader.ReadInt();
        starValue = reader.ReadInt();
        isAutoPlay = reader.ReadBool();
        playIndex = reader.ReadInt();
        playCount = reader.ReadInt();
    }
}

public struct CombatHistoryDamageResultState : IPacketSerializable
{
    public uint receiverCombatId;
    public ValueProp props;
    public int blockedDamage;
    public int unblockedDamage;
    public int overkillDamage;
    public bool wasBlockBroken;
    public bool wasFullyBlocked;
    public bool wasTargetKilled;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(receiverCombatId);
        writer.WriteEnum(props);
        writer.WriteInt(blockedDamage);
        writer.WriteInt(unblockedDamage);
        writer.WriteInt(overkillDamage);
        writer.WriteBool(wasBlockBroken);
        writer.WriteBool(wasFullyBlocked);
        writer.WriteBool(wasTargetKilled);
    }

    public void Deserialize(PacketReader reader)
    {
        receiverCombatId = reader.ReadUInt();
        props = reader.ReadEnum<ValueProp>();
        blockedDamage = reader.ReadInt();
        unblockedDamage = reader.ReadInt();
        overkillDamage = reader.ReadInt();
        wasBlockBroken = reader.ReadBool();
        wasFullyBlocked = reader.ReadBool();
        wasTargetKilled = reader.ReadBool();
    }
}

public struct CombatHistoryPowerState : IPacketSerializable
{
    public ModelId id;
    public bool hasOwner;
    public uint ownerCombatId;
    public int ownerOccurrence;
    public int amount;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteModelEntry(id);
        writer.WriteBool(hasOwner);
        if (hasOwner) writer.WriteUInt(ownerCombatId);
        writer.WriteInt(ownerOccurrence);
        writer.WriteInt(amount);
    }

    public void Deserialize(PacketReader reader)
    {
        id = reader.ReadModelIdAssumingType<PowerModel>();
        hasOwner = reader.ReadBool();
        if (hasOwner) ownerCombatId = reader.ReadUInt();
        ownerOccurrence = reader.ReadInt();
        amount = reader.ReadInt();
    }
}

public struct CombatHistoryOrbState : IPacketSerializable
{
    public ModelId id;
    public bool hasOwner;
    public ulong ownerId;
    public int queueIndex;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteModelEntry(id);
        writer.WriteBool(hasOwner);
        if (hasOwner) writer.WriteULong(ownerId);
        writer.WriteInt(queueIndex);
    }

    public void Deserialize(PacketReader reader)
    {
        id = reader.ReadModelIdAssumingType<OrbModel>();
        hasOwner = reader.ReadBool();
        if (hasOwner) ownerId = reader.ReadULong();
        queueIndex = reader.ReadInt();
    }
}

public struct CombatHistoryPotionState : IPacketSerializable
{
    public ModelId id;
    public bool hasOwner;
    public ulong ownerId;
    public int slotIndex;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteModelEntry(id);
        writer.WriteBool(hasOwner);
        if (hasOwner) writer.WriteULong(ownerId);
        writer.WriteInt(slotIndex);
    }

    public void Deserialize(PacketReader reader)
    {
        id = reader.ReadModelIdAssumingType<PotionModel>();
        hasOwner = reader.ReadBool();
        if (hasOwner) ownerId = reader.ReadULong();
        slotIndex = reader.ReadInt();
    }
}

public struct CombatHistoryEntryState : IPacketSerializable
{
    public SavedCombatHistoryEntryKind kind;
    public int roundNumber;
    public CombatSide currentSide;
    public bool hasActor;
    public uint actorCombatId;
    public List<CombatHistoryPlayerTurnState> playerTurns;
    public int amount;
    public string decimalAmount;
    public ValueProp props;
    public bool flag;
    public int cardIndex;
    public int cardPlayIndex;
    public int damageResultIndex;
    public List<int> damageResultIndexes;
    public bool hasCreature;
    public uint creatureCombatId;
    public bool hasPlayer;
    public ulong playerId;
    public List<uint> targetCombatIds;
    public bool hasAffliction;
    public ModelId afflictionId;
    public int powerIndex;
    public int orbIndex;
    public int potionIndex;
    public bool hasMonster;
    public uint monsterCombatId;
    public SavedMoveState move;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteEnum(kind);
        writer.WriteInt(roundNumber);
        writer.WriteEnum(currentSide);
        writer.WriteBool(hasActor);
        if (hasActor) writer.WriteUInt(actorCombatId);
        writer.WriteList(playerTurns);
        writer.WriteInt(amount);
        writer.WriteString(decimalAmount ?? string.Empty);
        writer.WriteEnum(props);
        writer.WriteBool(flag);
        writer.WriteInt(cardIndex);
        writer.WriteInt(cardPlayIndex);
        writer.WriteInt(damageResultIndex);
        WriteInts(writer, damageResultIndexes);
        writer.WriteBool(hasCreature);
        if (hasCreature) writer.WriteUInt(creatureCombatId);
        writer.WriteBool(hasPlayer);
        if (hasPlayer) writer.WriteULong(playerId);
        WriteUInts(writer, targetCombatIds);
        writer.WriteBool(hasAffliction);
        if (hasAffliction) writer.WriteModelEntry(afflictionId);
        writer.WriteInt(powerIndex);
        writer.WriteInt(orbIndex);
        writer.WriteInt(potionIndex);
        writer.WriteBool(hasMonster);
        if (hasMonster) writer.WriteUInt(monsterCombatId);
        writer.Write(move);
    }

    public void Deserialize(PacketReader reader)
    {
        kind = reader.ReadEnum<SavedCombatHistoryEntryKind>();
        roundNumber = reader.ReadInt();
        currentSide = reader.ReadEnum<CombatSide>();
        hasActor = reader.ReadBool();
        if (hasActor) actorCombatId = reader.ReadUInt();
        playerTurns = reader.ReadList<CombatHistoryPlayerTurnState>();
        amount = reader.ReadInt();
        decimalAmount = reader.ReadString();
        props = reader.ReadEnum<ValueProp>();
        flag = reader.ReadBool();
        cardIndex = reader.ReadInt();
        cardPlayIndex = reader.ReadInt();
        damageResultIndex = reader.ReadInt();
        damageResultIndexes = ReadInts(reader);
        hasCreature = reader.ReadBool();
        if (hasCreature) creatureCombatId = reader.ReadUInt();
        hasPlayer = reader.ReadBool();
        if (hasPlayer) playerId = reader.ReadULong();
        targetCombatIds = ReadUInts(reader);
        hasAffliction = reader.ReadBool();
        if (hasAffliction) afflictionId = reader.ReadModelIdAssumingType<AfflictionModel>();
        powerIndex = reader.ReadInt();
        orbIndex = reader.ReadInt();
        potionIndex = reader.ReadInt();
        hasMonster = reader.ReadBool();
        if (hasMonster) monsterCombatId = reader.ReadUInt();
        move = reader.Read<SavedMoveState>();
    }

    private static void WriteInts(PacketWriter writer, List<int>? values)
    {
        writer.WriteInt(values?.Count ?? 0);
        if (values == null) return;
        foreach (int value in values) writer.WriteInt(value);
    }

    private static List<int> ReadInts(PacketReader reader)
    {
        int count = reader.ReadInt();
        var values = new List<int>(count);
        for (int i = 0; i < count; i++) values.Add(reader.ReadInt());
        return values;
    }

    private static void WriteUInts(PacketWriter writer, List<uint>? values)
    {
        writer.WriteInt(values?.Count ?? 0);
        if (values == null) return;
        foreach (uint value in values) writer.WriteUInt(value);
    }

    private static List<uint> ReadUInts(PacketReader reader)
    {
        int count = reader.ReadInt();
        var values = new List<uint>(count);
        for (int i = 0; i < count; i++) values.Add(reader.ReadUInt());
        return values;
    }
}

public struct ExtendedCombatHistoryState : IPacketSerializable
{
    public List<CombatHistoryCreatureState> creatures;
    public List<CombatHistoryCardState> cards;
    public List<CombatHistoryCardPlayState> cardPlays;
    public List<CombatHistoryDamageResultState> damageResults;
    public List<CombatHistoryPowerState> powers;
    public List<CombatHistoryOrbState> orbs;
    public List<CombatHistoryPotionState> potions;
    public List<CombatHistoryEntryState> entries;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteList(creatures);
        writer.WriteList(cards);
        writer.WriteList(cardPlays);
        writer.WriteList(damageResults);
        writer.WriteList(powers);
        writer.WriteList(orbs);
        writer.WriteList(potions);
        writer.WriteList(entries);
    }

    public void Deserialize(PacketReader reader)
    {
        creatures = reader.ReadList<CombatHistoryCreatureState>();
        cards = reader.ReadList<CombatHistoryCardState>();
        cardPlays = reader.ReadList<CombatHistoryCardPlayState>();
        damageResults = reader.ReadList<CombatHistoryDamageResultState>();
        powers = reader.ReadList<CombatHistoryPowerState>();
        orbs = reader.ReadList<CombatHistoryOrbState>();
        potions = reader.ReadList<CombatHistoryPotionState>();
        entries = reader.ReadList<CombatHistoryEntryState>();
        CombatHistoryStateSupport.Validate(this, "deserialize");
    }
}

internal static class CombatHistoryStateSupport
{
    private const int MaxHistoryEntries = 100_000;
    private static readonly FieldInfo EntriesField = AccessTools.Field(typeof(CombatHistory), "_entries");
    private static readonly FieldInfo ChangedField = AccessTools.Field(typeof(CombatHistory), "Changed");
    private static readonly FieldInfo PlayerTurnsField = AccessTools.Field(typeof(CombatHistoryEntry), "_playerTurnNumbers");
    private static readonly FieldInfo ActorField = AccessTools.Field(typeof(CombatHistoryEntry), "<Actor>k__BackingField");
    private static readonly FieldInfo RoundField = AccessTools.Field(typeof(CombatHistoryEntry), "<RoundNumber>k__BackingField");
    private static readonly FieldInfo SideField = AccessTools.Field(typeof(CombatHistoryEntry), "<CurrentSide>k__BackingField");
    private static readonly FieldInfo HistoryField = AccessTools.Field(typeof(CombatHistoryEntry), "<History>k__BackingField");
    private static readonly FieldInfo CardOwnerField = AccessTools.Field(typeof(CardModel), "_owner");
    private static readonly FieldInfo PowerOwnerField = AccessTools.Field(typeof(PowerModel), "_owner");
    private static readonly FieldInfo OrbOwnerField = AccessTools.Field(typeof(OrbModel), "_owner");
    private static readonly FieldInfo OrbListField = AccessTools.Field(typeof(MegaCrit.Sts2.Core.Entities.Orbs.OrbQueue), "_orbs");
    private static readonly FieldInfo CreatureBlockField = AccessTools.Field(typeof(Creature), "_block");

    internal static ExtendedCombatHistoryState Capture(CombatState combat)
    {
        CombatHistory history = CombatManager.Instance.History;
        List<CombatHistoryEntry> entries = history.Entries.ToList();
        if (entries.Count > MaxHistoryEntries)
            throw new InvalidOperationException($"Combat history contains {entries.Count} entries; limit={MaxHistoryEntries}.");

        var context = new CaptureContext(combat);
        var result = new ExtendedCombatHistoryState
        {
            creatures = context.Creatures,
            cards = context.Cards,
            cardPlays = context.CardPlays,
            damageResults = context.DamageResults,
            powers = context.Powers,
            orbs = context.Orbs,
            potions = context.Potions,
            entries = entries.Select(context.CaptureEntry).ToList()
        };
        Validate(result, "capture");
        ReconnectLog.Write($"Captured combat history: entries={result.entries.Count}, cards={result.cards.Count}, " +
                           $"plays={result.cardPlays.Count}, damage={result.damageResults.Count}.");
        return result;
    }

    internal static void Restore(CombatState combat, ExtendedCombatHistoryState saved)
    {
        Validate(saved, "restore input");
        CombatHistory history = CombatManager.Instance.History;
        var context = new RestoreContext(combat, saved);
        List<CombatHistoryEntry> restored = saved.entries.Select(entry => context.RestoreEntry(entry, history)).ToList();
        var liveEntries = (List<CombatHistoryEntry>)EntriesField.GetValue(history)!;
        liveEntries.Clear();
        liveEntries.AddRange(restored);

        ExtendedCombatHistoryState actual = Capture(combat);
        string expectedHash = Fingerprint(saved);
        string actualHash = Fingerprint(actual);
        if (expectedHash != actualHash)
        {
            string difference = DescribeFirstDifference(saved, actual);
            ReconnectLog.Write($"Combat history restore mismatch detail: {difference}.");
            throw new InvalidDataException(
                $"Combat history did not restore exactly: expected={expectedHash}, actual={actualHash}, " +
                $"firstDifference={difference}.");
        }
        (ChangedField.GetValue(history) as Action)?.Invoke();
        ReconnectLog.Write($"Restored combat history: entries={restored.Count}, hash={actualHash}.");
    }

    internal static string Fingerprint(ExtendedCombatHistoryState state)
    {
        var writer = new PacketWriter { WarnOnGrow = false };
        writer.Write(state);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            writer.Buffer.AsSpan(0, writer.BytePosition)));
    }

    internal static string DescribeFirstDifference(
        ExtendedCombatHistoryState expected,
        ExtendedCombatHistoryState actual)
        => DescribeListDifference("creatures", expected.creatures, actual.creatures)
           ?? DescribeListDifference("cards", expected.cards, actual.cards)
           ?? DescribeListDifference("cardPlays", expected.cardPlays, actual.cardPlays)
           ?? DescribeListDifference("damageResults", expected.damageResults, actual.damageResults)
           ?? DescribeListDifference("powers", expected.powers, actual.powers)
           ?? DescribeListDifference("orbs", expected.orbs, actual.orbs)
           ?? DescribeListDifference("potions", expected.potions, actual.potions)
           ?? DescribeListDifference("entries", expected.entries, actual.entries)
           ?? "serialized payload differs, but no field-level difference was found";

    private static string? DescribeListDifference<T>(
        string label,
        IReadOnlyList<T> expected,
        IReadOnlyList<T> actual)
        where T : struct, IPacketSerializable
    {
        if (expected.Count != actual.Count)
            return $"{label}.Count: expected={expected.Count}, actual={actual.Count}";

        for (int index = 0; index < expected.Count; index++)
        {
            byte[] expectedBytes = SerializeDiagnosticValue(expected[index]);
            byte[] actualBytes = SerializeDiagnosticValue(actual[index]);
            if (expectedBytes.AsSpan().SequenceEqual(actualBytes)) continue;

            string path = $"{label}[{index}]";
            string? fieldDifference = DescribeValueDifference(expected[index], actual[index], path, 0);
            if (fieldDifference != null) return fieldDifference;
            return $"{path}: serialized expected={ShortHash(expectedBytes)}/{expectedBytes.Length}B, " +
                   $"actual={ShortHash(actualBytes)}/{actualBytes.Length}B";
        }

        return null;
    }

    private static string? DescribeValueDifference(
        object? expected,
        object? actual,
        string path,
        int depth)
    {
        if (expected == null || actual == null)
            return expected == null && actual == null
                ? null
                : $"{path}: expected={FormatDiagnosticValue(expected)}, actual={FormatDiagnosticValue(actual)}";
        if (expected.GetType() != actual.GetType())
            return $"{path}.Type: expected={expected.GetType().FullName}, actual={actual.GetType().FullName}";
        if (depth >= 20)
            return Equals(expected, actual)
                ? null
                : $"{path}: values differ beyond diagnostic depth limit";

        if (expected is IList expectedList && actual is IList actualList)
        {
            if (expectedList.Count != actualList.Count)
                return $"{path}.Count: expected={expectedList.Count}, actual={actualList.Count}";
            for (int index = 0; index < expectedList.Count; index++)
            {
                string? difference = DescribeValueDifference(
                    expectedList[index], actualList[index], $"{path}[{index}]", depth + 1);
                if (difference != null) return difference;
            }
            return null;
        }

        Type type = expected.GetType();
        if (IsDiagnosticScalar(type))
            return Equals(expected, actual)
                ? null
                : $"{path}: expected={FormatDiagnosticValue(expected)}, actual={FormatDiagnosticValue(actual)}";

        FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public)
            .OrderBy(field => field.MetadataToken)
            .ToArray();
        if (fields.Length > 0)
        {
            foreach (FieldInfo field in fields)
            {
                string? difference = DescribeValueDifference(
                    field.GetValue(expected), field.GetValue(actual), $"{path}.{field.Name}", depth + 1);
                if (difference != null) return difference;
            }
            return null;
        }

        return Equals(expected, actual)
            ? null
            : $"{path}: expected={FormatDiagnosticValue(expected)}, actual={FormatDiagnosticValue(actual)}";
    }

    private static bool IsDiagnosticScalar(Type type)
    {
        Type scalar = Nullable.GetUnderlyingType(type) ?? type;
        return scalar.IsPrimitive || scalar.IsEnum || scalar == typeof(string) ||
               scalar == typeof(decimal) || scalar == typeof(Guid);
    }

    private static byte[] SerializeDiagnosticValue(IPacketSerializable value)
    {
        var writer = new PacketWriter { WarnOnGrow = false };
        value.Serialize(writer);
        return writer.Buffer.AsSpan(0, writer.BytePosition).ToArray();
    }

    private static string ShortHash(byte[] value)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(value))[..12];

    private static string FormatDiagnosticValue(object? value)
    {
        if (value == null) return "null";
        string text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
                      ?? value.GetType().FullName
                      ?? "<unknown>";
        text = text.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return text.Length <= 160 ? text : text[..157] + "...";
    }

    internal static void Validate(ExtendedCombatHistoryState state, string phase)
    {
        if (state.creatures == null || state.cards == null || state.cardPlays == null || state.damageResults == null ||
            state.powers == null || state.orbs == null || state.potions == null || state.entries == null)
            throw new InvalidDataException($"Combat history {phase} contains a null collection.");
        if (state.entries.Count > MaxHistoryEntries)
            throw new InvalidDataException($"Combat history {phase} contains too many entries: {state.entries.Count}.");
        foreach (CombatHistoryEntryState entry in state.entries)
        {
            if (!Enum.IsDefined(entry.kind) || !Enum.IsDefined(entry.currentSide) || entry.roundNumber < 0)
                throw new InvalidDataException($"Combat history {phase} contains invalid entry metadata.");
            if (entry.playerTurns == null || entry.playerTurns.Select(turn => turn.playerId).Distinct().Count() != entry.playerTurns.Count)
                throw new InvalidDataException($"Combat history {phase} contains invalid player-turn metadata.");
        }
        if (state.creatures.Select(creature => creature.combatId).Distinct().Count() != state.creatures.Count ||
            state.creatures.Any(creature => creature.isPlayer == creature.isMonster ||
                (creature.isPlayer && creature.hasPetOwner) || !Enum.IsDefined(creature.side)))
            throw new InvalidDataException($"Combat history {phase} contains invalid creature metadata.");
    }

    private static uint RequireCombatId(Creature? creature, string label)
        => creature?.CombatId ?? throw new InvalidOperationException($"Combat history {label} has no stable combat ID.");

    private sealed class CaptureContext
    {
        private readonly CombatState _combat;
        private readonly Dictionary<CardModel, int> _cardIndexes = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<CardPlay, int> _cardPlayIndexes = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<DamageResult, int> _damageIndexes = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<PowerModel, int> _powerIndexes = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<OrbModel, int> _orbIndexes = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<PotionModel, int> _potionIndexes = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<uint, Creature> _creaturesById = new();

        internal readonly List<CombatHistoryCreatureState> Creatures = new();
        internal readonly List<CombatHistoryCardState> Cards = new();
        internal readonly List<CombatHistoryCardPlayState> CardPlays = new();
        internal readonly List<CombatHistoryDamageResultState> DamageResults = new();
        internal readonly List<CombatHistoryPowerState> Powers = new();
        internal readonly List<CombatHistoryOrbState> Orbs = new();
        internal readonly List<CombatHistoryPotionState> Potions = new();

        internal CaptureContext(CombatState combat) => _combat = combat;

        internal CombatHistoryEntryState CaptureEntry(CombatHistoryEntry entry)
        {
            var saved = new CombatHistoryEntryState
            {
                roundNumber = (int)RoundField.GetValue(entry)!,
                currentSide = (CombatSide)SideField.GetValue(entry)!,
                hasActor = entry.Actor != null,
                actorCombatId = entry.Actor == null ? 0 : CaptureCreature(entry.Actor, "actor"),
                playerTurns = ((Dictionary<ulong, int>)PlayerTurnsField.GetValue(entry)!)
                    .OrderBy(pair => pair.Key)
                    .Select(pair => new CombatHistoryPlayerTurnState { playerId = pair.Key, turnNumber = pair.Value })
                    .ToList(),
                decimalAmount = string.Empty,
                cardIndex = -1,
                cardPlayIndex = -1,
                damageResultIndex = -1,
                damageResultIndexes = new List<int>(),
                targetCombatIds = new List<uint>(),
                powerIndex = -1,
                orbIndex = -1,
                potionIndex = -1,
                move = new SavedMoveState { kind = SavedMoveKind.None, id = string.Empty, followUpStateId = string.Empty,
                    handlerOwnerType = string.Empty, handlerMethod = string.Empty, unsupportedReason = string.Empty }
            };

            switch (entry)
            {
                case BlockGainedEntry value:
                    saved.kind = SavedCombatHistoryEntryKind.BlockGained;
                    saved.amount = value.Amount; saved.props = value.Props;
                    saved.cardPlayIndex = value.CardPlay == null ? -1 : CaptureCardPlay(value.CardPlay);
                    break;
                case CardAfflictedEntry value:
                    saved.kind = SavedCombatHistoryEntryKind.CardAfflicted;
                    saved.cardIndex = CaptureCard(value.Card);
                    saved.hasAffliction = value.Affliction != null;
                    saved.afflictionId = value.Affliction?.Id!;
                    break;
                case CardDiscardedEntry value:
                    saved.kind = SavedCombatHistoryEntryKind.CardDiscarded; saved.cardIndex = CaptureCard(value.Card); break;
                case CardDrawnEntry value:
                    saved.kind = SavedCombatHistoryEntryKind.CardDrawn; saved.cardIndex = CaptureCard(value.Card); saved.flag = value.FromHandDraw; break;
                case CardExhaustedEntry value:
                    saved.kind = SavedCombatHistoryEntryKind.CardExhausted; saved.cardIndex = CaptureCard(value.Card); break;
                case CardGeneratedEntry value:
                    saved.kind = SavedCombatHistoryEntryKind.CardGenerated; saved.cardIndex = CaptureCard(value.Card);
                    saved.hasPlayer = value.Creator != null; saved.playerId = value.Creator?.NetId ?? 0; break;
                case CardPlayFinishedEntry value:
                    saved.kind = SavedCombatHistoryEntryKind.CardPlayFinished; saved.cardPlayIndex = CaptureCardPlay(value.CardPlay); saved.flag = value.WasEthereal; break;
                case CardPlayStartedEntry value:
                    saved.kind = SavedCombatHistoryEntryKind.CardPlayStarted; saved.cardPlayIndex = CaptureCardPlay(value.CardPlay); break;
                case CreatureAttackedEntry value:
                    saved.kind = SavedCombatHistoryEntryKind.CreatureAttacked;
                    saved.damageResultIndexes = value.DamageResults.Select(CaptureDamageResult).ToList(); break;
                case DamageReceivedEntry value:
                    saved.kind = SavedCombatHistoryEntryKind.DamageReceived;
                    saved.damageResultIndex = CaptureDamageResult(value.Result);
                    saved.hasCreature = value.Dealer != null; saved.creatureCombatId = value.Dealer == null ? 0 : CaptureCreature(value.Dealer, "damage dealer");
                    saved.cardIndex = value.CardSource == null ? -1 : CaptureCard(value.CardSource); break;
                case EnergySpentEntry value:
                    saved.kind = SavedCombatHistoryEntryKind.EnergySpent; saved.amount = value.Amount;
                    saved.hasPlayer = TryActorPlayer(entry.Actor, out ulong energyPlayer); saved.playerId = energyPlayer; break;
                case MonsterPerformedMoveEntry value:
                    saved.kind = SavedCombatHistoryEntryKind.MonsterPerformedMove;
                    Creature monsterCreature = entry.Actor != null && ReferenceEquals(entry.Actor.Monster, value.Monster)
                        ? entry.Actor
                        : _combat.Creatures.FirstOrDefault(creature => ReferenceEquals(creature.Monster, value.Monster))
                        ?? throw new InvalidOperationException($"Combat history move {value.Monster.Id} has no owning creature.");
                    saved.hasMonster = true; saved.monsterCombatId = CaptureCreature(monsterCreature, "move owner");
                    saved.move = MonsterMoveStateSupport.CaptureHistoryMove(value.Move, value.Monster.MoveStateMachine?.States, monsterCreature);
                    saved.targetCombatIds = (value.Targets
                        ?? throw new InvalidOperationException("Combat history move has no target collection."))
                        .Select(target => CaptureCreature(target, "move target")).ToList(); break;
                case OrbChanneledEntry value:
                    saved.kind = SavedCombatHistoryEntryKind.OrbChanneled; saved.orbIndex = CaptureOrb(value.Orb); break;
                case PotionUsedEntry value:
                    saved.kind = SavedCombatHistoryEntryKind.PotionUsed; saved.potionIndex = CapturePotion(value.Potion);
                    saved.hasCreature = value.Target != null; saved.creatureCombatId = value.Target == null ? 0 : CaptureCreature(value.Target, "potion target"); break;
                case PowerReceivedEntry value:
                    saved.kind = SavedCombatHistoryEntryKind.PowerReceived; saved.powerIndex = CapturePower(value.Power);
                    saved.decimalAmount = value.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    saved.hasCreature = value.Applier != null; saved.creatureCombatId = value.Applier == null ? 0 : CaptureCreature(value.Applier, "power applier"); break;
                case StarsModifiedEntry value:
                    saved.kind = SavedCombatHistoryEntryKind.StarsModified; saved.amount = value.Amount;
                    saved.hasPlayer = TryActorPlayer(entry.Actor, out ulong starsPlayer); saved.playerId = starsPlayer; break;
                case SummonedEntry value:
                    saved.kind = SavedCombatHistoryEntryKind.Summoned; saved.amount = value.Amount;
                    saved.hasPlayer = TryActorPlayer(entry.Actor, out ulong summonedPlayer); saved.playerId = summonedPlayer; break;
                default:
                    throw new NotSupportedException($"Unsupported CombatHistoryEntry type {entry.GetType().FullName}.");
            }
            return saved;
        }

        private bool TryActorPlayer(Creature? actor, out ulong playerId)
        {
            if (actor == null)
            {
                playerId = 0;
                return false;
            }
            Player? player = _combat.Players.FirstOrDefault(candidate => ReferenceEquals(candidate.Creature, actor));
            playerId = player?.NetId ?? 0;
            return player != null;
        }

        private uint CaptureCreature(Creature creature, string label)
        {
            uint combatId = RequireCombatId(creature, label);
            if (_creaturesById.TryGetValue(combatId, out Creature? existing))
            {
                if (!ReferenceEquals(existing, creature))
                    throw new InvalidOperationException(
                        $"Combat history {label} reuses combat ID {combatId} for different creatures.");
                return combatId;
            }

            Player? player = creature.Player;
            MonsterModel? monster = creature.Monster;
            if ((player == null) == (monster == null))
                throw new InvalidOperationException(
                    $"Combat history {label} creature {combatId} has an invalid player/monster identity.");
            Creatures.Add(new CombatHistoryCreatureState
            {
                combatId = combatId,
                isPlayer = player != null,
                playerId = player?.NetId ?? 0,
                isMonster = monster != null,
                monsterId = monster?.Id!,
                hasPetOwner = creature.PetOwner != null,
                petOwnerId = creature.PetOwner?.NetId ?? 0,
                side = creature.Side,
                slotName = creature.SlotName ?? string.Empty,
                currentHp = creature.CurrentHp,
                maxHp = creature.MaxHp,
                block = creature.Block
            });
            _creaturesById.Add(combatId, creature);
            return combatId;
        }

        private int CaptureCard(CardModel card)
        {
            if (_cardIndexes.TryGetValue(card, out int existing)) return existing;
            bool hasId = CombatCardReplayMapper.TryGetExistingCardId(card, out uint id);
            var saved = new CombatHistoryCardState
            {
                hasCombatCardId = hasId,
                combatCardId = id,
                hasOwner = card.Owner != null,
                ownerId = card.Owner?.NetId ?? 0,
                card = card.ToSerializable(),
                runtime = ExtendedReconnectCapture.CaptureCardRuntime(card, hasId ? id : 0)
            };
            int index = Cards.Count;
            Cards.Add(saved);
            _cardIndexes.Add(card, index);
            return index;
        }

        private int CaptureCardPlay(CardPlay play)
        {
            if (_cardPlayIndexes.TryGetValue(play, out int existing)) return existing;
            var saved = new CombatHistoryCardPlayState
            {
                cardIndex = CaptureCard(play.Card), playerId = play.Player.NetId,
                hasTarget = play.Target != null,
                targetCombatId = play.Target == null ? 0 : CaptureCreature(play.Target, "card target"),
                resultPile = play.ResultPile,
                energySpent = play.Resources.EnergySpent, energyValue = play.Resources.EnergyValue,
                starsSpent = play.Resources.StarsSpent, starValue = play.Resources.StarValue,
                isAutoPlay = play.IsAutoPlay, playIndex = play.PlayIndex, playCount = play.PlayCount
            };
            int index = CardPlays.Count;
            CardPlays.Add(saved);
            _cardPlayIndexes.Add(play, index);
            return index;
        }

        private int CaptureDamageResult(DamageResult result)
        {
            if (_damageIndexes.TryGetValue(result, out int existing)) return existing;
            var saved = new CombatHistoryDamageResultState
            {
                receiverCombatId = CaptureCreature(result.Receiver, "damage receiver"), props = result.Props,
                blockedDamage = result.BlockedDamage, unblockedDamage = result.UnblockedDamage,
                overkillDamage = result.OverkillDamage, wasBlockBroken = result.WasBlockBroken,
                wasFullyBlocked = result.WasFullyBlocked, wasTargetKilled = result.WasTargetKilled
            };
            int index = DamageResults.Count;
            DamageResults.Add(saved);
            _damageIndexes.Add(result, index);
            return index;
        }

        private int CapturePower(PowerModel power)
        {
            if (_powerIndexes.TryGetValue(power, out int existing)) return existing;
            Creature? owner = power.Owner;
            bool ownerIsLive = owner != null && _combat.Creatures.Any(creature => ReferenceEquals(creature, owner));
            int occurrence = !ownerIsLive
                ? -1
                : owner!.Powers.Where(candidate => candidate.Id == power.Id).ToList()
                    .FindIndex(candidate => ReferenceEquals(candidate, power));
            var saved = new CombatHistoryPowerState
            {
                id = power.Id, hasOwner = owner != null,
                ownerCombatId = owner == null ? 0 : CaptureCreature(owner, "power owner"),
                ownerOccurrence = occurrence, amount = power.Amount
            };
            int index = Powers.Count;
            Powers.Add(saved);
            _powerIndexes.Add(power, index);
            return index;
        }

        private int CaptureOrb(OrbModel orb)
        {
            if (_orbIndexes.TryGetValue(orb, out int existing)) return existing;
            Player? owner = OrbOwnerField.GetValue(orb) as Player;
            int queueIndex = -1;
            if (owner?.PlayerCombatState?.OrbQueue != null)
                queueIndex = ((IEnumerable)OrbListField.GetValue(owner.PlayerCombatState.OrbQueue)!).Cast<OrbModel>().ToList()
                    .FindIndex(candidate => ReferenceEquals(candidate, orb));
            var saved = new CombatHistoryOrbState
            {
                id = orb.Id, hasOwner = owner != null, ownerId = owner?.NetId ?? 0, queueIndex = queueIndex
            };
            int index = Orbs.Count;
            Orbs.Add(saved);
            _orbIndexes.Add(orb, index);
            return index;
        }

        private int CapturePotion(PotionModel potion)
        {
            if (_potionIndexes.TryGetValue(potion, out int existing)) return existing;
            Player? owner = _combat.Players.FirstOrDefault(player => player.PotionSlots.Any(candidate => ReferenceEquals(candidate, potion)));
            int slot = owner == null ? -1 : owner.PotionSlots.ToList().FindIndex(candidate => ReferenceEquals(candidate, potion));
            var saved = new CombatHistoryPotionState
            {
                id = potion.Id, hasOwner = owner != null, ownerId = owner?.NetId ?? 0, slotIndex = slot
            };
            int index = Potions.Count;
            Potions.Add(saved);
            _potionIndexes.Add(potion, index);
            return index;
        }
    }

    private sealed class RestoreContext
    {
        private readonly CombatState _combat;
        private readonly ExtendedCombatHistoryState _saved;
        private readonly Dictionary<uint, Creature> _creatures;
        private readonly List<CardModel> _cards;
        private readonly List<CardPlay> _cardPlays;
        private readonly List<DamageResult> _damageResults;
        private readonly List<PowerModel> _powers;
        private readonly List<OrbModel> _orbs;
        private readonly List<PotionModel> _potions;

        internal RestoreContext(CombatState combat, ExtendedCombatHistoryState saved)
        {
            _combat = combat;
            _saved = saved;
            _creatures = saved.creatures.ToDictionary(creature => creature.combatId, RestoreCreature);
            _cards = saved.cards.Select(RestoreCard).ToList();
            _damageResults = saved.damageResults.Select(RestoreDamageResult).ToList();
            _powers = saved.powers.Select(RestorePower).ToList();
            _orbs = saved.orbs.Select(RestoreOrb).ToList();
            _potions = saved.potions.Select(RestorePotion).ToList();
            _cardPlays = saved.cardPlays.Select(RestoreCardPlay).ToList();
        }

        internal CombatHistoryEntry RestoreEntry(CombatHistoryEntryState saved, CombatHistory history)
        {
            Type type = saved.kind switch
            {
                SavedCombatHistoryEntryKind.BlockGained => typeof(BlockGainedEntry),
                SavedCombatHistoryEntryKind.CardAfflicted => typeof(CardAfflictedEntry),
                SavedCombatHistoryEntryKind.CardDiscarded => typeof(CardDiscardedEntry),
                SavedCombatHistoryEntryKind.CardDrawn => typeof(CardDrawnEntry),
                SavedCombatHistoryEntryKind.CardExhausted => typeof(CardExhaustedEntry),
                SavedCombatHistoryEntryKind.CardGenerated => typeof(CardGeneratedEntry),
                SavedCombatHistoryEntryKind.CardPlayFinished => typeof(CardPlayFinishedEntry),
                SavedCombatHistoryEntryKind.CardPlayStarted => typeof(CardPlayStartedEntry),
                SavedCombatHistoryEntryKind.CreatureAttacked => typeof(CreatureAttackedEntry),
                SavedCombatHistoryEntryKind.DamageReceived => typeof(DamageReceivedEntry),
                SavedCombatHistoryEntryKind.EnergySpent => typeof(EnergySpentEntry),
                SavedCombatHistoryEntryKind.MonsterPerformedMove => typeof(MonsterPerformedMoveEntry),
                SavedCombatHistoryEntryKind.OrbChanneled => typeof(OrbChanneledEntry),
                SavedCombatHistoryEntryKind.PotionUsed => typeof(PotionUsedEntry),
                SavedCombatHistoryEntryKind.PowerReceived => typeof(PowerReceivedEntry),
                SavedCombatHistoryEntryKind.StarsModified => typeof(StarsModifiedEntry),
                SavedCombatHistoryEntryKind.Summoned => typeof(SummonedEntry),
                _ => throw new InvalidDataException($"Unsupported history kind {saved.kind}.")
            };
            var entry = (CombatHistoryEntry)RuntimeHelpers.GetUninitializedObject(type);
            ActorField.SetValue(entry, saved.hasActor ? Creature(saved.actorCombatId) : null);
            RoundField.SetValue(entry, saved.roundNumber);
            SideField.SetValue(entry, saved.currentSide);
            HistoryField.SetValue(entry, history);
            PlayerTurnsField.SetValue(entry, saved.playerTurns.ToDictionary(turn => turn.playerId, turn => turn.turnNumber));

            switch (entry)
            {
                case BlockGainedEntry:
                    Set(type, entry, "<Amount>k__BackingField", saved.amount);
                    Set(type, entry, "<Props>k__BackingField", saved.props);
                    Set(type, entry, "<CardPlay>k__BackingField", Optional(_cardPlays, saved.cardPlayIndex));
                    break;
                case CardAfflictedEntry:
                    Set(type, entry, "<Card>k__BackingField", Item(_cards, saved.cardIndex, "card"));
                    Set(type, entry, "<Affliction>k__BackingField", saved.hasAffliction
                        ? ModelDb.GetById<AfflictionModel>(saved.afflictionId).MutableClone() : null);
                    break;
                case CardDiscardedEntry or CardExhaustedEntry:
                    Set(type, entry, "<Card>k__BackingField", Item(_cards, saved.cardIndex, "card"));
                    break;
                case CardDrawnEntry:
                    Set(type, entry, "<Card>k__BackingField", Item(_cards, saved.cardIndex, "card"));
                    Set(type, entry, "<FromHandDraw>k__BackingField", saved.flag);
                    break;
                case CardGeneratedEntry:
                    Set(type, entry, "<Card>k__BackingField", Item(_cards, saved.cardIndex, "card"));
                    Set(type, entry, "<Creator>k__BackingField", saved.hasPlayer ? Player(saved.playerId) : null);
                    break;
                case CardPlayFinishedEntry:
                    Set(type, entry, "<CardPlay>k__BackingField", Item(_cardPlays, saved.cardPlayIndex, "card play"));
                    Set(type, entry, "<WasEthereal>k__BackingField", saved.flag);
                    break;
                case CardPlayStartedEntry:
                    Set(type, entry, "<CardPlay>k__BackingField", Item(_cardPlays, saved.cardPlayIndex, "card play"));
                    break;
                case CreatureAttackedEntry:
                    Set(type, entry, "<DamageResults>k__BackingField", saved.damageResultIndexes
                        .Select(index => Item(_damageResults, index, "damage result")).ToList());
                    break;
                case DamageReceivedEntry:
                    Set(type, entry, "<Result>k__BackingField", Item(_damageResults, saved.damageResultIndex, "damage result"));
                    Set(type, entry, "<Dealer>k__BackingField", saved.hasCreature ? Creature(saved.creatureCombatId) : null);
                    Set(type, entry, "<CardSource>k__BackingField", Optional(_cards, saved.cardIndex));
                    break;
                case EnergySpentEntry or StarsModifiedEntry or SummonedEntry:
                    Set(type, entry, "<Amount>k__BackingField", saved.amount);
                    break;
                case MonsterPerformedMoveEntry:
                    Creature owner = Creature(saved.monsterCombatId);
                    Set(type, entry, "<Monster>k__BackingField", owner.Monster
                        ?? throw new InvalidDataException($"History move owner {saved.monsterCombatId} is not a monster."));
                    Set(type, entry, "<Move>k__BackingField", MonsterMoveStateSupport.RestoreHistoryMove(owner, saved.move));
                    Set(type, entry, "<Targets>k__BackingField", saved.targetCombatIds.Select(Creature).ToList());
                    break;
                case OrbChanneledEntry:
                    Set(type, entry, "<Orb>k__BackingField", Item(_orbs, saved.orbIndex, "orb"));
                    break;
                case PotionUsedEntry:
                    Set(type, entry, "<Potion>k__BackingField", Item(_potions, saved.potionIndex, "potion"));
                    Set(type, entry, "<Target>k__BackingField", saved.hasCreature ? Creature(saved.creatureCombatId) : null);
                    break;
                case PowerReceivedEntry:
                    Set(type, entry, "<Power>k__BackingField", Item(_powers, saved.powerIndex, "power"));
                    Set(type, entry, "<Amount>k__BackingField", decimal.Parse(saved.decimalAmount, System.Globalization.CultureInfo.InvariantCulture));
                    Set(type, entry, "<Applier>k__BackingField", saved.hasCreature ? Creature(saved.creatureCombatId) : null);
                    break;
            }
            return entry;
        }

        private CardModel RestoreCard(CombatHistoryCardState saved)
        {
            if (saved.hasCombatCardId && NetCombatCardDb.Instance.TryGetCard(saved.combatCardId, out CardModel? live) && live != null)
                return live;
            CardModel card = CardModel.FromSerializable(saved.card);
            if (saved.hasOwner) CardOwnerField.SetValue(card, Player(saved.ownerId));
            CombatStateRestorer.RestoreCardExtras(card, saved.runtime);
            if (saved.hasCombatCardId)
                CombatCardReplayMapper.RegisterAuthoritativeCardId(card, saved.combatCardId, "combat history");
            return card;
        }

        private CardPlay RestoreCardPlay(CombatHistoryCardPlayState saved)
        {
            var play = (CardPlay)RuntimeHelpers.GetUninitializedObject(typeof(CardPlay));
            Set(typeof(CardPlay), play, "<Card>k__BackingField", Item(_cards, saved.cardIndex, "card"));
            Set(typeof(CardPlay), play, "<Player>k__BackingField", Player(saved.playerId));
            Set(typeof(CardPlay), play, "<Target>k__BackingField", saved.hasTarget ? Creature(saved.targetCombatId) : null);
            Set(typeof(CardPlay), play, "<ResultPile>k__BackingField", saved.resultPile);
            object resources = default(ResourceInfo);
            Set(typeof(ResourceInfo), resources, "<EnergySpent>k__BackingField", saved.energySpent);
            Set(typeof(ResourceInfo), resources, "<EnergyValue>k__BackingField", saved.energyValue);
            Set(typeof(ResourceInfo), resources, "<StarsSpent>k__BackingField", saved.starsSpent);
            Set(typeof(ResourceInfo), resources, "<StarValue>k__BackingField", saved.starValue);
            Set(typeof(CardPlay), play, "<Resources>k__BackingField", (ResourceInfo)resources);
            Set(typeof(CardPlay), play, "<IsAutoPlay>k__BackingField", saved.isAutoPlay);
            Set(typeof(CardPlay), play, "<PlayIndex>k__BackingField", saved.playIndex);
            Set(typeof(CardPlay), play, "<PlayCount>k__BackingField", saved.playCount);
            return play;
        }

        private DamageResult RestoreDamageResult(CombatHistoryDamageResultState saved)
        {
            var result = new DamageResult(Creature(saved.receiverCombatId), saved.props);
            Type type = typeof(DamageResult);
            Set(type, result, "<BlockedDamage>k__BackingField", saved.blockedDamage);
            Set(type, result, "<UnblockedDamage>k__BackingField", saved.unblockedDamage);
            Set(type, result, "<OverkillDamage>k__BackingField", saved.overkillDamage);
            Set(type, result, "<WasBlockBroken>k__BackingField", saved.wasBlockBroken);
            Set(type, result, "<WasFullyBlocked>k__BackingField", saved.wasFullyBlocked);
            Set(type, result, "<WasTargetKilled>k__BackingField", saved.wasTargetKilled);
            return result;
        }

        private PowerModel RestorePower(CombatHistoryPowerState saved)
        {
            Creature? owner = saved.hasOwner ? Creature(saved.ownerCombatId) : null;
            if (owner != null && saved.ownerOccurrence >= 0)
            {
                List<PowerModel> matches = owner.Powers.Where(power => power.Id == saved.id).ToList();
                if (saved.ownerOccurrence < matches.Count) return matches[saved.ownerOccurrence];
            }
            PowerModel power = ModelDb.GetById<PowerModel>(saved.id).ToMutable(saved.amount);
            if (owner != null) PowerOwnerField.SetValue(power, owner);
            return power;
        }

        private OrbModel RestoreOrb(CombatHistoryOrbState saved)
        {
            Player? owner = saved.hasOwner ? Player(saved.ownerId) : null;
            if (owner?.PlayerCombatState?.OrbQueue != null && saved.queueIndex >= 0)
            {
                List<OrbModel> live = ((IEnumerable)OrbListField.GetValue(owner.PlayerCombatState.OrbQueue)!).Cast<OrbModel>().ToList();
                if (saved.queueIndex < live.Count && live[saved.queueIndex].Id == saved.id) return live[saved.queueIndex];
            }
            OrbModel orb = (OrbModel)ModelDb.GetById<OrbModel>(saved.id).MutableClone();
            if (owner != null) OrbOwnerField.SetValue(orb, owner);
            return orb;
        }

        private PotionModel RestorePotion(CombatHistoryPotionState saved)
        {
            Player? owner = saved.hasOwner ? Player(saved.ownerId) : null;
            if (owner != null && saved.slotIndex >= 0 && saved.slotIndex < owner.PotionSlots.Count &&
                owner.PotionSlots[saved.slotIndex]?.Id == saved.id)
                return owner.PotionSlots[saved.slotIndex]!;
            return (PotionModel)ModelDb.GetById<PotionModel>(saved.id).MutableClone();
        }

        private Creature RestoreCreature(CombatHistoryCreatureState saved)
        {
            Creature? live = _combat.Creatures.FirstOrDefault(creature => creature.CombatId == saved.combatId);
            if (live != null)
            {
                bool identityMatches = saved.isPlayer
                    ? live.Player?.NetId == saved.playerId
                    : live.Monster?.Id == saved.monsterId &&
                      (saved.hasPetOwner
                          ? live.PetOwner?.NetId == saved.petOwnerId
                          : live.PetOwner == null);
                if (!identityMatches)
                    throw new InvalidDataException(
                        $"Combat history creature {saved.combatId} conflicts with the live combat roster.");
                return live;
            }

            if (saved.isPlayer)
            {
                Player player = Player(saved.playerId);
                if (player.Creature.CombatId != saved.combatId)
                    throw new InvalidDataException(
                        $"Combat history player {saved.playerId} references missing creature {saved.combatId}.");
                return player.Creature;
            }

            MonsterModel canonical = ModelDb.GetById<MonsterModel>(saved.monsterId);
            var shadow = new Creature((MonsterModel)canonical.MutableClone(), saved.side, saved.slotName)
            {
                CombatId = saved.combatId,
                CombatState = _combat,
                PetOwner = saved.hasPetOwner ? Player(saved.petOwnerId) : null
            };
            shadow.SetMaxHpInternal(saved.maxHp);
            shadow.SetCurrentHpInternal(saved.currentHp);
            CreatureBlockField.SetValue(shadow, saved.block);
            return shadow;
        }

        private Creature Creature(uint combatId)
            => _creatures.GetValueOrDefault(combatId)
               ?? throw new InvalidDataException($"Combat history references missing creature {combatId}.");

        private Player Player(ulong playerId)
            => _combat.Players.FirstOrDefault(player => player.NetId == playerId)
               ?? throw new InvalidDataException($"Combat history references missing player {playerId}.");
    }

    private static T Item<T>(IReadOnlyList<T> values, int index, string label)
    {
        if (index < 0 || index >= values.Count)
            throw new InvalidDataException($"Combat history references invalid {label} index {index}/{values.Count}.");
        return values[index];
    }

    private static T? Optional<T>(IReadOnlyList<T> values, int index) where T : class
        => index < 0 ? null : Item(values, index, typeof(T).Name);

    private static void Set(Type type, object instance, string fieldName, object? value)
    {
        FieldInfo field = AccessTools.Field(type, fieldName)
            ?? throw new MissingFieldException(type.FullName, fieldName);
        field.SetValue(instance, value);
    }
}
