namespace Sanova.Core;

/// <summary>Coarse phases of long-running operations. The UI maps each to a localized label.</summary>
public enum OperationStage
{
    Preparing,
    BackingUpSaves,
    CopyingGameFiles,
    Downloading,
    Extracting,
    Applying,
    Verifying,
    CleaningUp,
    RollingBack,
    Removing,
    Completed,
}

/// <summary>Immutable progress snapshot reported by engine operations.</summary>
public sealed record OperationProgress(
    OperationStage Stage,
    long BytesDone = 0,
    long BytesTotal = 0,
    int ItemsDone = 0,
    int ItemsTotal = 0,
    string? CurrentItem = null)
{
    /// <summary>Fraction 0..1 based on bytes when known, otherwise items; null when indeterminate.</summary>
    public double? Fraction
    {
        get
        {
            if (BytesTotal > 0) return Math.Clamp((double)BytesDone / BytesTotal, 0, 1);
            if (ItemsTotal > 0) return Math.Clamp((double)ItemsDone / ItemsTotal, 0, 1);
            return null;
        }
    }
}

/// <summary>Synchronous progress sink that does not post to a synchronization context.</summary>
public sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
