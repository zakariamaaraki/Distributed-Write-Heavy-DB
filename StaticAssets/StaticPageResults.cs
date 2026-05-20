namespace LsmWriteDb.StaticAssets;

internal static class StaticPageResults
{
    public static IResult Html(IWebHostEnvironment environment, params string[] pathSegments)
    {
        var webRootPath = environment.WebRootPath
            ?? Path.Combine(environment.ContentRootPath, "wwwroot");
        var filePath = Path.Combine([webRootPath, .. pathSegments]);

        return File.Exists(filePath)
            ? Results.File(filePath, "text/html; charset=utf-8")
            : Results.NotFound(new { error = $"Static page not found: {string.Join('/', pathSegments)}" });
    }
}
