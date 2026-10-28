using Clean.Core;

namespace Clean.Core.Tests;

public sealed class PolicyTests
{
    [Theory]
    [InlineData(@"c:\DATA\child\..\file.txt", @"C:\Data", true)]
    [InlineData(@"C:\Database\file", @"C:\Data", false)]
    [InlineData(@"C:\Data\..\outside", @"C:\Data", false)]
    [InlineData(@"C:\Data", @"C:\Data\", true)]
    [InlineData(@"C:\Data\file", @"C:\", true)]
    public void DirectoryBoundaries(string path, string root, bool expected) => Assert.Equal(expected, PathPolicy.Within(path, root));

    [Theory]
    [InlineData(@"\\server\share\file")]
    [InlineData(@"\\?\C:\file")]
    [InlineData(@"C:\file:stream")]
    [InlineData(@"C:relative")]
    [InlineData(@"C:\Data.\file")]
    [InlineData(@"C:\Data \file")]
    [InlineData(@"C:\NUL.txt")]
    [InlineData(@"relative\file")]
    [InlineData(@"C:\*.tmp")]
    public void AmbiguousPathsRejected(string path) => Assert.Throws<ArgumentException>(() => PathPolicy.Normalize(path));

    [Fact] public void ExclusionDoesNotMatchPrefixSibling()
    {
        var policy = new PathPolicy([@"C:\Windows"]);
        Assert.False(policy.Allows(@"C:\Data\file", @"C:\", [@"c:\data"]));
        Assert.True(policy.Allows(@"C:\Database\file", @"C:\", [@"c:\data"]));
        Assert.False(policy.Allows(@"C:\Windows\file", @"C:\", []));
    }
    [Fact] public void MemoryUsesPhysicalFormula() => Assert.Equal(600ul, new MemorySnapshot(DateTimeOffset.UtcNow, 1000, 400, [], 0).UsedBytes);
}
