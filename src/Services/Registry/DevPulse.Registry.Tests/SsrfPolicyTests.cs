using System.Net;
using DevPulse.Security.Ssrf;

namespace DevPulse.Registry.Tests;

public class SsrfPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("2002:7f00:1::")]
    [InlineData("2001:db8::1")]
    public void Les_adresses_internes_ne_sont_pas_publiques(string address) =>
        Assert.False(IpAddressPolicy.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void Les_adresses_publiques_sont_acceptees(string address) =>
        Assert.True(IpAddressPolicy.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("http://2130706433/")]       // 127.0.0.1 en décimal
    [InlineData("http://0x7f.0.0.1/")]       // en hexadécimal
    [InlineData("http://0177.0.0.1/")]       // en octal
    [InlineData("http://127.1/")]            // forme courte
    [InlineData("http://[::1]/")]
    [InlineData("http://[::ffff:7f00:1]/")]
    [InlineData("http://localhost/")]
    [InlineData("http://LOCALHOST./")]
    [InlineData("http://foo.localhost/")]
    [InlineData("http://metadata.google.internal/")]
    [InlineData("http://rabbitmq/")]
    [InlineData("gopher://api.example.com/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://admin:admin@api.example.com/")]
    [InlineData("https://api.example.com:22/")]
    public void Les_ecritures_detournees_d_une_cible_interne_sont_refusees(string url) =>
        Assert.NotNull(TargetUrlPolicy.Validate(new Uri(url), allowPrivateTargets: false));

    [Theory]
    [InlineData("https://api.example.com/health")]
    [InlineData("http://api.example.com:8080/health")]
    [InlineData("https://8.8.8.8/")]
    public void Une_cible_publique_est_acceptee(string url) =>
        Assert.Null(TargetUrlPolicy.Validate(new Uri(url), allowPrivateTargets: false));

    [Fact]
    public void Le_mode_local_autorise_localhost_mais_garde_le_controle_du_schema()
    {
        Assert.Null(TargetUrlPolicy.Validate(new Uri("http://localhost:5020/health"), allowPrivateTargets: true));
        Assert.NotNull(TargetUrlPolicy.Validate(new Uri("ftp://localhost/"), allowPrivateTargets: true));
    }
}
