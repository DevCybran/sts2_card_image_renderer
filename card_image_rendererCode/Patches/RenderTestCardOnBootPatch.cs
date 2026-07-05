using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

namespace card_image_renderer.card_image_rendererCode.Patches;

// Temporary scaffold: renders every card in the game once models are ready, so we can verify the
// rendering pipeline works before wiring it up to anything user-facing.
[HarmonyPatch(typeof(ModelDb), nameof(ModelDb.InitIds))]
internal static class RenderTestCardOnBootPatch
{
    private static void Postfix()
    {
        RenderAllCardsAsync();
    }

    private static async void RenderAllCardsAsync()
    {
        List<CardModel> allCards = ModelDb.AllCards.ToList();
        for (int i = 0; i < allCards.Count; i++)
        {
            try
            {
                CardModel card = allCards[i].ToMutable();
                string fileName = card.Id.Entry.ToLowerInvariant();
                await CardRenderer.RenderCardToPngAsync(card, $"user://card_image_renderer/{fileName}.png", i + 1, allCards.Count);
            }
            catch (Exception e)
            {
                MainFile.Logger.Error($"Failed to render card '{allCards[i].Id}': {e}");
            }
        }
    }
}
