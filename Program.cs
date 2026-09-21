using System.Diagnostics;
using Npgsql;
using StackExchange.Redis;
using Enyim.Caching;
using Enyim.Caching.Configuration;
using Enyim.Caching.Memcached;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// --- НАСТРОЙКИ ПОДКЛЮЧЕНИЙ ---
const string PgConnectionString = "Host=127.0.0.1;Port=5433;Username=postgres;Password=123456789;Database=testdb";
const string RedisConnectionString = "127.0.0.1:6380,abortConnect=false,connectTimeout=5000,syncTimeout=5000";
const string MemcachedHost = "127.0.0.1";
const int MemcachedPort = 11211;

// Количество запросов для теста
const int Iterations = 100;

// --- ИНИЦИАЛИЗАЦИЯ REDIS ---
Console.WriteLine("Подключение к Redis...");
var redis = ConnectionMultiplexer.Connect(RedisConnectionString);
var redisDb = redis.GetDatabase();

// --- ИНИЦИАЛИЗАЦИЯ MEMCACHED ---
Console.WriteLine("Подключение к Memcached...");
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Warning);
});

var memcachedOptions = Options.Create(new MemcachedClientOptions());
memcachedOptions.Value.AddServer(MemcachedHost, MemcachedPort);

var memcachedConfig = new MemcachedClientConfiguration(loggerFactory, memcachedOptions);
var memcachedClient = new MemcachedClient(loggerFactory, memcachedConfig);

Console.WriteLine("Подготовка базы данных...");
PrepareDatabase();

// --- ЗАПУСК ТЕСТОВ ---
var timeNoCache = RunTest("Без кэша", GetUserFromDb);

var timeRedis = RunTest("Redis", (id) => GetUserWithCache(id,
    key => redisDb.StringGet(key),
    (key, val) => redisDb.StringSet(key, val, TimeSpan.FromMinutes(1))));

var timeMemcached = RunTest("Memcached", (id) => GetUserWithCache(id,
    key => memcachedClient.Get(key) as string,
    (key, val) => memcachedClient.Store(StoreMode.Set, key, val, TimeSpan.FromSeconds(60))));

// --- ИТОГИ ---
Console.WriteLine("\n=== ИТОГИ ===");
Console.WriteLine($"Без кэша:  {timeNoCache:F2} сек");
Console.WriteLine($"Redis:     {timeRedis:F2} сек (Ускорение в {timeNoCache / timeRedis:F1} раз)");
Console.WriteLine($"Memcached: {timeMemcached:F2} сек (Ускорение в {timeNoCache / timeMemcached:F1} раз)");


// --- ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ ---

void PrepareDatabase()
{
    using var conn = new NpgsqlConnection(PgConnectionString);
    conn.Open();
    using var cmd = new NpgsqlCommand();
    cmd.Connection = conn;

    cmd.CommandText = "CREATE TABLE IF NOT EXISTS users (id SERIAL PRIMARY KEY, name VARCHAR(100), email VARCHAR(100));";
    cmd.ExecuteNonQuery();

    cmd.CommandText = "TRUNCATE TABLE users RESTART IDENTITY;";
    cmd.ExecuteNonQuery();

    for (int i = 0; i < 1000; i++)
    {
        cmd.CommandText = $"INSERT INTO users (name, email) VALUES ('User_{i}', 'user_{i}@example.com');";
        cmd.ExecuteNonQuery();
    }
    Console.WriteLine("База данных готова (1000 записей).");
}

string? GetUserFromDb(int userId)
{
    using var conn = new NpgsqlConnection(PgConnectionString);
    conn.Open();
    using var cmd = new NpgsqlCommand("SELECT name, email FROM users WHERE id = @id", conn);
    cmd.Parameters.AddWithValue("id", userId);

    Thread.Sleep(50);

    using var reader = cmd.ExecuteReader();
    if (reader.Read())
    {
        return $"{reader.GetString(0)}|{reader.GetString(1)}";
    }
    return null;
}

string? GetUserWithCache(int userId, Func<string, string?> getFromCache, Action<string, string> setToCache)
{
    string key = $"user:{userId}";

    var cachedData = getFromCache(key);
    if (cachedData != null)
    {
        return cachedData;
    }

    var data = GetUserFromDb(userId);
    if (data != null)
    {
        setToCache(key, data);
    }
    return data;
}

double RunTest(string mode, Func<int, string?> getUserFunc)
{
    Console.WriteLine($"\n--- Запуск теста: {mode} ---");
    var sw = Stopwatch.StartNew();

    int userId = 1;

    for (int i = 0; i < Iterations; i++)
    {
        getUserFunc(userId);
    }

    sw.Stop();
    double totalSeconds = sw.Elapsed.TotalSeconds;
    Console.WriteLine($"Общее время: {totalSeconds:F2} сек");
    Console.WriteLine($"Среднее время на запрос: {(totalSeconds / Iterations) * 1000:F2} мс");
    return totalSeconds;
}