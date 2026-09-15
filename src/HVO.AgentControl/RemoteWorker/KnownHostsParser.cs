using System.Security.Cryptography;

namespace HVO.AgentControl.RemoteWorker;

public sealed record KnownHostIdentity(string Algorithm, string Fingerprint, string ContentHash);

public static class KnownHostsParser
{
    private static readonly HashSet<string> AllowedAlgorithms = ["ssh-ed25519", "ecdsa-sha2-nistp256", "rsa-sha2-256", "rsa-sha2-512"];

    public static KnownHostIdentity Parse(ApprovedExecutionHost host, int expectedUid)
    {
        using var stream = ControllerPrivateFile.OpenRead(host.KnownHostsPath, expectedUid, ControllerFileModes.Private0600 | ControllerFileModes.PublicKey0644);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        if (memory.Length is 0 or > 1024 * 1024) throw new InvalidOperationException("known_hosts has an invalid bounded size.");
        var bytes = memory.ToArray();
        try
        {
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            if (text.IndexOf('\0') >= 0) throw new InvalidOperationException("known_hosts contains invalid data.");
            var expectedHost = host.Port == 22 ? host.Hostname : $"[{host.Hostname}]:{host.Port}";
            var matches = new List<(string Algorithm, byte[] Key)>();
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 3 || fields[0].StartsWith('@') || fields[0].StartsWith('|') || fields[0].Contains('*') || fields[0].Contains('?') || fields[0].Contains(','))
                    throw new InvalidOperationException("known_hosts must contain one unhashed, unmarked, exact host key entry.");
                if (!string.Equals(fields[0], expectedHost, StringComparison.Ordinal)) throw new InvalidOperationException("known_hosts contains an entry for a different host or port.");
                if (!AllowedAlgorithms.Contains(fields[1])) throw new InvalidOperationException("known_hosts key algorithm is not approved.");
                byte[] key;
                try { key = Convert.FromBase64String(fields[2]); }
                catch (FormatException exception) { throw new InvalidOperationException("known_hosts public key is malformed.", exception); }
                if (key.Length is < 32 or > 16 * 1024) { CryptographicOperations.ZeroMemory(key); throw new InvalidOperationException("known_hosts public key has an invalid size."); }
                matches.Add((fields[1], key));
            }
            if (matches.Count != 1) throw new InvalidOperationException("known_hosts must contain exactly one host key entry.");
            try
            {
                var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(matches[0].Key)).TrimEnd('=');
                return new KnownHostIdentity(matches[0].Algorithm, fingerprint, "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            }
            finally { foreach (var match in matches) CryptographicOperations.ZeroMemory(match.Key); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
