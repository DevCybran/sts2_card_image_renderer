using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace card_image_renderer.card_image_rendererCode;

public static class CardRenderer
{
    // Crop + PNG encode + disk write take far longer than capturing the render (per-phase timing
    // showed ~90% of per-card time in crop+save), and neither touches the shared viewport/card once
    // we have the captured Image - so they run in the background instead of blocking the main render
    // loop. Bounded to this many in-flight cards at once, so we don't hold an unbounded number of
    // captured-but-unsaved Images (tens of MB each) in memory if saving lags behind capturing.
    private const int MaxConcurrentSaves = 16;

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
        using SemaphoreSlim saveThrottle = new(MaxConcurrentSaves, MaxConcurrentSaves);
        List<Task> pendingSaves = new();
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
                        await QueueRenderAsync(viewport, card, saveThrottle, pendingSaves, cardModel, $"user://card_image_renderer/{fileName}.png", completed, totalOperations);
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
                            await QueueRenderAsync(viewport, card, saveThrottle, pendingSaves, cardModel, $"user://card_image_renderer/{fileName}_upgraded.png", completed, totalOperations);
                        }
                    }
                    catch (Exception e)
                    {
                        MainFile.Logger.Error($"Failed to render upgraded card '{canonicalCard.Id}': {e}");
                    }
                    onProgress?.Invoke(completed, totalOperations);
                }
            }

            await Task.WhenAll(pendingSaves);
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

    // Acquires a save slot, captures the card on the shared viewport/card (must happen one at a time
    // on the main thread), then hands the resulting Image off to a background crop+save task without
    // awaiting it - the caller's loop can move straight on to the next card's capture.
    private static async Task QueueRenderAsync(SubViewport viewport, NCard card, SemaphoreSlim saveThrottle, List<Task> pendingSaves, CardModel model, string outputPath, int cardNumber, int totalCards)
    {
        await saveThrottle.WaitAsync();

        Image image;
        Vector2I renderSize;
        long setupMs, waitMs, readbackMs;
        try
        {
            (image, renderSize, setupMs, waitMs, readbackMs) = await CaptureCardAsync(viewport, card, model);
        }
        catch
        {
            saveThrottle.Release();
            throw;
        }

        pendingSaves.Add(CropAndSaveAsync(saveThrottle, image, renderSize, model, outputPath, cardNumber, totalCards, setupMs, waitMs, readbackMs));
    }

    private static async Task<(Image Image, Vector2I RenderSize, long SetupMs, long WaitMs, long ReadbackMs)> CaptureCardAsync(SubViewport viewport, NCard card, CardModel model)
    {
        SceneTree sceneTree = (SceneTree)Engine.GetMainLoop();
        Stopwatch stopwatch = Stopwatch.StartNew();

        card.Model = model;
        card.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
        ReplaceWithHighResPortrait(card, model);
        long setupMs = stopwatch.ElapsedMilliseconds;

        // The viewport needs at least one render pass before its texture has real content.
        await sceneTree.ToSignal(sceneTree, SceneTree.SignalName.ProcessFrame);
        await sceneTree.ToSignal(sceneTree, SceneTree.SignalName.ProcessFrame);
        long afterWaitMs = stopwatch.ElapsedMilliseconds;

        Image image = viewport.GetTexture().GetImage();
        long afterReadbackMs = stopwatch.ElapsedMilliseconds;

        return (image, viewport.Size, setupMs, afterWaitMs - setupMs, afterReadbackMs - afterWaitMs);
    }

    // Runs on a background thread pool task. Image/DirAccess/ProjectSettings only touch their own
    // data (no scene tree/RenderingServer access), which is why this is safe to run off the main
    // thread - unlike anything that touches viewport/card, which must stay on the main thread.
    private static async Task CropAndSaveAsync(SemaphoreSlim saveThrottle, Image image, Vector2I renderSize, CardModel model, string outputPath, int cardNumber, int totalCards, long setupMs, long waitMs, long readbackMs)
    {
        try
        {
            await Task.Run(() =>
            {
                // Image wraps a large *native* pixel buffer (tens of MB at our render size) behind a
                // small managed wrapper object, so the .NET GC has no visibility into how much memory
                // is actually at stake and won't collect these promptly on its own. Across hundreds of
                // cards, waiting for GC/finalizers to catch up let native memory balloon far faster
                // than it was reclaimed, so we explicitly dispose both images as soon as we're done.
                using Image disposableImage = image;
                Stopwatch stopwatch = Stopwatch.StartNew();

                Rect2I usedRect = disposableImage.GetUsedRect();
                if (usedRect.Position.X <= 0 || usedRect.Position.Y <= 0
                    || usedRect.End.X >= renderSize.X || usedRect.End.Y >= renderSize.Y)
                {
                    MainFile.Logger.Warn($"Card render for '{model.Id}' touched the edge of the {renderSize} render target; output may be clipped. Consider increasing OversizeFactor.");
                }
                using Image cropped = disposableImage.GetRegion(usedRect);
                long cropMs = stopwatch.ElapsedMilliseconds;

                string absolutePath = ProjectSettings.GlobalizePath(outputPath);
                DirAccess.MakeDirRecursiveAbsolute(outputPath.GetBaseDir());
                Error error = cropped.SavePng(outputPath);
                long saveMs = stopwatch.ElapsedMilliseconds - cropMs;

                if (error != Error.Ok)
                {
                    throw new InvalidOperationException($"Failed to save card render to '{absolutePath}': {error}");
                }

                int digits = totalCards.ToString().Length;
                string paddedCardNumber = cardNumber.ToString().PadLeft(digits, '0');
                string paddedTotalCards = totalCards.ToString().PadLeft(digits, '0');
                double progressPercent = cardNumber / (double)totalCards * 100;
                long totalMs = setupMs + waitMs + readbackMs + cropMs + saveMs;
                MainFile.Logger.Info($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ({paddedCardNumber}/{paddedTotalCards}, {progressPercent:F1}%) Rendered card '{model.Id}' ({usedRect.Size}) to '{absolutePath}' [setup={setupMs}ms wait={waitMs}ms readback={readbackMs}ms crop={cropMs}ms save={saveMs}ms total={totalMs}ms].");
            });
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to crop/save card '{model.Id}': {e}");
        }
        finally
        {
            saveThrottle.Release();
        }
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
