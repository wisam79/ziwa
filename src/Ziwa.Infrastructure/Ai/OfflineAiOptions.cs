namespace Ziwa.Infrastructure.Ai;

public sealed record OfflineAiOptions(
    string ModelsDirectory,
    string BackgroundModelFileName = "background-remover.onnx",
    string FaceDetectorModelFileName = "face-detector.onnx",
    int MaxInferenceEdgePx = 384,
    int InferenceStepDelayMs = 0)
{
    public string BackgroundModelPath => Path.Combine(ModelsDirectory, BackgroundModelFileName);
    public string FaceDetectorModelPath => Path.Combine(ModelsDirectory, FaceDetectorModelFileName);
}
