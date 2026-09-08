// A key whose set holds one nameable member and one that names nothing: the first becomes a vertex, the
// second becomes the site's single unresolved row. See docs/adr/0015.
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace MixedKeyFixture;

public sealed class MixedController : ControllerBase
{
    private readonly IMemoryCache _cache = null!;

    public void Mixed(bool flag, string runtimeKey) => _cache.Set(flag ? "known:key" : runtimeKey, 1);
}
