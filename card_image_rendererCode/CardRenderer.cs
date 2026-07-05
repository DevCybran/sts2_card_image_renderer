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

    public static async Task RenderCardToPngAsync(CardModel model, string outputPath)
    {
        SceneTree sceneTree = (SceneTree)Engine.GetMainLoop();

        SubViewport viewport = new()
        {
            Size = new Vector2I((int)NCard.defaultSize.X, (int)NCard.defaultSize.Y),
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        sceneTree.Root.AddChild(viewport);

        NCard card = GD.Load<PackedScene>(CardScenePath).Instantiate<NCard>();
        viewport.AddChild(card);
        // The card's local (0,0) is its visual center (children use negative offsets around it),
        // so shift it to the middle of the viewport instead of the top-left corner.
        card.Position = (Vector2)viewport.Size / 2f;
        card.Model = model;
        card.UpdateVisuals(PileType.None, CardPreviewMode.Normal);

        // The viewport needs at least one render pass before its texture has real content.
        await sceneTree.ToSignal(sceneTree, SceneTree.SignalName.ProcessFrame);
        await sceneTree.ToSignal(sceneTree, SceneTree.SignalName.ProcessFrame);

        Image image = viewport.GetTexture().GetImage();
        string absolutePath = ProjectSettings.GlobalizePath(outputPath);
        DirAccess.MakeDirRecursiveAbsolute(outputPath.GetBaseDir());
        Error error = image.SavePng(outputPath);

        viewport.QueueFree();

        if (error != Error.Ok)
        {
            throw new InvalidOperationException($"Failed to save card render to '{absolutePath}': {error}");
        }

        MainFile.Logger.Info($"Rendered card '{model.Id}' to '{absolutePath}'.");
    }
}
