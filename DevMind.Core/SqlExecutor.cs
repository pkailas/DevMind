// File: SqlExecutor.cs
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Shared Core helper for executing read-only SQL queries.
// Used by both ConsoleAgenticHost and TuiAgenticHost.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.SqlClient;

namespace DevMind
{
    /// <summary>
    /// Shared Core helper for executing read-only SQL queries and formatting results.
    /// Read-only guard, transaction rollback, connection string masking all enforced here.
    /// </summary>
    public static class SqlExecutor
    {
        private static readonly string[] _writeKeywords =
        { "INSERT", "UPDATE", "DELETE", "MERGE", "DROP", "ALTER", "CREATE", "TRUNCATE", "EXEC" };

        /// <summary>
        /// Executes a SQL query with read-only guard and returns formatted results.
        /// </summary>
        /// <param name="query">SQL query to execute.</param>
        /// <param name="connectionString">Database connection string (never logged).</param>
        /// <param name="allowWrite">If true, skip the read-only guard.</param>
        /// <param name="maxRows">Maximum rows to return (0 = use all).</param>
        /// <param name="commandTimeout">Command timeout in seconds.</param>
        /// <returns>Formatted text table or error message.</returns>
        public static string ExecuteQuery(
            string query,
            string connectionString,
            bool allowWrite,
            int maxRows,
            int commandTimeout)
        {
            if (string.IsNullOrWhiteSpace(query))
                return "[ERROR] Empty query.";

            if (string.IsNullOrWhiteSpace(connectionString))
                return "[ERROR] No connection string available.";

            // Read-only guard
            if (!allowWrite)
            {
                var trimmed = query.Trim();
                var upper = trimmed.ToUpperInvariant();

                // Check for multiple statements (semicolon outside string literals)
                if (trimmed.Contains(';'))
                    return "[ERROR] Multiple statements are not allowed in read-only mode. Use a single SELECT statement.";

                // Must start with SELECT or WITH (CTE)
                if (!upper.StartsWith("SELECT") && !upper.StartsWith("WITH"))
                    return "[ERROR] Read-only mode: only SELECT statements are allowed.";

                // Check for write keywords
                foreach (var kw in _writeKeywords)
                {
                    // Check for EXEC/sp_executesql
                    if (kw == "EXEC" && (upper.Contains("EXEC ") || upper.Contains("EXECUTE ") || upper.Contains("SP_EXECUTESQL")))
                        return "[ERROR] Read-only mode: EXEC/EXECUTE/SP_EXECUTESQL is not allowed.";

                    // Check for SELECT...INTO
                    if (kw == "INSERT" && upper.Contains("SELECT") && upper.Contains("INTO"))
                        return "[ERROR] Read-only mode: SELECT...INTO is not allowed.";
                }
            }

            try
            {
                using var connection = new SqlConnection(connectionString);
                connection.Open();

                using var transaction = connection.BeginTransaction();
                try
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = query;
                    command.CommandTimeout = commandTimeout;

                    var rows = new List<List<string>>();
                    var columnNames = new List<string>();
                    int totalRows = 0;

                    using var reader = command.ExecuteReader();
                    for (int i = 0; i < reader.FieldCount; i++)
                        columnNames.Add(reader.GetName(i));

                    while (reader.Read())
                    {
                        if (maxRows > 0 && rows.Count >= maxRows)
                        {
                            totalRows++; // count truncated rows
                            // skip remaining
                            while (reader.Read()) totalRows++;
                            break;
                        }
                        totalRows++;
                        var row = new List<string>();
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            var val = reader.IsDBNull(i) ? "(null)" : reader.GetValue(i)?.ToString() ?? "(null)";
                            // Cap cell width at 80 chars
                            if (val.Length > 80) val = val.Substring(0, 77) + "...";
                            row.Add(val);
                        }
                        rows.Add(row);
                    }

                    // Rollback transaction (always, since we're read-only)
                    transaction.Rollback();

                    // Format as text table
                    return FormatTable(columnNames, rows, totalRows, maxRows);
                }
                catch
                {
                    // Attempt rollback on error
                    try { transaction.Rollback(); } catch { /* ignore */ }
                    throw;
                }
            }
            catch (SqlException ex)
            {
                return $"[SQL ERROR] {ex.Message}";
            }
            catch (Exception ex)
            {
                return $"[ERROR] {ex.Message}";
            }
        }

        /// <summary>
        /// Formats query results as a compact text table.
        /// </summary>
        private static string FormatTable(List<string> columns, List<List<string>> rows, int totalRows, int maxRows)
        {
            if (columns == null || columns.Count == 0)
                return "[No columns returned]";

            var sb = new StringBuilder();

            // Calculate column widths
            var widths = new int[columns.Count];
            for (int i = 0; i < columns.Count; i++)
                widths[i] = columns[i].Length;

            foreach (var row in rows)
                for (int i = 0; i < row.Count && i < columns.Count; i++)
                    widths[i] = Math.Max(widths[i], row[i].Length);

            // Header
            var header = new StringBuilder();
            var separator = new StringBuilder();
            header.Append("| ");
            separator.Append("|-");
            for (int i = 0; i < columns.Count; i++)
            {
                var cell = columns[i].PadRight(widths[i]);
                header.Append(cell).Append(" | ");
                separator.Append(new string('-', widths[i] + 2)).Append("-");
            }
            sb.AppendLine(header.ToString());
            sb.AppendLine(separator.ToString());

            // Rows
            foreach (var row in rows)
            {
                var line = new StringBuilder();
                line.Append("| ");
                for (int i = 0; i < columns.Count; i++)
                {
                    var val = i < row.Count ? row[i] : "";
                    line.Append(val.PadRight(widths[i])).Append(" | ");
                }
                sb.AppendLine(line.ToString());
            }

            // Row count
            if (maxRows > 0 && totalRows > maxRows)
                sb.AppendLine($"Showing {maxRows} of {totalRows} rows (truncated).");
            else
                sb.AppendLine($"{totalRows} row{(totalRows == 1 ? "" : "s")}.");

            return sb.ToString();
        }

        /// <summary>
        /// Masks a connection string for logging (replaces password values with ***).
        /// </summary>
        public static string MaskConnectionString(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                return "(null)";

            var masked = connectionString;
            var lower = masked.ToLowerInvariant();

            // Mask Password
            if (lower.Contains("password=") || lower.Contains("pwd="))
            {
                var pattern = lower.Contains("password=") ? "password=" : "pwd=";
                var idx = masked.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var after = masked.Substring(idx + pattern.Length);
                    var end = after.IndexOf(';');
                    if (end > 0)
                        masked = masked.Substring(0, idx + pattern.Length) + "***" + after.Substring(end);
                    else
                        masked = masked.Substring(0, idx + pattern.Length) + "***";
                }
            }

            // Mask Secret
            if (lower.Contains("secret="))
            {
                var idx = masked.IndexOf("secret=", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var after = masked.Substring(idx + 7);
                    var end = after.IndexOf(';');
                    if (end > 0)
                        masked = masked.Substring(0, idx + 7) + "***" + after.Substring(end);
                    else
                        masked = masked.Substring(0, idx + 7) + "***";
                }
            }

            return masked;
        }
    }
}
