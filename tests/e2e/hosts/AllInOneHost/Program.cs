// Sealer + liberator + job store in one process, with no HTTP hop and no bearer tokens.
// The read-only allow-list still applies because InProcessJobGateway runs every request
// through the same JobAdmissionPolicy the HTTP endpoints use.
using TapirSLMP.Contracts;
using TapirSLMP.Liberator;
using TapirSLMP.Sealer;
using TapirSLMP.Storage;
using TapirSLMP.Storage.Sqlite;

var builder = Host.CreateApplicationBuilder(args);
var configuration = builder.Configuration;

var policyOptions = new JobPolicyOptions
{
    AllowStateChangingCommands =
        configuration.GetValue("Freezer:AllowStateChangingCommands", false),
    DefaultJobTtlSeconds = configuration.GetValue("Freezer:DefaultJobTtlSeconds", 60),
    MaximumJobTtlSeconds = configuration.GetValue("Freezer:MaximumJobTtlSeconds", 300),
    MaximumAttempts = configuration.GetValue("Freezer:MaximumAttempts", 3),
    MaximumLeaseSeconds = configuration.GetValue("Freezer:MaximumLeaseSeconds", 120),
};
policyOptions.Validate();

var store = new SqliteJobStore(new SqliteJobStoreOptions
{
    ConnectionString = configuration["Freezer:SqliteConnectionString"]
        ?? "Data Source=data/tapirslmp.db;Cache=Shared;Pooling=True",
});
await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IJobStore>(store);
builder.Services.AddSingleton(new JobAdmissionPolicy(policyOptions));
builder.Services.AddSingleton<InProcessJobGateway>();
builder.Services.AddSingleton<IJobSubmissionGateway>(p => p.GetRequiredService<InProcessJobGateway>());
builder.Services.AddSingleton<IJobWorkerGateway>(p => p.GetRequiredService<InProcessJobGateway>());

builder.Services.AddTapirSealer(SealerOptions.FromConfiguration(configuration));
builder.Services.AddTapirLiberator(LiberatorOptions.FromConfiguration(configuration));

await builder.Build().RunAsync().ConfigureAwait(false);
