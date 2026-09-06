namespace TapirSLMP.Storage.Postgres;

public sealed class PostgresJobStoreOptions
{
    public string ConnectionString { get; set; } =
        "Host=localhost;Port=5432;Database=tapirslmp;Username=tapirslmp;Password=change-me";
}
