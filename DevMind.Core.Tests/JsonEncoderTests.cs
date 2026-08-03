using System.Text.Encodings.Web;
using System.Text.Json;
using Xunit;

namespace DevMind.Core.Tests;

public class JsonEncoderTests
{
    /*
     * Proves that JsonSerializer with JavaScriptEncoder.UnsafeRelaxedJsonEscaping
     * does NOT escape HTML-sensitive characters (+ > < ' ` &) that appear in
     * unified diffs, code samples, and shell output.
     *
     * This is the encoder used by DevMind.McpServer.JsonOpts for all MCP tool
     * return payloads (devmind_task_*, run_shell, shell_job_status, http_request).
     * The JsonOpts fields themselves are private and live in McpServer (outside
     * this test project), so we exercise the encoder directly here.
     */
    [Fact]
    public void RelaxedEncoder_PreservesDiffMarkersAndCodeChars()
    {
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        var payload = new
        {
            text = "+ > < ' ` &",
            diff = @"--- a/file.cs
+++ b/file.cs
@@ -1,3 +1,4 @@
 using System;
+using System.Linq;
 public class Foo { }",
        };

        string json = JsonSerializer.Serialize(payload, opts);

        // The relaxed encoder must NOT escape these HTML-sensitive chars.
        Assert.DoesNotContain(@"\u002B", json); // +
        Assert.DoesNotContain(@"\u003E", json); // >
        Assert.DoesNotContain(@"\u003C", json); // <
        Assert.DoesNotContain(@"\u0027", json); // '
        Assert.DoesNotContain(@"\u0060", json); // `
        Assert.DoesNotContain(@"\u0026", json); // &

        // Literal characters must appear in the output.
        Assert.Contains("\"+ > < ' ` &\"", json);
        Assert.Contains("+using System.Linq;", json);
        Assert.Contains("+++ b/file.cs", json);
    }

    [Fact]
    public void RelaxedEncoder_StillEscapesJsonRequiredChars()
    {
        var opts = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        string json = JsonSerializer.Serialize(new { value = "line1\nline2\ttab\"quote" }, opts);

        // JSON-required escapes must still be present.
        Assert.Contains("\\n", json);
        Assert.Contains("\\t", json);
        Assert.Contains("\\\"", json);
    }
}
