using Microsoft.Extensions.Caching.Memory;

namespace Inngest.AiAgentSample;

public sealed class ApiKeys(IMemoryCache cache)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly Lock _gate = new();

    public bool TryAdd(string taskId, string apiKey)
    {
        lock (_gate)
        {
            if (cache.TryGetValue(KeyOf(taskId), out _))
            {
                return false;
            }

            cache.Set(KeyOf(taskId), apiKey, Lifetime);
            return true;
        }
    }

    public string? Get(string taskId) => cache.Get<string>(KeyOf(taskId));

    public void Remove(string taskId) => cache.Remove(KeyOf(taskId));

    private static string KeyOf(string taskId) => "api-key:" + taskId;
}
