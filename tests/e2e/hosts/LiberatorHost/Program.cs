using TapirSLMP.Client;
using TapirSLMP.Liberator;

var builder = Host.CreateApplicationBuilder(args);
var liberatorOptions = LiberatorOptions.FromConfiguration(builder.Configuration);
var clientOptions = FreezerClientOptions.FromConfiguration(builder.Configuration);
liberatorOptions.ValidateAgainstGatewayTimeout(clientOptions.RequestTimeout);

builder.Services.AddTapirFreezerClient(clientOptions);
builder.Services.AddTapirLiberator(liberatorOptions);

await builder.Build().RunAsync().ConfigureAwait(false);
