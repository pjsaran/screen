using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

using Captr.Core.Supervision;

using Shouldly;

namespace Captr.Integration.Tests.Build;

/// <summary>
/// build/Sign-Artifacts.ps1 and build/Verify-Signatures.ps1: a dry run needs no
/// certificate, the pinned FFmpeg is proven before it is signed, and verification
/// fails on every way a shipped file can be wrongly signed.
/// </summary>
/// <remarks>
/// The certificates here are made on the spot, in memory, and written only to this
/// test's temporary folder — nothing is added to any certificate store. Windows does
/// not trust them, which is why verification of the real signing run uses
/// -AllowUntrustedRoot; the release pipeline never does. The tests that timestamp
/// need the network (a timestamp authority, and nuget.org for the pinned signtool)
/// and are skipped, saying so, without it.
/// </remarks>
[Trait("Category", "Os")]
public sealed class SigningScriptTests : IDisposable
{
    private const string Publisher = "Captr Test Signing";
    private const string PfxPassword = "test-only";

    private readonly string _dir = Directory.CreateTempSubdirectory("captr-signing-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Every signing variable removed, so nothing configured on the machine
    /// running the tests leaks in.</summary>
    private static Dictionary<string, string?> NoSigningEnvironment() => new()
    {
        ["CAPTR_SIGN_METHOD"] = null,
        ["CAPTR_SIGN_PFX"] = null,
        ["CAPTR_SIGN_PFX_BASE64"] = null,
        ["CAPTR_SIGN_PFX_PASSWORD"] = null,
        ["CAPTR_SIGN_PASSWORD"] = null,
        ["CAPTR_SIGN_CERT_THUMBPRINT"] = null,
        ["CAPTR_ARTIFACT_SIGNING_ENDPOINT"] = null,
        ["CAPTR_SIGN_TIMESTAMP_URL"] = null,
        ["CAPTR_SIGN_PUBLISHER"] = null,
    };

    /// <summary>A small stand-in for a shipped folder: one of Captr's own DLLs, one
    /// unsigned third-party DLL, one Microsoft-signed runtime DLL, and a stand-in
    /// ffmpeg.exe with a capabilities.json pinning its digest.</summary>
    private string Payload()
    {
        string payload = Path.Combine(_dir, "payload");
        string ffmpeg = Path.Combine(payload, "ffmpeg");
        Directory.CreateDirectory(ffmpeg);

        string unsignedDll = typeof(SigningScriptTests).Assembly.Location;
        File.Copy(unsignedDll, Path.Combine(payload, "Captr.Sample.dll"));
        File.Copy(unsignedDll, Path.Combine(payload, "ThirdParty.Unsigned.dll"));
        File.Copy(typeof(object).Assembly.Location, Path.Combine(payload, "System.Private.CoreLib.dll"));

        string fakeFfmpeg = Path.Combine(ffmpeg, "ffmpeg.exe");
        File.Copy(Environment.ProcessPath!, fakeFfmpeg);
        File.WriteAllText(Path.Combine(ffmpeg, "capabilities.json"), JsonSerializer.Serialize(new
        {
            buildId = "test",
            binaryDigests = new Dictionary<string, string> { ["ffmpeg.exe"] = PeImageDigest.Compute(fakeFfmpeg) },
        }));

        return payload;
    }

    private string MakePfx(string commonName)
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.3")], false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        string path = Path.Combine(_dir, $"{commonName.Replace(' ', '-')}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, PfxPassword));
        return path;
    }

    private static Task<(int ExitCode, string Output)> SignAsync(IEnumerable<string> arguments, Dictionary<string, string?> environment) =>
        BuildScriptHarness.RunAsync(BuildScriptHarness.Script(@"build\Sign-Artifacts.ps1"), arguments, environment, TimeSpan.FromMinutes(10));

    private static Task<(int ExitCode, string Output)> VerifyAsync(IEnumerable<string> arguments) =>
        BuildScriptHarness.RunAsync(BuildScriptHarness.Script(@"build\Verify-Signatures.ps1"), arguments, NoSigningEnvironment());

    /// <summary>Signs files the way PowerShell does on its own: optionally with a
    /// LEGACY timestamp, never an RFC 3161 one - which is what the release rules must
    /// catch.</summary>
    private async Task PowerShellSignAsync(string pfx, string file, string? legacyTimestampServer = null)
    {
        string script = Path.Combine(_dir, "ps-sign.ps1");
        await File.WriteAllTextAsync(script, """
            param($Pfx, $File, $Server)
            $cert = [System.Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadPkcs12FromFile($Pfx, 'test-only')
            if ($Server) { Set-AuthenticodeSignature -FilePath $File -Certificate $cert -HashAlgorithm SHA256 -TimestampServer $Server | Out-Null }
            else { Set-AuthenticodeSignature -FilePath $File -Certificate $cert -HashAlgorithm SHA256 | Out-Null }
            """, TestContext.Current.CancellationToken);
        List<string> arguments = ["-Pfx", pfx, "-File", file];
        if (legacyTimestampServer is not null)
        {
            arguments.AddRange(["-Server", legacyTimestampServer]);
        }

        (int exitCode, string output) = await BuildScriptHarness.RunAsync(script, arguments);
        exitCode.ShouldBe(0, output);
    }

    private static async Task<bool> NetworkAvailableAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using HttpResponseMessage nuget = await http.GetAsync("https://api.nuget.org/v3/index.json");
            using HttpResponseMessage timestamp = await http.GetAsync("http://timestamp.digicert.com");
            return nuget.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    [Fact]
    public async Task A_dry_run_needs_no_certificate_and_changes_nothing()
    {
        string payload = Payload();
        string own = Path.Combine(payload, "Captr.Sample.dll");
        byte[] before = await File.ReadAllBytesAsync(own, TestContext.Current.CancellationToken);

        (int exitCode, string output) = await SignAsync(["-Path", payload, "-DryRun"], NoSigningEnvironment());

        exitCode.ShouldBe(0, output);
        output.ShouldContain("Dry run");
        output.ShouldContain("sign  " + own);
        output.ShouldNotContain("sign  " + Path.Combine(payload, "System.Private.CoreLib.dll"),
            customMessage: "a file Microsoft already signed keeps Microsoft's signature");
        (await File.ReadAllBytesAsync(own, TestContext.Current.CancellationToken)).ShouldBe(before);
    }

    [Fact]
    public async Task An_ffmpeg_that_is_not_the_pinned_build_is_never_signed_even_in_a_dry_run()
    {
        string payload = Payload();
        string ffmpeg = Path.Combine(payload, "ffmpeg", "ffmpeg.exe");
        byte[] bytes = await File.ReadAllBytesAsync(ffmpeg, TestContext.Current.CancellationToken);
        bytes[bytes.Length / 2] ^= 0x01;
        await File.WriteAllBytesAsync(ffmpeg, bytes, TestContext.Current.CancellationToken);

        (int exitCode, string output) = await SignAsync(["-Path", payload, "-DryRun"], NoSigningEnvironment());

        exitCode.ShouldNotBe(0);
        output.ShouldContain("not the pinned FFmpeg build");
    }

    [Fact]
    public async Task An_unconfigured_build_warns_and_succeeds_but_a_release_that_requires_signing_fails()
    {
        string payload = Payload();

        (int developerExit, string developerOutput) = await SignAsync(["-Path", payload], NoSigningEnvironment());
        (int releaseExit, string releaseOutput) = await SignAsync(["-Path", payload, "-Require"], NoSigningEnvironment());

        developerExit.ShouldBe(0, developerOutput);
        developerOutput.ShouldContain("UNSIGNED BUILD");
        releaseExit.ShouldNotBe(0);
        releaseOutput.ShouldContain("no signing method is configured");
    }

    [Fact]
    public async Task A_signing_method_with_a_missing_variable_is_named_rather_than_half_working()
    {
        // The documentation once named CAPTR_SIGN_PASSWORD while the script read
        // CAPTR_SIGN_PFX_PASSWORD: the password arrived as nothing, and signtool
        // failed with an error about the timestamp URL.
        Dictionary<string, string?> environment = NoSigningEnvironment();
        environment["CAPTR_SIGN_PFX"] = MakePfx(Publisher);

        (int exitCode, string output) = await SignAsync(["-Path", Payload()], environment);

        exitCode.ShouldNotBe(0);
        output.ShouldContain("CAPTR_SIGN_PFX_PASSWORD");
    }

    [Fact]
    public async Task Verification_fails_on_an_unsigned_file()
    {
        string payload = Payload();

        (int exitCode, string output) = await VerifyAsync(["-Path", payload, "-ExpectedPublisher", Publisher, "-AllowUntrustedRoot"]);

        exitCode.ShouldNotBe(0);
        output.ShouldContain("UNSIGNED");
        output.ShouldContain("Captr.Sample.dll");
    }

    [Fact]
    public async Task Verification_fails_on_a_signature_without_a_timestamp()
    {
        string file = Path.Combine(Payload(), "Captr.Sample.dll");
        await PowerShellSignAsync(MakePfx(Publisher), file);

        (int exitCode, string output) = await VerifyAsync(["-Path", file, "-ExpectedPublisher", Publisher, "-AllowUntrustedRoot"]);

        exitCode.ShouldNotBe(0);
        output.ShouldContain("NOT TIMESTAMPED");
    }

    [Fact]
    public async Task Verification_fails_on_the_wrong_publisher()
    {
        string file = Path.Combine(Payload(), "Captr.Sample.dll");
        await PowerShellSignAsync(MakePfx("Somebody Else"), file);

        (int exitCode, string output) = await VerifyAsync(["-Path", file, "-ExpectedPublisher", Publisher, "-AllowUntrustedRoot"]);

        exitCode.ShouldNotBe(0);
        output.ShouldContain("WRONG SIGNER");
        output.ShouldContain("Somebody Else");
    }

    [Fact]
    public async Task Verification_fails_on_a_legacy_timestamp()
    {
        if (!await NetworkAvailableAsync())
        {
            Assert.Skip("No network: a timestamp authority is needed to produce a timestamp at all.");
        }

        string file = Path.Combine(Payload(), "Captr.Sample.dll");
        await PowerShellSignAsync(MakePfx(Publisher), file, legacyTimestampServer: "http://timestamp.digicert.com");

        (int exitCode, string output) = await VerifyAsync(["-Path", file, "-ExpectedPublisher", Publisher, "-AllowUntrustedRoot"]);

        exitCode.ShouldNotBe(0);
        output.ShouldContain("NOT RFC 3161");
    }

    [Fact]
    public async Task A_real_signing_run_signs_what_is_ours_keeps_what_is_trusted_and_passes_verification()
    {
        if (!await NetworkAvailableAsync())
        {
            Assert.Skip("No network: signing needs the pinned signtool from nuget.org and a timestamp authority.");
        }

        string payload = Payload();
        string ffmpeg = Path.Combine(payload, "ffmpeg", "ffmpeg.exe");
        string pinnedDigest = PeImageDigest.Compute(ffmpeg);
        string runtime = Path.Combine(payload, "System.Private.CoreLib.dll");
        byte[] runtimeBefore = await File.ReadAllBytesAsync(runtime, TestContext.Current.CancellationToken);

        Dictionary<string, string?> environment = NoSigningEnvironment();
        environment["CAPTR_SIGN_PFX"] = MakePfx(Publisher);
        environment["CAPTR_SIGN_PFX_PASSWORD"] = PfxPassword;
        (int signExit, string signOutput) = await SignAsync(["-Path", payload, "-Require"], environment);
        signExit.ShouldBe(0, signOutput);

        (int verifyExit, string verifyOutput) = await VerifyAsync(["-Path", payload, "-ExpectedPublisher", Publisher, "-AllowUntrustedRoot"]);
        verifyExit.ShouldBe(0, verifyOutput);

        (await File.ReadAllBytesAsync(runtime, TestContext.Current.CancellationToken))
            .ShouldBe(runtimeBefore, "Microsoft's signature on its own file is left exactly as it was");
        PeImageDigest.Compute(ffmpeg).ShouldBe(pinnedDigest, "signing must not break the application's check of the pinned FFmpeg");
        signOutput.ShouldNotContain(PfxPassword, customMessage: "the password never appears in any output");
    }
}
