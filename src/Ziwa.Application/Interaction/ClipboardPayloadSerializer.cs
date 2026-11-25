using System.Collections.Immutable;
using System.Text.Json;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Domain.Validation;

namespace Ziwa.Application.Interaction;

public static class ClipboardPayloadSerializer
{
    public const string FormatTag = "ZiwaClipboardV1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private sealed record ClipboardEnvelope(string Format, ImmutableArray<SceneNode> Nodes);

    public static string SerializeNodes(IEnumerable<SceneNode> nodes)
    {
        ImmutableArray<SceneNode> arr = nodes.ToImmutableArray();
        if (arr.IsDefaultOrEmpty)
        {
            throw new ArgumentException("Cannot serialize empty clipboard node list.", nameof(nodes));
        }

        return JsonSerializer.Serialize(new ClipboardEnvelope(FormatTag, arr), JsonOptions);
    }

    public static bool TryDeserializeAndClone(
        string? rawPayload,
        out ImmutableArray<SceneNode> clonedNodes,
        double offsetMm = 5.0)
    {
        clonedNodes = [];
        if (string.IsNullOrWhiteSpace(rawPayload))
        {
            return false;
        }

        ClipboardEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ClipboardEnvelope>(rawPayload, JsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }

        if (envelope is null ||
            !string.Equals(envelope.Format, FormatTag, StringComparison.Ordinal) ||
            envelope.Nodes.IsDefaultOrEmpty)
        {
            return false;
        }

        ValidationResult validation = SchemaValidator.ValidateNodes(envelope.Nodes);
        if (!validation.IsValid)
        {
            return false;
        }

        clonedNodes = envelope.Nodes
            .Select(n => CloneWithFreshIds(n, offsetMm))
            .ToImmutableArray();
        return true;
    }

    private static SceneNode CloneWithFreshIds(SceneNode node, double offsetMm)
    {
        RectMm shifted = node.BoundsMm with
        {
            X = node.BoundsMm.X + offsetMm,
            Y = node.BoundsMm.Y + offsetMm,
        };

        if (node is GroupNode group)
        {
            ImmutableArray<SceneNode> clonedChildren = group.Children
                .Select(c => CloneWithFreshIds(c, offsetMm))
                .ToImmutableArray();
            return group with { Id = NodeId.New(), BoundsMm = shifted, Children = clonedChildren };
        }

        return node with { Id = NodeId.New(), BoundsMm = shifted };
    }
}
