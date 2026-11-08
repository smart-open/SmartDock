using Npgsql;

// 一次性执行 deploy/sql 下的迁移脚本（M0 简版；M1 换正式迁移体系）
// 用法：
//   dotnet run --project tools/DbInit [-- 连接串]                整库初始化（建库+DDL）
//   dotnet run --project tools/DbInit -- --verify                查 point_history 行数与最近 5 行
//   dotnet run --project tools/DbInit -- --sql "<语句>"           执行单条 SQL（场景测试数据准备）
//   dotnet run --project tools/DbInit -- --sqlfile <路径>         执行 SQL 文件（多语句，整文件一次执行）
//   dotnet run --project tools/DbInit -- --scalar "<查询>"        执行查询返回单值（场景断言用）
//   dotnet run --project tools/DbInit -- --scalarfile <路径>      执行查询文件返回单值（场景断言用，避开 PS 引号问题）
var rawArgs = args.ToList();
var verifyMode = rawArgs.Remove("--verify");

var sqlMode = rawArgs.Remove("--sql");
var sqlFileMode = rawArgs.Remove("--sqlfile");
var scalarMode = rawArgs.Remove("--scalar");
var scalarFileMode = rawArgs.Remove("--scalarfile");
string? sqlPayload = null;
if (sqlMode || sqlFileMode || scalarMode || scalarFileMode)
{
    sqlPayload = rawArgs.Count > 0 ? rawArgs[0] : throw new ArgumentException("--sql/--sqlfile/--scalar/--scalarfile requires an argument");
    rawArgs.RemoveAt(0);

    // scalar/sql 的参数是内联语句；sqlfile/scalarfile 的参数是文件路径
    if (sqlFileMode || scalarFileMode)
    {
        sqlPayload = File.ReadAllText(sqlPayload);
    }
}

// 场景参数取走之后，剩余的第一个参数才是连接串（否则文件路径会被误当连接串）
var connectionString = rawArgs.Count > 0
    ? rawArgs[0]
    : "Host=192.168.106.128;Port=5435;Database=postgres;Username=postgres;Password=Postgres123!";
var dbConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { Database = "smartdock" }.ConnectionString;

// 场景模式：只需要 smartdock 库连接，跳过建库/DDL 流程
if (sqlMode || sqlFileMode || scalarMode || scalarFileMode)
{
    await using var scenarioConnection = new NpgsqlConnection(dbConnectionString);
    await scenarioConnection.OpenAsync();
    await using var scenarioCommand = new NpgsqlCommand(sqlPayload, scenarioConnection);

    if (scalarMode || scalarFileMode)
    {
        var result = await scenarioCommand.ExecuteScalarAsync();
        Console.WriteLine(result?.ToString() ?? "NULL");
    }
    else
    {
        var affected = await scenarioCommand.ExecuteNonQueryAsync();
        Console.WriteLine($"OK ({affected} rows affected)");
    }

    return;
}
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
