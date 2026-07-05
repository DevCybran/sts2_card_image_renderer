using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace card_image_renderer.card_image_rendererCode.Patches;

// Temporary scaffold: renders a single hard-coded card once models are ready, so we can verify the
// rendering pipeline works before wiring it up to anything user-facing.
[HarmonyPatch(typeof(ModelDb), nameof(ModelDb.InitIds))]
internal static class RenderTestCardOnBootPatch
{
    private static void Postfix()
    {
        RenderBashCard();
    }

    private static async void RenderBashCard()
    {
        try
        {
            CardModel bash = ModelDb.Card<Bash>().ToMutable();
            await CardRenderer.RenderCardToPngAsync(bash, "user://card_image_renderer/bash.png");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to render test card: {e}");
        }
    }
}
