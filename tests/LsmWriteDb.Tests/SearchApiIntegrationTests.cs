using System.Net;
using System.Net.Http.Json;
using LsmWriteDb.ChangeLogs;
using LsmWriteDb.Raft;
using LsmWriteDb.Search;
using LsmWriteDb.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace LsmWriteDb.Tests;

public sealed class SearchApiIntegrationTests
{
    [Fact]
    public async Task RestApi_CreatesSearchesAndRebuildsIndex()
    {
        var dataPath = CreateTempDataPath();
        try
        {
            await using var app = await CreateAppAsync(dataPath);
            var database = app.Services.GetRequiredService<DatabaseEngine>();
            await database.CreateTableAsync("articles");
            await database.PutAsync("articles", "1", "{\"title\":\"distributed database\"}");

            using var client = app.GetTestClient();
            var create = await client.PutAsJsonAsync("/search/indexes/articles_text",
                new CreateSearchIndexRequest("articles", ["value.title"]));

            Assert.Equal(HttpStatusCode.Created, create.StatusCode);

            var search = await client.PostAsJsonAsync("/search/articles_text", new SearchRequest("distributed"));
            Assert.Equal(HttpStatusCode.OK, search.StatusCode);
            var result = await search.Content.ReadFromJsonAsync<SearchResponse>();
            Assert.NotNull(result);
            Assert.Equal(["1"], result!.Hits.Select(hit => hit.Key));

            var rebuild = await client.PostAsync("/search/articles_text/rebuild", null);
            Assert.Equal(HttpStatusCode.NoContent, rebuild.StatusCode);
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task InternalEnsureEndpoint_MaterializesPropagatedIndexDefinition()
    {
        var dataPath = CreateTempDataPath();
        try
        {
            await using var app = await CreateAppAsync(dataPath);
            var database = app.Services.GetRequiredService<DatabaseEngine>();
            await database.CreateTableAsync("articles");
            await database.PutAsync("articles", "1", "{\"title\":\"replicated search\"}");

            using var client = app.GetTestClient();
            var ensure = await client.PutAsJsonAsync("/raft/search-indexes/articles_text/ensure",
                new CreateSearchIndexRequest("articles", ["value.title"]));

            Assert.Equal(HttpStatusCode.OK, ensure.StatusCode);
            Assert.Equal("articles", Assert.Single(await database.ListSearchIndexesAsync()).Table);

            var search = await client.PostAsJsonAsync("/search/articles_text", new SearchRequest("replicated"));
            var result = await search.Content.ReadFromJsonAsync<SearchResponse>();
            Assert.Equal(["1"], result!.Hits.Select(hit => hit.Key));
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    private static async Task<WebApplication> CreateAppAsync(string dataPath)
    {
        var options = new LsmStoreOptions(dataPath, FlushThreshold: 2);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ChangeLogService>();
        builder.Services.AddSingleton<DatabaseEngine>();
        builder.Services.AddSingleton(new RaftOptions { Enabled = false, NodeId = "test", PublicUrl = "http://test" });
        builder.Services.AddSingleton(new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
        builder.Services.AddSingleton<TableRaftCoordinator>();

        var app = builder.Build();
        app.MapSearchEndpoints();
        await app.Services.GetRequiredService<DatabaseEngine>().InitializeAsync();
        await app.StartAsync();
        return app;
    }

    private static string CreateTempDataPath() => Path.Combine(Path.GetTempPath(), "LsmWriteDb.Tests", Guid.NewGuid().ToString("N"));

    private static void DeleteTempDataPath(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
