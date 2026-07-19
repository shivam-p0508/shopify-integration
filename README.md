# shopify-integration

Minimal .NET worker-service skeleton for a Windows-deployable Shopify order monitor.

## Project

- Solution: `./ShopifyIntegration.sln`
- Service project: `./src/ShopifyOrderMonitorService`

## Build

```bash
dotnet build ShopifyIntegration.sln
```

## Run locally

```bash
dotnet run --project src/ShopifyOrderMonitorService/ShopifyOrderMonitorService.csproj
```

The worker polls the Shopify Admin GraphQL API on a configurable interval and writes one JSON
file per order under the configured output directory. It remembers where it got to via a
checkpoint file, so restarting is safe.

### Configure

Set the following in `appsettings.json`, `appsettings.Development.json`, or via environment
variables (e.g. `Shopify__ClientSecret`, `OrderSync__Interval`):

- `Shopify:Shop` — the store handle (the part before `.myshopify.com`)
- `Shopify:ClientId` / `Shopify:ClientSecret` — OAuth client credentials
- `Shopify:ApiVersion` — the Shopify Admin API version to call
- `OrderSync:*` — polling interval/overlap, page sizes, output directory, checkpoint path, etc.

Prefer setting `ClientSecret` via the `Shopify__ClientSecret` environment variable rather than
committing it to `appsettings.json`.


## Publish for Windows Service deployment

```bash
dotnet publish src/ShopifyOrderMonitorService/ShopifyOrderMonitorService.csproj -c Release -r win-x64 --self-contained false
```