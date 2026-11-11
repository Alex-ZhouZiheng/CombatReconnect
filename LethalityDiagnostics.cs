using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatReconnect;

[HarmonyPatch(typeof(LethalityPower), nameof(LethalityPower.ModifyDamageMultiplicative))]
internal static class LethalityDiagnosticsPatch
{
    private static void Postfix(LethalityPower __instance, object[] __args, decimal __result)
    {
        try
        {
            ValueProp props = (ValueProp)__args[2];
            CardModel? card = __args[4] as CardModel;
            CardPlay? play = __args[5] as CardPlay;
            if (card == null || play == null) return;

            int attacksThisTurn = CombatManager.Instance.History.CardPlaysStarted.Count(entry =>
                entry.HappenedThisTurn(__instance.CombatState) &&
                entry.CardPlay.Card.Type == CardType.Attack &&
                ReferenceEquals(entry.CardPlay.Player, __instance.Owner.Player));
            bool ownerMatches = card.Owner != null &&
                                ReferenceEquals(card.Owner.Creature, __instance.Owner);

            ReconnectLog.Write(
                $"Lethality damage decision: replay={DeterministicReplayEngine.IsReplaying}, " +
                $"result={__result}, amount={__instance.Amount}, card={card.Id}, " +
                $"combatId={(CombatCardReplayMapper.TryGetExistingCardId(card, out uint id) ? id.ToString() : "none")}, " +
                $"pile={card.Pile?.Type.ToString() ?? "none"}, playIndex={card.CurrentPlayIndex}, " +
                $"poweredAttack={props.IsPoweredAttack()}, ownerMatches={ownerMatches}, " +
                $"attacksThisTurn={attacksThisTurn}.");
        }
        catch (Exception ex)
        {
            ReconnectLog.Write($"Lethality diagnostic failed: {ex.Message}");
        }
    }
}
