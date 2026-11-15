# API and contracts

| Contract | File | For |
|----------|------|-----|
| Customer API, GraphQL | [customer.graphql](customer.graphql) | Web shop and mobile app |
| Back-office API, GraphQL | [backoffice.graphql](backoffice.graphql) | Back office |
| Provider webhooks and health endpoints | [openapi.yaml](openapi.yaml) | Providers, operations |
| Commands and integration events | [integration-events.md](integration-events.md) | Service developers |
| Service contracts in code | `src/Modules/*/OrderProcessing.*.Contracts` | Service developers; the C# is the source of truth |

## Changing a contract

- **Additive only.** New optional fields, new events, new enum values that consumers tolerate.
- **GraphQL has no versions.** Add fields, mark old ones `@deprecated`, remove them when no persisted
  query uses them. A breaking HTTP change gets a new path version; a breaking event change is a new event type.
- **Error codes are contract.** Clients branch on `code`, never on the message.
- **The schema files cannot drift.** `GraphQLApiTests` fails when a file differs from the served schema.
  To change it on purpose, run `UPDATE_GRAPHQL_SCHEMA=1 dotnet test` and review the diff.
- **The OpenAPI file is written by hand** and reviewed like code.
