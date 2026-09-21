namespace TerraScale.Auth.Connections;

using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;

internal static class SamlLogin
{
    internal const string ProtocolNamespace = "urn:oasis:names:tc:SAML:2.0:protocol";

    internal const string AssertionNamespace = "urn:oasis:names:tc:SAML:2.0:assertion";

    internal static string AcsUrl(AuthOptions options, string slug, string connectionId) =>
        $"{options.PublicOrigin.ToString().TrimEnd('/')}/{slug}/connections/{connectionId}/saml/acs";
}
