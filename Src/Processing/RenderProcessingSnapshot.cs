namespace SoundFlow.Processing;

/// <summary>Approximate counters copied independently of rendering; device latency is reported separately.</summary>
public readonly record struct RenderProcessingSnapshot(int QuantumFrames, long DecodedFrames, int RetainedFrames, long Underruns);
