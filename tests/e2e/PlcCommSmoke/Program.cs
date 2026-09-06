using PlcComm.Slmp;

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 5000;
var options = new SlmpConnectionOptions(
    host,
    SlmpPlcProfile.IqR,
    port,
    SlmpTransportMode.Tcp,
    SlmpTargetAddress.OwnStation)
{
    Timeout = TimeSpan.FromSeconds(20),
};

await using var client = await SlmpClientFactory.OpenAndConnectAsync(options);
var address = SlmpDeviceParser.Parse("D200", SlmpPlcProfile.IqR);
await client.WriteWordsAsync(address, [0xBEEF]);
var result = await client.ReadWordsRawAsync(address, 1);
if (result.Length != 1 || result[0] != 0xBEEF)
{
    throw new InvalidOperationException(
        $"PlcComm.Slmp compatibility check failed: {string.Join(",", result.Select(value => value.ToString("X4")))}");
}

Console.WriteLine("PlcComm.Slmp compatibility round-trip passed");
