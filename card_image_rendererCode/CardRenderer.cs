using System;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace card_image_renderer.card_image_rendererCode;

public static class CardRenderer
{
    // Same scene NCard.Create() pulls from its NodePool; we instantiate it directly instead of
    // going through the pool so we don't depend on NodePool/TestMode having been initialized yet.
    private const string CardScenePath = "res://scenes/cards/card.tscn";

    // Renders at this multiple of NCard.defaultSize for higher-resolution output.
    private const float RenderScale = 5f;

    // Several card elements (banners, cost badges, ancient borders, highlight glow, etc.) extend
    // well beyond NCard.defaultSize. Rather than hand-tracking every such element's offset, we
    // render into a generously oversized transparent viewport and let Image.GetUsedRect() (Godot's
    // own non-transparent-pixel bounding box, used for sprite trimming) tell us the real footprint.
    private const float OversizeFactor = 3f;

    public static async Task RenderCardToPngAsync(CardModel model, string outputPath, int cardNumber, int totalCards)
    {
        SceneTree sceneTree = (SceneTree)Engine.GetMainLoop();

        Vector2 scaledCardSize = NCard.defaultSize * RenderScale;
        Vector2I renderSize = new(
            (int)(scaledCardSize.X * OversizeFactor),
            (int)(scaledCardSize.Y * OversizeFactor));
        SubViewport viewport = new()
        {
            Size = renderSize,
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        sceneTree.Root.AddChild(viewport);

        NCard card = GD.Load<PackedScene>(CardScenePath).Instantiate<NCard>();
        viewport.AddChild(card);
        card.Scale = Vector2.One * RenderScale;
        // The card's local (0,0) is its visual center (children use negative offsets around it),
        // so shift it to the middle of the viewport instead of the top-left corner. Position isn't
        // affected by Scale since it's anchored at the card's default PivotOffset of (0,0).
        card.Position = (Vector2)viewport.Size / 2f;
        card.Model = model;
        card.UpdateVisuals(PileType.None, CardPreviewMode.Normal);

        // The viewport needs at least one render pass before its texture has real content.
        await sceneTree.ToSignal(sceneTree, SceneTree.SignalName.ProcessFrame);
        await sceneTree.ToSignal(sceneTree, SceneTree.SignalName.ProcessFrame);

        Image image = viewport.GetTexture().GetImage();
        viewport.QueueFree();

        Rect2I usedRect = image.GetUsedRect();
        if (usedRect.Position.X <= 0 || usedRect.Position.Y <= 0
            || usedRect.End.X >= renderSize.X || usedRect.End.Y >= renderSize.Y)
        {
            MainFile.Logger.Warn($"Card render for '{model.Id}' touched the edge of the {renderSize} render target; output may be clipped. Consider increasing OversizeFactor.");
        }
        Image cropped = image.GetRegion(usedRect);

        string absolutePath = ProjectSettings.GlobalizePath(outputPath);
        DirAccess.MakeDirRecursiveAbsolute(outputPath.GetBaseDir());
        Error error = cropped.SavePng(outputPath);

        if (error != Error.Ok)
        {
            throw new InvalidOperationException($"Failed to save card render to '{absolutePath}': {error}");
        }

        int digits = totalCards.ToString().Length;
        string paddedCardNumber = cardNumber.ToString().PadLeft(digits, '0');
        string paddedTotalCards = totalCards.ToString().PadLeft(digits, '0');
        double progressPercent = cardNumber / (double)totalCards * 100;
        MainFile.Logger.Info($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ({paddedCardNumber}/{paddedTotalCards}, {progressPercent:F1}%) Rendered card '{model.Id}' ({usedRect.Size}) to '{absolutePath}'.");
    }
}
