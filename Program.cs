using Amazon;
using Amazon.SimpleEmailV2;
using Amazon.SQS;
using EmailPlatform.Email;
using EmailPlatform.Email.Configuration;
using EmailPlatform.Shared.Clients;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Formatting.Compact;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new CompactJsonFormatter())
    .Enrich.FromLogContext()
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSerilog((sp, cfg) => cfg
        .WriteTo.Console(new CompactJsonFormatter())
        .Enrich.FromLogContext());

    builder.Services.Configure<EmailOptions>(
        builder.Configuration.GetSection(EmailOptions.SectionName));
    builder.Services.Configure<StorageClientOptions>(
        builder.Configuration.GetSection(StorageClientOptions.SectionName));

    // Factor 4: SQS + SES as attached resources — endpoints flow from config.
    builder.Services.AddSingleton<IAmazonSQS>(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<EmailOptions>>().Value;
        var config = new AmazonSQSConfig
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(opts.Region)
        };
        if (!string.IsNullOrWhiteSpace(opts.ServiceUrl))
        {
            config.ServiceURL = opts.ServiceUrl;
            config.AuthenticationRegion = opts.Region;
        }
        return new AmazonSQSClient(config);
    });

    builder.Services.AddSingleton<IAmazonSimpleEmailServiceV2>(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<EmailOptions>>().Value;
        var config = new AmazonSimpleEmailServiceV2Config
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(opts.Region)
        };
        if (!string.IsNullOrWhiteSpace(opts.ServiceUrl))
        {
            config.ServiceURL = opts.ServiceUrl;
            config.AuthenticationRegion = opts.Region;
        }
        return new AmazonSimpleEmailServiceV2Client(config);
    });

    // Typed HttpClient to Storage.
    builder.Services.AddHttpClient<IStorageClient, StorageClient>((sp, client) =>
    {
        var opts = sp.GetRequiredService<IOptions<StorageClientOptions>>().Value;
        client.BaseAddress = new Uri(opts.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(30);
    }).AddStandardResilienceHandler();

    builder.Services.AddSingleton<EmailSender>();
    builder.Services.AddHostedService<EmailWorker>();

    var host = builder.Build();

    var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
    lifetime.ApplicationStopping.Register(() =>
        Log.Information("Email worker received shutdown signal — draining in-flight messages..."));
    lifetime.ApplicationStopped.Register(() =>
        Log.Information("Email worker stopped cleanly."));

    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Email worker failed to start");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}
