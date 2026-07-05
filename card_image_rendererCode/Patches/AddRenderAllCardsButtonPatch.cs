using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace card_image_renderer.card_image_rendererCode.Patches;

// Adds a plain debug button to the main menu that triggers rendering every card in the game.
// Uses a vanilla Godot Button rather than NMainMenuTextButton/the MainMenuTextButtons VBoxContainer,
// since that button type expects a specific internal child structure (a MegaLabel at child index 0,
// focus/reticle wiring, etc.) that isn't worth replicating for a debug trigger.
[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu._Ready))]
internal static class AddRenderAllCardsButtonPatch
{
    private static void Postfix(NMainMenu __instance)
    {
        Button button = new()
        {
            Text = "Render All Cards",
            AnchorLeft = 0f,
            AnchorTop = 1f,
            AnchorRight = 0f,
            AnchorBottom = 1f,
            OffsetLeft = 15f,
            OffsetTop = -60f,
            OffsetRight = 250f,
            OffsetBottom = -15f,
        };
        button.Pressed += () => OnPressed(button);
        __instance.AddChild(button);
    }

    private static async void OnPressed(Button button)
    {
        button.Disabled = true;
        try
        {
            await CardRenderer.RenderAllCardsAsync();
        }
        finally
        {
            button.Disabled = false;
        }
    }
}
