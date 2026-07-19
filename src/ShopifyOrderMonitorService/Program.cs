using Microsoft.Extensions.Options;
using ShopifyOrderMonitorService;
using ShopifyOrderMonitorService.Configuration;
using ShopifyOrderMonitorService.OrderSync;
using ShopifyOrderMonitorService.Shopify;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "Shopify Order Monitor Service";
});

builder.Services
    .AddOptions<ShopifyOptions>()
    .Bind(builder.Configuration.GetSection(ShopifyOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ShopifyOptions>, ShopifyOptionsValidator>();

builder.Services
    .AddOptions<OrderSyncOptions>()
    .Bind(builder.Configuration.GetSection(OrderSyncOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<OrderSyncOptions>, OrderSyncOptionsValidator>();

builder.Services.AddHttpClient(ShopifyApiClient.HttpClientName, http =>
{
    http.Timeout = TimeSpan.FromSeconds(60);
    http.DefaultRequestHeaders.Add("User-Agent", "ShopifyOrderMonitorService/1.0");
    http.DefaultRequestHeaders.Add("Accept", "application/json");
});

builder.Services.AddSingleton<ShopifyApiClient>();
builder.Services.AddSingleton<OrderCheckpointStore>();
builder.Services.AddSingleton<OrderFileWriter>();
builder.Services.AddSingleton<OrderSyncEngine>();
builder.Services.AddHostedService<OrderMonitorWorker>();

var host = builder.Build();
host.Run();
