using Xunit;

namespace DevMind.Core.Tests
{
    public class SqlExecutorTests
    {
        private static string FakeConnectionString => "Server=localhost;Database=test;Trusted_Connection=True;";

        [Fact]
        public void ExecuteQuery_WithCteDelete_Rejected()
        {
            var query = "WITH cte AS (SELECT Id FROM parsely.FieldLocators) DELETE FROM parsely.FieldLocators WHERE Id IN (SELECT Id FROM cte)";
            var result = SqlExecutor.ExecuteQuery(query, FakeConnectionString, allowWrite: false, maxRows: 100, commandTimeout: 30, out _);

            Assert.Contains("Read-only mode", result);
            Assert.DoesNotContain("[No columns returned]", result);
        }

        [Fact]
        public void ExecuteQuery_Delete_Rejected()
        {
            var query = "DELETE FROM parsely.FieldLocators WHERE Id = 1";
            var result = SqlExecutor.ExecuteQuery(query, FakeConnectionString, allowWrite: false, maxRows: 100, commandTimeout: 30, out _);

            Assert.Contains("Read-only mode", result);
        }

        [Fact]
        public void ExecuteQuery_Update_Rejected()
        {
            var query = "UPDATE parsely.FieldLocators SET Name = 'test' WHERE Id = 1";
            var result = SqlExecutor.ExecuteQuery(query, FakeConnectionString, allowWrite: false, maxRows: 100, commandTimeout: 30, out _);

            Assert.Contains("Read-only mode", result);
        }

        [Fact]
        public void ExecuteQuery_Insert_Rejected()
        {
            var query = "INSERT INTO parsely.FieldLocators (Name) VALUES ('test')";
            var result = SqlExecutor.ExecuteQuery(query, FakeConnectionString, allowWrite: false, maxRows: 100, commandTimeout: 30, out _);

            Assert.Contains("Read-only mode", result);
        }

        [Fact]
        public void ExecuteQuery_Merge_Rejected()
        {
            var query = "MERGE INTO parsely.FieldLocators AS target USING (SELECT 1 AS Id) AS source ON target.Id = source.Id WHEN MATCHED THEN UPDATE SET Name = 'x'";
            var result = SqlExecutor.ExecuteQuery(query, FakeConnectionString, allowWrite: false, maxRows: 100, commandTimeout: 30, out _);

            Assert.Contains("Read-only mode", result);
        }

        [Fact]
        public void ExecuteQuery_Drop_Rejected()
        {
            var query = "DROP TABLE parsely.FieldLocators";
            var result = SqlExecutor.ExecuteQuery(query, FakeConnectionString, allowWrite: false, maxRows: 100, commandTimeout: 30, out _);

            Assert.Contains("Read-only mode", result);
        }

        [Fact]
        public void ExecuteQuery_Alter_Rejected()
        {
            var query = "ALTER TABLE parsely.FieldLocators ADD Col INT";
            var result = SqlExecutor.ExecuteQuery(query, FakeConnectionString, allowWrite: false, maxRows: 100, commandTimeout: 30, out _);

            Assert.Contains("Read-only mode", result);
        }

        [Fact]
        public void ExecuteQuery_Create_Rejected()
        {
            var query = "CREATE TABLE parsely.NewTable (Id INT)";
            var result = SqlExecutor.ExecuteQuery(query, FakeConnectionString, allowWrite: false, maxRows: 100, commandTimeout: 30, out _);

            Assert.Contains("Read-only mode", result);
        }

        [Fact]
        public void ExecuteQuery_Truncate_Rejected()
        {
            var query = "TRUNCATE TABLE parsely.FieldLocators";
            var result = SqlExecutor.ExecuteQuery(query, FakeConnectionString, allowWrite: false, maxRows: 100, commandTimeout: 30, out _);

            Assert.Contains("Read-only mode", result);
        }

        [Fact]
        public void ExecuteQuery_Exec_Rejected()
        {
            var query = "EXEC sp_help";
            var result = SqlExecutor.ExecuteQuery(query, FakeConnectionString, allowWrite: false, maxRows: 100, commandTimeout: 30, out _);

            Assert.Contains("Read-only mode", result);
        }

        [Fact]
        public void ExecuteQuery_Execute_Rejected()
        {
            var query = "EXECUTE sp_help";
            var result = SqlExecutor.ExecuteQuery(query, FakeConnectionString, allowWrite: false, maxRows: 100, commandTimeout: 30, out _);

            Assert.Contains("Read-only mode", result);
        }

        [Fact]
        public void ExecuteQuery_SelectInto_Rejected()
        {
            var query = "SELECT Id INTO NewTable FROM parsely.FieldLocators";
            var result = SqlExecutor.ExecuteQuery(query, FakeConnectionString, allowWrite: false, maxRows: 100, commandTimeout: 30, out _);

            Assert.Contains("Read-only mode", result);
        }

        [Fact]
        public void ExecuteQuery_WithCteSelect_Allowed()
        {
            var query = "WITH cte AS (SELECT Id FROM parsely.FieldLocators) SELECT * FROM cte";
            var result = SqlExecutor.ExecuteQuery(query, FakeConnectionString, allowWrite: false, maxRows: 100, commandTimeout: 30, out _);

            Assert.DoesNotContain("Read-only mode", result);
        }

        [Fact]
        public void ExecuteQuery_SelectAllowed()
        {
            var query = "SELECT Id FROM parsely.FieldLocators";
            var result = SqlExecutor.ExecuteQuery(query, FakeConnectionString, allowWrite: false, maxRows: 100, commandTimeout: 30, out _);

            Assert.DoesNotContain("Read-only mode", result);
        }

        [Fact]
        public void ExecuteQuery_AllowWrite_SkipsGuard()
        {
            var query = "DELETE FROM parsely.FieldLocators WHERE Id = 1";
            var result = SqlExecutor.ExecuteQuery(query, FakeConnectionString, allowWrite: true, maxRows: 100, commandTimeout: 30, out _);

            Assert.DoesNotContain("Read-only mode", result);
        }
    }
}
