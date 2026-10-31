// File: MemorySavePromptTests.cs  v1.0
//
// The "Save memory?" prompt's text and answer rules (MemorySavePrompt), without a running
// Terminal.Gui app. The dialog itself is driven in PromptTextDialogTests.

using System;
using System.IO;
using Xunit;

namespace DevMind.TUI.Tests
{
    public class MemorySavePromptTests
    {
        [Fact]
        public void Message_NamesTheTopic_TheSize_AndTheThreeAnswers()
        {
            string msg = MemorySavePrompt.BuildMessage("sap-spec", "SAP spec notes", 1234, @"C:\work\repo\.devmind\memory");

            Assert.Equal(
                "\"sap-spec\" — SAP spec notes (1,234 chars)\n" +
                @"1 = this folder (C:\work\repo\.devmind\memory) · 2 = global (all folders) · Esc = don't save",
                msg);
        }

        [Fact]
        public void Message_NoDescription_UsesTheSlug_AndALongOneIsCut()
        {
            Assert.StartsWith("\"t\" — t (0 chars)\n", MemorySavePrompt.BuildMessage("t", null, 0, @"C:\r"));

            string longDesc = new string('d', 200);
            string first = MemorySavePrompt.BuildMessage("t", longDesc, 1, @"C:\r").Split('\n')[0];
            Assert.Contains(new string('d', 79) + "…", first);
            Assert.DoesNotContain(new string('d', 80), first);
        }

        [Theory]
        [InlineData("1")]
        [InlineData("2")]
        [InlineData(" 2 ")]
        public void Validate_Accepts1Or2(string input) => Assert.Null(MemorySavePrompt.Validate(input));

        [Theory]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData("3")]
        [InlineData("yes")]
        public void Validate_ReAsks_OnAnythingElse(string input)
            => Assert.Equal(MemorySavePrompt.ReAskMessage, MemorySavePrompt.Validate(input));

        [Fact]
        public void ToChoice_MapsTheAnswers()
        {
            Assert.Equal(MemorySaveChoice.Repo, MemorySavePrompt.ToChoice("1"));
            Assert.Equal(MemorySaveChoice.Global, MemorySavePrompt.ToChoice(" 2"));
            Assert.Equal(MemorySaveChoice.Declined, MemorySavePrompt.ToChoice(null));
        }

        [Fact]
        public void ShortenPath_HomeBecomesTilde_AndALongPathKeepsItsTail()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.Equal(@"~\src\.devmind\memory", MemorySavePrompt.ShortenPath(Path.Combine(home, "src", ".devmind", "memory")));

            string deep = @"D:\a-very-long-folder-name\another-long-folder-name\Documents\ComfyUI\workflows\.devmind\memory";
            Assert.Equal(@"D:\…\workflows\.devmind\memory", MemorySavePrompt.ShortenPath(deep));
            Assert.Equal(@"D:\short\.devmind\memory", MemorySavePrompt.ShortenPath(@"D:\short\.devmind\memory"));
        }
    }
}
