using AwesomeAssertions;
using Ahtola.Data.Sqlite.Browser;

namespace Ahtola.Tests;

public sealed class AhtolaBrowserCapabilitiesTests
{
    [Test]
    public void IsSupportedIsTrueOnlyWhenEveryCapabilityIsPresent()
    {
        var capabilities = new AhtolaBrowserCapabilities(
            IsCrossOriginIsolated: true,
            HasSharedArrayBuffer: true,
            HasOriginPrivateFileSystem: true,
            HasSynchronousAccessHandles: true,
            HasWebLocks: true);

        capabilities.IsSupported.Should().BeTrue();
        capabilities.MissingCapabilities.Should().BeEmpty();
    }

    [Test]
    public void IsSupportedIsFalseWhenOnlySynchronousAccessHandleCreationFails()
    {
        // Mirrors exactly what the real probe reports when
        // createSyncAccessHandle fails (or the prerequisite checks pass but
        // the browser still cannot actually create one): every other signal
        // stays true, only this one flips to false.
        var capabilities = new AhtolaBrowserCapabilities(
            IsCrossOriginIsolated: true,
            HasSharedArrayBuffer: true,
            HasOriginPrivateFileSystem: true,
            HasSynchronousAccessHandles: false,
            HasWebLocks: true);

        capabilities.IsSupported.Should().BeFalse();
        capabilities.MissingCapabilities.Should().Equal(
            "Origin Private File System synchronous access handles");
    }

    [Test]
    public void MissingCapabilitiesListsEveryUnavailableFeatureInOrder()
    {
        var capabilities = new AhtolaBrowserCapabilities(
            IsCrossOriginIsolated: false,
            HasSharedArrayBuffer: false,
            HasOriginPrivateFileSystem: false,
            HasSynchronousAccessHandles: false,
            HasWebLocks: false);

        capabilities.IsSupported.Should().BeFalse();
        capabilities.MissingCapabilities.Should().Equal(
            "cross-origin isolation",
            "SharedArrayBuffer",
            "Origin Private File System",
            "Origin Private File System synchronous access handles",
            "Web Locks");
    }

    [Test]
    public void MissingCapabilitiesReportsOnlyOriginPrivateFileSystemWhenOpfsItselfIsAbsent()
    {
        // This is the exact, structured signature the WebKit smoke-check
        // script (scripts/Invoke-BrowserSmokeCheck.ps1) treats as the known,
        // permanent Playwright-WebKit test-engine gap: OPFS itself is
        // unavailable, which also makes the synchronous-handle probe
        // unreachable, while every other capability is present.
        var capabilities = new AhtolaBrowserCapabilities(
            IsCrossOriginIsolated: true,
            HasSharedArrayBuffer: true,
            HasOriginPrivateFileSystem: false,
            HasSynchronousAccessHandles: false,
            HasWebLocks: true);

        capabilities.MissingCapabilities.Should().Equal(
            "Origin Private File System",
            "Origin Private File System synchronous access handles");
    }
}
