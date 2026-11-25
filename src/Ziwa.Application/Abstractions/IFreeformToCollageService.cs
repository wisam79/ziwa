using Ziwa.Domain.Models;

namespace Ziwa.Application.Abstractions;

public interface IFreeformToCollageService
{
    ZiwaDocument TransferFreeformPageToCollage(
        PageModel sourceFreeformPage,
        ZiwaDocument targetCollageDocument,
        double captureDpi = 300.0);
}
