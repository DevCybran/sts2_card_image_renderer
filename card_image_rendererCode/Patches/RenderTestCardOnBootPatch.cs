using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace card_image_renderer.card_image_rendererCode.Patches;

// Temporary scaffold: renders a handful of hard-coded cards once models are ready, so we can verify
// the rendering pipeline works before wiring it up to anything user-facing.
[HarmonyPatch(typeof(ModelDb), nameof(ModelDb.InitIds))]
internal static class RenderTestCardOnBootPatch
{
    private static readonly Func<CardModel>[] TestCards =
    {
        ModelDb.Card<Bash>,
        ModelDb.Card<DefendIronclad>,
        ModelDb.Card<IronWave>,
        ModelDb.Card<PommelStrike>,
        ModelDb.Card<Anger>,
    };

    private static void Postfix()
    {
        RenderTestCardsAsync();
    }

    private static async void RenderTestCardsAsync()
    {
        foreach (Func<CardModel> getCard in TestCards)
        {
            try
            {
                CardModel card = getCard().ToMutable();
                string fileName = card.Id.Entry.ToLowerInvariant();
                await CardRenderer.RenderCardToPngAsync(card, $"user://card_image_renderer/{fileName}.png");
            }
            catch (Exception e)
            {
                MainFile.Logger.Error($"Failed to render test card: {e}");
            }
        }
    }
}
