// File: DiffFixture.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// One old/new pair, shared by the two diff tests that must agree about it: the line model
// and the text projection. It is deliberately two hunks with an insertion in the SECOND —
// the shape that makes an off-by-one in the line numbering visible, because every new-file
// number after the insertion is one ahead of its old-file number.

namespace DevMind.Core.Tests
{
    internal static class DiffFixture
    {
        public const string Old =
            "using System;\n" +
            "\n" +
            "namespace Demo\n" +
            "{\n" +
            "    public class Widget\n" +
            "    {\n" +
            "        public int Count;\n" +
            "\n" +
            "        public void Reset()\n" +
            "        {\n" +
            "            Count = 0;\n" +
            "        }\n" +
            "\n" +
            "        public void Bump()\n" +
            "        {\n" +
            "            Count++;\n" +
            "        }\n" +
            "    }\n" +
            "}\n";

        public const string New =
            "using System;\n" +
            "\n" +
            "namespace Demo\n" +
            "{\n" +
            "    public class Widget\n" +
            "    {\n" +
            "        public int Count = 1;\n" +
            "\n" +
            "        public void Reset()\n" +
            "        {\n" +
            "            Count = 0;\n" +
            "        }\n" +
            "\n" +
            "        public void Bump()\n" +
            "        {\n" +
            "            // keep it positive\n" +
            "            Count++;\n" +
            "        }\n" +
            "    }\n" +
            "}\n";
    }
}
