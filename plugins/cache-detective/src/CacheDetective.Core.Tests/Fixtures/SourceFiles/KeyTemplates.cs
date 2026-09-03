using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace KeyTemplateFixture;

public sealed class KeyController : ControllerBase
{
    private const string ConstantKey = "constant:key";
    private static readonly string ReadonlyKey = "readonly:key";
    private readonly IMemoryCache _cache = null!;

    public string RequestRegion => "ignored";

    public void Literal() => _cache.Set("literal:key", 1);

    public void Constant() => _cache.Set(ConstantKey, 1);

    public void Readonly() => _cache.Set(ReadonlyKey, 1);

    public void Interpolated(int id) => _cache.Set($"product:{id}", 1);

    public void SameInterpolated(int id) => _cache.Set($"product:{id}", 2);

    public void Formatted(int orderId) => _cache.Set(string.Format("order:{0}", orderId), 1);

    public void Concatenated(int tenantId) => _cache.Set(string.Concat("tenant:", tenantId), 1);

    public void Joined(int userId) => _cache.Set(string.Join(":", "user", userId), 1);

    public void Added(int cartId) => _cache.Set("cart:" + cartId, 1);

    public void Local(string value)
    {
        var region = value;
        _cache.Set($"local:{region}", 1);
    }

    public void Property() => _cache.Set($"property:{RequestRegion}", 1);

    public void UnknownPart() => _cache.Set($"unknown:{Guid.NewGuid()}", 1);

    public void FiveHops(int id) => _cache.Set(Five1(id), 1);

    public void SixHops(int id) => _cache.Set(Six1(id), 1);

    public void Builder(int id) => _cache.Set(KeyBuilder.Build(id), 1);

    public void Dynamic(string dynamicKey) => _cache.Set(dynamicKey, 1);

    private static string Five1(int value) => Five2(value);
    private static string Five2(int value) => Five3(value);
    private static string Five3(int value) => Five4(value);
    private static string Five4(int value) => Five5(value);
    private static string Five5(int value) => $"five:{value}";

    private static string Six1(int value) => Six2(value);
    private static string Six2(int value) => Six3(value);
    private static string Six3(int value) => Six4(value);
    private static string Six4(int value) => Six5(value);
    private static string Six5(int value) => Six6(value);
    private static string Six6(int value) => $"six:{value}";
}

public static class KeyBuilder
{
    public static string Build(int id) => $"builder:{id}";
}
