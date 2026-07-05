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
        __instance.AddChild(button);

        ProgressBar progressBar = new()
        {
            AnchorLeft = 0f,
            AnchorTop = 1f,
            AnchorRight = 0f,
            AnchorBottom = 1f,
            OffsetLeft = 260f,
            OffsetTop = -60f,
            OffsetRight = 460f,
            OffsetBottom = -15f,
            MinValue = 0,
            MaxValue = 1,
            Value = 0,
            ShowPercentage = true,
            Visible = false,
        };
        __instance.AddChild(progressBar);

        button.Pressed += () => OnPressed(button, progressBar);
    }

    private static async void OnPressed(Button button, ProgressBar progressBar)
    {
        button.Disabled = true;
        progressBar.Value = 0;
        progressBar.Visible = true;
        try
        {
            await CardRenderer.RenderAllCardsAsync((current, total) =>
            {
                progressBar.MaxValue = total;
                progressBar.Value = current;
            });
        }
        finally
        {
            button.Disabled = false;
            progressBar.Visible = false;
        }
    }
}
