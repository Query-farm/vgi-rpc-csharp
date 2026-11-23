namespace QueryFarm.VgiRpc.Errors;

/// <summary>
/// The error kinds of <c>STATE_REF_V1.md</c> §6 and of argument-reference resolution (vgi
/// <c>large-values-3.4.md</c> §5a), as <see cref="RpcException"/> factories. The wire types match
/// the reference's exception class names, and every message is generic: no URLs, tokens, object
/// contents or digests of client data.
/// </summary>
public static class StateRefErrors
{
    /// <summary>Unknown form, unseal or scope failure, bad layout, expired tag, digest mismatch.</summary>
    public const string InvalidStateRefKind = "invalid_state_ref";

    /// <summary>A stored <c>StateRef</c>'s object is gone.</summary>
    public const string StateRefMissingKind = "state_ref_missing";

    /// <summary>A stored object over the server's <c>max_fetch_bytes</c>.</summary>
    public const string StateRefTooLargeKind = "state_ref_too_large";

    /// <summary><c>state_ref()</c> called outside a stream's init request.</summary>
    public const string StateRefMisuseKind = "state_ref_misuse";

    /// <summary>A transient storage failure while fetching an object.</summary>
    public const string StorageUnavailableKind = "storage_unavailable";

    /// <summary>An argument reference that does not open, match, or resolve here.</summary>
    public const string InvalidArgumentsRefKind = "invalid_arguments_ref";

    /// <summary>A fetched argument object whose length or SHA-256 differs from the sealed values.</summary>
    public const string ArgumentsRefIntegrityKind = "arguments_ref_integrity";

    /// <summary>An argument object storage reports absent.</summary>
    public const string ArgumentsRefMissingKind = "arguments_ref_missing";

    /// <summary>The message of <see cref="StateRefMisuse"/>, before the method suffix.</summary>
    public const string MisuseMessage = "state_ref() may only be called while handling a stream's init request";

    /// <summary><c>invalid_state_ref</c> (<c>INVALID_ARGUMENT</c>; answered HTTP 400 like a token failure).</summary>
    public static RpcException InvalidStateRef(string message) =>
        new("InvalidStateRefError", message, errorKind: InvalidStateRefKind, errorCode: ErrorCodes.InvalidArgument);

    /// <summary><c>state_ref_too_large</c> (<c>RESOURCE_EXHAUSTED</c>).</summary>
    public static RpcException StateRefTooLarge(string message) =>
        new("StateRefTooLargeError", message, errorKind: StateRefTooLargeKind, errorCode: ErrorCodes.ResourceExhausted);

    /// <summary><c>state_ref_misuse</c> (<c>FAILED_PRECONDITION</c>): <c>state_ref()</c> called while
    /// handling anything but a stream's init request.</summary>
    /// <param name="protocol">Wire name of the protocol, when known.</param>
    /// <param name="method">Wire name of the method, when known.</param>
    public static RpcException StateRefMisuse(string? protocol, string? method) =>
        new("StateRefMisuseError",
            protocol is null && method is null ? MisuseMessage : $"{MisuseMessage} ({protocol}.{method})",
            errorKind: StateRefMisuseKind, errorCode: ErrorCodes.FailedPrecondition);

    /// <summary><c>storage_unavailable</c> (<c>UNAVAILABLE</c>).</summary>
    public static RpcException StorageUnavailable(string message) =>
        new("StorageUnavailableError", message, errorKind: StorageUnavailableKind, errorCode: ErrorCodes.Unavailable);

    /// <summary><c>invalid_arguments_ref</c> (<c>INVALID_ARGUMENT</c>).</summary>
    public static RpcException InvalidArgumentsRef(string message) =>
        new("InvalidArgumentsRefError", message, errorKind: InvalidArgumentsRefKind, errorCode: ErrorCodes.InvalidArgument);

    /// <summary><c>arguments_ref_integrity</c> (<c>DATA_LOSS</c>).</summary>
    public static RpcException ArgumentsRefIntegrity(string message) =>
        new("ArgumentsRefIntegrityError", message, errorKind: ArgumentsRefIntegrityKind, errorCode: ErrorCodes.DataLoss);

    /// <summary><c>arguments_ref_missing</c> (<c>NOT_FOUND</c>). vgi re-raises it with its
    /// prepared-statement message.</summary>
    public static RpcException ArgumentsRefMissing(string message) =>
        new("ArgumentsRefMissingError", message, errorKind: ArgumentsRefMissingKind, errorCode: ErrorCodes.NotFound);
}
