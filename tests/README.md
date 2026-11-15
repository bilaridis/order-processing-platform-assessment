# Tests

| Project | Proves |
|---------|--------|
| `OrderProcessing.Modules.UnitTests` | The `Order` lifecycle rules; the whole order flow by message against fakes, including failed publishes, redeliveries and dead letters; pricing rules; the integration services; the GraphQL schemas and the views |
| `OrderProcessing.ArchitectureTests` | Module boundaries and layering, on the compiled assemblies |

All run in seconds with no database, broker or mocking library. They show the pattern for each kind of
test; integration, API and provider contract tests are in the
[build order](../docs/architecture/04-implementation-guide.md#suggested-build-order).
