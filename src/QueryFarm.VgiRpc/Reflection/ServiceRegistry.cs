using System.Collections.Concurrent;
using System.Reflection;

namespace QueryFarm.VgiRpc.Reflection;

/// <summary>
/// Reflects a service interface into its <see cref="RpcMethodInfo"/> set, keyed by wire name.
/// Cached per interface <see cref="Type"/> — reflection/schema derivation happens once.
/// </summary>
public static class ServiceRegistry
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, RpcMethodInfo>> s_cache = new();

    public static IReadOnlyDictionary<string, RpcMethodInfo> GetMethods(Type serviceInterface)
    {
        if (!serviceInterface.IsInterface)
        {
            throw new ArgumentException($"'{serviceInterface}' must be an interface — vgi-rpc services are plain interfaces, not concrete classes.", nameof(serviceInterface));
        }

        return s_cache.GetOrAdd(serviceInterface, static type =>
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            var byName = new Dictionary<string, RpcMethodInfo>(methods.Length);
            foreach (var method in methods)
            {
                var info = new RpcMethodInfo(method);
                if (!byName.TryAdd(info.WireName, info))
                {
                    throw new InvalidOperationException(
                        $"'{type}' declares two methods that both resolve to the wire name '{info.WireName}' " +
                        $"(one is '{method.Name}'). Disambiguate with [RpcName].");
                }
            }

            return byName;
        });
    }
}
