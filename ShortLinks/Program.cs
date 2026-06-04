using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(_ =>
{
    var connectionString = builder.Configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("Connection string 'Default' is required.");

    return new NpgsqlDataSourceBuilder(connectionString).Build();
});

builder.Services.AddSingleton<LinkCodeGenerator>();
builder.Services.AddSingleton<UrlSafetyValidator>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("create-link", limiterOptions =>
    {
        limiterOptions.PermitLimit = 20;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueLimit = 0;
    });
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/admin") || context.Request.Path.StartsWithSegments("/api/admin"),
    protectedApp => protectedApp.Use(async (context, next) =>
    {
        if (!AdminAuth.IsAuthorized(context, app.Configuration))
        {
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"ShortLinks Admin\", charset=\"UTF-8\"";
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Authentication required.");
            return;
        }

        await next(context);
    }));
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/admin/")
    {
        context.Request.Path = "/admin/index.html";
    }

    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();

await Database.InitializeAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), app.Logger);

app.MapPost("/api/links", async (
        ShortenRequest request,
        HttpContext httpContext,
        NpgsqlDataSource dataSource,
        LinkCodeGenerator codeGenerator,
        UrlSafetyValidator validator,
        IConfiguration configuration) =>
    {
        var validation = await validator.ValidateAsync(request.Url);
        if (!validation.IsValid || validation.NormalizedUrl is null)
        {
            return Results.BadRequest(new ErrorResponse(validation.Error ?? "The supplied URL is not allowed."));
        }

        await using var connection = await dataSource.OpenConnectionAsync(httpContext.RequestAborted);

        const int maxAttempts = 8;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var code = codeGenerator.Create();

            try
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO short_links (code, target_url, created_ip)
                    VALUES (@Code, @TargetUrl, @CreatedIp::inet)
                    """,
                    new
                    {
                        Code = code,
                        TargetUrl = validation.NormalizedUrl,
                        CreatedIp = httpContext.Connection.RemoteIpAddress?.ToString()
                    });

                var publicBaseUrl = GetPublicBaseUrl(configuration, httpContext.Request);
                return Results.Created($"/api/links/{code}", new ShortenResponse(code, $"{publicBaseUrl}/{code}"));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // Extremely unlikely, but retry with a fresh random code if the unique index is hit.
            }
        }

        return Results.Problem("Could not generate a unique short link. Please try again.");
    })
    .RequireRateLimiting("create-link");

app.MapGet("/admin", (IWebHostEnvironment environment) =>
    Results.File(Path.Combine(environment.WebRootPath, "admin", "index.html"), "text/html"))
    .WithOrder(-100);

app.MapGet("/api/admin/links", async (
        HttpContext httpContext,
        NpgsqlDataSource dataSource,
        IConfiguration configuration,
        string? search,
        string? sort,
        string? dir) =>
    {
        var orderBy = AdminQueries.GetOrderBy(sort, dir);
        var publicBaseUrl = GetPublicBaseUrl(configuration, httpContext.Request);
        var searchTerm = string.IsNullOrWhiteSpace(search) ? null : $"%{search.Trim()}%";

        await using var connection = await dataSource.OpenConnectionAsync(httpContext.RequestAborted);
        var links = await connection.QueryAsync<AdminLinkRow>(
            $"""
            SELECT
                code,
                target_url AS TargetUrl,
                created_at AS CreatedAt,
                created_ip::text AS CreatedIp,
                click_count AS ClickCount,
                last_clicked_at AS LastClickedAt
            FROM short_links
            WHERE @Search IS NULL
               OR code ILIKE @Search
               OR target_url ILIKE @Search
               OR created_ip::text ILIKE @Search
            ORDER BY {orderBy}
            LIMIT 250
            """,
            new { Search = searchTerm });

        return Results.Ok(links.Select(link => new AdminLinkResponse(
            link.Code,
            link.TargetUrl,
            $"{publicBaseUrl}/{link.Code}",
            link.CreatedAt,
            link.CreatedIp,
            link.ClickCount,
            link.LastClickedAt)));
    });

app.MapPut("/api/admin/links/{code:regex(^[A-Za-z0-9]{{6,12}}$)}", async (
        string code,
        AdminUpdateLinkRequest request,
        HttpContext httpContext,
        NpgsqlDataSource dataSource,
        UrlSafetyValidator validator) =>
    {
        var validation = await validator.ValidateAsync(request.TargetUrl);
        if (!validation.IsValid || validation.NormalizedUrl is null)
        {
            return Results.BadRequest(new ErrorResponse(validation.Error ?? "The supplied URL is not allowed."));
        }

        await using var connection = await dataSource.OpenConnectionAsync(httpContext.RequestAborted);
        var rows = await connection.ExecuteAsync(
            """
            UPDATE short_links
            SET target_url = @TargetUrl
            WHERE code = @Code
            """,
            new { Code = code, TargetUrl = validation.NormalizedUrl });

        return rows == 0 ? Results.NotFound(new ErrorResponse("Short link not found.")) : Results.NoContent();
    });

app.MapDelete("/api/admin/links/{code:regex(^[A-Za-z0-9]{{6,12}}$)}", async (
        string code,
        HttpContext httpContext,
        NpgsqlDataSource dataSource) =>
    {
        await using var connection = await dataSource.OpenConnectionAsync(httpContext.RequestAborted);
        var rows = await connection.ExecuteAsync(
            """
            DELETE FROM short_links
            WHERE code = @Code
            """,
            new { Code = code });

        return rows == 0 ? Results.NotFound(new ErrorResponse("Short link not found.")) : Results.NoContent();
    });

app.MapGet("/{code:regex(^[A-Za-z0-9]{{6,12}}$)}", async (
        string code,
        HttpContext httpContext,
        NpgsqlDataSource dataSource) =>
    {
        await using var connection = await dataSource.OpenConnectionAsync(httpContext.RequestAborted);

        var targetUrl = await connection.QuerySingleOrDefaultAsync<string?>(
            """
            UPDATE short_links
            SET click_count = click_count + 1,
                last_clicked_at = now()
            WHERE code = @Code
            RETURNING target_url
            """,
            new { Code = code });

        return targetUrl is null
            ? Results.NotFound("Short link not found.")
            : Results.Redirect(targetUrl, permanent: false);
    });

app.MapFallbackToFile("index.html");

app.Run();

static string GetPublicBaseUrl(IConfiguration configuration, HttpRequest request)
{
    var configured = configuration["PublicBaseUrl"];
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return configured.TrimEnd('/');
    }

    return $"{request.Scheme}://{request.Host}".TrimEnd('/');
}

internal sealed record ShortenRequest([property: Required, Url, MaxLength(2048)] string Url);

internal sealed record ShortenResponse(string Code, string ShortUrl);

internal sealed record ErrorResponse(string Error);

internal sealed record AdminUpdateLinkRequest([property: Required, Url, MaxLength(2048)] string TargetUrl);

internal sealed class AdminLinkRow
{
    public string Code { get; set; } = string.Empty;

    public string TargetUrl { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public string? CreatedIp { get; set; }

    public long ClickCount { get; set; }

    public DateTimeOffset? LastClickedAt { get; set; }
}

internal sealed record AdminLinkResponse(
    string Code,
    string TargetUrl,
    string ShortUrl,
    DateTimeOffset CreatedAt,
    string? CreatedIp,
    long ClickCount,
    DateTimeOffset? LastClickedAt);

internal sealed record UrlValidationResult(bool IsValid, string? NormalizedUrl = null, string? Error = null);

internal static class AdminAuth
{
    public static bool IsAuthorized(HttpContext context, IConfiguration configuration)
    {
        var expectedUsername = configuration["Admin:Username"] ?? configuration["ADMIN_USERNAME"];
        var expectedPassword = configuration["Admin:Password"] ?? configuration["ADMIN_PASSWORD"];

        if (string.IsNullOrWhiteSpace(expectedUsername) || string.IsNullOrWhiteSpace(expectedPassword))
        {
            return false;
        }

        if (!context.Request.Headers.Authorization.ToString().StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var encodedCredentials = context.Request.Headers.Authorization.ToString()["Basic ".Length..].Trim();
        string decodedCredentials;
        try
        {
            decodedCredentials = Encoding.UTF8.GetString(Convert.FromBase64String(encodedCredentials));
        }
        catch
        {
            return false;
        }

        var separatorIndex = decodedCredentials.IndexOf(':');
        if (separatorIndex <= 0)
        {
            return false;
        }

        var username = decodedCredentials[..separatorIndex];
        var password = decodedCredentials[(separatorIndex + 1)..];

        return FixedTimeEquals(username, expectedUsername) && FixedTimeEquals(password, expectedPassword);
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);

        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

internal static class AdminQueries
{
    private static readonly Dictionary<string, string> SortColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["code"] = "code",
        ["targetUrl"] = "target_url",
        ["shortUrl"] = "code",
        ["createdAt"] = "created_at",
        ["createdIp"] = "created_ip",
        ["clickCount"] = "click_count",
        ["lastClickedAt"] = "last_clicked_at"
    };

    public static string GetOrderBy(string? sort, string? dir)
    {
        var column = !string.IsNullOrWhiteSpace(sort) && SortColumns.TryGetValue(sort, out var mapped)
            ? mapped
            : "created_at";
        var direction = string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";

        return $"{column} {direction}, code ASC";
    }
}

internal sealed class LinkCodeGenerator
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const int CodeLength = 7;

    public string Create()
    {
        Span<char> chars = stackalloc char[CodeLength];
        Span<byte> bytes = stackalloc byte[CodeLength];
        RandomNumberGenerator.Fill(bytes);

        for (var i = 0; i < CodeLength; i++)
        {
            chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        }

        return new string(chars);
    }
}

internal sealed class UrlSafetyValidator
{
    private static readonly HashSet<string> BlockedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "localhost.localdomain",
        "fu87.app"
    };

    public async Task<UrlValidationResult> ValidateAsync(string? submittedUrl)
    {
        if (string.IsNullOrWhiteSpace(submittedUrl))
        {
            return new(false, Error: "Enter a URL to shorten.");
        }

        submittedUrl = submittedUrl.Trim();

        if (submittedUrl.Length > 2048)
        {
            return new(false, Error: "URLs must be 2,048 characters or fewer.");
        }

        if (!Uri.TryCreate(submittedUrl, UriKind.Absolute, out var uri))
        {
            return new(false, Error: "Enter an absolute URL, including https:// or http://.");
        }

        if (uri.Scheme is not ("https" or "http"))
        {
            return new(false, Error: "Only http and https URLs can be shortened.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return new(false, Error: "URLs with embedded usernames or passwords are not allowed.");
        }

        if (BlockedHosts.Contains(uri.Host) || uri.Host.EndsWith(".fu87.app", StringComparison.OrdinalIgnoreCase))
        {
            return new(false, Error: "This service cannot shorten its own domain.");
        }

        if (IPAddress.TryParse(uri.Host, out var literalAddress) && IsPrivateOrReservedAddress(literalAddress))
        {
            return new(false, Error: "Private, local, or reserved network addresses are not allowed.");
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.Host);
        }
        catch
        {
            return new(false, Error: "The URL host could not be resolved.");
        }

        if (addresses.Length == 0 || addresses.Any(IsPrivateOrReservedAddress))
        {
            return new(false, Error: "The URL host resolves to a private, local, or reserved network address.");
        }

        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty
        };

        return new(true, builder.Uri.AbsoluteUri);
    }

    private static bool IsPrivateOrReservedAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();

            return bytes[0] switch
            {
                0 => true,
                10 => true,
                100 when bytes[1] >= 64 && bytes[1] <= 127 => true,
                127 => true,
                169 when bytes[1] == 254 => true,
                172 when bytes[1] >= 16 && bytes[1] <= 31 => true,
                192 when bytes[1] == 0 && bytes[2] == 0 => true,
                192 when bytes[1] == 168 => true,
                198 when bytes[1] >= 18 && bytes[1] <= 19 => true,
                224 or 240 or 255 => true,
                _ => false
            };
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal
                || address.IsIPv6Multicast
                || address.IsIPv6SiteLocal
                || address.Equals(IPAddress.IPv6Loopback)
                || address.Equals(IPAddress.IPv6None)
                || address.ToString().StartsWith("fc", StringComparison.OrdinalIgnoreCase)
                || address.ToString().StartsWith("fd", StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }
}

internal static class Database
{
    public static async Task InitializeAsync(NpgsqlDataSource dataSource, ILogger logger)
    {
        const int attempts = 20;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync();
                await connection.ExecuteAsync(
                    """
                    CREATE TABLE IF NOT EXISTS short_links (
                        id bigserial PRIMARY KEY,
                        code varchar(12) NOT NULL UNIQUE,
                        target_url text NOT NULL,
                        created_at timestamptz NOT NULL DEFAULT now(),
                        created_ip inet NULL,
                        click_count bigint NOT NULL DEFAULT 0,
                        last_clicked_at timestamptz NULL
                    );

                    CREATE INDEX IF NOT EXISTS ix_short_links_created_at
                    ON short_links (created_at DESC);
                    """);

                return;
            }
            catch (Exception ex) when (attempt < attempts)
            {
                logger.LogWarning(ex, "Database is not ready yet; retrying startup migration attempt {Attempt}/{Attempts}.", attempt, attempts);
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }
}
