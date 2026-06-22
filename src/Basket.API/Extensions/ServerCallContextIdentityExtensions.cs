#nullable enable

namespace eShop.Basket.API.Extensions;

internal static class ServerCallContextIdentityExtensions
{
    public static string? GetUserIdentity(this ServerCallContext context)
    {
        var user = context.GetHttpContext().User;

        // Prefer the 'sub' claim (subject) when present
        var id = user.FindFirst("sub")?.Value;
        if (!string.IsNullOrEmpty(id))
        {
            return id;
        }

        // Fall back to standard name identifier if mapping or token handling changed
        id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(id))
        {
        return id;
        }

        // As a last resort, fall back to Name (mainly for debugging/local scenarios)
        return user.Identity?.Name;
    }

    public static string? GetUserName(this ServerCallContext context)
        => context.GetHttpContext().User.FindFirst(x => x.Type == ClaimTypes.Name)?.Value;
}
