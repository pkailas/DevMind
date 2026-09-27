// File: TuiConfigProseMaxWidthTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The proseMaxWidth setting: the default when the key is absent, and the value
// when it is present. A file with no proseMaxWidth must give the default — not
// zero, not the view width — because the cap is the point and its absence from
// the file is not a reason to drop it.

using System.IO;
using Xunit;

namespace DevMind.TUI.Tests
{
    public class TuiConfigProseMaxWidthTests
    {
        [Fact]
        public void ProseMaxWidthDefaultsTo110_WhenTheKeyIsAbsent()
        {
            // No file at all: defaults.
            string missing = Path.Combine(Path.GetTempPath(), $"devmind-missing-{System.Guid.NewGuid():N}.json");
            Assert.Equal(110, TuiConfig.LoadFrom(missing).ProseMaxWidth);

            // A file that does not name the key: same default.
            string path = Path.Combine(Path.GetTempPath(), $"devmind-{System.Guid.NewGuid():N}.json");
            File.WriteAllText(path, "{ \"outputLineCap\": 3 }");
            try
            {
                var config = TuiConfig.LoadFrom(path);
                Assert.Equal(110, config.ProseMaxWidth);
                // The neighbour setting is unaffected by the new key's absence.
                Assert.Equal(3, config.OutputLineCap);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ProseMaxWidthReadsTheValueFromTheFile()
        {
            string path = Path.Combine(Path.GetTempPath(), $"devmind-{System.Guid.NewGuid():N}.json");
            File.WriteAllText(path, "{ \"proseMaxWidth\": 90 }");
            try
            {
                Assert.Equal(90, TuiConfig.LoadFrom(path).ProseMaxWidth);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ProseMaxWidthZeroMeansUncapped_AndReadsBackAsZero()
        {
            // 0 is a deliberate "no cap", not "not set": it must read back as 0,
            // because a file that then lost its zero would silently re-cap the prose
            // on the next launch.
            string path = Path.Combine(Path.GetTempPath(), $"devmind-{System.Guid.NewGuid():N}.json");
            File.WriteAllText(path, "{ \"proseMaxWidth\": 0 }");
            try
            {
                var config = TuiConfig.LoadFrom(path);
                Assert.Equal(0, config.ProseMaxWidth);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
