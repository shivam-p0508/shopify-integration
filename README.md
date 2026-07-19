# shopify-integration

Minimal .NET worker-service skeleton for a Windows-deployable Shopify order monitor.

## Project

- Solution: `/home/runner/work/shopify-integration/shopify-integration/ShopifyIntegration.sln`
- Service project: `/home/runner/work/shopify-integration/shopify-integration/src/ShopifyOrderMonitorService`

## Build

```bash
dotnet build /home/runner/work/shopify-integration/shopify-integration/ShopifyIntegration.sln
```

## Run locally

```bash
dotnet run --project /home/runner/work/shopify-integration/shopify-integration/src/ShopifyOrderMonitorService/ShopifyOrderMonitorService.csproj
```

The worker intentionally contains only a polling placeholder and no Shopify integration code yet.

## Publish for Windows Service deployment

```bash
dotnet publish /home/runner/work/shopify-integration/shopify-integration/src/ShopifyOrderMonitorService/ShopifyOrderMonitorService.csproj -c Release -r win-x64 --self-contained false
```