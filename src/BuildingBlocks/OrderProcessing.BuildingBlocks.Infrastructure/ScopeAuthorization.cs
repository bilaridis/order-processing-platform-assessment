using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace OrderProcessing.BuildingBlocks.Infrastructure;

public static class ScopeAuthorization
{
    /// <summary>Requires at least one of the given OAuth 2.0 scopes.</summary>
    public static AuthorizationPolicyBuilder RequireScope(this AuthorizationPolicyBuilder policy, params string[] anyOf) =>
        policy.RequireAssertion(context => anyOf.Any(context.User.HasScope));

    /// <summary>
    /// Identity providers send scopes either as one space-separated "scope" claim or as repeated claims
    /// ("scope" or "scp"); all forms are accepted.
    /// </summary>
    public static bool HasScope(this ClaimsPrincipal user, string scope) =>
        user.Claims
            .Where(claim => claim.Type is "scope" or "scp" or "http://schemas.microsoft.com/identity/claims/scope")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(scope, StringComparer.Ordinal);
}
