using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace Refit
{
    public sealed class GetAttribute(string path) : Attribute { }
}

namespace Grpc.Core
{
    public class ClientBase<T> { }
    public sealed class BindServiceMethodAttribute : Attribute { }
}

namespace ExternalFixture
{
    public interface IHttpClientFactory { HttpClient CreateClient(string name); }
    public static class HttpClientRegistration
    {
        public static void AddHttpClient<TClient, TImplementation>(this object services) { }
    }

    public interface ICatalogService { }

    public sealed class CatalogService : ControllerBase, ICatalogService
    {
        public async Task Get(HttpClient client) => await client.GetAsync("/catalog");
    }

    public static class Startup
    {
        public static void Configure(object services) => services.AddHttpClient<ICatalogService, CatalogService>();
    }

    [Route("api/v1/[controller]")]
    public sealed class CatalogController : ControllerBase
    {
        private readonly IHttpClientFactory _factory = null!;

        [HttpGet("items/{id:int}")]
        public async Task GetItems(HttpClient client, string baseUri, int page) => await client.GetAsync($"{baseUri}/catalog/items?page={page}");

        [Route("ping")]
        public void Ping() { }

        [Route("cardtypes")]
        [HttpGet]
        public void CardTypes() { }

        [HttpGet]
        public void PrefixGet() { }

        [HttpGet("verbed")]
        public void VerbedGet() { }

        public async Task Send(HttpClient client, string baseUri) => await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"{baseUri}/orders"));

        public async Task SendLocal(HttpClient client, string baseUri)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUri}/orders");
            await client.SendAsync(request);
        }

        public async Task Named()
        {
            var client = _factory.CreateClient("catalog");
            await client.GetAsync("/items");
        }

        public async Task Unknown(HttpClient client, string url) => await client.GetAsync(url);

        public async Task LocalHelper(HttpClient client, string baseUri, int page)
        {
            var uri = Api.Items(baseUri, page);
            await client.GetStringAsync(uri);
        }
    }

    public static class Api
    {
        public static string Items(string baseUri, int page) => $"{baseUri}/catalog/items?page={page}";
    }

    public interface IProducts
    {
        [Refit.Get("/products/{id}")]
        Task GetProduct(int id);
    }

    public sealed class RefitController : ControllerBase
    {
        public async Task Get(IProducts products) => await products.GetProduct(1);
    }

    public class Basket
    {
        public class BasketClient : Grpc.Core.ClientBase<BasketClient>
        {
            public Task GetBasketByIdAsync() => Task.CompletedTask;
        }

        [Grpc.Core.BindServiceMethod]
        public class BasketBase
        {
            public virtual Task GetBasketById() => Task.CompletedTask;
        }
    }

    public sealed class GrpcController : ControllerBase
    {
        public async Task Get(Basket.BasketClient client) => await client.GetBasketByIdAsync();
    }

    public sealed class BasketServer : Basket.BasketBase
    {
        public override Task GetBasketById() => Task.CompletedTask;
    }
}
