using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Corvus.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TodoApp.Identity.Server;
using TodoApp.Identity.Server.Models;
using IS = TodoApp.Identity.Storage;

namespace TodoApp.Identity.Handlers;

/// <summary>
/// Implements the Identity API server operations: register, login, userinfo.
/// </summary>
internal sealed class IdentityHandler : IApiDefaultHandler
{
    private readonly AccountStore _store;
    private readonly IConfiguration _config;

    public IdentityHandler(AccountStore store, IConfiguration config)
    {
        _store = store;
        _config = config;
    }

    public async ValueTask<RegisterAccountResult> HandleRegisterAccountAsync(
        RegisterAccountParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken = default)
    {
        RegisterRequest body = parameters.Body;

        string email = (string)body.Email;
        using var lookup = await _store.FindByEmailAsync(email, cancellationToken);
        if (lookup.Account is not null)
        {
            return RegisterAccountResult.Conflict(
                body: ErrorResponse.Build((ref b) =>
                    b.Create(error: "An account with this email already exists.")),
                workspace: workspace);
        }

        string sub = Guid.NewGuid().ToString();
        string displayName = body.DisplayName.IsUndefined() ? email : (string)body.DisplayName;
        string passwordHash;
        {
            using var utf8Password = body.Password.GetUtf8String();
            passwordHash = HashPassword(utf8Password.Span);
        }

        await _store.AddAccountAsync(sub, email, displayName, passwordHash, cancellationToken);

        string token = IssueJwt(sub, email, displayName);
        return RegisterAccountResult.Ok(
            body: TokenResponse.Build((ref b) => b.Create(token: token)),
            workspace: workspace);
    }

    public async ValueTask<LoginAccountResult> HandleLoginAccountAsync(
        LoginAccountParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken = default)
    {
        LoginRequest body = parameters.Body;

        string email = (string)body.Email;
        using var lookup = await _store.FindByEmailAsync(email, cancellationToken);
        if (lookup.Account is null)
        {
            return LoginAccountResult.Unauthorized();
        }

        var account = lookup.Account.Value;
        using var utf8Password = body.Password.GetUtf8String();
        using var utf8StoredHash = account.PasswordHash.GetUtf8String();
        if (!VerifyPassword(utf8Password.Span, utf8StoredHash.Span))
        {
            return LoginAccountResult.Unauthorized();
        }

        string token = IssueJwt(
            (string)account.Sub,
            (string)account.Email,
            (string)account.DisplayName);

        return LoginAccountResult.Ok(
            body: TokenResponse.Build((ref b) => b.Create(token: token)),
            workspace: workspace);
    }

    public async ValueTask<GetUserInfoResult> HandleGetUserInfoAsync(
        GetUserInfoParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken = default)
    {
        if (parameters.Authorization.IsUndefined())
        {
            return GetUserInfoResult.Unauthorized();
        }

        ReadOnlySpan<byte> bearerPrefix = "Bearer "u8;
        string bearerToken;
        {
            using var utf8Auth = parameters.Authorization.GetUtf8String();
            if (utf8Auth.Span.Length <= bearerPrefix.Length
                || !utf8Auth.Span[..bearerPrefix.Length].SequenceEqual(bearerPrefix))
            {
                return GetUserInfoResult.Unauthorized();
            }

            // JWT library requires a string — allocate only the token portion
            bearerToken = Encoding.UTF8.GetString(utf8Auth.Span[bearerPrefix.Length..]);
        }

        string? sub = await ValidateTokenAsync(bearerToken);
        if (sub is null)
        {
            return GetUserInfoResult.Unauthorized();
        }

        using var lookup = await _store.FindBySubAsync(sub, cancellationToken);
        if (lookup.Account is null)
        {
            return GetUserInfoResult.NotFound();
        }

        var account = lookup.Account.Value;
        return GetUserInfoResult.Ok(
            body: UserInfo.Build((ref b) =>
                b.Create(
                    sub: JsonString.From(account.Sub),
                    email: JsonEmail.From(account.Email),
                    name: UserInfo.TheUserSDisplayName.From(account.DisplayName))),
            workspace: workspace);
    }

    private string IssueJwt(string sub, string email, string displayName)
    {
        string signingKey = _config["Jwt:SigningKey"]
            ?? throw new InvalidOperationException("Jwt:SigningKey not configured");

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "todo-app-identity",
            Claims = new Dictionary<string, object>
            {
                ["sub"] = sub,
                ["email"] = email,
                ["name"] = displayName,
            },
            Expires = DateTime.UtcNow.AddHours(24),
            SigningCredentials = credentials,
        };

        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(descriptor);
    }

    private async Task<string?> ValidateTokenAsync(string token)
    {
        string signingKey = _config["Jwt:SigningKey"]
            ?? throw new InvalidOperationException("Jwt:SigningKey not configured");

        var handler = new JsonWebTokenHandler();
        var result = await handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "todo-app-identity",
            ValidateAudience = false,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ValidateLifetime = true,
        });

        if (!result.IsValid)
        {
            return null;
        }

        return result.Claims["sub"]?.ToString();
    }

    private static string HashPassword(ReadOnlySpan<byte> utf8Password)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(utf8Password, hash);
        return Convert.ToBase64String(hash);
    }

    private static bool VerifyPassword(ReadOnlySpan<byte> utf8Password, ReadOnlySpan<byte> utf8StoredHash)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(utf8Password, hash);

        Span<byte> decoded = stackalloc byte[SHA256.HashSizeInBytes];
        if (Base64.DecodeFromUtf8(utf8StoredHash, decoded, out _, out int written) != OperationStatus.Done
            || written != SHA256.HashSizeInBytes)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(hash, decoded);
    }
}
