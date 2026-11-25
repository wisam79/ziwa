using Ziwa.Application.Session;
using Ziwa.Domain.Models;

namespace Ziwa.Application.Services;

public sealed class WorkspaceDocumentService
{
    private readonly DocumentSession _session;

    public WorkspaceDocumentService(ZiwaDocument initialDocument)
        : this(new DocumentSession(initialDocument))
    {
    }

    public WorkspaceDocumentService(DocumentSession session)
    {
        _session = session;
    }

    public DocumentSession Session => _session;
    public ZiwaDocument CurrentDocument => _session.EffectiveDocument;

    public void SetDocument(ZiwaDocument document) => _session.ReplaceDocument(document);
}
