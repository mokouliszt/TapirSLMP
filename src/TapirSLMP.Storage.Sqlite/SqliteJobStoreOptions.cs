namespace TapirSLMP.Storage.Sqlite;

public sealed class SqliteJobStoreOptions
{
    public string ConnectionString { get; set; } =
        "Data Source=data/tapirslmp.db;Cache=Shared;Pooling=True";

    public int BusyTimeoutMilliseconds { get; set; } = 5000;
}
