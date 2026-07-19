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

The worker intentionally contains only a polling placeholder and no Shopify integration code yet.

## Publish for Windows Service deployment

```bash
dotnet publish src/ShopifyOrderMonitorService/ShopifyOrderMonitorService.csproj -c Release -r win-x64 --self-contained false
```