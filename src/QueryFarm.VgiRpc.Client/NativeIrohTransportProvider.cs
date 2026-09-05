using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using QueryFarm.VgiRpc.Transport;

namespace QueryFarm.VgiRpc.Client;

/// <summary>P/Invoke adapter for the versioned <c>vgi_iroh_cabi</c> native library.</summary>
public sealed class NativeIrohTransportProvider : IIrohTransportProvider, IIrohHttpTransportProvider, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<EndpointConfiguration, EndpointSafeHandle> _endpoints = [];
    // A generated identity must survive endpoint recreation with different relay/deadline policy.
    // It is private, never returned, and lives only for the process lifetime.
    private static readonly byte[] s_processEphemeralSecret = RandomNumberGenerator.GetBytes(32);
    private bool _disposed;

    /// <summary>Process-shared provider whose ephemeral identity is stable across connections.</summary>
    public static NativeIrohTransportProvider Shared { get; } = new();

    public static bool IsAvailable()
    {
        try { return Native.AbiVersion() == Native.AbiVersionExpected; }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    /// <summary>Return this provider's stable local EndpointId, creating it if necessary.</summary>
    public string GetLocalEndpointId(IrohConnectOptions? options = null)
    {
        options ??= new IrohConnectOptions();
        options.Validate();
        var endpoint = GetOrCreateEndpoint(options);
        var bytes = new byte[65];
        var error = Native.Error.Create();
        ThrowIfError(Native.EndpointId(endpoint, bytes, (nuint)bytes.Length, out var required, ref error),
            "endpoint ID", error);
        var length = Array.IndexOf(bytes, (byte)0);
        if (length != 64 || required > (nuint)bytes.Length)
            throw new IrohTransportException("Native Iroh endpoint returned a non-canonical EndpointId.",
                IrohErrorStage.Bind, IrohErrorCategory.Protocol, IrohDispatchCertainty.NotSent);
        return System.Text.Encoding.ASCII.GetString(bytes, 0, length);
    }

    public ValueTask<IRpcTransport> OpenArrowMuxAsync(
        IrohEndpoint endpoint, IrohConnectOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromException<IRpcTransport>(Cancelled(cancellationToken, IrohDispatchCertainty.NotSent));
        // Consume identity material before returning so callers may clear their mutable key buffer.
        var nativeEndpoint = GetOrCreateEndpoint(options);
        var remoteRelay = options.RemoteRelayUrl;
        var directAddresses = options.DirectAddresses.ToArray();
        bool endpointReference = false;
        nativeEndpoint.DangerousAddRef(ref endpointReference);
        // The ABI call blocks, but polls its cancellation callback. Do not pass the token to
        // Task.Run: a pre-cancelled task would bypass the SDK's structured transport error.
        try
        {
            return new ValueTask<IRpcTransport>(Task.Run(() =>
                Open(endpoint, nativeEndpoint, endpointReference, remoteRelay, directAddresses, cancellationToken)));
        }
        catch
        {
            if (endpointReference) nativeEndpoint.DangerousRelease();
            throw;
        }
    }

    public ValueTask<IrohHttpResponse> SendHttpAsync(
        IrohEndpoint endpoint,
        IrohHttpRequest request,
        IrohConnectOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (endpoint.Scheme != "httpi")
            return ValueTask.FromException<IrohHttpResponse>(new IrohTransportException(
                "HTTP-over-Iroh requires an httpi:// endpoint.", IrohErrorStage.Parse,
                IrohErrorCategory.InvalidInput, IrohDispatchCertainty.NotSent));
        if (request.MaxResponseBytes <= 0 || request.MaxResponseHeaderBytes <= 0)
            return ValueTask.FromException<IrohHttpResponse>(new IrohTransportException(
                "HTTP-over-Iroh response limits must be positive.", IrohErrorStage.Parse,
                IrohErrorCategory.InvalidInput, IrohDispatchCertainty.NotSent));
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromException<IrohHttpResponse>(Cancelled(
                cancellationToken, IrohDispatchCertainty.NotSent));

        var nativeEndpoint = GetOrCreateEndpoint(options);
        bool endpointReference = false;
        nativeEndpoint.DangerousAddRef(ref endpointReference);
        try
        {
            return new ValueTask<IrohHttpResponse>(Task.Run(() => OpenHttp(
                endpoint, request, options, nativeEndpoint, endpointReference, cancellationToken)));
        }
        catch
        {
            if (endpointReference) nativeEndpoint.DangerousRelease();
            throw;
        }
    }

    private static IRpcTransport Open(IrohEndpoint endpoint, EndpointSafeHandle nativeEndpoint,
        bool endpointReference, string? remoteRelay, IReadOnlyList<string> directAddresses, CancellationToken token)
    {
        try
        {
            if (token.IsCancellationRequested) throw Cancelled(token, IrohDispatchCertainty.NotSent);
            using var remote = new NativeRemote(endpoint.EndpointId, remoteRelay, directAddresses);
            using var cancellation = new NativeCancellation(token);
            var error = Native.Error.Create();
            var remoteValue = remote.Value;
            ThrowIfError(Native.StreamOpenCancellable(nativeEndpoint, in remoteValue,
                NativeCancellation.Check, cancellation.UserData, out var streamPointer, ref error), "stream open", error);
            var stream = new StreamSafeHandle(streamPointer);
            try
            {
                var result = new NativeIrohTransport(new NativeState(nativeEndpoint, endpointReference, stream));
                endpointReference = false;
                return result;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch (Exception failure) when (failure is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw MissingNativeLibrary(failure);
        }
        finally
        {
            if (endpointReference) nativeEndpoint.DangerousRelease();
        }
    }

    private static IrohHttpResponse OpenHttp(
        IrohEndpoint endpoint,
        IrohHttpRequest request,
        IrohConnectOptions options,
        EndpointSafeHandle nativeEndpoint,
        bool endpointReference,
        CancellationToken token)
    {
        HttpResponseSafeHandle? response = null;
        try
        {
            if (token.IsCancellationRequested) throw Cancelled(token, IrohDispatchCertainty.NotSent);
            using var remote = new NativeRemote(endpoint.EndpointId, options.RemoteRelayUrl,
                options.DirectAddresses.ToArray());
            using var nativeRequest = new NativeHttpRequest(request);
            using var cancellation = new NativeCancellation(token);
            var error = Native.Error.Create();
            var remoteValue = remote.Value;
            var requestValue = nativeRequest.Value;
            ThrowIfError(Native.HttpRequestStartCancellable(nativeEndpoint, in remoteValue,
                in requestValue, NativeCancellation.Check, cancellation.UserData,
                out var responsePointer, ref error), "HTTP request", error);
            response = new HttpResponseSafeHandle(responsePointer);

            var remoteId = ReadHttpRemoteId(response);
            if (!string.Equals(remoteId, endpoint.EndpointId, StringComparison.Ordinal))
                throw new IrohTransportException(
                    "Iroh HTTP authenticated peer identity did not match the requested EndpointId.",
                    IrohErrorStage.Read, IrohErrorCategory.Authentication, IrohDispatchCertainty.Sent);
            var status = Native.HttpResponseStatus(response);
            if (status is < 100 or > 999)
                throw new IrohTransportException("Iroh HTTP response returned an invalid status code.",
                    IrohErrorStage.Read, IrohErrorCategory.Protocol, IrohDispatchCertainty.Sent);
            var headers = ReadHttpHeaders(response, request.MaxResponseHeaderBytes);
            var body = new NativeHttpResponseStream(nativeEndpoint, endpointReference, response,
                request.MaxResponseBytes, options.IoTimeout);
            endpointReference = false;
            response = null;
            return new IrohHttpResponse(status, headers, body, remoteId);
        }
        catch (Exception failure) when (failure is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw MissingNativeLibrary(failure);
        }
        finally
        {
            response?.Dispose();
            if (endpointReference) nativeEndpoint.DangerousRelease();
        }
    }

    private static string ReadHttpRemoteId(HttpResponseSafeHandle response)
    {
        var bytes = new byte[65];
        var error = Native.Error.Create();
        ThrowIfError(Native.HttpResponseRemoteId(response, bytes, (nuint)bytes.Length,
            out var required, ref error), "HTTP remote identity", error);
        var length = Array.IndexOf(bytes, (byte)0);
        if (length != 64 || required != 65)
            throw new IrohTransportException("Iroh HTTP response returned a non-canonical EndpointId.",
                IrohErrorStage.Read, IrohErrorCategory.Protocol, IrohDispatchCertainty.Sent);
        return System.Text.Encoding.ASCII.GetString(bytes, 0, length);
    }

    private static IReadOnlyList<KeyValuePair<string, string>> ReadHttpHeaders(
        HttpResponseSafeHandle response, long limit)
    {
        var count = Native.HttpResponseHeaderCount(response);
        if (count > 1024 || count > (nuint)limit)
            throw new IrohTransportException("Iroh HTTP response contains too many headers.",
                IrohErrorStage.Read, IrohErrorCategory.ResourceExhausted, IrohDispatchCertainty.Sent);
        var result = new List<KeyValuePair<string, string>>(checked((int)count));
        long total = 0;
        for (nuint index = 0; index < count; index++)
        {
            var error = Native.Error.Create();
            ThrowIfError(Native.HttpResponseHeader(response, index, null, 0, out var nameRequired,
                null, 0, out var valueRequired, ref error), "HTTP response header", error);
            if ((ulong)nameRequired > (ulong)Math.Max(0, limit - total)
                || (ulong)valueRequired > (ulong)Math.Max(0, limit - total - checked((long)nameRequired)))
                throw new IrohTransportException("Iroh HTTP response headers exceed the configured limit.",
                    IrohErrorStage.Read, IrohErrorCategory.ResourceExhausted, IrohDispatchCertainty.Sent);
            var name = new byte[checked((int)nameRequired)];
            var value = new byte[checked((int)valueRequired)];
            error = Native.Error.Create();
            ThrowIfError(Native.HttpResponseHeader(response, index, name, nameRequired, out _,
                value, valueRequired, out _, ref error), "HTTP response header", error);
            if (name.Length == 0 || name.Any(character => !IsHttpToken(character)))
                throw new IrohTransportException("Iroh HTTP response contains an invalid header name.",
                    IrohErrorStage.Read, IrohErrorCategory.Protocol, IrohDispatchCertainty.Sent);
            if (value.Any(character => character is 0 or 10 or 13))
                throw new IrohTransportException("Iroh HTTP response contains an invalid header value.",
                    IrohErrorStage.Read, IrohErrorCategory.Protocol, IrohDispatchCertainty.Sent);
            total += name.Length + value.Length;
            result.Add(new(System.Text.Encoding.ASCII.GetString(name),
                System.Text.Encoding.Latin1.GetString(value)));
        }
        return result;
    }

    private static bool IsHttpToken(byte value) =>
        value is >= 0x21 and <= 0x7e && value is not (byte)'(' and not (byte)')'
            and not (byte)'<' and not (byte)'>' and not (byte)'@' and not (byte)','
            and not (byte)';' and not (byte)':' and not (byte)'\\' and not (byte)'"'
            and not (byte)'/' and not (byte)'[' and not (byte)']' and not (byte)'?'
            and not (byte)'=' and not (byte)'{' and not (byte)'}';

    private EndpointSafeHandle GetOrCreateEndpoint(IrohConnectOptions options)
    {
        var effectiveSecret = options.SecretKey ?? s_processEphemeralSecret;
        var requested = EndpointConfiguration.From(options, effectiveSecret);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_endpoints.TryGetValue(requested, out var existing)) return existing;
            try
            {
                if (Native.AbiVersion() != Native.AbiVersionExpected)
                    throw new IrohTransportException("The loaded vgi_iroh_cabi ABI version does not match this SDK.",
                        IrohErrorStage.Bind, IrohErrorCategory.Unsupported, IrohDispatchCertainty.NotSent);
                using var config = new NativeEndpointConfiguration(options, effectiveSecret);
                var error = Native.Error.Create();
                var configValue = config.Value;
                ThrowIfError(Native.EndpointCreate(in configValue, out var pointer, ref error), "endpoint creation", error);
                var endpoint = new EndpointSafeHandle(pointer);
                _endpoints.Add(requested, endpoint);
                return endpoint;
            }
            catch (Exception failure) when (failure is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                throw MissingNativeLibrary(failure);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var endpoint in _endpoints.Values) endpoint.Dispose();
            _endpoints.Clear();
        }
    }

    private static IrohTransportException Cancelled(CancellationToken token, IrohDispatchCertainty certainty) =>
        new("Iroh operation cancelled.", IrohErrorStage.Cancel, IrohErrorCategory.Cancelled, certainty,
            new OperationCanceledException(token));

    private static IrohTransportException MissingNativeLibrary(Exception failure) => new(
        "iroh:// requires a version-matched vgi_iroh_cabi native library bundled for this runtime.",
        IrohErrorStage.Bind, IrohErrorCategory.Unsupported, IrohDispatchCertainty.NotSent, failure);

    private static void ThrowIfError(int result, string operation, Native.Error error)
    {
        if (result == 0) return;
        var stage = Enum.IsDefined((IrohErrorStage)error.Stage) ? (IrohErrorStage)error.Stage : IrohErrorStage.Internal;
        var category = Enum.IsDefined((IrohErrorCategory)error.Category) ? (IrohErrorCategory)error.Category : IrohErrorCategory.Internal;
        var certainty = Enum.IsDefined((IrohDispatchCertainty)error.DispatchCertainty)
            ? (IrohDispatchCertainty)error.DispatchCertainty : IrohDispatchCertainty.Unknown;
        throw new IrohTransportException($"Iroh {operation} failed: {error.GetMessage()}", stage, category, certainty);
    }

    private sealed record EndpointConfiguration(string SecretFingerprint, bool NoRelay,
        string RelayUrls, long ConnectTimeoutTicks, long IoTimeoutTicks)
    {
        internal static EndpointConfiguration From(IrohConnectOptions options, byte[] effectiveSecret)
        {
            var fingerprint = Convert.ToHexString(SHA256.HashData(effectiveSecret));
            return new(fingerprint, options.NoRelay, string.Join("\n", options.RelayUrls),
                options.ConnectTimeout.Ticks, options.IoTimeout.Ticks);
        }
    }

    private sealed class NativeAllocation : IDisposable
    {
        internal IntPtr Pointer { get; private set; }
        private int Length { get; }

        internal NativeAllocation(string value)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            Length = bytes.Length + 1;
            Pointer = Marshal.AllocHGlobal(Length);
            Marshal.Copy(bytes, 0, Pointer, bytes.Length);
            Marshal.WriteByte(Pointer, bytes.Length, 0);
            CryptographicOperations.ZeroMemory(bytes);
        }

        internal NativeAllocation(byte[] secret)
        {
            Length = secret.Length * 2 + 1;
            Pointer = Marshal.AllocHGlobal(Length);
            ReadOnlySpan<byte> hex = "0123456789abcdef"u8;
            for (var index = 0; index < secret.Length; index++)
            {
                Marshal.WriteByte(Pointer, index * 2, hex[secret[index] >> 4]);
                Marshal.WriteByte(Pointer, index * 2 + 1, hex[secret[index] & 0xf]);
            }
            Marshal.WriteByte(Pointer, Length - 1, 0);
        }

        public void Dispose()
        {
            if (Pointer == IntPtr.Zero) return;
            for (var index = 0; index < Length; index++) Marshal.WriteByte(Pointer, index, 0);
            Marshal.FreeHGlobal(Pointer);
            Pointer = IntPtr.Zero;
        }
    }

    private sealed class NativePointerArray : IDisposable
    {
        private readonly List<NativeAllocation> _strings = [];
        internal IntPtr Pointer { get; private set; }
        internal nuint Count => (nuint)_strings.Count;

        internal NativePointerArray(IEnumerable<string> values)
        {
            foreach (var value in values) _strings.Add(new NativeAllocation(value));
            if (_strings.Count == 0) return;
            Pointer = Marshal.AllocHGlobal(IntPtr.Size * _strings.Count);
            Marshal.Copy(_strings.Select(value => value.Pointer).ToArray(), 0, Pointer, _strings.Count);
        }

        public void Dispose()
        {
            if (Pointer != IntPtr.Zero)
            {
                for (var index = 0; index < IntPtr.Size * _strings.Count; index++) Marshal.WriteByte(Pointer, index, 0);
                Marshal.FreeHGlobal(Pointer);
                Pointer = IntPtr.Zero;
            }
            foreach (var value in _strings) value.Dispose();
        }
    }

    private sealed class NativeEndpointConfiguration : IDisposable
    {
        private readonly NativeAllocation _secret;
        private readonly NativePointerArray _relays;
        internal Native.EndpointConfig Value { get; }

        internal NativeEndpointConfiguration(IrohConnectOptions options, byte[] effectiveSecret)
        {
            _secret = new NativeAllocation(effectiveSecret);
            _relays = new NativePointerArray(options.RelayUrls);
            Value = new Native.EndpointConfig
            {
                AbiVersion = Native.AbiVersionExpected,
                SecretKey = _secret.Pointer,
                RelayMode = options.NoRelay ? 1u : _relays.Count == 0 ? 0u : 2u,
                RelayUrls = _relays.Pointer,
                RelayUrlCount = _relays.Count,
                ConnectTimeoutMs = checked((ulong)options.ConnectTimeout.TotalMilliseconds),
                IoTimeoutMs = checked((ulong)options.IoTimeout.TotalMilliseconds),
            };
        }
        public void Dispose() { _secret.Dispose(); _relays.Dispose(); }
    }

    private sealed class NativeRemote : IDisposable
    {
        private readonly NativeAllocation _endpointId;
        private readonly NativeAllocation? _relay;
        private readonly NativePointerArray _directAddresses;
        internal Native.Remote Value { get; }

        internal NativeRemote(string endpointId, string? relay, IReadOnlyList<string> directAddresses)
        {
            _endpointId = new NativeAllocation(endpointId);
            _relay = relay is null ? null : new NativeAllocation(relay);
            _directAddresses = new NativePointerArray(directAddresses);
            Value = new Native.Remote
            {
                EndpointId = _endpointId.Pointer,
                RelayUrl = _relay?.Pointer ?? IntPtr.Zero,
                DirectAddresses = _directAddresses.Pointer,
                DirectAddressCount = _directAddresses.Count,
            };
        }
        public void Dispose() { _endpointId.Dispose(); _relay?.Dispose(); _directAddresses.Dispose(); }
    }

    private sealed class NativeBytes : IDisposable
    {
        internal IntPtr Pointer { get; private set; }
        internal nuint Length { get; }

        internal NativeBytes(byte[] value)
        {
            Length = (nuint)value.Length;
            if (value.Length == 0) return;
            Pointer = Marshal.AllocHGlobal(value.Length);
            Marshal.Copy(value, 0, Pointer, value.Length);
        }

        public void Dispose()
        {
            if (Pointer == IntPtr.Zero) return;
            Marshal.FreeHGlobal(Pointer);
            Pointer = IntPtr.Zero;
        }
    }

    private sealed class NativeHttpRequest : IDisposable
    {
        private readonly NativeAllocation _method;
        private readonly NativeAllocation _path;
        private readonly List<NativeBytes> _headerBytes = [];
        private readonly NativeBytes _body;
        private IntPtr _headers;
        internal Native.HttpRequest Value { get; }

        internal NativeHttpRequest(IrohHttpRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Method)
                || request.Method.Any(character => character > 0x7f || !IsHttpToken((byte)character)))
                throw new IrohTransportException("Iroh HTTP request method is invalid.", IrohErrorStage.Parse,
                    IrohErrorCategory.InvalidInput, IrohDispatchCertainty.NotSent);
            if (string.IsNullOrEmpty(request.Path) || request.Path[0] != '/' || request.Path.Any(character => character <= 0x1f || character == 0x7f))
                throw new IrohTransportException("Iroh HTTP request path is invalid.", IrohErrorStage.Parse,
                    IrohErrorCategory.InvalidInput, IrohDispatchCertainty.NotSent);
            if (request.Headers.Count > 1024)
                throw new IrohTransportException("Iroh HTTP request contains too many headers.", IrohErrorStage.Parse,
                    IrohErrorCategory.ResourceExhausted, IrohDispatchCertainty.NotSent);

            var encodedHeaders = new List<(byte[] Name, byte[] Value)>(request.Headers.Count);
            foreach (var (name, value) in request.Headers)
            {
                var nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
                if (nameBytes.Length == 0 || name.Any(character => character > 0x7f)
                    || nameBytes.Any(character => !IsHttpToken(character)))
                    throw new IrohTransportException("Iroh HTTP request contains an invalid header name.",
                        IrohErrorStage.Parse, IrohErrorCategory.InvalidInput, IrohDispatchCertainty.NotSent);
                var valueBytes = System.Text.Encoding.Latin1.GetBytes(value);
                if (value.Any(character => character > 0xff || character is '\0' or '\r' or '\n'))
                    throw new IrohTransportException("Iroh HTTP request contains an invalid header value.",
                        IrohErrorStage.Parse, IrohErrorCategory.InvalidInput, IrohDispatchCertainty.NotSent);
                encodedHeaders.Add((nameBytes, valueBytes));
            }

            _method = new NativeAllocation(request.Method);
            _path = new NativeAllocation(request.Path);
            _body = new NativeBytes(request.Body);
            try
            {
                if (encodedHeaders.Count != 0)
                {
                    var nativeSize = Marshal.SizeOf<Native.Header>();
                    _headers = Marshal.AllocHGlobal(checked(nativeSize * encodedHeaders.Count));
                    for (var index = 0; index < encodedHeaders.Count; index++)
                    {
                        var (nameBytes, valueBytes) = encodedHeaders[index];
                        var nativeName = new NativeBytes(nameBytes);
                        var nativeValue = new NativeBytes(valueBytes);
                        _headerBytes.Add(nativeName);
                        _headerBytes.Add(nativeValue);
                        Marshal.StructureToPtr(new Native.Header
                        {
                            Name = nativeName.Pointer,
                            NameLength = nativeName.Length,
                            Value = nativeValue.Pointer,
                            ValueLength = nativeValue.Length,
                        }, IntPtr.Add(_headers, checked(index * nativeSize)), false);
                    }
                }
                Value = new Native.HttpRequest
                {
                    Method = _method.Pointer,
                    Path = _path.Pointer,
                    Headers = _headers,
                    HeaderCount = (nuint)request.Headers.Count,
                    Body = _body.Pointer,
                    BodyLength = _body.Length,
                };
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_headers != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_headers);
                _headers = IntPtr.Zero;
            }
            foreach (var value in _headerBytes) value.Dispose();
            _body.Dispose();
            _path.Dispose();
            _method.Dispose();
        }
    }

    private sealed class NativeCancellation : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate byte Callback(IntPtr userData);
        internal static readonly Callback Check = IsCancellationRequested;
        private GCHandle _handle;
        internal IntPtr UserData => GCHandle.ToIntPtr(_handle);
        internal NativeCancellation(CancellationToken token) => _handle = GCHandle.Alloc(token);
        private static byte IsCancellationRequested(IntPtr value)
        {
            try { return ((CancellationToken)GCHandle.FromIntPtr(value).Target!).IsCancellationRequested ? (byte)1 : (byte)0; }
            catch { return 1; }
        }
        public void Dispose() { if (_handle.IsAllocated) _handle.Free(); }
    }

    private sealed class EndpointSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal EndpointSafeHandle(IntPtr value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() { Native.EndpointCancel(handle); Native.EndpointFree(handle); return true; }
    }

    private sealed class StreamSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal StreamSafeHandle(IntPtr value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() { Native.StreamCancel(handle); Native.StreamFree(handle); return true; }
    }

    private sealed class HttpResponseSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal HttpResponseSafeHandle(IntPtr value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle()
        {
            Native.HttpResponseCancel(handle);
            Native.HttpResponseFree(handle);
            return true;
        }
    }

    private sealed class NativeState(EndpointSafeHandle endpoint, bool endpointReference, StreamSafeHandle stream) : IDisposable
    {
        private int _disposed;
        private int _finished;
        internal StreamSafeHandle Stream { get; } = stream;
        internal void Finish()
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0) return;
            var error = Native.Error.Create();
            ThrowIfError(Native.StreamFinish(Stream, ref error), "finish", error);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { Finish(); }
            finally
            {
                Stream.Dispose();
                if (endpointReference) endpoint.DangerousRelease();
            }
        }
    }

    private sealed class NativeIrohTransport(NativeState state) : IRpcTransport, IDisposable
    {
        private Stream? _input;
        private Stream? _output;
        public Stream Input => _input ??= new NativeInputStream(state);
        public Stream Output => _output ??= new NativeOutputStream(state);
        public void Dispose() => state.Dispose();
    }

    private sealed class NativeInputStream(NativeState state) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBuffer(buffer, offset, count);
            if (count == 0) return 0;
            var target = offset == 0 && count == buffer.Length ? buffer : new byte[count];
            var error = Native.Error.Create();
            ThrowIfError(Native.StreamRead(state.Stream, target, (nuint)count, out var read, ref error), "read", error);
            var actual = checked((int)read);
            if (!ReferenceEquals(target, buffer)) Buffer.BlockCopy(target, 0, buffer, offset, actual);
            return actual;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (buffer.Length == 0) return 0;
            var temporary = new byte[buffer.Length];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var error = Native.Error.Create();
                ThrowIfError(Native.StreamReadTimeout(state.Stream, temporary, (nuint)temporary.Length, 50,
                    out var read, out var timedOut, ref error), "read", error);
                if (timedOut != 0) { await Task.Yield(); continue; }
                temporary.AsMemory(0, checked((int)read)).CopyTo(buffer);
                return checked((int)read);
            }
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NativeOutputStream(NativeState state) : Stream
    {
        private bool _finished;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_finished) { state.Finish(); _finished = true; }
            base.Dispose(disposing);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            ValidateBuffer(buffer, offset, count);
            var source = offset == 0 && count == buffer.Length ? buffer : buffer.AsSpan(offset, count).ToArray();
            var error = Native.Error.Create();
            ThrowIfError(Native.StreamWrite(state.Stream, source, (nuint)count, ref error), "write", error);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var source = buffer.ToArray();
            await Task.Run(() =>
            {
                using var cancellation = new NativeCancellation(token);
                var error = Native.Error.Create();
                ThrowIfError(Native.StreamWriteCancellable(state.Stream, source, (nuint)source.Length,
                    NativeCancellation.Check, cancellation.UserData, ref error), "write", error);
            }).ConfigureAwait(false);
        }
    }

    private sealed class NativeHttpResponseStream(
        EndpointSafeHandle endpoint,
        bool endpointReference,
        HttpResponseSafeHandle response,
        long limit,
        TimeSpan idleTimeout) : Stream
    {
        private long _read;
        private int _disposed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBuffer(buffer, offset, count);
            if (count == 0) return 0;
            var target = offset == 0 && count == buffer.Length ? buffer : new byte[count];
            var error = Native.Error.Create();
            ThrowIfError(Native.HttpResponseRead(response, target, (nuint)count, out var read, ref error),
                "HTTP response read", error);
            var actual = checked((int)read);
            EnforceLimit(actual);
            if (!ReferenceEquals(target, buffer)) Buffer.BlockCopy(target, 0, buffer, offset, actual);
            return actual;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (buffer.Length == 0) return 0;
            var target = new byte[buffer.Length];
            var lastProgress = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                if (token.IsCancellationRequested)
                {
                    Native.HttpResponseCancel(response.DangerousGetHandle());
                    throw Cancelled(token, IrohDispatchCertainty.Sent);
                }
                var error = Native.Error.Create();
                ThrowIfError(Native.HttpResponseReadTimeout(response, target, (nuint)target.Length, 50,
                    out var read, out var timedOut, ref error), "HTTP response read", error);
                if (timedOut != 0)
                {
                    if (lastProgress.Elapsed >= idleTimeout)
                    {
                        Native.HttpResponseCancel(response.DangerousGetHandle());
                        throw new IrohTransportException("Iroh HTTP response exceeded its idle timeout.",
                            IrohErrorStage.Read, IrohErrorCategory.Timeout, IrohDispatchCertainty.Sent);
                    }
                    await Task.Yield();
                    continue;
                }
                var actual = checked((int)read);
                EnforceLimit(actual);
                target.AsMemory(0, actual).CopyTo(buffer);
                return actual;
            }
        }

        private void EnforceLimit(int count)
        {
            if (count > limit - _read)
            {
                Native.HttpResponseCancel(response.DangerousGetHandle());
                throw new IrohTransportException("Iroh HTTP response body exceeds the configured limit.",
                    IrohErrorStage.Read, IrohErrorCategory.ResourceExhausted, IrohDispatchCertainty.Sent);
            }
            _read += count;
        }

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                response.Dispose();
                if (endpointReference) endpoint.DangerousRelease();
            }
            if (disposing) GC.SuppressFinalize(this);
            base.Dispose(disposing);
        }

        ~NativeHttpResponseStream() => Dispose(false);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static void ValidateBuffer(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count) throw new ArgumentException("Range exceeds buffer.");
    }

    private static class Native
    {
        internal const uint AbiVersionExpected = 1;
        private const string Library = "vgi_iroh_cabi";
        [StructLayout(LayoutKind.Sequential)]
        internal struct EndpointConfig
        {
            internal uint AbiVersion; internal IntPtr SecretKey; internal uint RelayMode;
            internal IntPtr RelayUrls; internal nuint RelayUrlCount; internal ulong ConnectTimeoutMs; internal ulong IoTimeoutMs;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Remote
        {
            internal IntPtr EndpointId; internal IntPtr RelayUrl; internal IntPtr DirectAddresses; internal nuint DirectAddressCount;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Header
        {
            internal IntPtr Name; internal nuint NameLength; internal IntPtr Value; internal nuint ValueLength;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct HttpRequest
        {
            internal IntPtr Method; internal IntPtr Path; internal IntPtr Headers; internal nuint HeaderCount;
            internal IntPtr Body; internal nuint BodyLength;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Error
        {
            internal uint Stage; internal uint Category; internal uint DispatchCertainty;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512, ArraySubType = UnmanagedType.I1)] internal byte[] Message;
            internal static Error Create() => new() { Message = new byte[512] };
            internal readonly string GetMessage()
            {
                var length = Array.IndexOf(Message, (byte)0);
                return System.Text.Encoding.UTF8.GetString(Message, 0, length < 0 ? Message.Length : length);
            }
        }
        [DllImport(Library, EntryPoint = "vgi_iroh_abi_version", CallingConvention = CallingConvention.Cdecl)] internal static extern uint AbiVersion();
        [DllImport(Library, EntryPoint = "vgi_iroh_endpoint_create", CallingConvention = CallingConvention.Cdecl)] internal static extern int EndpointCreate(in EndpointConfig config, out IntPtr endpoint, ref Error error);
        [DllImport(Library, EntryPoint = "vgi_iroh_endpoint_cancel", CallingConvention = CallingConvention.Cdecl)] internal static extern void EndpointCancel(IntPtr endpoint);
        [DllImport(Library, EntryPoint = "vgi_iroh_endpoint_free", CallingConvention = CallingConvention.Cdecl)] internal static extern void EndpointFree(IntPtr endpoint);
        [DllImport(Library, EntryPoint = "vgi_iroh_endpoint_id", CallingConvention = CallingConvention.Cdecl)] internal static extern int EndpointId(EndpointSafeHandle endpoint, [Out] byte[] buffer, nuint capacity, out nuint required, ref Error error);
        [DllImport(Library, EntryPoint = "vgi_iroh_stream_open_cancellable", CallingConvention = CallingConvention.Cdecl)] internal static extern int StreamOpenCancellable(EndpointSafeHandle endpoint, in Remote remote, NativeCancellation.Callback callback, IntPtr userData, out IntPtr stream, ref Error error);
        [DllImport(Library, EntryPoint = "vgi_iroh_stream_read", CallingConvention = CallingConvention.Cdecl)] internal static extern int StreamRead(StreamSafeHandle stream, [Out] byte[] buffer, nuint capacity, out nuint read, ref Error error);
        [DllImport(Library, EntryPoint = "vgi_iroh_stream_read_timeout", CallingConvention = CallingConvention.Cdecl)] internal static extern int StreamReadTimeout(StreamSafeHandle stream, [Out] byte[] buffer, nuint capacity, ulong timeoutMs, out nuint read, out byte timedOut, ref Error error);
        [DllImport(Library, EntryPoint = "vgi_iroh_stream_write", CallingConvention = CallingConvention.Cdecl)] internal static extern int StreamWrite(StreamSafeHandle stream, byte[] buffer, nuint length, ref Error error);
        [DllImport(Library, EntryPoint = "vgi_iroh_stream_write_cancellable", CallingConvention = CallingConvention.Cdecl)] internal static extern int StreamWriteCancellable(StreamSafeHandle stream, byte[] buffer, nuint length, NativeCancellation.Callback callback, IntPtr userData, ref Error error);
        [DllImport(Library, EntryPoint = "vgi_iroh_stream_finish", CallingConvention = CallingConvention.Cdecl)] internal static extern int StreamFinish(StreamSafeHandle stream, ref Error error);
        [DllImport(Library, EntryPoint = "vgi_iroh_stream_cancel", CallingConvention = CallingConvention.Cdecl)] internal static extern void StreamCancel(IntPtr stream);
        [DllImport(Library, EntryPoint = "vgi_iroh_stream_free", CallingConvention = CallingConvention.Cdecl)] internal static extern void StreamFree(IntPtr stream);
        [DllImport(Library, EntryPoint = "vgi_iroh_http_request_start_cancellable", CallingConvention = CallingConvention.Cdecl)] internal static extern int HttpRequestStartCancellable(EndpointSafeHandle endpoint, in Remote remote, in HttpRequest request, NativeCancellation.Callback callback, IntPtr userData, out IntPtr response, ref Error error);
        [DllImport(Library, EntryPoint = "vgi_iroh_http_response_status", CallingConvention = CallingConvention.Cdecl)] internal static extern ushort HttpResponseStatus(HttpResponseSafeHandle response);
        [DllImport(Library, EntryPoint = "vgi_iroh_http_response_remote_id", CallingConvention = CallingConvention.Cdecl)] internal static extern int HttpResponseRemoteId(HttpResponseSafeHandle response, [Out] byte[] buffer, nuint capacity, out nuint required, ref Error error);
        [DllImport(Library, EntryPoint = "vgi_iroh_http_response_header_count", CallingConvention = CallingConvention.Cdecl)] internal static extern nuint HttpResponseHeaderCount(HttpResponseSafeHandle response);
        [DllImport(Library, EntryPoint = "vgi_iroh_http_response_header", CallingConvention = CallingConvention.Cdecl)] internal static extern int HttpResponseHeader(HttpResponseSafeHandle response, nuint index, [Out] byte[]? name, nuint nameCapacity, out nuint nameRequired, [Out] byte[]? value, nuint valueCapacity, out nuint valueRequired, ref Error error);
        [DllImport(Library, EntryPoint = "vgi_iroh_http_response_read", CallingConvention = CallingConvention.Cdecl)] internal static extern int HttpResponseRead(HttpResponseSafeHandle response, [Out] byte[] buffer, nuint capacity, out nuint read, ref Error error);
        [DllImport(Library, EntryPoint = "vgi_iroh_http_response_read_timeout", CallingConvention = CallingConvention.Cdecl)] internal static extern int HttpResponseReadTimeout(HttpResponseSafeHandle response, [Out] byte[] buffer, nuint capacity, ulong timeoutMs, out nuint read, out byte timedOut, ref Error error);
        [DllImport(Library, EntryPoint = "vgi_iroh_http_response_cancel", CallingConvention = CallingConvention.Cdecl)] internal static extern void HttpResponseCancel(IntPtr response);
        [DllImport(Library, EntryPoint = "vgi_iroh_http_response_free", CallingConvention = CallingConvention.Cdecl)] internal static extern void HttpResponseFree(IntPtr response);
    }
}
