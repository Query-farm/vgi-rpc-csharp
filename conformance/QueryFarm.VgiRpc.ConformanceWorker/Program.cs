// Conformance worker entry point. Registers ConformanceServiceImpl and serves it over stdio —
// the transport `vgi-rpc-test --cmd` drives by default. --unix/--tcp/--http/--access-log land
// in later milestones (M4-M6); see docs/roadmap.md and docs/porting-guide.md (canonical Python
// repo) for the full mandatory-flags contract this worker will eventually need to satisfy.

using QueryFarm.VgiRpc.Conformance;
using QueryFarm.VgiRpc.ConformanceWorker;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Transport;

var server = new RpcServer(typeof(IConformanceService), new ConformanceServiceImpl());
var transport = new StdioTransport();
await server.ServeAsync(transport);
