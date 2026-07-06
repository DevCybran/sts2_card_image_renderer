using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace card_image_renderer.card_image_rendererCode.Patches;

// Adds a plain debug button to the main menu that opens an options dialog for rendering cards.
// Uses vanilla Godot controls (Button/ConfirmationDialog/CheckBox) rather than the game's own
// NMainMenuTextButton/NModalContainer conventions, since those expect internal structure (a
// MegaLabel child, IScreenContext, backstop wiring, etc.) that isn't worth replicating for a
// debug trigger.
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

        button.Pressed += () => OpenOptionsDialog(__instance, button, progressBar);
    }

    private static void OpenOptionsDialog(NMainMenu mainMenu, Button button, ProgressBar progressBar)
    {
        // AcceptDialog's DialogText label isn't part of the same layout as children added via
        // AddChild() - they don't stack automatically, they'd overlap. So we leave DialogText empty
        // and build the entire prompt + checkboxes ourselves in one VBoxContainer instead.
        ConfirmationDialog dialog = new()
        {
            Title = "Render Cards",
            Size = new Vector2I(360, 260),
        };

        Label promptLabel = new() { Text = "Select which cards to render:" };
        CheckBox baseCardsCheckbox = new() { Text = "Unupgraded (base) cards", ButtonPressed = true };
        CheckBox upgradedCardsCheckbox = new() { Text = "Upgraded cards", ButtonPressed = false };
        VBoxContainer optionsBox = new()
        {
            Position = new Vector2(20f, 50f),
        };
        optionsBox.AddChild(promptLabel);
        optionsBox.AddChild(baseCardsCheckbox);
        optionsBox.AddChild(upgradedCardsCheckbox);
        dialog.AddChild(optionsBox);

        dialog.Confirmed += () => OnRenderConfirmed(button, progressBar, baseCardsCheckbox.ButtonPressed, upgradedCardsCheckbox.ButtonPressed);
        dialog.VisibilityChanged += () =>
        {
            if (!dialog.Visible)
            {
                dialog.QueueFree();
            }
        };

        mainMenu.AddChild(dialog);
        dialog.PopupCentered();
    }

    private static async void OnRenderConfirmed(Button button, ProgressBar progressBar, bool renderBaseCards, bool renderUpgradedCards)
    {
        button.Disabled = true;
        progressBar.Value = 0;
        progressBar.Visible = true;
        try
        {
            if (renderBaseCards)
            {
                await CardRenderer.RenderAllCardsAsync((current, total) =>
                {
                    progressBar.MaxValue = total;
                    progressBar.Value = current;
                });
            }
            if (renderUpgradedCards)
            {
                // TODO: render upgraded card variants. No-op for now.
            }
        }
        finally
        {
            button.Disabled = false;
            progressBar.Visible = false;
        }
    }
}
