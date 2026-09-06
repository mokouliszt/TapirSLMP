using TapirSLMP.Freezer.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddTapirFreezer(builder.Configuration);

var app = builder.Build();
app.UseTapirFreezerHeaders();
app.MapTapirFreezer();

await app.Services.InitializeTapirFreezerAsync().ConfigureAwait(false);
await app.RunAsync().ConfigureAwait(false);
