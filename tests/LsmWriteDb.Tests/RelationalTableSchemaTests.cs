using LsmWriteDb.ChangeLogs;
using LsmWriteDb.Sql;
using LsmWriteDb.Storage;
using LsmWriteDb.Transactions;

namespace LsmWriteDb.Tests;

public sealed class RelationalTableSchemaTests
{
    [Fact]
    public void ValidateRow_EnforcesPrimaryKeyAndColumnTypes()
    {
        var schema = new RelationalTableSchema("users", [
            new RelationalColumnDefinition("id", RelationalColumnType.Int, IsPrimaryKey: true),
            new RelationalColumnDefinition("name", RelationalColumnType.Text),
            new RelationalColumnDefinition("active", RelationalColumnType.Boolean, IsNullable: true)]);

        schema.ValidateRow("42", "{\"name\":\"Ada\",\"active\":true}");

        var missing = Assert.Throws<RelationalSchemaException>(() => schema.ValidateRow("42", "{\"active\":true}"));
        Assert.Contains("name", missing.Message);
        Assert.Throws<RelationalSchemaException>(() => schema.ValidateRow("not-an-int", "{\"name\":\"Ada\"}"));
        Assert.Throws<RelationalSchemaException>(() => schema.ValidateRow("42", "{\"name\":42}"));
        Assert.Throws<RelationalSchemaException>(() => schema.ValidateRow("42", "{\"id\":42,\"name\":\"Ada\"}"));
        Assert.Throws<RelationalSchemaException>(() => schema.ValidateRow("42", "{\"name\":\"Ada\",\"extra\":true}"));
    }

    [Fact]
    public async Task DatabaseEngine_PersistsRelationalSchemaInCatalog()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), "LsmWriteDb.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new LsmStoreOptions(dataPath, 100);
            var schema = new RelationalTableSchema("users", [
                new RelationalColumnDefinition("id", RelationalColumnType.Int, IsPrimaryKey: true),
                new RelationalColumnDefinition("name", RelationalColumnType.Text)]);
            var database = new DatabaseEngine(options, new ChangeLogService(options));
            await database.InitializeAsync();
            Assert.True(await database.CreateRelationalTableAsync(schema));

            var catalog = await File.ReadAllTextAsync(Path.Combine(dataPath, "catalog.json"));
            Assert.Contains("relationalTables", catalog, StringComparison.OrdinalIgnoreCase);

            var restored = new DatabaseEngine(options, new ChangeLogService(options));
            await restored.InitializeAsync();
            var restoredSchema = await restored.GetRelationalSchemaAsync("users");
            Assert.NotNull(restoredSchema);
            Assert.Equal(RelationalColumnType.Int, restoredSchema!.PrimaryKey.Type);
            Assert.Equal("name", restoredSchema.Columns[1].Name);
        }
        finally
        {
            if (Directory.Exists(dataPath)) Directory.Delete(dataPath, recursive: true);
        }
    }
}