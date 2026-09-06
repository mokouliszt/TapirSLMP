using TapirSLMP.Client;
using TapirSLMP.Sealer;

var builder = Host.CreateApplicationBuilder(args);
var sealerOptions = SealerOptions.FromConfiguration(builder.Configuration);
var clientOptions = FreezerClientOptions.FromConfiguration(builder.Configuration);
sealerOptions.ValidateAgainstGatewayTimeout(clientOptions.RequestTimeout);

builder.Services.AddTapirFreezerClient(clientOptions);
builder.Services.AddTapirSealer(sealerOptions);

await builder.Build().RunAsync().ConfigureAwait(false);
