// File: PackageIds.cs
// Copyright (c) iOnline Consulting LLC. All rights reserved.

namespace DevMind
{
    /// <summary>
    /// Package GUID constants matching the VSCT command table symbols.
    /// </summary>
    internal static class PackageGuids
    {
        /// <summary>GUID string for the DevMind package and command set.</summary>
        public const string DevMindPackageGuidString = "e05eada1-21cb-451b-9626-e30e9e6344ae";
    }

    /// <summary>
    /// Command ID constants matching the VSCT command table symbols.
    /// </summary>
    internal static class PackageIds
    {
        /// <summary>Command ID for the Show Tool Window command.</summary>
        public const int ShowToolWindow = 0x0100;
    }
}
