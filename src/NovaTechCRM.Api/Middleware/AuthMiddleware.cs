using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace NovaTechCRM.Api.Middleware;

// Extracts and validates the JWT Bearer token, populating HttpContext.User.
// We rolled our own rather than using the built-in AddAuthentication() pipeline because
// we needed per-request tenant resolution before the auth check, and the built-in
// middleware ordering made that awkward. See PR #41 for the full discussion.
public class AuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _config;

    private static readonly string[] _publicPaths =
    [
        "/api/webhooks",
        "/api/auth",
        "/health",
        "/swagger",
    ];

    public AuthMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next   = next;
        _config = config;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";

        // skip auth for public endpoints
        if (_publicPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(ctx);
            return;
        }

        var token = ExtractToken(ctx);

        if (token != null)
        {
            var principal = ValidateToken(token);
            if (principal != null)
                ctx.User = principal;
        }

        // do NOT short-circuit here — let [Authorize] attributes on controllers decide
        // whether an anonymous request should be rejected. This allows mixed-auth endpoints.
        await _next(ctx);
    }

    private static string? ExtractToken(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.FirstOrDefault();
        if (header?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
            return header["Bearer ".Length..].Trim();

        // also accept token in query string for webhook callbacks that can't set headers
        return ctx.Request.Query["token"].FirstOrDefault();
    }

    private ClaimsPrincipal? ValidateToken(string token)
    {
        var secret = _config["Jwt:Secret"] ?? throw new InvalidOperationException("JWT secret not configured");

        var handler = new JwtSecurityTokenHandler();
        var key     = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));

        try
        {
            return handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey         = key,
                ValidateIssuer           = false,
                ValidateAudience         = false,
                ClockSkew                = TimeSpan.FromMinutes(5),
            }, out _);
        }
        catch
        {
            return null;
        }
    }
}
