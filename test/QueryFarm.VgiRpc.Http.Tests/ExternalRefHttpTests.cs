using Apache.Arrow;
using Apache.Arrow.Types;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using QueryFarm.VgiRpc.Client.Http;
using QueryFarm.VgiRpc.External;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

/// <summary>
/// The HTTP unary dispatcher answering through <see cref="ICallContext.RespondWithExternalRef"/>:
/// the ref's pointer goes out as-is, nothing is uploaded, and the external-channel cap
/// (<c>max_externalized_response_bytes</c>) does not apply. End-to-end resolution against real
/// storage is the canonical <c>TestExternalRef</c> group in test_csharp_conformance.py.
/// </summary>
public sealed class ExternalRefHttpTests
{
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly Schema s_params = new([new Field("value", StringType.Default, nullable: false)], null);

    private static string Protocol => WireNaming.ForProtocol(typeof(IRefService));

    public interface IRefService
    {
        Task<string> FetchAsync(string value, ICallContext? ctx = null);
    }

    private sealed class RefService(ExternalRef reference) : IRefService
    {
        public Task<string> FetchAsync(string value, ICallContext? ctx = null)
        {
            ctx!.RespondWithExternalRef(reference);
            return Task.FromResult<string>(null!);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unary_WritesThePointer_UploadsNothing_AndIgnoresTheExternalCap(bool withDigest)
    {
        var reference = new ExternalRef("https://storage.invalid/published/1", withDigest ? Digest : null);
        var storage = new CountingStorage();
        await using var host = await StartHostAsync(new RefService(reference), new ExternalizationOptions
        {
            External = new ServerExternalConfig { Storage = storage, ExternalizeThresholdBytes = 1 },
            MaxExternalizedResponseBytes = 1,
        });
        await using var client = new HttpRpcClient(host.Address, new HttpRpcClientOptions { Protocol = Protocol });
        using var request = new RecordBatch(s_params, [new StringArray.Builder().Append("x").Build()], 1);

        var pointer = await client.CallUnaryAsync("fetch", request, cancellationToken: TestContext.Current.CancellationToken);
        using (pointer.Batch)
        {
            Assert.True(ExternalLocation.IsExternalLocationBatch(pointer.Batch, pointer.Metadata));
            Assert.Equal("result", pointer.Batch.Schema.GetFieldByIndex(0).Name);
            Assert.Equal(reference.Url, pointer.Metadata![MetadataKeys.Location]);
            Assert.Equal(withDigest, pointer.Metadata.ContainsKey(MetadataKeys.LocationSha256));
        }

        Assert.Equal(0, storage.Count);
    }

    [Fact]
    public async Task Unary_RefWorksWithoutAnyExternalization()
    {
        var reference = new ExternalRef("https://storage.invalid/published/2", Digest);
        await using var host = await StartHostAsync(new RefService(reference), externalization: null);
        await using var client = new HttpRpcClient(host.Address, new HttpRpcClientOptions { Protocol = Protocol });
        using var request = new RecordBatch(s_params, [new StringArray.Builder().Append("x").Build()], 1);

        var pointer = await client.CallUnaryAsync("fetch", request, cancellationToken: TestContext.Current.CancellationToken);
        using (pointer.Batch)
        {
            Assert.Equal(reference.Url, pointer.Metadata![MetadataKeys.Location]);
            Assert.Equal(Digest, pointer.Metadata[MetadataKeys.LocationSha256]);
        }
    }

    private static async Task<TestHost> StartHostAsync(IRefService service, ExternalizationOptions? externalization)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapVgiRpc(new RpcServer(typeof(IRefService), service), externalization: externalization);
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new TestHost(app, new Uri(address));
    }

    private sealed class CountingStorage : IExternalStorage
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task<string> UploadAsync(byte[] data, Schema schema, string? contentEncoding, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _count);
            return Task.FromResult($"https://storage.invalid/object/{n}");
        }
    }

    private sealed class TestHost(WebApplication app, Uri address) : IAsyncDisposable
    {
        public Uri Address { get; } = address;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
