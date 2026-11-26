using System.Collections.Immutable;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Domain.Validation;

namespace Ziwa.Application.Session;

public sealed class DocumentSession
{
    private readonly UndoRedoStack _history;
    private string? _pendingInteractionDescription;

    public DocumentSession(ZiwaDocument initialDocument, TimeSpan? coalesceWindow = null)
    {
        ValidateOrThrow(initialDocument);
        CommittedDocument = initialDocument;
        _history = new UndoRedoStack(coalesceWindow);
    }

    public event Action? SessionChanged;

    public ZiwaDocument CommittedDocument { get; private set; }
    public ZiwaDocument? InteractionPreview { get; private set; }
    public ZiwaDocument EffectiveDocument => InteractionPreview ?? CommittedDocument;

    public ImmutableArray<NodeId> SelectedNodeIds { get; private set; } = [];
    public SlotId? SelectedCollageSlotId { get; private set; }
    public NodeId? ActiveCropNodeId { get; private set; }
    public NodeId? ActiveTextEditNodeId { get; private set; }
    public NodeId? EditingStickerNodeId { get; private set; }

    public bool IsInteracting => InteractionPreview is not null;
    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;
    public int UndoCount => _history.UndoCount;
    public int RedoCount => _history.RedoCount;
    public long Generation { get; private set; }
    public long Revision { get; private set; }

    public void BeginInteraction(string descriptionAr)
    {
        if (IsInteracting)
        {
            return;
        }

        _pendingInteractionDescription = descriptionAr;
        InteractionPreview = CommittedDocument;
        SessionChanged?.Invoke();
    }

    public void UpdateTransientPreview(Func<ZiwaDocument, ZiwaDocument> previewMutator)
    {
        ArgumentNullException.ThrowIfNull(previewMutator);

        ZiwaDocument baseDoc = InteractionPreview ?? CommittedDocument;
        ZiwaDocument next = previewMutator(baseDoc);
        ValidateOrThrow(next);
        InteractionPreview = next;
        SessionChanged?.Invoke();
    }

    public bool CommitInteraction()
    {
        if (InteractionPreview is null)
        {
            return false;
        }

        ZiwaDocument after = InteractionPreview;
        ZiwaDocument before = CommittedDocument;
        InteractionPreview = null;
        string description = _pendingInteractionDescription ?? "تعديل تفاعلي";
        _pendingInteractionDescription = null;

        if (!ReferenceEquals(before, after) && !Equals(before, after))
        {
            _history.Push(description, before, after);
            CommittedDocument = after;
            Revision++;
            PruneSelection();
            SessionChanged?.Invoke();
            return true;
        }

        SessionChanged?.Invoke();
        return false;
    }

    public void CancelInteraction()
    {
        if (InteractionPreview is null)
        {
            return;
        }

        InteractionPreview = null;
        _pendingInteractionDescription = null;
        SessionChanged?.Invoke();
    }

    public bool ApplyCommand(
        string descriptionAr,
        Func<ZiwaDocument, ZiwaDocument> mutator,
        string? coalesceKey = null,
        DateTimeOffset? timestampUtc = null)
    {
        ArgumentNullException.ThrowIfNull(mutator);

        if (IsInteracting)
        {
            CancelInteraction();
        }

        ZiwaDocument before = CommittedDocument;
        ZiwaDocument after = mutator(before);
        ValidateOrThrow(after);

        if (ReferenceEquals(before, after) || Equals(before, after))
        {
            return false;
        }

        _history.Push(descriptionAr, before, after, coalesceKey, timestampUtc);
        CommittedDocument = after;
        Revision++;
        PruneSelection();
        SessionChanged?.Invoke();
        return true;
    }

    public void ReplaceDocument(ZiwaDocument newDocument)
    {
        ValidateOrThrow(newDocument);
        InteractionPreview = null;
        _pendingInteractionDescription = null;
        CommittedDocument = newDocument;
        Generation++;
        Revision++;
        SelectedNodeIds = [];
        SelectedCollageSlotId = null;
        ActiveCropNodeId = null;
        ActiveTextEditNodeId = null;
        EditingStickerNodeId = null;
        _history.Clear();
        SessionChanged?.Invoke();
    }

    public bool Undo()
    {
        if (IsInteracting)
        {
            CancelInteraction();
        }

        if (!_history.TryUndo(out UndoSnapshotEntry? entry) || entry is null)
        {
            return false;
        }

        CommittedDocument = entry.Before;
        Revision++;
        PruneSelection();
        SessionChanged?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (IsInteracting)
        {
            CancelInteraction();
        }

        if (!_history.TryRedo(out UndoSnapshotEntry? entry) || entry is null)
        {
            return false;
        }

        CommittedDocument = entry.After;
        Revision++;
        PruneSelection();
        SessionChanged?.Invoke();
        return true;
    }

    public void SetSelection(ImmutableArray<NodeId> nodeIds)
    {
        HashSet<NodeId> validIds = EffectiveDocument.ActivePage.Nodes
            .Where(n => n.IsVisible && !n.IsLocked)
            .Select(n => n.Id)
            .ToHashSet();

        ImmutableArray<NodeId> filtered = nodeIds.Where(validIds.Contains).Distinct().ToImmutableArray();
        if (SelectedNodeIds.SequenceEqual(filtered) && SelectedCollageSlotId is null)
        {
            return;
        }

        SelectedNodeIds = filtered;
        SelectedCollageSlotId = null;
        if (ActiveCropNodeId is { } cropId && !filtered.Contains(cropId))
        {
            ActiveCropNodeId = null;
        }

        if (ActiveTextEditNodeId is { } textId && !filtered.Contains(textId))
        {
            ActiveTextEditNodeId = null;
        }

        if (EditingStickerNodeId is { } stickerId && !filtered.Contains(stickerId))
        {
            EditingStickerNodeId = null;
        }

        SessionChanged?.Invoke();
    }

    public void SelectCollageSlot(SlotId? slotId)
    {
        if (slotId is { } id)
        {
            bool exists = EffectiveDocument.ActivePage.CollageLayout?.Slots.Any(s => s.Id == id) == true;
            if (!exists)
            {
                return;
            }
        }

        if (SelectedCollageSlotId == slotId && SelectedNodeIds.IsDefaultOrEmpty)
        {
            return;
        }

        SelectedNodeIds = [];
        ActiveCropNodeId = null;
        ActiveTextEditNodeId = null;
        EditingStickerNodeId = null;
        SelectedCollageSlotId = slotId;
        SessionChanged?.Invoke();
    }

    public void OpenStickerEditor(NodeId? stickerNodeId)
    {
        if (stickerNodeId is { } id)
        {
            SceneNode? node = EffectiveDocument.ActivePage.Nodes.FirstOrDefault(n => n.Id == id);
            if (node is not StickerNode || node.IsLocked)
            {
                return;
            }

            SelectedNodeIds = [id];
            SelectedCollageSlotId = null;
            ActiveCropNodeId = null;
            ActiveTextEditNodeId = null;
            EditingStickerNodeId = id;
            SessionChanged?.Invoke();
            return;
        }

        if (EditingStickerNodeId is not null)
        {
            EditingStickerNodeId = null;
            SessionChanged?.Invoke();
        }
    }

    public void ClearSelection()
    {
        if (SelectedNodeIds.IsDefaultOrEmpty &&
            SelectedCollageSlotId is null &&
            ActiveCropNodeId is null &&
            ActiveTextEditNodeId is null &&
            EditingStickerNodeId is null)
        {
            return;
        }

        SelectedNodeIds = [];
        SelectedCollageSlotId = null;
        ActiveCropNodeId = null;
        ActiveTextEditNodeId = null;
        EditingStickerNodeId = null;
        SessionChanged?.Invoke();
    }

    public void EnterCropMode(NodeId imageNodeId)
    {
        SceneNode? node = EffectiveDocument.ActivePage.Nodes.FirstOrDefault(n => n.Id == imageNodeId);
        if (node is not ImageNode || node.IsLocked)
        {
            return;
        }

        SelectedNodeIds = [imageNodeId];
        ActiveCropNodeId = imageNodeId;
        ActiveTextEditNodeId = null;
        BeginInteraction("قص الصورة الموضعي");
    }

    public bool CommitCropMode()
    {
        if (ActiveCropNodeId is null)
        {
            return false;
        }

        ActiveCropNodeId = null;
        return CommitInteraction();
    }

    public void ExitCropMode(bool cancel = true)
    {
        if (ActiveCropNodeId is null)
        {
            return;
        }

        ActiveCropNodeId = null;
        if (cancel)
        {
            CancelInteraction();
        }
        else
        {
            CommitInteraction();
        }
    }

    public void EnterTextEditMode(NodeId textNodeId)
    {
        SceneNode? node = EffectiveDocument.ActivePage.Nodes.FirstOrDefault(n => n.Id == textNodeId);
        if (node is not TextNode || node.IsLocked)
        {
            return;
        }

        SelectedNodeIds = [textNodeId];
        ActiveTextEditNodeId = textNodeId;
        ActiveCropNodeId = null;
        SessionChanged?.Invoke();
    }

    public void ExitTextEditMode()
    {
        if (ActiveTextEditNodeId is null)
        {
            return;
        }

        ActiveTextEditNodeId = null;
        SessionChanged?.Invoke();
    }

    public OperationTicket CreateOperationTicket(NodeId? targetNodeId = null, CancellationToken token = default)
    {
        return new OperationTicket(Guid.NewGuid(), CommittedDocument.Id, targetNodeId, token);
    }

    public bool TryApplyAsyncResult(
        OperationTicket ticket,
        string descriptionAr,
        Func<ZiwaDocument, ZiwaDocument> mutator)
    {
        ArgumentNullException.ThrowIfNull(mutator);

        if (ticket.Token.IsCancellationRequested)
        {
            return false;
        }

        if (CommittedDocument.Id != ticket.DocumentId)
        {
            return false;
        }

        if (ticket.TargetNodeId is { } targetId)
        {
            SceneNode? target = CommittedDocument.ActivePage.Nodes.FirstOrDefault(n => n.Id == targetId);
            if (target is null || target.IsLocked)
            {
                return false;
            }
        }

        return ApplyCommand(descriptionAr, mutator);
    }

    private void PruneSelection()
    {
        HashSet<NodeId> existingIds = EffectiveDocument.ActivePage.Nodes.Select(n => n.Id).ToHashSet();
        SelectedNodeIds = SelectedNodeIds.Where(existingIds.Contains).ToImmutableArray();
        if (ActiveCropNodeId is { } cropId && !existingIds.Contains(cropId))
        {
            ActiveCropNodeId = null;
        }

        if (ActiveTextEditNodeId is { } textId && !existingIds.Contains(textId))
        {
            ActiveTextEditNodeId = null;
        }

        if (EditingStickerNodeId is { } stickerId && !existingIds.Contains(stickerId))
        {
            EditingStickerNodeId = null;
        }

        if (SelectedCollageSlotId is { } slotId &&
            EffectiveDocument.ActivePage.CollageLayout?.Slots.Any(s => s.Id == slotId) != true)
        {
            SelectedCollageSlotId = null;
        }
    }

    private static void ValidateOrThrow(ZiwaDocument document)
    {
        ValidationResult validation = SchemaValidator.ValidateDocument(document);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"Document failed schema validation: {string.Join("; ", validation.Errors)}");
        }
    }
}
