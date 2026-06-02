# TodoApp.Identity

> **⚠️ This is a toy identity provider for demonstration purposes only. Do not use in production.**

This service exists solely to complete the authentication loop in the ToDo App demo. It lacks every security measure a real identity provider requires:

- Passwords are hashed with a single unsalted SHA-256 (no bcrypt/scrypt/argon2)
- No rate limiting, account lockout, or brute-force protection
- No HTTPS enforcement
- No refresh tokens or token revocation
- No email verification
- Secrets stored in plain configuration

In a real system, use a proper identity provider (e.g. Azure AD, Auth0, Duende IdentityServer).

## What it does

| Endpoint | Purpose |
|----------|---------|
| `POST /register` | Create an account, receive a JWT |
| `POST /login` | Authenticate, receive a JWT |
| `GET /userinfo` | Validate a Bearer token, return user claims |

## Architecture

- **API spec**: `specs/identity-api.json` (OpenAPI 3.2) → generated server handlers via `corvusjson openapi-server`
- **Storage schema**: `Schemas/identity-storage.json` → source-generated CTJ types at build time
- **Persistence**: Single blob (`identity/accounts.json`) in Azurite, using the same streaming read/write + ETag concurrency pattern as the main API
