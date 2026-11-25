using Xunit;
using Ziwa.Application.Commands;
using Ziwa.Application.Interaction;
using Ziwa.Application.Services;
using Ziwa.Application.Session;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.Application.Tests;

public sealed class DocumentSessionAndCommandRegistryTests
{
    private static ZiwaDocument CreateDoc() =>
        SampleVerticalSliceFactory.CreateShowcaseDocument(new AssetId("asset-1"));

    [Fact]
    public void FiftyContinuousPreviewUpdates_CommitIntoSingleUndoStep()
    {
        var session = new DocumentSession(CreateDoc());
        SceneNode target = session.CommittedDocument.ActivePage.Nodes[0];
        double initialX = target.BoundsMm.X;

        session.BeginInteraction("سحب عنصر");
        for (int i = 1; i <= 50; i++)
        {
            double delta = i * 0.5;
            session.UpdateTransientPreview(doc =>
                FreeformSceneOperations.TranslateNodes(
                    session.CommittedDocument,
                    new HashSet<NodeId> { target.Id },
                    delta,
                    delta));
            Assert.Equal(0, session.UndoCount);
        }

        Assert.True(session.IsInteracting);
        Assert.True(session.CommitInteraction());
        Assert.False(session.IsInteracting);
        Assert.Equal(1, session.UndoCount);

        SceneNode moved = session.CommittedDocument.ActivePage.Nodes[0];
        Assert.Equal(initialX + 25.0, moved.BoundsMm.X, precision: 6);

        Assert.True(session.Undo());
        Assert.Equal(initialX, session.CommittedDocument.ActivePage.Nodes[0].BoundsMm.X, precision: 6);
        Assert.Equal(0, session.UndoCount);
        Assert.True(session.CanRedo);
    }

    [Fact]
    public void CancelInteraction_RestoresCommittedStateWithZeroUndoPollution()
    {
        var session = new DocumentSession(CreateDoc());
        SceneNode target = session.CommittedDocument.ActivePage.Nodes[0];
        double initialX = target.BoundsMm.X;

        session.BeginInteraction("تحريك ملغى");
        session.UpdateTransientPreview(doc =>
            FreeformSceneOperations.TranslateNodes(doc, new HashSet<NodeId> { target.Id }, 18.0, 10.0));
        Assert.Equal(initialX + 18.0, session.EffectiveDocument.ActivePage.Nodes[0].BoundsMm.X, precision: 6);

        session.CancelInteraction();
        Assert.False(session.IsInteracting);
        Assert.Equal(initialX, session.EffectiveDocument.ActivePage.Nodes[0].BoundsMm.X, precision: 6);
        Assert.Equal(0, session.UndoCount);
    }

    [Fact]
    public void RapidKeyboardNudgesWithinWindow_CoalesceIntoSingleUndoEntry()
    {
        var session = new DocumentSession(CreateDoc(), TimeSpan.FromMilliseconds(400));
        NodeId targetId = session.CommittedDocument.ActivePage.Nodes[0].Id;
        double initialX = session.CommittedDocument.ActivePage.Nodes[0].BoundsMm.X;
        DateTimeOffset t0 = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

        for (int i = 0; i < 5; i++)
        {
            session.ApplyCommand(
                "تحريك بالأسهم",
                doc => FreeformSceneOperations.TranslateNodes(doc, new HashSet<NodeId> { targetId }, 1.0, 0.0),
                coalesceKey: $"nudge:{targetId.Value}",
                timestampUtc: t0.AddMilliseconds(i * 60));
        }

        Assert.Equal(1, session.UndoCount);
        Assert.Equal(initialX + 5.0, session.CommittedDocument.ActivePage.Nodes[0].BoundsMm.X, precision: 6);

        // Next nudge after window (> 400ms) starts a second Undo step
        session.ApplyCommand(
            "تحريك بالأسهم",
            doc => FreeformSceneOperations.TranslateNodes(doc, new HashSet<NodeId> { targetId }, 2.0, 0.0),
            coalesceKey: $"nudge:{targetId.Value}",
            timestampUtc: t0.AddMilliseconds(900));

        Assert.Equal(2, session.UndoCount);
    }

    [Fact]
    public void OperationTicket_RejectsCancelledOrDocumentSwitchedResults()
    {
        var session = new DocumentSession(CreateDoc());
        NodeId targetId = session.CommittedDocument.ActivePage.Nodes[0].Id;

        using var cts = new CancellationTokenSource();
        OperationTicket cancelledTicket = session.CreateOperationTicket(targetId, cts.Token);
        cts.Cancel();

        Assert.False(session.TryApplyAsyncResult(
            cancelledTicket,
            "نتيجة ملغاة",
            doc => FreeformSceneOperations.TranslateNodes(doc, new HashSet<NodeId> { targetId }, 5.0, 0.0)));

        OperationTicket staleDocTicket = session.CreateOperationTicket(targetId);
        session.ReplaceDocument(CreateDoc()); // Different DocumentId

        Assert.False(session.TryApplyAsyncResult(
            staleDocTicket,
            "نتيجة لمستند قديم",
            doc => FreeformSceneOperations.TranslateNodes(doc, new HashSet<NodeId> { targetId }, 5.0, 0.0)));
    }

    [Fact]
    public async Task CommandRegistry_ResolvesAndExecutesNormalizedShortcuts()
    {
        var registry = new CommandRegistry();
        int count = 0;

        registry.Register(new CommandDefinition(
            new CommandId("test.action"),
            "اختبار",
            "Ctrl+Shift+G",
            "عام",
            () => true,
            _ =>
            {
                count++;
                return Task.CompletedTask;
            }));

        Assert.True(await registry.TryExecuteShortcutAsync("ctrl + shift + g"));
        Assert.Equal(1, count);
    }
}
