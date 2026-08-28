namespace QueryFarm.VgiRpc.Client;

/// <summary>Marks a typed client stream method whose worker sends a header IPC stream.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RpcStreamHeaderAttribute : Attribute
{
}
