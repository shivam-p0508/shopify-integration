using ShopifyOrderMonitorService;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "Shopify Order Monitor Service";
});
builder.Services.Configure<OrderMonitorOptions>(
    builder.Configuration.GetSection(OrderMonitorOptions.SectionName));
builder.Services.AddHostedService<OrderMonitorWorker>();

var host = builder.Build();
host.Run();
