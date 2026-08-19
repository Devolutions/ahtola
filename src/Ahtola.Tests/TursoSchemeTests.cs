using AwesomeAssertions;

namespace Ahtola.Tests;

// turso:// is the scheme the Turso dashboard hands out; it addresses the same endpoint as libsql://.
public sealed class TursoSchemeTests
{
    [Test]
    [TestCase("turso://db.example.test")]
    [TestCase("TURSO://db.example.test")]
    public void TursoSchemeIsRecognisedAsRemote(string dataSource)
    {
        AhtolaConnectionCapabilities.IsRemoteDataSource(dataSource).Should().BeTrue();
    }

    [Test]
    public void TursoSchemeResolvesToTheSameEndpointAsLibsql()
    {
        Uri viaTurso = AhtolaConnectionOptions
            .Parse("Data Source=turso://db.example.test;Auth Token=t").GetRemoteUri();
        Uri viaLibsql = AhtolaConnectionOptions
            .Parse("Data Source=libsql://db.example.test;Auth Token=t").GetRemoteUri();

        viaTurso.Should().Be(viaLibsql);
        viaTurso.Scheme.Should().Be(Uri.UriSchemeHttps);
    }

    [Test]
    public void TursoSchemeCarriesAnAuthTokenWithoutThrowing()
    {
        // The token is only legal over TLS, and turso maps to https like libsql does.
        Action remoteUri = () => AhtolaConnectionOptions
            .Parse("Data Source=turso://db.example.test;Auth Token=t").GetRemoteUri();

        remoteUri.Should().NotThrow();
    }

    [Test]
    public void UnknownSchemeIsStillRejected()
    {
        Action remoteUri = () => AhtolaConnectionOptions
            .Parse("Data Source=tursox://db.example.test;Auth Token=t").GetRemoteUri();

        remoteUri.Should().Throw<InvalidOperationException>();
    }
}
