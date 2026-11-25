using System.Collections.Immutable;
using Ziwa.Domain.Collage;
using Ziwa.Domain.PrePress;
using Ziwa.Domain.Primitives;

namespace Ziwa.Domain.Models;

public sealed record PrintTicketSpec(
    double ExportDpi = 300.0,
    bool IncludeBleed = false,
    bool IncludeCropMarks = false,
    bool IncludeRegistrationMarks = false,
    bool IncludeCutContour = false,
    CmykProfileKind CmykProfile = CmykProfileKind.CoatedFogra39);

public sealed record PageModel(
    PageId Id,
    string Name,
    double WidthMm,
    double HeightMm,
    double BleedMm,
    double SafeMarginMm,
    BackgroundSpec Background,
    ImmutableArray<SceneNode> Nodes,
    CollageLayoutModel? CollageLayout = null);

public sealed record ZiwaDocument(
    DocumentId Id,
    int SchemaVersion,
    string Title,
    EditorWorkflow Mode,
    ImmutableArray<PageModel> Pages,
    int ActivePageIndex,
    PrintTicketSpec PrintSettings,
    DateTimeOffset UpdatedAtUtc)
{
    public PageModel ActivePage => Pages[ActivePageIndex];
}
