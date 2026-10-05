// File: SqlExecutorSqliteTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-73: run_sql (SqlExecutor) sent every connection string to SqlClient. In job-2166 the
// agent pointed it at the VLink test SQLite database four ways and burned ~4 iterations on
// SQL Server "server was not found" errors and "Keyword not supported: 'filename'". A clearly
// SQLite string now opens through Microsoft.Data.Sqlite, always Mode=ReadOnly.

using Microsoft.Data.Sqlite;
using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class SqlExecutorSqliteTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _db;

        public SqlExecutorSqliteTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_h73_sqlite_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _db = Path.Combine(_dir, "test.db");
            using var conn = new SqliteConnection($"Data Source={_db}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE Settings (Key TEXT, Value TEXT);" +
                "INSERT INTO Settings VALUES ('Dispatch:OrderingKey', 'abc'), ('Smtp:Host', 'smtp.local');";
            cmd.ExecuteNonQuery();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        // The four strings job-2166's agent tried, in order.
        [Theory]
        [InlineData(@"Data Source=C:\Users\pkailas\AppData\Local\Temp\vlinkwh-tests\e6b37a4ea1cf4b5f9e904b98dba94e56\data\test.db", SqlExecutor.SqlProvider.Sqlite)]
        [InlineData(@"Filename=C:\Users\pkailas\AppData\Local\Temp\vlinkwh-tests\e6b37a4ea1cf4b5f9e904b98dba94e56\data\test.db", SqlExecutor.SqlProvider.Sqlite)]
        [InlineData("Data Source=:memory:", SqlExecutor.SqlProvider.Sqlite)]
        [InlineData("Data Source=nonexistent-test", SqlExecutor.SqlProvider.SqlServer)] // a server name, as before
        // Neighbours: other SQLite extensions, and SQL Server strings that must not move.
        [InlineData("DataSource=app.sqlite3", SqlExecutor.SqlProvider.Sqlite)]
        [InlineData("Data Source=\"C:\\a b\\app.SQLITE\";Cache=Shared", SqlExecutor.SqlProvider.Sqlite)]
        [InlineData("Server=prod.db;Database=x;Trusted_Connection=True;", SqlExecutor.SqlProvider.SqlServer)]
        [InlineData("Data Source=sql01.db;Initial Catalog=App;Integrated Security=True", SqlExecutor.SqlProvider.SqlServer)]
        [InlineData("Data Source=(localdb)\\MSSQLLocalDB;Initial Catalog=App", SqlExecutor.SqlProvider.SqlServer)]
        [InlineData("not a connection string at all ===", SqlExecutor.SqlProvider.SqlServer)]
        [InlineData("", SqlExecutor.SqlProvider.SqlServer)]
        public void DetectProvider(string connectionString, SqlExecutor.SqlProvider expected)
            => Assert.Equal(expected, SqlExecutor.DetectProvider(connectionString));

        [Theory]
        [InlineData("Data Source={0}")]
        [InlineData("Filename={0}")]
        public void SelectAgainstSqliteFile_ReturnsRows(string template)
        {
            string result = SqlExecutor.ExecuteQuery(
                "SELECT Key, Value FROM Settings ORDER BY Key", string.Format(template, _db),
                allowWrite: false, maxRows: 100, commandTimeout: 30, out bool opened);

            Assert.True(opened, result);
            Assert.Contains("Dispatch:OrderingKey", result);
            Assert.Contains("smtp.local", result);
            Assert.Contains("2 rows.", result);
        }

        [Fact]
        public void InMemory_OpensReadOnly()
        {
            string result = SqlExecutor.ExecuteQuery("SELECT 1 AS One", "Data Source=:memory:",
                allowWrite: false, maxRows: 100, commandTimeout: 30, out bool opened);
            Assert.True(opened, result);
            Assert.Contains("1 row.", result);
        }

        [Fact]
        public void WriteWithAllowWrite_IsRefusedBySqlite_FileUnchanged()
        {
            // allow_write skips the keyword guard, but SQLite is opened Mode=ReadOnly regardless.
            string result = SqlExecutor.ExecuteQuery("DELETE FROM Settings", $"Data Source={_db};Mode=ReadWrite",
                allowWrite: true, maxRows: 100, commandTimeout: 30, out _);
            Assert.StartsWith("[SQL ERROR]", result);
            Assert.Contains("readonly", result, StringComparison.OrdinalIgnoreCase);

            string count = SqlExecutor.ExecuteQuery("SELECT COUNT(*) AS N FROM Settings", $"Data Source={_db}",
                allowWrite: false, maxRows: 100, commandTimeout: 30, out _);
            Assert.Contains("| 2 ", count);
        }

        [Fact]
        public void MissingFile_IsNotCreated()
        {
            string missing = Path.Combine(_dir, "missing.db");
            string result = SqlExecutor.ExecuteQuery("SELECT 1", $"Data Source={missing}",
                allowWrite: false, maxRows: 100, commandTimeout: 30, out bool opened);
            Assert.False(opened);
            Assert.StartsWith("[SQL ERROR]", result);
            Assert.False(File.Exists(missing));
        }
    }
}
