namespace QueryFarm.VgiRpc.Client;

/// <summary>Stage at which an Iroh transport operation failed.</summary>
public enum IrohErrorStage : uint
{
    Parse = 1, Bind = 2, Resolve = 3, Connect = 4, Alpn = 5, OpenStream = 6,
    Write = 7, Read = 8, Cancel = 9, Close = 10, Internal = 11,
}

/// <summary>Portable category for an Iroh transport failure.</summary>
public enum IrohErrorCategory : uint
{
    InvalidInput = 1, Unsupported = 2, Unavailable = 3, Timeout = 4, Protocol = 5,
    ConnectionReset = 6, Cancelled = 7, Authentication = 8, ResourceExhausted = 9, Internal = 10,
}

/// <summary>Whether request bytes may have reached the worker.</summary>
public enum IrohDispatchCertainty : uint { NotSent = 0, Unknown = 1, Sent = 2 }

/// <summary>A native-Iroh failure with portable retry-safety information.</summary>
public class IrohTransportException : IOException
{
    public IrohTransportException(string message, IrohErrorStage stage, IrohErrorCategory category,
        IrohDispatchCertainty dispatchCertainty, Exception? innerException = null)
        : base(message, innerException)
    {
        Stage = stage;
        Category = category;
        DispatchCertainty = dispatchCertainty;
    }

    public IrohErrorStage Stage { get; }
    public IrohErrorCategory Category { get; }
    public IrohDispatchCertainty DispatchCertainty { get; }
}

/// <summary>A canonical endpoint parse failure.</summary>
public sealed class IrohUriException : FormatException
{
    public IrohUriException(string message) : base(message) { }
    public IrohErrorStage Stage => IrohErrorStage.Parse;
    public IrohErrorCategory Category => IrohErrorCategory.InvalidInput;
    public IrohDispatchCertainty DispatchCertainty => IrohDispatchCertainty.NotSent;
}
