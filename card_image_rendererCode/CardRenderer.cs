using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace card_image_renderer.card_image_rendererCode;

public static class CardRenderer
{
    public static async Task RenderCardsAsync(bool renderBaseCards, bool renderUpgradedCards, Action<int, int>? onProgress = null)
    {
        List<CardModel> allCards = ModelDb.AllCards.ToList();
        int totalOperations = allCards.Count * ((renderBaseCards ? 1 : 0) + (renderUpgradedCards ? 1 : 0));
        int completed = 0;

        // Reused across the whole batch instead of being created and QueueFree()'d per card: each
        // render target is tens of millions of pixels, and repeatedly allocating/tearing one down
        // hundreds of times in a tight loop outpaces the engine's deferred GPU cleanup, ballooning
        // memory usage over the course of a full run.
        (SubViewport viewport, NCard card) = CreateRenderRig();
        try
        {
            if (renderBaseCards)
            {
                foreach (CardModel canonicalCard in allCards)
                {
                    completed++;
                    try
                    {
                        CardModel cardModel = canonicalCard.ToMutable();
                        string fileName = cardModel.Id.Entry.ToLowerInvariant();
                        await RenderCardToPngAsync(viewport, card, cardModel, $"user://card_image_renderer/{fileName}.png", completed, totalOperations);
                    }
                    catch (Exception e)
                    {
                        MainFile.Logger.Error($"Failed to render card '{canonicalCard.Id}': {e}");
                    }
                    onProgress?.Invoke(completed, totalOperations);
                }
            }

            if (renderUpgradedCards)
            {
                foreach (CardModel canonicalCard in allCards)
                {
                    completed++;
                    try
                    {
                        CardModel cardModel = canonicalCard.ToMutable();
                        if (!cardModel.IsUpgradable)
                        {
                            MainFile.Logger.Warn($"Skipping upgraded render for '{cardModel.Id}': card is not upgradable.");
                        }
                        else
                        {
                            // Actually upgrades the model (not just an upgrade *preview*, see
                            // NInspectCardScreen/UpdateCardDisplay), same pairing CardModel.FromSerializable
                            // uses to restore an upgraded card from a save.
                            cardModel.UpgradeInternal();
                            cardModel.FinalizeUpgradeInternal();
                            string fileName = cardModel.Id.Entry.ToLowerInvariant();
                            await RenderCardToPngAsync(viewport, card, cardModel, $"user://card_image_renderer/{fileName}_upgraded.png", completed, totalOperations);
                        }
                    }
                    catch (Exception e)
                    {
                        MainFile.Logger.Error($"Failed to render upgraded card '{canonicalCard.Id}': {e}");
                    }
                    onProgress?.Invoke(completed, totalOperations);
                }
            }
        }
        finally
        {
            viewport.QueueFree();
        }
    }

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

    private static (SubViewport, NCard) CreateRenderRig()
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

        return (viewport, card);
    }

    private static async Task RenderCardToPngAsync(SubViewport viewport, NCard card, CardModel model, string outputPath, int cardNumber, int totalCards)
    {
        SceneTree sceneTree = (SceneTree)Engine.GetMainLoop();

        // Temporary instrumentation to find out which phase actually dominates render time before
        // optimizing anything - remove once we know.
        Stopwatch stopwatch = Stopwatch.StartNew();

        card.Model = model;
        card.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
        ReplaceWithHighResPortrait(card, model);
        long setupMs = stopwatch.ElapsedMilliseconds;

        // The viewport needs at least one render pass before its texture has real content.
        await sceneTree.ToSignal(sceneTree, SceneTree.SignalName.ProcessFrame);
        await sceneTree.ToSignal(sceneTree, SceneTree.SignalName.ProcessFrame);
        long afterWaitMs = stopwatch.ElapsedMilliseconds;

        // Image wraps a large *native* pixel buffer (tens of MB at our render size) behind a small
        // managed wrapper object, so the .NET GC has no visibility into how much memory is actually
        // at stake and won't collect these promptly on its own. Across hundreds of cards, waiting for
        // GC/finalizers to catch up let native memory balloon far faster than it was reclaimed, so we
        // explicitly dispose both images as soon as we're done with them instead.
        using Image image = viewport.GetTexture().GetImage();
        long afterReadbackMs = stopwatch.ElapsedMilliseconds;

        Rect2I usedRect = image.GetUsedRect();
        Vector2I renderSize = viewport.Size;
        if (usedRect.Position.X <= 0 || usedRect.Position.Y <= 0
            || usedRect.End.X >= renderSize.X || usedRect.End.Y >= renderSize.Y)
        {
            MainFile.Logger.Warn($"Card render for '{model.Id}' touched the edge of the {renderSize} render target; output may be clipped. Consider increasing OversizeFactor.");
        }
        using Image cropped = image.GetRegion(usedRect);
        long afterCropMs = stopwatch.ElapsedMilliseconds;

        string absolutePath = ProjectSettings.GlobalizePath(outputPath);
        DirAccess.MakeDirRecursiveAbsolute(outputPath.GetBaseDir());
        Error error = cropped.SavePng(outputPath);
        long afterSaveMs = stopwatch.ElapsedMilliseconds;

        if (error != Error.Ok)
        {
            throw new InvalidOperationException($"Failed to save card render to '{absolutePath}': {error}");
        }

        int digits = totalCards.ToString().Length;
        string paddedCardNumber = cardNumber.ToString().PadLeft(digits, '0');
        string paddedTotalCards = totalCards.ToString().PadLeft(digits, '0');
        double progressPercent = cardNumber / (double)totalCards * 100;
        long waitMs = afterWaitMs - setupMs;
        long readbackMs = afterReadbackMs - afterWaitMs;
        long cropMs = afterCropMs - afterReadbackMs;
        long saveMs = afterSaveMs - afterCropMs;
        MainFile.Logger.Info($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ({paddedCardNumber}/{paddedTotalCards}, {progressPercent:F1}%) Rendered card '{model.Id}' ({usedRect.Size}) to '{absolutePath}' [setup={setupMs}ms wait={waitMs}ms readback={readbackMs}ms crop={cropMs}ms save={saveMs}ms total={afterSaveMs}ms].");
    }

    // CardModel.Portrait loads from the runtime texture atlas (e.g. 250x190 for Bash), which is
    // downsampled from the original art for VRAM efficiency - fine at gameplay scale, but blurry once
    // we scale the card up 5x. The original full-resolution art still exists on disk uncompressed
    // (e.g. 1000x760 for Bash) at "packed/card_portraits/{pool}/{entry}.png"; CardModel.PortraitPngPath
    // computes that same path internally but is private, so we reconstruct it here instead.
    private static void ReplaceWithHighResPortrait(NCard card, CardModel model)
    {
        if (!model.HasPortrait)
        {
            return;
        }

        string highResPath = ImageHelper.GetImagePath($"packed/card_portraits/{model.Pool.Title.ToLowerInvariant()}/{model.Id.Entry.ToLowerInvariant()}.png");
        if (!ResourceLoader.Exists(highResPath))
        {
            return;
        }

        Texture2D highResPortrait = ResourceLoader.Load<Texture2D>(highResPath, null, ResourceLoader.CacheMode.Reuse);
        string portraitNodePath = model.Rarity == CardRarity.Ancient ? "%AncientPortrait" : "%Portrait";
        card.GetNode<TextureRect>(portraitNodePath).Texture = highResPortrait;
    }
}
