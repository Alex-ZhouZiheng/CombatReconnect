using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

namespace CombatReconnect;

internal static class MonsterMoveStateSupport
{
    private const string UnsetMoveId = "UNSET_MOVE";
    private const string StunnedMoveId = "STUNNED";
    private const string ReviveMoveId = "REVIVE_MOVE";
    private const string IllusionPowerType = "MegaCrit.Sts2.Core.Models.Powers.IllusionPower";
    private static readonly FieldInfo CurrentState = AccessTools.Field(typeof(MonsterMoveStateMachine), "_currentState");
    private static readonly FieldInfo Performed = AccessTools.Field(typeof(MoveState), "_performedAtLeastOnce");
    private static readonly PropertyInfo NextMove = AccessTools.Property(typeof(MonsterModel), nameof(MonsterModel.NextMove));
    private static readonly ConditionalWeakTable<MoveState, StunHandlerDescriptor> StunHandlers = new();
    private static readonly ConditionalWeakTable<MoveState, HistoryMoveDescriptor> HistoryMoves = new();

    internal static SavedMoveState CaptureHistoryMove(object? value, object? rawStates, Creature owner)
    {
        if (value is MoveState move && HistoryMoves.TryGetValue(move, out HistoryMoveDescriptor? descriptor))
            return descriptor.State;
        return Capture(value, rawStates, owner);
    }

    internal static MoveState RestoreHistoryMove(Creature owner, SavedMoveState saved)
    {
        if (string.IsNullOrEmpty(saved.id))
            throw new InvalidDataException($"Monster {owner.Monster?.Id} history move has no ID.");
        var move = new MoveState(saved.id, _ => Task.CompletedTask, Array.Empty<AbstractIntent>())
        {
            FollowUpStateId = saved.followUpStateId,
            MustPerformOnceBeforeTransitioning = saved.mustPerformOnce
        };
        Performed.SetValue(move, saved.performedAtLeastOnce);
        HistoryMoves.Add(move, new HistoryMoveDescriptor(saved));
        return move;
    }

    internal static SavedMoveState Capture(object? value, object? rawStates, Creature owner)
    {
        if (value is not MoveState move)
            return new SavedMoveState
            {
                kind = value == null ? SavedMoveKind.None : SavedMoveKind.Unsupported,
                id = value is MonsterState state ? state.Id : string.Empty,
                unsupportedReason = value == null ? string.Empty : $"Move object has unsupported type {value.GetType().FullName}."
            };

        IDictionary? states = rawStates as IDictionary;
        if (states != null && states.Contains(move.Id) && ReferenceEquals(states[move.Id], move))
            return FromMove(move, SavedMoveKind.Registered);
        if (move.Id == UnsetMoveId)
            return FromMove(move, SavedMoveKind.UnsetSentinel);
        if (move.Id == StunnedMoveId && StunHandlers.TryGetValue(move, out StunHandlerDescriptor? descriptor))
        {
            SavedMoveState saved = FromMove(move, SavedMoveKind.DynamicStunned);
            saved.handlerOwner = descriptor.Owner;
            saved.handlerOwnerType = descriptor.OwnerType;
            saved.handlerMethod = descriptor.Method;
            return saved;
        }
        if (move.Id == ReviveMoveId)
        {
            PowerModel? illusion = owner.Powers.FirstOrDefault(power => power.GetType().FullName == IllusionPowerType);
            if (illusion != null)
            {
                SavedMoveState saved = FromMove(move, SavedMoveKind.DynamicIllusionRevive);
                saved.handlerOwner = SavedMoveHandlerOwner.Power;
                saved.handlerOwnerType = IllusionPowerType;
                saved.handlerMethod = "ReviveMove";
                return saved;
            }
        }

        SavedMoveState unsupported = FromMove(move, SavedMoveKind.Unsupported);
        unsupported.unsupportedReason = $"Dynamic move {move.Id} has no deterministic restorer.";
        return unsupported;
    }

    internal static string Describe(SavedMoveState move)
        => $"{move.id}({move.kind}, handler={move.handlerOwner}:{move.handlerOwnerType}.{move.handlerMethod})";

    internal static void ValidateSnapshot(ExtendedReconnectMessage extended, string phase)
    {
        foreach (ExtendedCreatureState creature in extended.creatures.Where(saved => saved.isMonster))
        {
            foreach (CreatureReferenceFieldState reference in creature.monsterCreatureReferences)
            {
                if (string.IsNullOrEmpty(reference.declaringType) || string.IsNullOrEmpty(reference.name))
                    throw new InvalidDataException(
                        $"Cannot rewind during {phase}: monster {creature.monsterId} ({creature.combatId}) " +
                        "has incomplete creature reference metadata.");
                if (reference.hasCombatId && extended.creatures.All(saved => saved.combatId != reference.combatId))
                    throw new InvalidDataException(
                        $"Cannot rewind during {phase}: monster {creature.monsterId} ({creature.combatId}) " +
                        $"references missing combat creature {reference.combatId} through " +
                        $"{reference.declaringType}.{reference.name}.");
            }
            ValidateMove(creature, creature.currentMove, "current", phase);
            ValidateMove(creature, creature.nextMove, "next", phase);
            bool hasDynamic = IsDynamic(creature.currentMove.kind) || IsDynamic(creature.nextMove.kind);
            if (hasDynamic && !creature.currentAndNextMoveAreSame)
                throw new InvalidDataException(
                    $"Cannot rewind during {phase}: monster {creature.monsterId} ({creature.combatId}) has a dynamic move " +
                    "whose current and next references differ.");
            if (creature.currentAndNextMoveAreSame &&
                (creature.currentMove.id != creature.nextMove.id || creature.currentMove.kind != creature.nextMove.kind))
                throw new InvalidDataException(
                    $"Invalid move snapshot during {phase}: monster {creature.monsterId} ({creature.combatId}) " +
                    "marks different current and next moves as the same object.");
        }
        int dynamicCount = extended.creatures.Count(creature => creature.isMonster &&
            (IsDynamic(creature.currentMove.kind) || IsDynamic(creature.nextMove.kind)));
        ReconnectLog.Write(
            $"Validated monster move snapshot during {phase}: monsters={extended.creatures.Count(creature => creature.isMonster)}, " +
            $"dynamic={dynamicCount}.");
    }

    internal static void Restore(Creature creature, ExtendedCreatureState saved)
    {
        MonsterModel monster = creature.Monster
            ?? throw new InvalidOperationException($"Creature {creature.CombatId} has no monster model.");
        MonsterMoveStateMachine machine = monster.MoveStateMachine
            ?? throw new InvalidOperationException($"Monster {saved.monsterId} has no move state machine.");
        ValidateMove(saved, saved.currentMove, "current", "restore");
        ValidateMove(saved, saved.nextMove, "next", "restore");

        MoveState current;
        MoveState next;
        if (saved.currentAndNextMoveAreSame)
        {
            if (saved.currentMove.id != saved.nextMove.id || saved.currentMove.kind != saved.nextMove.kind)
                throw new InvalidDataException($"Monster {saved.monsterId} has an inconsistent shared move snapshot.");
            current = next = RestoreMove(creature, machine, saved.currentMove);
        }
        else
        {
            current = RestoreMove(creature, machine, saved.currentMove);
            next = RestoreMove(creature, machine, saved.nextMove);
        }

        CurrentState.SetValue(machine, current);
        NextMove.SetValue(monster, next);
        VerifyRestoredMove(creature, current, saved.currentMove, "current");
        VerifyRestoredMove(creature, next, saved.nextMove, "next");
        if (saved.currentAndNextMoveAreSame != ReferenceEquals(CurrentState.GetValue(machine), monster.NextMove))
            throw new InvalidDataException($"Monster {saved.monsterId} move state did not round-trip during restore.");
    }

    internal static void RegisterStunnedMove(MoveState move, Delegate handler)
    {
        StunHandlerDescriptor descriptor;
        if (handler.Target is MonsterModel monster)
            descriptor = new StunHandlerDescriptor(SavedMoveHandlerOwner.Monster, monster.GetType().FullName ?? string.Empty, handler.Method.Name);
        else if (handler.Target is PowerModel power)
            descriptor = new StunHandlerDescriptor(SavedMoveHandlerOwner.Power, power.GetType().FullName ?? string.Empty, handler.Method.Name);
        else if (handler.Method.DeclaringType?.FullName?.StartsWith(typeof(CreatureCmd).FullName!, StringComparison.Ordinal) == true)
            descriptor = new StunHandlerDescriptor(SavedMoveHandlerOwner.NoOp, string.Empty, string.Empty);
        else
            descriptor = new StunHandlerDescriptor(SavedMoveHandlerOwner.None,
                handler.Target?.GetType().FullName ?? handler.Method.DeclaringType?.FullName ?? string.Empty,
                handler.Method.Name);
        StunHandlers.Remove(move);
        StunHandlers.Add(move, descriptor);
    }

    private static SavedMoveState FromMove(MoveState move, SavedMoveKind kind)
        => new()
        {
            kind = kind,
            id = move.Id,
            followUpStateId = move.FollowUpState?.Id ?? move.FollowUpStateId ?? string.Empty,
            mustPerformOnce = move.MustPerformOnceBeforeTransitioning,
            performedAtLeastOnce = (bool)Performed.GetValue(move)!
        };

    private static void ValidateMove(ExtendedCreatureState creature, SavedMoveState move, string position, string phase)
    {
        if (!Enum.IsDefined(move.kind) || !Enum.IsDefined(move.handlerOwner))
            throw new InvalidDataException(
                $"Cannot rewind during {phase}: monster {creature.monsterId} ({creature.combatId}) has invalid " +
                $"{position} move metadata {move.kind}/{move.handlerOwner}.");
        if (move.kind == SavedMoveKind.Unsupported)
            throw new InvalidDataException(
                $"Cannot rewind during {phase}: monster {creature.monsterId} ({creature.combatId}) has unsupported " +
                $"{position} move {Describe(move)}: {move.unsupportedReason}");
        if (move.kind == SavedMoveKind.None || string.IsNullOrEmpty(move.id))
            throw new InvalidDataException(
                $"Cannot rewind during {phase}: monster {creature.monsterId} ({creature.combatId}) has no {position} move.");
        if (move.kind == SavedMoveKind.UnsetSentinel && move.id != UnsetMoveId)
            throw new InvalidDataException(
                $"Cannot rewind during {phase}: monster {creature.monsterId} ({creature.combatId}) has invalid unset move ID {move.id}.");
        if (move.kind == SavedMoveKind.DynamicStunned && move.id != StunnedMoveId)
            throw new InvalidDataException(
                $"Cannot rewind during {phase}: monster {creature.monsterId} ({creature.combatId}) has invalid stunned move ID {move.id}.");
        if (move.kind == SavedMoveKind.DynamicIllusionRevive && move.id != ReviveMoveId)
            throw new InvalidDataException(
                $"Cannot rewind during {phase}: monster {creature.monsterId} ({creature.combatId}) has invalid revive move ID {move.id}.");
        if (move.kind == SavedMoveKind.DynamicStunned && move.handlerOwner == SavedMoveHandlerOwner.None)
            throw new InvalidDataException(
                $"Cannot rewind during {phase}: monster {creature.monsterId} ({creature.combatId}) has an unsupported stun handler " +
                $"{move.handlerOwnerType}.{move.handlerMethod}.");
        if (move.kind == SavedMoveKind.DynamicIllusionRevive &&
            (move.handlerOwner != SavedMoveHandlerOwner.Power || move.handlerOwnerType != IllusionPowerType ||
             move.handlerMethod != "ReviveMove"))
            throw new InvalidDataException(
                $"Cannot rewind during {phase}: monster {creature.monsterId} ({creature.combatId}) has an invalid revive handler.");
        if (IsDynamic(move.kind) && move.handlerOwner is SavedMoveHandlerOwner.Monster or SavedMoveHandlerOwner.Power)
        {
            Type? ownerType = AccessTools.TypeByName(move.handlerOwnerType);
            MethodInfo? method = ownerType?.GetMethod(move.handlerMethod,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(IReadOnlyList<Creature>) }, null);
            if (method == null || method.ReturnType != typeof(Task))
                throw new InvalidDataException(
                    $"Cannot rewind during {phase}: monster {creature.monsterId} ({creature.combatId}) move handler " +
                    $"{move.handlerOwnerType}.{move.handlerMethod} is unavailable.");
        }
    }

    private static MoveState RestoreMove(Creature creature, MonsterMoveStateMachine machine, SavedMoveState saved)
    {
        MoveState move = saved.kind switch
        {
            SavedMoveKind.Registered => machine.States.TryGetValue(saved.id, out MonsterState? state) && state is MoveState registered
                ? registered
                : throw new InvalidDataException($"Monster {creature.Monster!.Id} is missing registered move {saved.id}."),
            SavedMoveKind.UnsetSentinel when saved.id == UnsetMoveId => new MoveState(),
            SavedMoveKind.DynamicStunned => RestoreStunnedMove(creature, saved),
            SavedMoveKind.DynamicIllusionRevive => new MoveState(ReviveMoveId, ResolveHandler(creature, saved), new HealIntent())
            {
                FollowUpStateId = saved.followUpStateId,
                MustPerformOnceBeforeTransitioning = saved.mustPerformOnce
            },
            _ => throw new InvalidDataException($"Monster {creature.Monster!.Id} move {Describe(saved)} cannot be restored.")
        };
        Performed.SetValue(move, saved.performedAtLeastOnce);
        return move;
    }

    private static MoveState RestoreStunnedMove(Creature creature, SavedMoveState saved)
    {
        MonsterModel monster = creature.Monster!;
        NextMove.SetValue(monster, new MoveState());
        CreatureCmd.Stun(creature, ResolveHandler(creature, saved), saved.followUpStateId).GetAwaiter().GetResult();
        MoveState move = monster.NextMove;
        if (move.Id != StunnedMoveId)
            throw new InvalidDataException($"Monster {monster.Id} did not recreate its stunned move.");
        move.MustPerformOnceBeforeTransitioning = saved.mustPerformOnce;
        return move;
    }

    private static void VerifyRestoredMove(
        Creature creature, MoveState actual, SavedMoveState expected, string position)
    {
        string followUp = actual.FollowUpState?.Id ?? actual.FollowUpStateId ?? string.Empty;
        bool performed = (bool)Performed.GetValue(actual)!;
        if (actual.Id != expected.id || followUp != expected.followUpStateId ||
            actual.MustPerformOnceBeforeTransitioning != expected.mustPerformOnce ||
            performed != expected.performedAtLeastOnce)
            throw new InvalidDataException(
                $"Monster {creature.Monster!.Id} {position} move did not round-trip: " +
                $"expected={Describe(expected)}, followUp={expected.followUpStateId}, must={expected.mustPerformOnce}, " +
                $"performed={expected.performedAtLeastOnce}; actual={actual.Id}, followUp={followUp}, " +
                $"must={actual.MustPerformOnceBeforeTransitioning}, performed={performed}.");
    }

    private static Func<IReadOnlyList<Creature>, Task> ResolveHandler(Creature creature, SavedMoveState saved)
    {
        if (saved.handlerOwner == SavedMoveHandlerOwner.NoOp)
            return _ => Task.CompletedTask;
        object target = saved.handlerOwner switch
        {
            SavedMoveHandlerOwner.Monster when creature.Monster?.GetType().FullName == saved.handlerOwnerType => creature.Monster,
            SavedMoveHandlerOwner.Power => creature.Powers.FirstOrDefault(power => power.GetType().FullName == saved.handlerOwnerType)
                ?? throw new InvalidDataException($"Monster {creature.Monster!.Id} is missing move handler owner {saved.handlerOwnerType}."),
            _ => throw new InvalidDataException(
                $"Monster {creature.Monster!.Id} has unsupported move handler owner {saved.handlerOwner}:{saved.handlerOwnerType}.")
        };
        MethodInfo method = target.GetType().GetMethod(saved.handlerMethod,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(IReadOnlyList<Creature>) }, null)
            ?? throw new InvalidDataException($"Move handler {saved.handlerOwnerType}.{saved.handlerMethod} was not found.");
        if (method.ReturnType != typeof(Task))
            throw new InvalidDataException($"Move handler {saved.handlerOwnerType}.{saved.handlerMethod} has an invalid return type.");
        return method.CreateDelegate<Func<IReadOnlyList<Creature>, Task>>(target);
    }

    private static bool IsDynamic(SavedMoveKind kind)
        => kind is SavedMoveKind.DynamicStunned or SavedMoveKind.DynamicIllusionRevive;

    private sealed record StunHandlerDescriptor(SavedMoveHandlerOwner Owner, string OwnerType, string Method);
    private sealed record HistoryMoveDescriptor(SavedMoveState State);
}

[HarmonyPatch]
internal static class RememberDynamicStunHandlerPatch
{
    private static MethodBase TargetMethod()
        => AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.Stun),
            new[] { typeof(Creature), typeof(Func<IReadOnlyList<Creature>, Task>), typeof(string) });

    private static void Prefix(Creature creature, out MoveState? __state)
        => __state = creature.Monster?.NextMove;

    private static void Postfix(
        Creature creature, Func<IReadOnlyList<Creature>, Task> stunMove, MoveState? __state)
    {
        if (creature.Monster?.NextMove is { Id: "STUNNED" } move && !ReferenceEquals(move, __state))
            MonsterMoveStateSupport.RegisterStunnedMove(move, stunMove);
    }
}
