// File: TuiConfigReleaseImagesTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The releaseImagesAfterTurn setting: on by default (absent key or no file), and an
// explicit false in the file reads back as false.

using System.IO;
using Xunit;

namespace DevMind.TUI.Tests
{
    public class TuiConfigReleaseImagesTests
    {
        [Fact]
        public void ReleaseImagesAfterTurnDefaultsToTrue_WhenTheKeyIsAbsent()
        {
            string missing = Path.Combine(Path.GetTempPath(), $"devmind-missing-{System.Guid.NewGuid():N}.json");
            Assert.True(TuiConfig.LoadFrom(missing).ReleaseImagesAfterTurn);

            string path = Path.Combine(Path.GetTempPath(), $"devmind-{System.Guid.NewGuid():N}.json");
            File.WriteAllText(path, "{ \"autoAttachImages\": false }");
            try
            {
                var config = TuiConfig.LoadFrom(path);
                Assert.True(config.ReleaseImagesAfterTurn);
                Assert.False(config.AutoAttachImages); // the neighbour key still reads
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ReleaseImagesAfterTurnReadsFalseFromTheFile()
        {
            string path = Path.Combine(Path.GetTempPath(), $"devmind-{System.Guid.NewGuid():N}.json");
            File.WriteAllText(path, "{ \"releaseImagesAfterTurn\": false }");
            try
            {
                Assert.False(TuiConfig.LoadFrom(path).ReleaseImagesAfterTurn);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
