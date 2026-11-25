using SkiaSharp;
using Ziwa.Domain.Primitives;

namespace Ziwa.Rendering.Assets;

public sealed class DisplayProxyCache : IDisposable
{
    private readonly long _maxBytes;
    private readonly Lock _gate = new();
    private readonly Dictionary<AssetId, LinkedListNode<CacheEntry>> _entries = new();
    private readonly LinkedList<CacheEntry> _lru = new();
    private long _currentBytes;
    private bool _disposed;

    public DisplayProxyCache(long maxBytes = 256L * 1024L * 1024L)
    {
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "Cache byte budget must be positive.");
        }

        _maxBytes = maxBytes;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public long CurrentBytes
    {
        get
        {
            lock (_gate)
            {
                return _currentBytes;
            }
        }
    }

    public bool TryGet(AssetId id, out SKImage? image)
    {
        lock (_gate)
        {
            if (_disposed || !_entries.TryGetValue(id, out var node))
            {
                image = null;
                return false;
            }

            _lru.Remove(node);
            _lru.AddFirst(node);
            image = node.Value.Image;
            return true;
        }
    }

    public SKImage Put(AssetId id, SKImage image)
    {
        long estimatedBytes = Math.Max(1L, (long)image.Width * image.Height * 4L);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_entries.TryGetValue(id, out var existing))
            {
                image.Dispose();
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return existing.Value.Image;
            }

            var entry = new CacheEntry(id, image, estimatedBytes);
            var node = new LinkedListNode<CacheEntry>(entry);
            _lru.AddFirst(node);
            _entries[id] = node;
            _currentBytes += estimatedBytes;

            while (_currentBytes > _maxBytes && _lru.Count > 1)
            {
                LinkedListNode<CacheEntry>? oldest = _lru.Last;
                if (oldest is null)
                {
                    break;
                }

                _lru.RemoveLast();
                _entries.Remove(oldest.Value.Id);
                _currentBytes -= oldest.Value.ByteSize;
                oldest.Value.Image.Dispose();
            }

            return image;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (CacheEntry entry in _lru)
            {
                entry.Image.Dispose();
            }

            _lru.Clear();
            _entries.Clear();
            _currentBytes = 0;
        }
    }

    private sealed record CacheEntry(AssetId Id, SKImage Image, long ByteSize);
}
