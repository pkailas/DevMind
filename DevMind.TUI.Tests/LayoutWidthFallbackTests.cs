// File: LayoutWidthFallbackTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Which width a table or a wrapped line is fitted to when the view has none.
//
// A resumed conversation is replayed before Terminal.Gui has laid the window out, so the
// output view reports a width of zero. The first version of the replay fell straight through
// to the 100-column constant, and every table in it wrapped at half of a 200-column screen —
// which looked like a decision not to use the width, and was not one. The console knows its
// own width long before the view does, so it is the second thing asked, and the constant is
// the last.

using Xunit;

namespace DevMind.TUI.Tests
{
    public class LayoutWidthFallbackTests
    {
        [Fact]
        public void TheViewsWidthWinsWhenItHasOne()
        {
            Assert.Equal(180, PipeTable.ResolveWidth(viewWidth: 180, consoleWidth: 200));
        }

        [Fact]
        public void BeforeTheFirstLayout_TheConsoleWidthIsUsed_NotTheConstant()
        {
            // The replay case: the view has no size yet, the console does.
            Assert.Equal(200, PipeTable.ResolveWidth(viewWidth: 0, consoleWidth: 200));
        }

        [Fact]
        public void TheConstantIsOnlyForWhenNothingCanSay()
        {
            Assert.Equal(PipeTable.FallbackWidth, PipeTable.ResolveWidth(viewWidth: 0, consoleWidth: 0));
            Assert.Equal(PipeTable.FallbackWidth, PipeTable.ResolveWidth(viewWidth: -1, consoleWidth: -1));
        }
    }
}
