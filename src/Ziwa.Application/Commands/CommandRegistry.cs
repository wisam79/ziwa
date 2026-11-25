namespace Ziwa.Application.Commands;

public readonly record struct CommandId(string Value);

public sealed record CommandDefinition(
    CommandId Id,
    string TitleAr,
    string ShortcutGesture,
    string Category,
    Func<bool> CanExecute,
    Func<CancellationToken, Task> ExecuteAsync);

public sealed class CommandRegistry
{
    private readonly Dictionary<string, CommandDefinition> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CommandDefinition> _ordered = new();

    public void Register(CommandDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Id.Value))
        {
            throw new ArgumentException("CommandId cannot be empty.", nameof(definition));
        }

        if (_byId.TryGetValue(definition.Id.Value, out CommandDefinition? existing))
        {
            int idx = _ordered.IndexOf(existing);
            if (idx >= 0)
            {
                _ordered[idx] = definition;
            }
        }
        else
        {
            _ordered.Add(definition);
        }

        _byId[definition.Id.Value] = definition;
    }

    public IReadOnlyList<CommandDefinition> GetAll() => _ordered;

    public bool TryGet(CommandId id, out CommandDefinition? definition)
    {
        return _byId.TryGetValue(id.Value, out definition);
    }

    public bool TryGetByShortcut(string shortcutGesture, out CommandDefinition? definition)
    {
        if (string.IsNullOrWhiteSpace(shortcutGesture))
        {
            definition = null;
            return false;
        }

        string normalized = NormalizeGesture(shortcutGesture);
        definition = _ordered.FirstOrDefault(
            c => !string.IsNullOrWhiteSpace(c.ShortcutGesture) &&
                 string.Equals(NormalizeGesture(c.ShortcutGesture), normalized, StringComparison.OrdinalIgnoreCase));
        return definition is not null;
    }

    public async Task<bool> TryExecuteAsync(CommandId id, CancellationToken cancellationToken = default)
    {
        if (!TryGet(id, out CommandDefinition? def) || def is null || !def.CanExecute())
        {
            return false;
        }

        await def.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> TryExecuteShortcutAsync(string shortcutGesture, CancellationToken cancellationToken = default)
    {
        if (!TryGetByShortcut(shortcutGesture, out CommandDefinition? def) || def is null || !def.CanExecute())
        {
            return false;
        }

        await def.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public static string NormalizeGesture(string gesture)
    {
        string[] parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join("+", parts.Select(p => p.ToUpperInvariant()));
    }
}
