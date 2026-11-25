using System.Collections.Immutable;
using Ziwa.Domain.Collage;
using Ziwa.Domain.Models;
using Ziwa.Domain.PrePress;
using Ziwa.Domain.Primitives;
using Ziwa.Domain.Validation;

namespace Ziwa.Application.Pages;

public static class MultiPageOperations
{
    public static ZiwaDocument AddBlankPage(ZiwaDocument doc, string? customName = null)
    {
        ArgumentNullException.ThrowIfNull(doc);

        PageModel reference = doc.ActivePage;
        string name = string.IsNullOrWhiteSpace(customName)
            ? $"صفحة {doc.Pages.Length + 1}"
            : customName.Trim();

        var blankPage = new PageModel(
            Id: PageId.New(),
            Name: name,
            WidthMm: reference.WidthMm,
            HeightMm: reference.HeightMm,
            BleedMm: reference.BleedMm,
            SafeMarginMm: reference.SafeMarginMm,
            Background: reference.Background,
            Nodes: [],
            CollageLayout: null);

        ImmutableArray<PageModel> nextPages = doc.Pages.Add(blankPage);
        return doc with
        {
            Pages = nextPages,
            ActivePageIndex = nextPages.Length - 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    public static ZiwaDocument DuplicatePage(ZiwaDocument doc, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(doc);

        if (doc.Pages.IsDefaultOrEmpty)
        {
            return doc;
        }

        int sourceIndex = Math.Clamp(pageIndex, 0, doc.Pages.Length - 1);
        PageModel source = doc.Pages[sourceIndex];

        ImmutableArray<SceneNode> clonedNodes = source.Nodes.IsDefault
            ? []
            : [.. source.Nodes.Select(CloneNodeWithNewIds)];

        CollageLayoutModel? clonedCollage = source.CollageLayout is { } collage
            ? collage with
            {
                Slots = collage.Slots.IsDefault
                    ? []
                    : [.. collage.Slots.Select(s => s with { Id = SlotId.New() })],
            }
            : null;

        PageModel clonedPage = source with
        {
            Id = PageId.New(),
            Name = $"{source.Name} (نسخة)",
            Nodes = clonedNodes,
            CollageLayout = clonedCollage,
        };

        int insertIndex = sourceIndex + 1;
        ImmutableArray<PageModel> nextPages = doc.Pages.Insert(insertIndex, clonedPage);
        return doc with
        {
            Pages = nextPages,
            ActivePageIndex = insertIndex,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    public static ZiwaDocument EnsureFrontAndBackPages(ZiwaDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        if (doc.Pages.IsDefaultOrEmpty)
        {
            return doc;
        }

        if (doc.Pages.Length == 1)
        {
            PageModel front = doc.Pages[0] with { Name = "الوجه الأمامي" };
            var back = new PageModel(
                Id: PageId.New(),
                Name: "الوجه الخلفي",
                WidthMm: front.WidthMm,
                HeightMm: front.HeightMm,
                BleedMm: front.BleedMm,
                SafeMarginMm: front.SafeMarginMm,
                Background: front.Background,
                Nodes: [],
                CollageLayout: null);

            return doc with
            {
                Pages = [front, back],
                ActivePageIndex = 1,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
        }

        return ToggleFrontBackPage(doc);
    }

    public static ZiwaDocument ToggleFrontBackPage(ZiwaDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        if (doc.Pages.IsDefaultOrEmpty || doc.Pages.Length < 2)
        {
            return doc;
        }

        int nextIndex = doc.ActivePageIndex == 0 ? 1 : 0;
        return doc with
        {
            ActivePageIndex = nextIndex,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    public static ZiwaDocument SelectPage(ZiwaDocument doc, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(doc);

        if (doc.Pages.IsDefaultOrEmpty)
        {
            return doc;
        }

        int nextIndex = Math.Clamp(pageIndex, 0, doc.Pages.Length - 1);
        if (nextIndex == doc.ActivePageIndex)
        {
            return doc;
        }

        return doc with
        {
            ActivePageIndex = nextIndex,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    public static ZiwaDocument MovePage(ZiwaDocument doc, int fromIndex, int toIndex)
    {
        ArgumentNullException.ThrowIfNull(doc);

        if (doc.Pages.IsDefaultOrEmpty || doc.Pages.Length <= 1)
        {
            return doc;
        }

        int src = Math.Clamp(fromIndex, 0, doc.Pages.Length - 1);
        int dst = Math.Clamp(toIndex, 0, doc.Pages.Length - 1);
        if (src == dst)
        {
            return doc;
        }

        PageId activePageId = doc.ActivePage.Id;
        List<PageModel> reordered = [.. doc.Pages];
        PageModel moving = reordered[src];
        reordered.RemoveAt(src);
        reordered.Insert(dst, moving);

        int nextActive = reordered.FindIndex(p => p.Id == activePageId);
        if (nextActive < 0)
        {
            nextActive = dst;
        }

        return doc with
        {
            Pages = [.. reordered],
            ActivePageIndex = nextActive,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    public static ZiwaDocument DeletePage(ZiwaDocument doc, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(doc);

        if (doc.Pages.IsDefaultOrEmpty || doc.Pages.Length <= 1)
        {
            return doc;
        }

        if (pageIndex < 0 || pageIndex >= doc.Pages.Length)
        {
            return doc;
        }

        ImmutableArray<PageModel> nextPages = doc.Pages.RemoveAt(pageIndex);
        int nextActive = doc.ActivePageIndex > pageIndex
            ? doc.ActivePageIndex - 1
            : doc.ActivePageIndex;
        nextActive = Math.Clamp(nextActive, 0, nextPages.Length - 1);

        return doc with
        {
            Pages = nextPages,
            ActivePageIndex = nextActive,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    public static ZiwaDocument SyncPaperAndBleedAcrossAllPages(
        ZiwaDocument doc,
        double widthMm,
        double heightMm,
        double bleedMm,
        double safeMarginMm)
    {
        ArgumentNullException.ThrowIfNull(doc);

        SchemaValidator.AssertFinitePositive(widthMm, nameof(widthMm));
        SchemaValidator.AssertFinitePositive(heightMm, nameof(heightMm));

        if (widthMm > SchemaValidator.MaxPageDimensionMm)
        {
            throw new ArgumentOutOfRangeException(
                nameof(widthMm),
                widthMm,
                $"Page width must be <= {SchemaValidator.MaxPageDimensionMm} mm.");
        }

        if (heightMm > SchemaValidator.MaxPageDimensionMm)
        {
            throw new ArgumentOutOfRangeException(
                nameof(heightMm),
                heightMm,
                $"Page height must be <= {SchemaValidator.MaxPageDimensionMm} mm.");
        }

        if (!SchemaValidator.IsFiniteNonNegative(bleedMm) || bleedMm > SchemaValidator.MaxBleedMm)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bleedMm),
                bleedMm,
                $"Bleed must be finite within [0, {SchemaValidator.MaxBleedMm}] mm.");
        }

        if (!SchemaValidator.IsFiniteNonNegative(safeMarginMm))
        {
            throw new ArgumentOutOfRangeException(
                nameof(safeMarginMm),
                safeMarginMm,
                "Safe margin must be finite and >= 0 mm.");
        }

        if (doc.Pages.IsDefaultOrEmpty)
        {
            return doc;
        }

        var nextPages = ImmutableArray.CreateBuilder<PageModel>(doc.Pages.Length);
        foreach (PageModel page in doc.Pages)
        {
            CollageLayoutModel? reflowedCollage = page.CollageLayout is { } layout
                ? PhysicalGridBuilder.BuildOrReflowLayout(widthMm, heightMm, layout.Spec, layout)
                : null;

            nextPages.Add(page with
            {
                WidthMm = widthMm,
                HeightMm = heightMm,
                BleedMm = bleedMm,
                SafeMarginMm = safeMarginMm,
                CollageLayout = reflowedCollage,
            });
        }

        return doc with
        {
            Pages = nextPages.ToImmutable(),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    public static ImmutableArray<int> ResolvePageIndices(
        ZiwaDocument doc,
        ExportPageRangeKind rangeKind,
        ImmutableArray<int> specificIndices = default)
    {
        ArgumentNullException.ThrowIfNull(doc);

        if (doc.Pages.IsDefaultOrEmpty)
        {
            return [];
        }

        int fallbackActive = Math.Clamp(doc.ActivePageIndex, 0, doc.Pages.Length - 1);

        switch (rangeKind)
        {
            case ExportPageRangeKind.AllPages:
                return [.. Enumerable.Range(0, doc.Pages.Length)];

            case ExportPageRangeKind.SpecificPages:
                if (specificIndices.IsDefaultOrEmpty)
                {
                    return [fallbackActive];
                }

                ImmutableArray<int> valid =
                [
                    .. specificIndices
                        .Where(i => i >= 0 && i < doc.Pages.Length)
                        .Distinct()
                        .OrderBy(i => i),
                ];

                return valid.IsDefaultOrEmpty ? [fallbackActive] : valid;

            default:
                return [fallbackActive];
        }
    }

    private static SceneNode CloneNodeWithNewIds(SceneNode node)
    {
        if (node is GroupNode group)
        {
            ImmutableArray<SceneNode> clonedChildren = group.Children.IsDefault
                ? []
                : [.. group.Children.Select(CloneNodeWithNewIds)];
            return group with
            {
                Id = NodeId.New(),
                Children = clonedChildren,
            };
        }

        return node with { Id = NodeId.New() };
    }
}
