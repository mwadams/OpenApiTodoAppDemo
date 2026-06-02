# TodoApp.Broker

> **This is a toy broker for demonstration purposes only. Do not use it as a production front-end proxy, gateway, or infrastructure deployment engine.**

This project exists to make the ToDo App demo self-contained. It deliberately combines several responsibilities that would normally be handled by separate production-grade systems:

| Demo responsibility | What the broker does here | Production equivalent |
|---------------------|---------------------------|-----------------------|
| Front-end proxy | Acts as the single browser-facing origin and forwards `/api/*`, auth, and static web requests to internal services | API gateway, reverse proxy, CDN, BFF, or ingress layer |
| Storage credential broker | Owns Azurite/blob credentials and injects short-lived SAS URIs into back-end API requests | Managed identity, policy-driven token service, centralized secret management |
| Provisioning engine | Creates demo blob containers/blobs, tracks provisioning tickets, and calls back the API when storage is ready | IaC pipeline, deployment controller, workflow engine, or cloud control-plane integration |
| Session/auth glue | Stores demo JWTs in cookies and forwards identity context | Real identity provider integration, hardened session middleware, authorization policies |

## What it is demonstrating

The broker demonstrates the pattern the app cares about:

1. The browser talks only to the broker.
2. The back-end API has no baked-in storage credentials.
3. The broker supplies SAS tokens in request headers.
4. Entity creation can trigger asynchronous storage provisioning.
5. The API initializes provisioned storage through a callback once the broker has created it.

This keeps the demo focused on spec-first OpenAPI generation, Corvus.Text.Json models, isolated blob-backed todo lists, and the separation between domain behavior and infrastructure provisioning.

## What it is not demonstrating

This is not an example of how to implement any of these components in production. In particular, it does not attempt to provide:

- Hardened authentication, authorization, or session management
- Tenant isolation beyond the demo's container/blob naming convention
- Secure secret rotation or managed identity integration
- Robust provisioning workflows, retries, compensation, or audit trails
- Rate limiting, abuse protection, request validation, or policy enforcement expected from a gateway
- Operational concerns such as observability, health modeling, rollout strategy, or disaster recovery

If this were a real system, the front-end gateway, identity integration, credential/token broker, and infrastructure provisioning engine would be designed, secured, deployed, and operated as separate production components.

## Generated surfaces

The broker is still spec-first:

| Surface | Source spec | Purpose |
|---------|-------------|---------|
| Frontend server | `specs/broker-frontend.json` | Public browser-facing auth, gateway, and proxy endpoints |
| Runtime server | `specs/broker-runtime.json` | Provisioning endpoints called by the back-end API |
| API callback client | `specs/todo-api.json` | Client used to notify the API that storage has been provisioned |

Generated code lives under `Generated/`; handwritten demo behavior lives primarily in `Handlers/` and `Services/`.
