namespace Groundwork.Persistence;

public enum SaveSlotStatus
{
    Empty,
    Loaded,
    Committed,
    AlreadyCommitted,
    Busy,
    Conflict,
    Unsupported,
    Corrupt,
    InvalidCheckpoint,
    StorageError,
    Indeterminate,
    Canceled,
}

/// <summary>The application's interpretation of its game payload in a decoded checkpoint.</summary>
public enum SaveGameValidation { Valid, Malformed, Unsupported }

public sealed record SaveRecoveryEvidence(string FileName, string Reason);

public sealed record SaveSlotResult(SaveSlotStatus Status, SaveCheckpoint? Checkpoint = null,
    SaveFrameIdentity? Identity = null, SaveRecoveryEvidence? Recovery = null, Exception? Error = null);

internal enum SaveWriteStage
{
    BeforeWrite, PartialHeader, PartialBody, PartialFooter, BeforeFlush, AfterFlush, BeforePublish, AfterPublish, BeforeAcknowledge, PartialIntent, AfterIntent,
}
