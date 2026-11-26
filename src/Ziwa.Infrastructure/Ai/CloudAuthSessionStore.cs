using Ziwa.Application.Abstractions;

namespace Ziwa.Infrastructure.Ai;

public sealed class CloudAuthSessionStore : ICloudAuthSessionStore
{
    private readonly object _sync = new();
    private CloudAuthSessionSnapshot? _current;

    public CloudAuthSessionStore(CloudAuthSessionSnapshot? initialSession = null)
    {
        _current = initialSession;
    }

    public event Action? SessionChanged;

    public CloudAuthSessionSnapshot? GetCurrentSession()
    {
        lock (_sync)
        {
            return _current;
        }
    }

    public void SetSession(CloudAuthSessionSnapshot? session)
    {
        lock (_sync)
        {
            _current = session;
        }

        SessionChanged?.Invoke();
    }

    public void ClearSession() => SetSession(null);
}
