using Npgsql;

// 一次性执行 deploy/sql 下的迁移脚本（M0 简版；M1 换正式迁移体系）
// 用法：dotnet run --project tools/DbInit [-- 连接串]；--verify 只查行数
var rawArgs = args.ToList();
var verifyMode = rawArgs.Remove("--verify");
var connectionString = rawArgs.Count > 0
    ? rawArgs[0]
    : "Host=192.168.106.128;Port=5435;Database=postgres;Username=postgres;Password=Postgres123!";
var dbConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { Database = "smartdock" }.ConnectionString;

if (verifyMode)
{
    await using var verifyConnection = new NpgsqlConnection(dbConnectionString);
    await verifyConnection.OpenAsync();
    var rowCount = await new NpgsqlCommand("SELECT COUNT(*) FROM point_history", verifyConnection).ExecuteScalarAsync();
    Console.WriteLine($"point_history rows: {rowCount}");

    await using var recentCommand = new NpgsqlCommand(
        "SELECT point_id, ts, value_num, quality FROM point_history ORDER BY ts DESC LIMIT 5", verifyConnection);
    await using var reader = await recentCommand.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var valueText = reader.IsDBNull(2) ? "null" : reader.GetDouble(2).ToString("F3");
        Console.WriteLine($"  point {reader.GetInt64(0)} @ {reader.GetFieldValue<DateTime>(1):HH:mm:ss} = {valueText} (q={reader.GetInt16(3)})");
    }

    return;
}

var sqlDir = Directory.GetCurrentDirectory();
for (var i = 0; i < 5 && !Directory.Exists(Path.Combine(sqlDir, "deploy", "sql")); i++)
{
    sqlDir = Path.GetDirectoryName(sqlDir)!;
}
sqlDir = Path.Combine(sqlDir, "deploy", "sql");
Console.WriteLine($"sql dir: {sqlDir}");

await using var connection = new NpgsqlConnection(connectionString);
await connection.OpenAsync();
Console.WriteLine($"connected: {connection.Database} @ {connection.Host}:{connection.Port}");

var dbExists = await new NpgsqlCommand(
    "SELECT 1 FROM pg_database WHERE datname = 'smartdock'", connection).ExecuteScalarAsync();
if (dbExists is null)
{
    await new NpgsqlCommand("CREATE DATABASE smartdock", connection).ExecuteNonQueryAsync();
    Console.WriteLine("database smartdock created");
}

await using var dbConnection = new NpgsqlConnection(dbConnectionString);
await dbConnection.OpenAsync();

// Npgsql 支持一个命令多个语句，整脚本执行（避免按 ; 拆坏 PARTITION OF 语法）
foreach (var scriptPath in Directory.GetFiles(sqlDir, "*.sql").OrderBy(path => path))
{
    Console.WriteLine($"applying {Path.GetFileName(scriptPath)}");
    var script = await File.ReadAllTextAsync(scriptPath);
    await using var command = new NpgsqlCommand(script, dbConnection);
    await command.ExecuteNonQueryAsync();
}

var tableExists = await new NpgsqlCommand(
    "SELECT 1 FROM information_schema.tables WHERE table_name = 'point_history'", dbConnection)
    .ExecuteScalarAsync();
Console.WriteLine(tableExists is not null ? "point_history READY" : "point_history MISSING");
Console.WriteLine("DDL complete");
