using System.Reflection;
using Ahtola.Data.Sqlite.Browser;
using AwesomeAssertions;

#pragma warning disable CA1416

namespace Ahtola.Tests;

/// <summary>
/// Adding a parameter to an existing constructor changes its CLR signature, so every assembly
/// already compiled against the previous shape fails at run time with
/// <see cref="MissingMethodException"/> — optional parameters are a compile-time convenience only
/// and buy no binary compatibility. Synchronous read-mirror mode is therefore offered through
/// dedicated overloads, and these tests pin both the original signatures and the new ones.
/// </summary>
public sealed class AhtolaBrowserBinaryCompatibilityTests
{
    private static readonly Type EncryptionOptions = typeof(AhtolaBrowserEncryptionOptions);

    /// <summary>The constructor signatures that shipped before synchronous read-mirror mode.</summary>
    private static readonly Type[][] LegacyOptionsConstructors =
    [
        [typeof(string), typeof(string), typeof(int), typeof(bool), EncryptionOptions],
        [typeof(string), typeof(int), typeof(bool), EncryptionOptions],
    ];

    private static readonly Type[][] LegacyDataSourceConstructors =
    [
        [typeof(string), typeof(string), typeof(int), typeof(bool), EncryptionOptions],
        [typeof(string), typeof(int), typeof(bool), EncryptionOptions],
    ];

    [Test]
    public void BrowserOptionsKeepEveryPreviouslyShippedConstructorSignature()
    {
        foreach (var signature in LegacyOptionsConstructors)
            AssertConstructorExists(typeof(AhtolaBrowserOptions), signature);
    }

    [Test]
    public void BrowserDataSourceKeepsEveryPreviouslyShippedConstructorSignature()
    {
        foreach (var signature in LegacyDataSourceConstructors)
            AssertConstructorExists(typeof(AhtolaBrowserDataSource), signature);
    }

    [Test]
    public void SynchronousModeIsOfferedThroughDedicatedOverloadsNotAddedParameters()
    {
        foreach (var legacy in LegacyOptionsConstructors)
        {
            AssertConstructorExists(
                typeof(AhtolaBrowserOptions),
                [.. legacy, typeof(AhtolaBrowserSynchronousMode)]);
        }

        foreach (var legacy in LegacyDataSourceConstructors)
        {
            AssertConstructorExists(
                typeof(AhtolaBrowserDataSource),
                [.. legacy, typeof(AhtolaBrowserSynchronousMode)]);
        }
    }

    /// <summary>
    /// A legacy caller binds by signature, not by source. Invoking through reflection is exactly
    /// what an already-compiled assembly does at run time.
    /// </summary>
    [Test]
    public void LegacyConstructorsStillBindAndDefaultToAsyncOnly()
    {
        var options = (AhtolaBrowserOptions)typeof(AhtolaBrowserOptions)
            .GetConstructor(LegacyOptionsConstructors[0])!
            .Invoke(["owned/data.db", "owned", 128 * 1024, false, null]);
        using (options)
        {
            options.DatabasePath.Should().Be("owned/data.db");
            options.SynchronousMode.Should().Be(AhtolaBrowserSynchronousMode.AsyncOnly);
            options.AllowsSynchronousReads.Should().BeFalse();
        }

        var parentOwned = (AhtolaBrowserOptions)typeof(AhtolaBrowserOptions)
            .GetConstructor(LegacyOptionsConstructors[1])!
            .Invoke(["owned/data.db", 128 * 1024, false, null]);
        using (parentOwned)
        {
            parentOwned.OwnedDirectory.Should().Be("owned");
            parentOwned.SynchronousMode.Should().Be(AhtolaBrowserSynchronousMode.AsyncOnly);
        }
    }

    [Test]
    public void SynchronousOverloadsBindAndCarryTheRequestedMode()
    {
        var options = (AhtolaBrowserOptions)typeof(AhtolaBrowserOptions)
            .GetConstructor([.. LegacyOptionsConstructors[0], typeof(AhtolaBrowserSynchronousMode)])!
            .Invoke([
                "owned/data.db",
                "owned",
                128 * 1024,
                false,
                null,
                AhtolaBrowserSynchronousMode.ReadOnlyMirror,
            ]);
        using (options)
        {
            options.SynchronousMode.Should().Be(AhtolaBrowserSynchronousMode.ReadOnlyMirror);
            options.AllowsSynchronousReads.Should().BeTrue();
        }

        using var parentOwned = new AhtolaBrowserOptions(
            "owned/data.db",
            128 * 1024,
            readOnly: false,
            encryption: null,
            synchronousMode: AhtolaBrowserSynchronousMode.ReadOnlyMirror);
        parentOwned.AllowsSynchronousReads.Should().BeTrue();
    }

    /// <summary>
    /// The legacy overload must stay the one a source-level call with the old argument list binds
    /// to, so recompiling against this version does not silently change the resolved member.
    /// </summary>
    [Test]
    public void SourceLevelCallsWithTheLegacyArgumentListStayUnambiguous()
    {
        using var byDirectory = new AhtolaBrowserOptions("owned/data.db", "owned");
        byDirectory.SynchronousMode.Should().Be(AhtolaBrowserSynchronousMode.AsyncOnly);

        using var byParent = new AhtolaBrowserOptions("owned/data.db");
        byParent.SynchronousMode.Should().Be(AhtolaBrowserSynchronousMode.AsyncOnly);

        using var withBuffer = new AhtolaBrowserOptions("owned/data.db", "owned", 128 * 1024, true);
        withBuffer.IsReadOnly.Should().BeTrue();
        withBuffer.SynchronousMode.Should().Be(AhtolaBrowserSynchronousMode.AsyncOnly);
    }

    /// <summary>
    /// No shipped browser constructor may grow an optional parameter for the mode: an optional
    /// parameter changes the signature just as much as a required one.
    /// </summary>
    [Test]
    public void NoConstructorMakesSynchronousModeOptional()
    {
        foreach (var type in new[] { typeof(AhtolaBrowserOptions), typeof(AhtolaBrowserDataSource) })
        {
            foreach (var constructor in type.GetConstructors())
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    if (parameter.ParameterType != typeof(AhtolaBrowserSynchronousMode))
                        continue;

                    parameter.IsOptional.Should().BeFalse(
                        $"{type.Name}'s synchronous-mode parameter must be required so the legacy "
                        + "signature keeps existing as its own overload");
                }
            }
        }
    }

    private static void AssertConstructorExists(Type type, Type[] signature)
    {
        var constructor = type.GetConstructor(
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            signature,
            modifiers: null);

        constructor.Should().NotBeNull(
            $"{type.FullName} must expose .ctor({string.Join(", ", signature.Select(static t => t.Name))})");
    }
}
