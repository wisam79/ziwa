namespace Ziwa.Domain.Primitives;

public readonly record struct DocumentId(Guid Value)
{
    public static DocumentId Empty => new(Guid.Empty);
    public static DocumentId New() => new(Guid.NewGuid());
    public bool IsEmpty => Value == Guid.Empty;
}

public readonly record struct PageId(Guid Value)
{
    public static PageId Empty => new(Guid.Empty);
    public static PageId New() => new(Guid.NewGuid());
    public bool IsEmpty => Value == Guid.Empty;
}

public readonly record struct NodeId(Guid Value)
{
    public static NodeId Empty => new(Guid.Empty);
    public static NodeId New() => new(Guid.NewGuid());
    public bool IsEmpty => Value == Guid.Empty;
}

public readonly record struct SlotId(Guid Value)
{
    public static SlotId Empty => new(Guid.Empty);
    public static SlotId New() => new(Guid.NewGuid());
    public bool IsEmpty => Value == Guid.Empty;
}

public readonly record struct AssetId(string Value)
{
    public static AssetId Empty => new(string.Empty);
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);
}
