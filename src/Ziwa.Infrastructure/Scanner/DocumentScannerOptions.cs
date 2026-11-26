namespace Ziwa.Infrastructure.Scanner;

public sealed record DocumentScannerOptions(
    int MaxDetectionEdgePx = 960,
    int MaxOutputEdgePx = 4096,
    int ProcessingStepDelayMs = 0);
