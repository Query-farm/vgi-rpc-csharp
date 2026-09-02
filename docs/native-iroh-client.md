# Native Iroh client

`RpcClient.ConnectIrohAsync("iroh://<endpoint-id>")` uses the in-process
`vgi_iroh_cabi` library. It does not download or execute a connector. The convenience overload
uses a process-shared endpoint pool. Every implicit endpoint instance is derived from one private
process-generated key, so the local EndpointId remains stable across connections even when relay or
timeout configuration requires another native endpoint instance. Explicit `SecretKey` values remain
separate configured identities.

This client is deliberately raw-only: it speaks `vgi-rpc/arrow-mux/1`. A `httpi://` URI is parsed
for cross-SDK conformance but rejected before provider dispatch because this SDK does not yet expose
an `iroh-http/2` client.

Release packages stage the platform library under the normal NuGet RID layout:
`runtimes/<rid>/native/vgi_iroh_cabi`. The managed package never embeds an absolute build path.
Release automation must supply and test the version-matched libraries for every advertised RID;
`NativeIrohTransportProvider.IsAvailable()` is the non-throwing runtime probe.

`IrohConnectOptions` controls local relays and identity. `RemoteRelayUrl` and `DirectAddresses` are
remote routing hints, including for direct-only private networks. Cancellation during connect and
async writes is polled inside the C ABI and cancels only that logical operation. Native errors retain
their stage, category, and dispatch certainty in `IrohTransportException`.

Treat `SecretKey` as sensitive mutable data. The adapter hex-encodes it directly into zeroed native
memory for endpoint creation and retains only a SHA-256 configuration fingerprint; callers should
clear their original byte array when it is no longer needed.
