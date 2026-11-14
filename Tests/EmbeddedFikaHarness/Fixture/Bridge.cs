using System;
using System.Collections.Generic;

namespace OptionalTransport;

public sealed class Bridge : IDisposable
{
    private Dictionary<string, int> counters;

    public void Initialize(object logger)
    {
        counters = (Dictionary<string, int>)logger;
        Increment("initialize");
        if (counters.ContainsKey("failInit")) throw new InvalidOperationException("initialization failed");
    }

    public void Update()
    {
        Increment("update");
        if (counters.ContainsKey("failUpdate")) throw new InvalidOperationException("update failed");
    }

    public void Dispose()
    {
        Increment("dispose");
        if (counters.ContainsKey("failDispose")) throw new InvalidOperationException("cleanup failed");
    }

    private void Increment(string key)
    {
        counters.TryGetValue(key, out var count);
        counters[key] = count + 1;
    }
}
