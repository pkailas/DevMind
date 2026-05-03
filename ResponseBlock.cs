// File: ResponseBlock.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

namespace DevMind
{
    public class ResponseBlock
    {
        public BlockType Type { get; set; }
        public string Content { get; set; }   // raw text, file source, or full PATCH text
        public string FileName { get; set; }  // for File, Patch, ReadRequest, Grep
        public string Command { get; set; }   // for Shell
        public int  RangeStart    { get; set; }  // for ReadRequest/Grep line-range (0 = full read)
        public int  RangeEnd      { get; set; }  // for ReadRequest/Grep line-range (0 = full read)
        public bool ForceFullRead { get; set; }  // READ! — bypass outline-first behavior
        public string Pattern     { get; set; }  // for Grep/Find — substring search pattern
        public string GlobPattern { get; set; }  // for Find — glob file pattern (e.g. *.cs, Services/*.cs)
        public string RenameFrom  { get; set; }  // for Rename — source filename
        public string RenameTo    { get; set; }  // for Rename — destination filename
        public string TestProject { get; set; }  // for Test — project file path or name
        public string TestFilter  { get; set; }  // for Test — optional filter string (nullable)
        public string MemoryTopic { get; set; }       // for RecallMemory, SaveMemory — topic slug
        public string MemoryContent { get; set; }     // for SaveMemory — content to save
        public string MemoryDescription { get; set; } // for SaveMemory — index description
        public bool FromToolCall { get; set; }        // true when block originated from a tool_call (skip fence stripping)
        public string ListFilesGlob { get; set; }     // for ListFiles — glob pattern (e.g. *.cs, Services/*.cs)
        public bool ListFilesRecursive { get; set; }  // for ListFiles — whether to recurse into subdirectories
    }
}
