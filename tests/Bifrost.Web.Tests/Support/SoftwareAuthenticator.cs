using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Bifrost.Web.Tests.Support;

/// <summary>
/// A WebAuthn authenticator in software: it makes real "none" attestations and real assertions (CBOR authenticator data, COSE key,
/// clientDataJSON, a signature the server must verify) from the options JSON the server sent, and it can be told to misbehave:
/// another origin, another RP ID, no user verification, a chosen sign count. The test's only trust is in the crypto it does itself,
/// not in the library under test.
/// </summary>
public sealed class SoftwareAuthenticator
{
    public enum Kind
    {
        Es256,
        EdDsa
    }

    private readonly Kind _kind;
    private readonly ECDsa? _ec;
    private readonly byte[]? _edSeed;
    private readonly byte[] _edPublic = new byte[Ed25519.PublicKeySize];

    public SoftwareAuthenticator(Kind kind = Kind.Es256)
    {
        _kind = kind;
        if (kind == Kind.Es256)
        {
            _ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        }
        else
        {
            _edSeed = RandomNumberGenerator.GetBytes(Ed25519.SecretKeySize);
            Ed25519.GeneratePublicKey(_edSeed, 0, _edPublic, 0);
        }
    }

    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(32);

    public uint SignCount { get; set; }

    public string Origin { get; set; } = "https://localhost";

    public string RpId { get; set; } = "localhost";

    public bool UserVerified { get; set; } = true;

    private static string B64(byte[] bytes) => WebEncoders.Base64UrlEncode(bytes);

    private byte[] CoseKey()
    {
        var writer = new CborWriter(CborConformanceMode.Lax);
        if (_kind == Kind.Es256)
        {
            var p = _ec!.ExportParameters(false);
            writer.WriteStartMap(5);
            writer.WriteInt32(1); writer.WriteInt32(2);
            writer.WriteInt32(3); writer.WriteInt32(-7);
            writer.WriteInt32(-1); writer.WriteInt32(1);
            writer.WriteInt32(-2); writer.WriteByteString(p.Q.X!);
            writer.WriteInt32(-3); writer.WriteByteString(p.Q.Y!);
        }
        else
        {
            writer.WriteStartMap(4);
            writer.WriteInt32(1); writer.WriteInt32(1);
            writer.WriteInt32(3); writer.WriteInt32(-8);
            writer.WriteInt32(-1); writer.WriteInt32(6);
            writer.WriteInt32(-2); writer.WriteByteString(_edPublic);
        }

        writer.WriteEndMap();
        return writer.Encode();
    }

    private byte[] AuthData(bool attested)
    {
        var data = new List<byte>();
        data.AddRange(SHA256.HashData(Encoding.UTF8.GetBytes(RpId)));
        // UP always; UV when told; AT for a registration.
        data.Add((byte)(0x01 | (UserVerified ? 0x04 : 0) | (attested ? 0x40 : 0)));
        data.AddRange(new[] { (byte)(SignCount >> 24), (byte)(SignCount >> 16), (byte)(SignCount >> 8), (byte)SignCount });
        if (attested)
        {
            data.AddRange(new byte[16]);
            data.Add((byte)(CredentialId.Length >> 8));
            data.Add((byte)CredentialId.Length);
            data.AddRange(CredentialId);
            data.AddRange(CoseKey());
        }

        return data.ToArray();
    }

    private byte[] ClientData(string type, string challenge) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["type"] = type, ["challenge"] = challenge, ["origin"] = Origin, ["crossOrigin"] = false });

    private byte[] Sign(byte[] data) =>
        _kind == Kind.Es256
            ? _ec!.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)
            : Ed25519Sign(data);

    private byte[] Ed25519Sign(byte[] data)
    {
        var signature = new byte[Ed25519.SignatureSize];
        Ed25519.Sign(_edSeed!, 0, data, 0, data.Length, signature, 0);
        return signature;
    }

    /// <summary>The form fields the page's script posts after navigator.credentials.create, for these creation options.</summary>
    public Dictionary<string, string> Register(string optionsJson, string label = "Test key")
    {
        using var options = JsonDocument.Parse(optionsJson);
        var challenge = options.RootElement.GetProperty("challenge").GetString()!;
        var attestation = new CborWriter(CborConformanceMode.Lax);
        attestation.WriteStartMap(3);
        attestation.WriteTextString("fmt"); attestation.WriteTextString("none");
        attestation.WriteTextString("attStmt"); attestation.WriteStartMap(0); attestation.WriteEndMap();
        attestation.WriteTextString("authData"); attestation.WriteByteString(AuthData(attested: true));
        attestation.WriteEndMap();
        return new Dictionary<string, string>
        {
            ["id"] = B64(CredentialId),
            ["rawId"] = B64(CredentialId),
            ["attestationObject"] = B64(attestation.Encode()),
            ["clientDataJson"] = B64(ClientData("webauthn.create", challenge)),
            ["transports"] = "internal",
            ["label"] = label
        };
    }

    /// <summary>The form fields the page's script posts after navigator.credentials.get, for these request options. Counts one use first.</summary>
    public Dictionary<string, string> Assert(string optionsJson, byte[]? userHandle = null)
    {
        using var options = JsonDocument.Parse(optionsJson);
        var challenge = options.RootElement.GetProperty("challenge").GetString()!;
        SignCount++; // a real authenticator counts every assertion
        var authData = AuthData(attested: false);
        var clientData = ClientData("webauthn.get", challenge);
        var signature = Sign([.. authData, .. SHA256.HashData(clientData)]);
        return new Dictionary<string, string>
        {
            ["id"] = B64(CredentialId),
            ["rawId"] = B64(CredentialId),
            ["authenticatorData"] = B64(authData),
            ["clientDataJson"] = B64(clientData),
            ["signature"] = B64(signature),
            ["userHandle"] = userHandle is null ? string.Empty : B64(userHandle)
        };
    }
}
