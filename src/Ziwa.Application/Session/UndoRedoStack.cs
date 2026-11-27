using Ziwa.Domain.Models;

namespace Ziwa.Application.Session;

public sealed record UndoSnapshotEntry(
    string DescriptionAr,
    ZiwaDocument Before,
    ZiwaDocument After,
    string? CoalesceKey,
    DateTimeOffset TimestampUtc);

public sealed class UndoRedoStack
{
    public const int MaxHistoryCount = 100;
    public static readonly TimeSpan DefaultCoalesceWindow = TimeSpan.FromMilliseconds(400);

    private readonly List<UndoSnapshotEntry> _undo = new();
    private readonly List<UndoSnapshotEntry> _redo = new();
    private readonly TimeSpan _coalesceWindow;

    public UndoRedoStack(TimeSpan? coalesceWindow = null)
    {
        _coalesceWindow = coalesceWindow ?? DefaultCoalesceWindow;
    }

    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? NextUndoDescription => _undo.Count > 0 ? _undo[^1].DescriptionAr : null;
    public string? NextRedoDescription => _redo.Count > 0 ? _redo[^1].DescriptionAr : null;

    public void Push(
        string descriptionAr,
        ZiwaDocument before,
        ZiwaDocument after,
        string? coalesceKey = null,
        DateTimeOffset? timestampUtc = null)
    {
        if (ReferenceEquals(before, after))
        {
            return;
        }

        DateTimeOffset now = timestampUtc ?? DateTimeOffset.UtcNow;
        _redo.Clear();

        if (!string.IsNullOrEmpty(coalesceKey) && _undo.Count > 0)
        {
            UndoSnapshotEntry top = _undo[^1];
            if (string.Equals(top.CoalesceKey, coalesceKey, StringComparison.Ordinal) &&
                (now - top.TimestampUtc) <= _coalesceWindow)
            {
                _undo[^1] = top with { After = after, TimestampUtc = now };
                return;
            }
        }

        _undo.Add(new UndoSnapshotEntry(descriptionAr, before, after, coalesceKey, now));
        if (_undo.Count > MaxHistoryCount)
        {
            _undo.RemoveAt(0);
        }
    }

    public bool TryUndo(out UndoSnapshotEntry? entry)
    {
        if (_undo.Count == 0)
        {
            entry = null;
            return false;
        }

        int lastIdx = _undo.Count - 1;
        entry = _undo[lastIdx];
        _undo.RemoveAt(lastIdx);
        _redo.Add(entry);
        return true;
    }

    public bool TryRedo(out UndoSnapshotEntry? entry)
    {
        if (_redo.Count == 0)
        {
            entry = null;
            return false;
        }

        int lastIdx = _redo.Count - 1;
        entry = _redo[lastIdx];
        _redo.RemoveAt(lastIdx);
        _undo.Add(entry);
        return true;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
